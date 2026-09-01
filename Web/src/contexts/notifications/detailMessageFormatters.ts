import type {
  ProcessingProgressEvent,
  LogRemovalProgressEvent,
  LogRemovalCompleteEvent,
  GameRemovalProgressEvent,
  ServiceRemovalProgressEvent,
  CorruptionRemovalStartedEvent,
  CorruptionRemovalCompleteEvent,
  GameDetectionStartedEvent,
  GameDetectionProgressEvent,
  GameDetectionCompleteEvent,
  CorruptionDetectionStartedEvent,
  CorruptionDetectionProgressEvent,
  CorruptionDetectionCompleteEvent,
  DatabaseResetProgressEvent,
  CacheClearProgressEvent,
  CacheClearCompleteEvent,
  ScheduledPrefillProgressEvent,
  DataImportStartedEvent,
  DataImportProgressEvent,
  DataImportCompleteEvent,
  EpicGameMappingsUpdatedEvent,
  XboxGameMappingsUpdatedEvent
} from '../SignalRContext/types';
import i18n from '@/i18n';
import { classifyRemovalKind, removalStageKey, withRemovalIdentity } from './removalKind';
import { formatBytes, formatCount } from '@/utils/formatters';
import { formatCorruptionProgress, structuralStartingKey } from './corruptionProgress';
import {
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  GENERIC_SKIPPED_I18N_KEY
} from './constants';

type GameDetectionInterpolation = Record<string, string | number | boolean | null>;

/** Merge SignalR/API context with top-level detection counts for i18n interpolation. */
export function buildGameDetectionInterpolation(
  context?: GameDetectionInterpolation | null,
  extras?: { totalGamesDetected?: number; newGamesCount?: number }
): GameDetectionInterpolation {
  return {
    totalGamesDetected: context?.totalGamesDetected ?? extras?.totalGamesDetected ?? 0,
    newGamesCount: context?.newGamesCount ?? extras?.newGamesCount ?? 0,
    ...(context ?? {})
  };
}

/**
 * Detail message formatter functions for notification events.
 * These extract inline message generation logic into reusable, testable functions.
 */

// ============================================================================
// Log Processing
// ============================================================================

/**
 * Formats the primary message for log processing progress.
 * Shows MB processed of total MB.
 * @param event - The processing progress event from SignalR
 * @returns Formatted message string
 */
export const formatLogProcessingMessage = (event: ProcessingProgressEvent): string => {
  return i18n.t('signalr.logProcessing.progress', {
    mbProcessed: event.mbProcessed?.toFixed(1) || '0',
    mbTotal: event.mbTotal?.toFixed(1) || '0'
  });
};

/**
 * Formats the completion detail message for log processing (simple version).
 * @param entriesProcessed - Number of entries successfully processed
 * @returns Formatted completion message
 */
export const formatLogProcessingCompletionMessage = (entriesProcessed?: number): string => {
  return i18n.t('signalr.logProcessing.completedEntries', {
    entriesProcessed: entriesProcessed?.toLocaleString() || '0'
  });
};

/**
 * Formats the detailed log processing completion message with timing info.
 * @param entriesProcessed - Number of log entries processed
 * @param linesProcessed - Number of lines processed
 * @param elapsed - Elapsed time in minutes
 * @returns Formatted completion message with timing info
 */
export const formatLogProcessingDetailMessage = (
  entriesProcessed?: number,
  linesProcessed?: number,
  elapsed?: number
): string => {
  return i18n.t('signalr.logProcessing.completedDetail', {
    entriesProcessed: entriesProcessed?.toLocaleString() || '0',
    linesProcessed: linesProcessed?.toLocaleString() || '0',
    elapsed: elapsed?.toFixed(1) || '0'
  });
};

// Recovery Message Formatters (for recovery.ts)
// ============================================================================

/**
 * Formats the log processing recovery message.
 * @param mbProcessed - Megabytes processed so far
 * @param mbTotal - Total megabytes to process
 * @returns Formatted message string
 */
export const formatLogProcessingRecoveryMessage = (
  mbProcessed?: number,
  mbTotal?: number
): string => {
  return i18n.t('signalr.logProcessing.progress', {
    mbProcessed: mbProcessed?.toFixed(1) || '0',
    mbTotal: mbTotal?.toFixed(1) || '0'
  });
};

/**
 * Formats the log processing recovery detail message.
 * The total line count is no longer known mid-run (the Rust processor's
 * line-counting pre-pass was removed), so only entries are shown.
 * @param entriesProcessed - Entries processed so far
 * @returns Formatted detail message string
 */
export const formatLogProcessingRecoveryDetailMessage = (entriesProcessed?: number): string => {
  return i18n.t('signalr.logProcessing.recoveryDetail', {
    entriesProcessed: entriesProcessed?.toLocaleString() || '0'
  });
};

// ============================================================================
// Log Removal
// ============================================================================

/**
 * Formats the message for log removal progress.
 * Shows service name and optionally lines removed count.
 * @param event - The log removal progress event from SignalR
 * @returns Formatted message string
 */
export const formatLogRemovalProgressMessage = (event: LogRemovalProgressEvent): string => {
  const linesRemoved = event.linesRemoved || 0;
  if (linesRemoved > 0) {
    return i18n.t('signalr.logRemoval.progressWithCount', {
      service: event.service,
      linesRemoved: linesRemoved.toLocaleString()
    });
  }
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.logRemoval.processingDatasource', {
        service: event.service,
        datasourceName: event.datasource ?? ''
      });
};

/**
 * Formats the success message for log removal completion.
 * @param event - The log removal complete event from SignalR
 * @returns Formatted success message string
 */
export const formatLogRemovalCompleteMessage = (event: LogRemovalCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t(GENERIC_COMPLETION_I18N_KEY);
};

// ============================================================================
// Game Removal
// ============================================================================

/**
 * Formats the message for game removal progress.
 * @param event - The game removal progress event from SignalR
 * @returns Formatted message string
 */
export const formatGameRemovalProgressMessage = (event: GameRemovalProgressEvent): string => {
  const kind = classifyRemovalKind(event);
  const fallbackContext: Record<string, string | number | boolean> = withRemovalIdentity(
    { gameName: event.gameName },
    kind,
    event.gameAppId,
    event.epicAppId
  );
  const fallbackStageKey = removalStageKey(kind, 'starting');

  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? fallbackContext)
    : i18n.t(fallbackStageKey, fallbackContext);
};

// ============================================================================
// Service Removal
// ============================================================================

/**
 * Formats the message for service removal progress.
 * @param event - The service removal progress event from SignalR
 * @returns Formatted message string
 */
export const formatServiceRemovalProgressMessage = (event: ServiceRemovalProgressEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.serviceRemove.starting.default', { service: event.serviceName });
};

// ============================================================================
// Corruption Removal
// ============================================================================

/**
 * Formats the message for corruption removal started.
 * @param event - The corruption removal started event from SignalR
 * @returns Formatted message string
 */
export const formatCorruptionRemovalStartedMessage = (
  event: CorruptionRemovalStartedEvent
): string => {
  return event.stageKey && event.stageKey !== 'signalr.corruptionRemove.starting'
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t(
        event.detectionMethod === 'structural'
          ? 'signalr.corruptionRemove.startingStructural'
          : 'signalr.corruptionRemove.starting',
        { service: event.service }
      );
};

/**
 * Formats the success message for corruption removal completion.
 * @param event - The corruption removal complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCorruptionRemovalCompleteMessage = (
  event: CorruptionRemovalCompleteEvent
): string => {
  return event.stageKey && event.stageKey !== 'signalr.corruptionRemove.success'
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t(
        event.detectionMethod === 'structural'
          ? 'signalr.corruptionRemove.successStructural'
          : 'signalr.corruptionRemove.success',
        { service: event.service }
      );
};

// ============================================================================
// Game Detection
// ============================================================================

/**
 * Formats the message for game detection started.
 * @param event - The game detection started event from SignalR
 * @returns Formatted message string
 */
export const formatGameDetectionStartedMessage = (event: GameDetectionStartedEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.gameDetect.starting.default');
};

/**
 * Formats the progress message for game detection.
 * @param event - The game detection progress event from SignalR
 * @returns Formatted progress message string
 */
export const formatGameDetectionProgressMessage = (event: GameDetectionProgressEvent): string => {
  return event.stageKey
    ? i18n.t(
        event.stageKey,
        buildGameDetectionInterpolation(event.context, {
          totalGamesDetected: event.gamesDetected
        })
      )
    : i18n.t('signalr.gameDetect.scan.inProgress');
};

/**
 * Formats the success message for game detection completion.
 * @param event - The game detection complete event from SignalR
 * @returns Formatted success message string
 */
export const formatGameDetectionCompleteMessage = (event: GameDetectionCompleteEvent): string => {
  const interpolation = buildGameDetectionInterpolation(event.context, {
    totalGamesDetected: event.totalGamesDetected,
    newGamesCount: event.newGamesCount
  });

  // A declined run reports success:true and reaches this success formatter, so the counts below
  // would report a scan that never looked at anything. Its reason travels in `error`.
  if (event.status === 'skipped') {
    return (
      event.error ??
      (event.stageKey ? i18n.t(event.stageKey, interpolation) : i18n.t(GENERIC_SKIPPED_I18N_KEY))
    );
  }

  return event.stageKey
    ? i18n.t(event.stageKey, interpolation)
    : i18n.t('signalr.gameDetect.complete.default', {
        totalGamesDetected: event.totalGamesDetected ?? 0
      });
};

/**
 * Formats the failure message for game detection.
 * @param event - The game detection complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatGameDetectionFailureMessage = (event: GameDetectionCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(
        event.stageKey,
        buildGameDetectionInterpolation(event.context, {
          totalGamesDetected: event.totalGamesDetected,
          newGamesCount: event.newGamesCount
        })
      )
    : i18n.t(GENERIC_FAILURE_I18N_KEY);
};

// ============================================================================
// Corruption Detection
// ============================================================================

/**
 * Formats the message for corruption detection started.
 * @param event - The corruption detection started event from SignalR
 * @returns Formatted message string
 */
export const formatCorruptionDetectionStartedMessage = (
  event: CorruptionDetectionStartedEvent
): string => {
  return event.stageKey &&
    event.stageKey !== 'signalr.corruptionDetect.starting' &&
    event.stageKey !== 'signalr.corruptionDetect.startingStructural'
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t(
        event.detectionMethod !== 'structural'
          ? 'signalr.corruptionDetect.startingRepeatedMiss'
          : structuralStartingKey(event.scanMode)
      );
};

/**
 * Formats the message for corruption detection progress.
 * @param event - The corruption detection progress event from SignalR
 * @returns Formatted message string
 */
export const formatCorruptionDetectionProgressMessage = (
  event: CorruptionDetectionProgressEvent
): string => formatCorruptionProgress(event).message;

/**
 * Formats the success message for corruption detection completion.
 * @param event - The corruption detection complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCorruptionDetectionCompleteMessage = (
  event: CorruptionDetectionCompleteEvent
): string => {
  const count = event.totalCorruptedChunks ?? 0;
  const context = {
    ...(event.context ?? {}),
    count,
    totalCorruptedChunks: count,
    repeatedMissCount: event.detectionCounts?.repeated_miss ?? 0,
    structuralCount: event.detectionCounts?.structural ?? 0
  };
  return event.stageKey && event.stageKey !== 'signalr.corruptionDetect.complete'
    ? i18n.t(event.stageKey, context)
    : i18n.t(
        event.detectionMethod !== 'structural'
          ? 'signalr.corruptionDetect.completeRepeatedMiss'
          : event.scanMode === 'incremental' &&
              (event.effectiveScanMode === 'baseline' ||
                event.context?.effectiveScanMode === 'baseline')
            ? 'signalr.corruptionDetect.completeStructuralBaseline'
            : event.scanMode === 'incremental'
              ? 'signalr.corruptionDetect.completeStructuralIncremental'
              : event.scanMode === 'full'
                ? 'signalr.corruptionDetect.completeStructuralFull'
                : 'signalr.corruptionDetect.completeStructural',
        context
      );
};

/**
 * Formats the failure message for corruption detection.
 * @param event - The corruption detection complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatCorruptionDetectionFailureMessage = (
  event: CorruptionDetectionCompleteEvent
): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.corruptionDetect.failed', { errorDetail: event.error ?? '' });
};

// ============================================================================
// Database Reset
// ============================================================================

/**
 * Formats the message for database reset progress.
 * @param event - The database reset progress event from SignalR
 * @returns Formatted message string
 */
export const formatDatabaseResetProgressMessage = (event: DatabaseResetProgressEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.dbReset.starting');
};

/**
 * Formats the completion message for database reset.
 * @param event - The database reset progress event from SignalR
 * @returns Formatted completion message string
 */
export const formatDatabaseResetCompleteMessage = (
  event: Pick<DatabaseResetProgressEvent, 'stageKey' | 'context'>
): string => {
  // The reset names the prefill daemons whose login outlived it. Without this the card
  // reports success while those services are still signed in. The value arrives as a JSON
  // array of platform names, which the declared context type does not cover.
  const context: Record<string, unknown> = event.context ?? {};
  const activeLogins = context.persistentLoginFailures;
  if (Array.isArray(activeLogins) && activeLogins.length > 0) {
    return i18n.t('signalr.dbReset.completeWithActiveLogins', {
      services: activeLogins.join(', ')
    });
  }

  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.dbReset.complete');
};

// ============================================================================
// Cache Clearing
// ============================================================================

/**
 * Formats the message for cache clear progress.
 * @param event - The cache clear progress event from SignalR
 * @returns Formatted message string
 */
export const formatCacheClearProgressMessage = (event: CacheClearProgressEvent): string => {
  const base = event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : (event.statusMessage ?? i18n.t('signalr.cacheClear.starting'));

  if (event.directoriesProcessed !== undefined && event.totalDirectories) {
    return i18n.t('signalr.cacheClear.progressDirectories', {
      base,
      processed: event.directoriesProcessed,
      total: event.totalDirectories
    });
  }

  return base;
};

/**
 * Formats the success message for cache clear completion.
 * @param event - The cache clear complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCacheClearCompleteMessage = (event: CacheClearCompleteEvent): string => {
  // The backend sends NO stageKey for this terminal - only `message`, which carries the real result
  // ("Successfully cleared 32 cache directories across 2 datasources..."). Falling straight through
  // to the generic string would throw that away, and the directory/datasource counts appear nowhere
  // else on the card. `message` is deprecated in favour of stageKey, so it is used only as the
  // fallback: the day the backend sends a stageKey, the translated text wins automatically.
  if (event.stageKey) {
    return i18n.t(event.stageKey, event.context ?? {});
  }
  return event.message || i18n.t(GENERIC_COMPLETION_I18N_KEY);
};

/**
 * Formats the failure message for cache clear.
 * @param event - The cache clear complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatCacheClearFailureMessage = (event: CacheClearCompleteEvent): string => {
  return (
    event.error ??
    (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
    i18n.t(GENERIC_FAILURE_I18N_KEY)
  );
};

// ============================================================================
// Data Import
// ============================================================================

/**
 * Formats the message for data import started.
 * @param event - The data import started event from SignalR
 * @returns Formatted message string
 */
export const formatDataImportStartedMessage = (event: DataImportStartedEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.generic.unknown');
};

/**
 * Formats the progress message for data import.
 * Shows records processed of total records.
 * @param event - The data import progress event from SignalR
 * @returns Formatted progress message string
 */
export const formatDataImportProgressMessage = (event: DataImportProgressEvent): string => {
  return i18n.t('signalr.dataImport.progress', {
    processed: event.recordsProcessed?.toLocaleString() || '0',
    total: event.totalRecords?.toLocaleString() || '0'
  });
};

/**
 * Formats the success message for data import completion.
 * @param event - The data import complete event from SignalR
 * @returns Formatted success message string
 */
/**
 * Result breakdown for a finished data import.
 *
 * The counts ride on the completion event and are stored into details, but NOTHING rendered them:
 * the card showed only the summary line, which carries imported/skipped and omits the ERROR count
 * entirely. A silent error count on an import is exactly the thing a user needs to see.
 */
export const formatDataImportCompleteDetailMessage = (
  event: DataImportCompleteEvent
): string | undefined => {
  const { recordsImported, recordsSkipped, recordsErrors } = event;

  if (
    recordsImported === undefined &&
    recordsSkipped === undefined &&
    recordsErrors === undefined
  ) {
    return undefined;
  }

  return i18n.t('signalr.dataImport.completedDetail', {
    imported: formatCount(recordsImported ?? 0),
    skipped: formatCount(recordsSkipped ?? 0),
    errors: formatCount(recordsErrors ?? 0)
  });
};

export const formatDataImportCompleteMessage = (event: DataImportCompleteEvent): string => {
  // Same shape as cache clear: no stageKey on this terminal, and `message` is the only place the
  // imported/skipped/error breakdown is visible (the counts land in details, which no renderer
  // shows for this type). Prefer a translated stageKey the moment the backend supplies one.
  if (event.stageKey) {
    return i18n.t(event.stageKey, event.context ?? {});
  }
  return event.message || i18n.t(GENERIC_COMPLETION_I18N_KEY);
};

/**
 * Formats the failure message for data import.
 * @param event - The data import complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatDataImportFailureMessage = (event: DataImportCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t(GENERIC_FAILURE_I18N_KEY);
};

// ============================================================================
// Mapping data updates
// ============================================================================

/**
 * Formats the detail message for Epic game mappings updated.
 * Shows new/updated game counts and total.
 * @param event - The Epic game mappings updated event from SignalR
 * @returns Formatted detail message string
 */
export const formatEpicGameMappingsUpdatedMessage = (
  event: EpicGameMappingsUpdatedEvent
): string => {
  const parts: string[] = [];
  if (event.newGames > 0) {
    parts.push(i18n.t('notifications.epicGameMappingsUpdated.newGames', { count: event.newGames }));
  }
  if (event.updatedGames > 0) {
    parts.push(
      i18n.t('notifications.epicGameMappingsUpdated.updatedGames', { count: event.updatedGames })
    );
  }
  const detail = parts.join(', ');
  return detail
    ? i18n.t('notifications.epicGameMappingsUpdated.totalWithDetail', {
        detail,
        count: event.totalGames
      })
    : i18n.t('notifications.epicGameMappingsUpdated.total', { count: event.totalGames });
};

/**
 * Formats the detail message for Xbox game mappings updated.
 * Shows newly discovered game + CDN-pattern counts (the backend payload's newMappings/newPatterns).
 * @param event - The Xbox game mappings updated event from SignalR
 * @returns Formatted detail message string
 */
export const formatXboxGameMappingsUpdatedMessage = (
  event: XboxGameMappingsUpdatedEvent
): string => {
  const parts: string[] = [];
  if (event.newMappings !== undefined && event.newMappings > 0) {
    parts.push(
      i18n.t('notifications.xboxGameMappingsUpdated.newGames', { count: event.newMappings })
    );
  }
  if (event.newPatterns !== undefined && event.newPatterns > 0) {
    parts.push(
      i18n.t('notifications.xboxGameMappingsUpdated.newPatterns', { count: event.newPatterns })
    );
  }
  return parts.join(', ');
};

// ============================================================================
// Scheduled Prefill
// ============================================================================

/**
 * Byte readout for the game a scheduled prefill is currently downloading.
 *
 * This is what actually shows the run is alive. The bar tracks the RUN percent, which divides the
 * active game's fraction by the number of games in the batch, so on a multi-game run it can crawl
 * for minutes on a large download and read as frozen. These bytes advance on every pushed tick of
 * a live download.
 */
export const formatScheduledPrefillDetailMessage = (
  event: ScheduledPrefillProgressEvent
): string | undefined => {
  const downloaded = event.bytesDownloaded;
  const total = event.totalBytes;

  // BOTH values are required. Only a live downloading tick carries them as a pair; the
  // service-completed tick reports the whole SERVICE's byte total in bytesDownloaded with no total,
  // and rendering that bare would read as the current game's progress when it is nothing of the
  // sort. No pair, no line.
  if (
    typeof downloaded !== 'number' ||
    downloaded <= 0 ||
    typeof total !== 'number' ||
    total <= 0
  ) {
    return undefined;
  }

  return i18n.t('management.schedules.services.scheduledPrefill.events.downloadedOfTotal', {
    downloaded: formatBytes(downloaded),
    total: formatBytes(total)
  });
};
