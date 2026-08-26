/**
 * Corruption scan progress: the card's message, detail line and progress mode, plus the details
 * object the registry attaches to a corruption detection card.
 *
 * Both halves read the same counters from three places - the event or REST response itself, the
 * scan summary, and the stage context - because a structural scan reports through whichever of
 * them is populated at that point in the run.
 */

import i18n from '@/i18n';
import { formatCount } from '@/utils/formatters';
import { hasUnresolvedInterpolation, translateRecoveryStage } from '@/utils/stageKeyMessage';
import type { CorruptionDetectionProgressEvent } from '../SignalRContext/types';
import type { NotificationProgressMode, StageContext } from './types';
import type { CorruptionDetectionMethod } from '@/types';
import type {
  StructuralBaselineStatus,
  StructuralEffectiveScanMode,
  StructuralScanMode,
  StructuralScanSummary
} from '@/types/corruptionScan';

interface CorruptionProgressInput {
  detectionMethod?: CorruptionDetectionProgressEvent['detectionMethod'];
  scanMode?: CorruptionDetectionProgressEvent['scanMode'];
  effectiveScanMode?: CorruptionDetectionProgressEvent['effectiveScanMode'];
  baselineStatus?: CorruptionDetectionProgressEvent['baselineStatus'];
  resumed?: boolean;
  stageKey?: string;
  context?: StageContext;
  percentComplete?: number | null;
  filesProcessed?: number;
  totalFiles?: number;
  filesDiscovered?: number;
  filesReused?: number;
  filesInspected?: number;
  filesRevalidated?: number;
  invalidFiles?: number;
  filesPendingRetry?: number;
}

interface CorruptionProgressPresentation {
  message: string;
  detailMessage?: string;
  progressMode: NotificationProgressMode;
  progressAriaValueText: string;
}

function finiteNonNegative(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
}

function preferredNumber(contextValue: unknown, legacyValue: unknown): number | undefined {
  return finiteNonNegative(contextValue) ?? finiteNonNegative(legacyValue);
}

function formatFilesPerSecond(rate: number): string {
  return formatCount(rate >= 10 ? Math.round(rate) : Number(rate.toFixed(1)));
}

function formatCoarseDuration(seconds: number): string {
  if (seconds < 60) return i18n.t('signalr.corruptionDetect.metrics.duration.underMinute');

  const totalMinutes = Math.max(1, Math.round(seconds / 60));
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours === 0) {
    return i18n.t('signalr.corruptionDetect.metrics.duration.minutes', {
      minutes: formatCount(totalMinutes)
    });
  }
  if (minutes === 0) {
    return i18n.t('signalr.corruptionDetect.metrics.duration.hours', {
      hours: formatCount(hours)
    });
  }
  return i18n.t('signalr.corruptionDetect.metrics.duration.hoursMinutes', {
    hours: formatCount(hours),
    minutes: formatCount(minutes)
  });
}

function safeCorruptionMessage(
  stageKey: string | undefined,
  context: StageContext,
  fallbackKey: string
): string {
  return translateRecoveryStage(stageKey, context, fallbackKey);
}

function structuralScanMode(
  input: CorruptionProgressInput,
  context: StageContext
): CorruptionDetectionProgressEvent['scanMode'] {
  const contextMode = context.scanMode;
  return (
    input.scanMode ??
    (contextMode === 'full' || contextMode === 'incremental' ? contextMode : undefined)
  );
}

function isInitialBaselineBuild(context: StageContext): boolean {
  return context.effectiveScanMode === 'baseline' || context.baselineStatus === 'building';
}

function structuralProgressKey(
  scanMode: CorruptionDetectionProgressEvent['scanMode'],
  context: StageContext
): string {
  if (scanMode === 'incremental' && isInitialBaselineBuild(context)) {
    return 'signalr.corruptionDetect.buildingBaseline';
  }
  if (scanMode === 'incremental' && context.resumed === true) {
    return 'signalr.corruptionDetect.resumingIncremental';
  }
  if (scanMode === 'incremental') return 'signalr.corruptionDetect.scanningIncremental';
  if (scanMode === 'full') return 'signalr.corruptionDetect.scanningFull';
  return 'signalr.corruptionDetect.scanningHeaders';
}

export function structuralStartingKey(
  scanMode: CorruptionDetectionProgressEvent['scanMode']
): string {
  if (scanMode === 'incremental') {
    return 'signalr.corruptionDetect.startingStructuralIncremental';
  }
  if (scanMode === 'full') return 'signalr.corruptionDetect.startingStructuralFull';
  return 'signalr.corruptionDetect.startingStructural';
}

/**
 * Normalize a live SignalR checkpoint or recovered REST snapshot once, then
 * produce the same stable primary copy and truthful optional metrics for both.
 */
export function formatCorruptionProgress(
  input: CorruptionProgressInput
): CorruptionProgressPresentation {
  const context: StageContext = {
    ...(input.context ?? {}),
    ...(input.scanMode !== undefined ? { scanMode: input.scanMode } : {}),
    ...(input.effectiveScanMode !== undefined
      ? { effectiveScanMode: input.effectiveScanMode }
      : {}),
    ...(input.baselineStatus !== undefined ? { baselineStatus: input.baselineStatus } : {}),
    ...(input.resumed !== undefined ? { resumed: input.resumed } : {}),
    ...(input.filesDiscovered !== undefined ? { filesDiscovered: input.filesDiscovered } : {}),
    ...(input.filesProcessed !== undefined ? { filesProcessed: input.filesProcessed } : {}),
    ...(input.filesReused !== undefined ? { filesReused: input.filesReused } : {}),
    ...(input.filesInspected !== undefined ? { filesInspected: input.filesInspected } : {}),
    ...(input.filesRevalidated !== undefined ? { filesRevalidated: input.filesRevalidated } : {}),
    ...(input.invalidFiles !== undefined ? { invalidFiles: input.invalidFiles } : {}),
    ...(input.filesPendingRetry !== undefined ? { filesPendingRetry: input.filesPendingRetry } : {})
  };
  const pass = context.pass;
  const detectionMethod =
    input.detectionMethod ??
    (context.detectionMethod === 'structural' || context.detectionMethod === 'repeated_miss'
      ? context.detectionMethod
      : undefined);
  const scanMode = structuralScanMode(input, context);
  const isEnumeration =
    input.stageKey === 'signalr.corruptionDetect.enumerating' || pass === 'counting';
  const isStructuralInspection =
    detectionMethod === 'structural' &&
    (input.stageKey === 'signalr.corruptionDetect.scanningHeaders' ||
      input.stageKey === 'signalr.corruptionDetect.scanning' ||
      pass === 'scanning');

  if (
    detectionMethod === 'structural' &&
    (input.stageKey === 'signalr.corruptionDetect.starting' ||
      input.stageKey === 'signalr.corruptionDetect.startingStructural')
  ) {
    const message = i18n.t(structuralStartingKey(scanMode));
    return {
      message,
      progressMode: 'determinate',
      progressAriaValueText: message
    };
  }

  if (isEnumeration) {
    const count = preferredNumber(context.filesDiscovered ?? context.count, input.filesProcessed);
    const rate = finiteNonNegative(context.filesPerSecond);
    const parts: string[] = [];
    if (detectionMethod === 'structural' && isInitialBaselineBuild(context)) {
      parts.push(i18n.t('signalr.corruptionDetect.metrics.initialBaseline'));
    }
    if (
      detectionMethod === 'structural' &&
      scanMode === 'incremental' &&
      context.resumed === true
    ) {
      parts.push(i18n.t('signalr.corruptionDetect.metrics.resumed'));
    }
    if (count !== undefined) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.found', { count: formatCount(Math.floor(count)) })
      );
    }
    if (rate !== undefined && rate > 0) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.rate', {
          filesPerSecond: formatFilesPerSecond(rate)
        })
      );
    }
    const detailMessage = parts.length > 0 ? parts.join(' • ') : undefined;
    const message = i18n.t('signalr.corruptionDetect.enumerating');
    return {
      message,
      detailMessage,
      progressMode: 'indeterminate',
      progressAriaValueText: detailMessage ? `${message} ${detailMessage}` : message
    };
  }

  if (isStructuralInspection) {
    const processed = preferredNumber(context.filesProcessed, input.filesProcessed);
    const total = preferredNumber(context.totalFiles ?? context.filesDiscovered, input.totalFiles);
    const suspects = preferredNumber(
      context.invalidFiles ?? context.totalCorrupted,
      context.suspects
    );
    const rate = finiteNonNegative(context.filesPerSecond);
    const etaSeconds = finiteNonNegative(context.etaSeconds);
    const parts: string[] = [];

    if (scanMode === 'incremental' && context.resumed === true) {
      parts.push(i18n.t('signalr.corruptionDetect.metrics.resumed'));
    }

    if (processed !== undefined) {
      const processedDisplay = formatCount(Math.floor(processed));
      if (total !== undefined && total > 0) {
        parts.push(
          i18n.t(
            scanMode === 'incremental'
              ? 'signalr.corruptionDetect.metrics.processedOfTotal'
              : 'signalr.corruptionDetect.metrics.checkedOfTotal',
            {
              processed: processedDisplay,
              total: formatCount(Math.floor(total))
            }
          )
        );
      } else {
        parts.push(
          i18n.t(
            scanMode === 'incremental'
              ? 'signalr.corruptionDetect.metrics.processed'
              : 'signalr.corruptionDetect.metrics.checked',
            {
              processed: processedDisplay
            }
          )
        );
      }
    }
    if (suspects !== undefined) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.invalid', {
          suspects: formatCount(Math.floor(suspects))
        })
      );
    }
    const reused = finiteNonNegative(context.filesReused);
    if (reused !== undefined) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.reused', {
          count: formatCount(Math.floor(reused))
        })
      );
    }
    const inspected = finiteNonNegative(context.filesInspected);
    if (inspected !== undefined) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.inspected', {
          count: formatCount(Math.floor(inspected))
        })
      );
    }
    const revalidated = finiteNonNegative(context.filesRevalidated);
    if (revalidated !== undefined) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.revalidated', {
          count: formatCount(Math.floor(revalidated))
        })
      );
    }
    const pendingRetry = finiteNonNegative(context.filesPendingRetry);
    if (pendingRetry !== undefined && pendingRetry > 0) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.pendingRetry', {
          count: formatCount(Math.floor(pendingRetry))
        })
      );
    }
    if (rate !== undefined && rate > 0) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.rate', {
          filesPerSecond: formatFilesPerSecond(rate)
        })
      );
    }
    if (
      etaSeconds !== undefined &&
      total !== undefined &&
      total > 0 &&
      processed !== undefined &&
      processed <= total &&
      rate !== undefined &&
      rate > 0
    ) {
      parts.push(
        i18n.t('signalr.corruptionDetect.metrics.remaining', {
          eta: formatCoarseDuration(etaSeconds)
        })
      );
    }

    const detailMessage = parts.length > 0 ? parts.join(' • ') : undefined;
    const message = i18n.t(structuralProgressKey(scanMode, context));
    return {
      message,
      detailMessage,
      progressMode: 'determinate',
      progressAriaValueText: detailMessage ? `${message} ${detailMessage}` : message
    };
  }

  const fallbackKey =
    detectionMethod === 'structural'
      ? structuralProgressKey(scanMode, context)
      : 'signalr.corruptionDetect.scanningLogs';
  const message = safeCorruptionMessage(
    input.stageKey === 'signalr.corruptionDetect.scanning' ? undefined : input.stageKey,
    context,
    fallbackKey
  );
  const safeMessage = hasUnresolvedInterpolation(message) ? i18n.t(fallbackKey) : message;
  return {
    message: safeMessage,
    progressMode: 'determinate',
    progressAriaValueText: safeMessage
  };
}

interface CorruptionNotificationSource {
  operationId?: string | null;
  detectionMethod?: CorruptionDetectionMethod;
  scanMode?: StructuralScanMode;
  effectiveScanMode?: StructuralEffectiveScanMode;
  baselineStatus?: StructuralBaselineStatus;
  resumed?: boolean;
  filesDiscovered?: number;
  filesProcessed?: number;
  filesReused?: number;
  filesInspected?: number;
  filesRevalidated?: number;
  invalidFiles?: number;
  filesPendingRetry?: number;
  filesPruned?: number;
  stateEntries?: number;
  stateCommitted?: boolean;
  scanSummary?: StructuralScanSummary;
  context?: StageContext;
}

const corruptionScanMode = (
  source: CorruptionNotificationSource
): StructuralScanMode | undefined => {
  const contextMode = source.context?.scanMode;
  return (
    source.scanMode ??
    source.scanSummary?.scanMode ??
    (contextMode === 'full' || contextMode === 'incremental' ? contextMode : undefined)
  );
};

const corruptionEffectiveScanMode = (
  source: CorruptionNotificationSource
): StructuralEffectiveScanMode | undefined => {
  const value =
    source.effectiveScanMode ??
    source.scanSummary?.effectiveScanMode ??
    source.context?.effectiveScanMode;
  return value === 'full' || value === 'incremental' || value === 'baseline' ? value : undefined;
};

const corruptionBaselineStatus = (
  source: CorruptionNotificationSource
): StructuralBaselineStatus | undefined => {
  const value =
    source.baselineStatus ?? source.scanSummary?.baselineStatus ?? source.context?.baselineStatus;
  return value === 'stateless' ||
    value === 'building' ||
    value === 'ready' ||
    value === 'incomplete'
    ? value
    : undefined;
};

const finiteContextCount = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;

export const corruptionNotificationDetails = (source: CorruptionNotificationSource) => ({
  operationId: source.operationId ?? undefined,
  detectionMethod: source.detectionMethod,
  scanMode: corruptionScanMode(source),
  effectiveScanMode: corruptionEffectiveScanMode(source),
  baselineStatus: corruptionBaselineStatus(source),
  resumed:
    source.resumed ??
    source.scanSummary?.resumed ??
    (typeof source.context?.resumed === 'boolean' ? source.context.resumed : undefined),
  filesDiscovered:
    finiteContextCount(source.filesDiscovered) ??
    finiteContextCount(source.scanSummary?.filesDiscovered) ??
    finiteContextCount(source.context?.filesDiscovered),
  filesProcessed:
    finiteContextCount(source.filesProcessed) ??
    finiteContextCount(source.scanSummary?.filesProcessed) ??
    finiteContextCount(source.context?.filesProcessed),
  filesReused:
    finiteContextCount(source.filesReused) ??
    finiteContextCount(source.scanSummary?.filesReused) ??
    finiteContextCount(source.context?.filesReused),
  filesInspected:
    finiteContextCount(source.filesInspected) ??
    finiteContextCount(source.scanSummary?.filesInspected) ??
    finiteContextCount(source.context?.filesInspected),
  filesRevalidated:
    finiteContextCount(source.filesRevalidated) ??
    finiteContextCount(source.scanSummary?.filesRevalidated) ??
    finiteContextCount(source.context?.filesRevalidated),
  invalidFiles:
    finiteContextCount(source.invalidFiles) ??
    finiteContextCount(source.scanSummary?.invalidFiles) ??
    finiteContextCount(source.context?.invalidFiles),
  filesPendingRetry:
    finiteContextCount(source.filesPendingRetry) ??
    finiteContextCount(source.scanSummary?.filesPendingRetry) ??
    finiteContextCount(source.context?.filesPendingRetry),
  filesPruned:
    finiteContextCount(source.filesPruned) ??
    finiteContextCount(source.scanSummary?.filesPruned) ??
    finiteContextCount(source.context?.filesPruned),
  stateEntries:
    finiteContextCount(source.stateEntries) ??
    finiteContextCount(source.scanSummary?.stateEntries) ??
    finiteContextCount(source.context?.stateEntries),
  stateCommitted:
    source.stateCommitted ??
    source.scanSummary?.stateCommitted ??
    (typeof source.context?.stateCommitted === 'boolean'
      ? source.context.stateCommitted
      : undefined)
});
