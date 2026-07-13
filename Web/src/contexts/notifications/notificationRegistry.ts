/**
 * Declarative notification registry.
 * Each entry describes the full lifecycle (started -> progress -> complete) of a
 * notification type, including the SignalR event names, handler configurations,
 * cancel wiring (cancelKind + tooltip), and recovery wiring.
 *
 * `wiring` splits entries into two families:
 *   - 'standard': the {@link useNotificationHandlers} loop subscribes
 *     started/progress/complete handlers from this entry.
 *   - 'special': metadata-only (cancelKind + recovery). SignalR handlers are
 *     hand-built in createSpecialCaseHandlers and wired via
 *     SPECIAL_NOTIFICATION_CONTRACTS. The standard loop skips these.
 *
 * The four special-wiring entries (depot_mapping, database_reset,
 * epic_game_mapping, steam_session_error) appear here ONLY so cancel + recovery
 * live in one config surface per type. Their handler bodies are NOT inlined:
 *   - depot_mapping: special completion handler with animation/cancellation logic
 *   - database_reset: terminal DatabaseResetComplete handled via createCompletionHandler,
 *     idempotent with the legacy progress-status completion
 *   - epic_game_mapping: progress-only with custom EpicGameMappingsUpdated handler
 *   - steam_session_error: custom one-shot error toast (not a lifecycle, no recovery)
 */

import type {
  NotificationRegistryEntry,
  NotificationType,
  SimpleRecoveryConfig,
  StageContext
} from './types';
import type { OperationStatus } from '@/types/operations';
import type { CorruptionDetectionMethod } from '@/types';
import type {
  StructuralBaselineStatus,
  StructuralEffectiveScanMode,
  StructuralScanMode,
  StructuralScanSummary
} from '@/types/corruptionScan';
import {
  ACTIVE_PROGRESS_PERCENT_CAP,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  NOTIFICATION_IDS,
  NOTIFICATION_STORAGE_KEYS,
  REMOVING_GAME_I18N_KEY,
  SCHEDULED_PREFILL_LEGACY_GENERIC_NOTIFICATION_ID
} from './constants';
import i18n from '@/i18n';
import {
  formatScheduledPrefillDetailMessage,
  formatDataImportCompleteDetailMessage,
  formatLogProcessingMessage,
  formatLogProcessingCompletionMessage,
  formatLogProcessingDetailMessage,
  formatLogProcessingRecoveryMessage,
  formatLogProcessingRecoveryDetailMessage,
  formatDepotMappingRecoveryDetailMessage,
  formatLogRemovalProgressMessage,
  formatLogRemovalCompleteMessage,
  formatGameRemovalProgressMessage,
  formatServiceRemovalProgressMessage,
  formatCorruptionRemovalStartedMessage,
  formatCorruptionRemovalCompleteMessage,
  formatGameDetectionStartedMessage,
  formatGameDetectionProgressMessage,
  formatGameDetectionCompleteMessage,
  formatGameDetectionFailureMessage,
  buildGameDetectionInterpolation,
  formatCorruptionDetectionStartedMessage,
  formatCorruptionDetectionProgressMessage,
  formatCorruptionProgress,
  formatCorruptionDetectionCompleteMessage,
  formatCorruptionDetectionFailureMessage,
  formatCacheClearProgressMessage,
  formatCacheClearCompleteMessage,
  formatCacheClearFailureMessage,
  formatDataImportStartedMessage,
  formatDataImportProgressMessage,
  formatDataImportCompleteMessage,
  formatDataImportFailureMessage
} from './detailMessageFormatters';
import { translateRecoveryStage, translateStageKeyMessage } from '@utils/stageKeyMessage';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { classifyRemovalKind, removalStageKey } from './removalKind';
import { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } from '@components/features/management/schedules/scheduled-prefill/constants';

import type {
  LogProcessingStartedEvent,
  ProcessingProgressEvent,
  LogProcessingCompleteEvent,
  LogRemovalStartedEvent,
  LogRemovalProgressEvent,
  LogRemovalCompleteEvent,
  GameRemovalStartedEvent,
  GameRemovalProgressEvent,
  GameRemovalCompleteEvent,
  ServiceRemovalStartedEvent,
  ServiceRemovalProgressEvent,
  ServiceRemovalCompleteEvent,
  CorruptionRemovalStartedEvent,
  CorruptionRemovalProgressEvent,
  CorruptionRemovalCompleteEvent,
  GameDetectionStartedEvent,
  GameDetectionProgressEvent,
  GameDetectionCompleteEvent,
  CorruptionDetectionStartedEvent,
  CorruptionDetectionProgressEvent,
  CorruptionDetectionCompleteEvent,
  CacheClearingStartedEvent,
  CacheClearProgressEvent,
  CacheClearCompleteEvent,
  DataImportStartedEvent,
  DataImportProgressEvent,
  DataImportCompleteEvent,
  EvictionScanStartedEvent,
  CacheSizeScanStartedEvent,
  CacheSizeScanProgressEvent,
  CacheSizeScanCompleteEvent,
  EvictionScanProgressEvent,
  EvictionScanCompleteEvent,
  EvictionRemovalStartedEvent,
  EvictionRemovalProgressEvent,
  EvictionRemovalCompleteEvent,
  ScheduledPrefillStartedEvent,
  ScheduledPrefillProgressEvent,
  ScheduledPrefillCompletedEvent
} from '../SignalRContext/types';

/**
 * Standard three-status-pattern helper for getStatus: maps 'completed' -> 'completed',
 * 'failed'|'cancelled' -> 'failed', everything else -> undefined.
 */
function standardGetStatus(event: { status?: string }): string | undefined {
  if (event.status === 'completed') return 'completed';
  if (event.status === 'failed' || event.status === 'cancelled') return 'failed';
  return undefined;
}

/**
 * Prefixes a translated corruption-removal progress message with the display
 * service name so the shared notification card always shows which service is
 * being worked. During "Remove All" the per-service position is appended when
 * the context carries both serviceIndex and serviceCount, e.g. `Steam (2/5): …`.
 * The prefix is skipped for the aggregate 'all' service and when no service is
 * present. Display-only: the raw service tag is unchanged everywhere else.
 */
function prefixCorruptionRemovalService(
  message: string,
  service: string | undefined,
  context: Record<string, string | number | boolean> | undefined
): string {
  if (!service || service === 'all') return message;
  const label = getServiceDisplayName(service);
  const index = context?.serviceIndex;
  const count = context?.serviceCount;
  if (index !== undefined && count !== undefined) {
    return `${label} (${index}/${count}): ${message}`;
  }
  return `${label}: ${message}`;
}

// ============================================================================
// Per-endpoint Recovery Response DTOs
// ============================================================================
// Each interface mirrors the C# controller response shape. Nullability follows
// the backend C# DTOs. The simple-recovery `createNotification` readers below
// access these REST property names directly (snake_case/camelCase as the wire
// delivers them) and must NOT be normalized against the SignalR event shapes.

/** GET /api/logs/process/status - RustLogProcessorService.GetStatus() */
interface LogProcessingStatusResponse {
  isProcessing: boolean;
  silentMode: boolean;
  percentComplete: number;
  mbProcessed: number;
  mbTotal: number;
  entriesProcessed: number;
  /** Final line count; 0 while running (the Rust line-count pre-pass was removed). */
  totalLines: number;
  stageKey?: string;
  context?: StageContext;
  /** camelCase — backend anonymous object → JsonNamingPolicy.CamelCase */
  operationId?: string;
}

/** GET /api/cache/operations - ActiveOperationsResponse */
interface CacheOperationProgressItem {
  operationId?: string;
  id?: string;
  statusMessage?: string;
  stageKey?: string;
  context?: StageContext;
  percentComplete: number;
  filesDeleted: number;
  directoriesProcessed: number;
  bytesDeleted: number;
}

interface CacheOperationsResponse {
  isProcessing: boolean;
  operations?: CacheOperationProgressItem[];
}

/** GET /api/database/reset-status - DatabaseResetStatusResponse */
interface DatabaseResetStatusResponse {
  isProcessing: boolean;
  /** Canonical OperationStatus or null (null replaces the legacy `"idle"` sentinel). */
  status?: OperationStatus | null;
  message?: string | null;
  /** C# `double?` - genuinely nullable */
  percentComplete?: number | null;
  stageKey?: string;
  context?: StageContext;
  operationId?: string | null;
  tablesCleared?: number | null;
  totalTables?: number | null;
  filesDeleted?: number | null;
}

/** GET /api/depots/rebuild/progress - SteamPicsProgress */
interface DepotRebuildProgressResponse {
  isProcessing: boolean;
  statusMessage: string;
  progressPercent: number;
  processedBatches?: number;
  totalBatches?: number;
  depotMappingsFound?: number;
  totalMappings: number;
  processedMappings: number;
  isLoggedOn: boolean;
  operationId?: string;
}

/** GET /api/logs/remove/status - RustServiceRemovalService.GetLogRemovalStatus() */
interface LogRemovalStatusResponse {
  isProcessing: boolean;
  service?: string | null;
  datasource?: string | null;
  operationId?: string | null;
  filesProcessed: number;
  percentComplete?: number | null;
  linesProcessed: number;
  linesRemoved: number;
  status?: OperationStatus | null;
  stageKey?: string;
  context?: StageContext;
}

/** GET /api/system/schedules/scheduledPrefill/run-status - ScheduledPrefillRunStatusDto */
interface ScheduledPrefillRunStatusResponse {
  isRunning: boolean;
  operationId?: string | null;
  showNotification?: boolean;
}

/** GET /api/games/detect/active - ActiveDetectionResponse */
interface GameDetectionOperationInfo {
  operationId?: string;
  statusMessage: string;
  percentComplete: number;
  scanType?: 'full' | 'incremental';
  totalGamesDetected?: number;
  context?: StageContext;
}

interface GameDetectionStatusResponse {
  isProcessing: boolean;
  operation: GameDetectionOperationInfo | null;
}

/**
 * GET /api/cache/corruption/detect/status - CacheController.GetCorruptionDetectionStatus()
 * Returns anonymous `{ isRunning: false }` when idle, or the full object below when active.
 * Active responses carry method-aware stage/context/progress; idle responses contain only
 * `isRunning: false`. The recovery handler keeps `?? 0` for that idle/legacy boundary.
 */
interface CorruptionDetectionStatusResponse {
  isRunning: boolean;
  operationId?: string | null;
  detectionMethod?: CorruptionDetectionMethod;
  scanMode?: StructuralScanMode;
  effectiveScanMode?: StructuralEffectiveScanMode;
  baselineStatus?: StructuralBaselineStatus;
  resumed?: boolean;
  status?: OperationStatus | null;
  message?: string | null;
  startTime?: string | null;
  stageKey?: string;
  context?: StageContext;
  percentComplete?: number;
  scanSummary?: StructuralScanSummary;
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

const corruptionNotificationDetails = (source: CorruptionNotificationSource) => ({
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

/** GET /api/migration/import/status - DataImportStatusResponse */
interface DataImportStatusResponse {
  isProcessing: boolean;
  status?: OperationStatus | null;
  message?: string | null;
  /** C# `double?` - genuinely nullable */
  percentComplete?: number | null;
  operationId?: string | null;
  stageKey?: string;
  context?: StageContext;
}

/**
 * GET /api/epic/game-mappings/schedule - EpicGameMappingController.GetScheduleStatus()
 * Returns EpicScheduleStatus from EpicMappingService.
 */
interface EpicGameMappingScheduleResponse {
  /** Always present */
  isProcessing: boolean;
  /** C# `string?` - only set when IsProcessing is true; null/absent when idle */
  statusMessage?: string | null;
  /** C# `double` (non-null) - always emitted; 0 when not processing */
  progressPercent: number;
  /**
   * C# `string?` - non-null when isProcessing is true; absent/undefined when idle.
   * Narrowed to `string | undefined` (not `| null`) to match the backend contract
   * and prevent null from slipping into details.operationId.
   */
  operationId?: string;
  /** Additional fields from EpicScheduleStatus (not used by recovery handler) */
  refreshIntervalHours?: number;
  nextRefreshIn?: number;
  lastRefreshTime?: string | null;
  isAuthenticated?: boolean;
  status?: string;
}

/** GET /api/stats/eviction/scan/status - anonymous object from StatsController */
interface EvictionScanStatusResponse {
  isProcessing: boolean;
  silentMode: boolean;
  status: string;
  percentComplete: number;
  message: string;
  operationId: string | null;
  stageKey?: string;
  context?: StageContext;
}

interface CacheSizeScanStatusResponse {
  isProcessing: boolean;
  status: string;
  percentComplete: number;
  message: string;
  operationId: string | null;
  stageKey?: string;
  context?: StageContext;
}

// ============================================================================
// Cancel tooltip keys (single source — UniversalNotificationBar derives from this)
// ============================================================================

const CANCEL_TOOLTIP = {
  logProcessing: 'common.notifications.cancelLogProcessing',
  logRemoval: 'common.notifications.cancelLogRemoval',
  gameRemoval: 'common.notifications.cancelGameRemoval',
  serviceRemoval: 'common.notifications.cancelServiceRemoval',
  corruptionRemoval: 'common.notifications.cancelCorruptionRemoval',
  gameDetection: 'common.notifications.cancelGameDetection',
  corruptionDetection: 'common.notifications.cancelCorruptionDetection',
  cacheClearing: 'common.notifications.cancelCacheClearing',
  dataImport: 'common.notifications.cancelDataImport',
  evictionScan: 'common.notifications.cancelEvictionScan',
  cacheSizeScan: 'common.notifications.cancelCacheSizeScan',
  scheduledPrefill: 'common.notifications.cancelScheduledPrefill',
  evictionRemoval: 'common.notifications.cancelEvictionRemoval',
  depotMapping: 'common.notifications.cancelDepotMapping',
  databaseReset: 'common.notifications.cancelDatabaseReset',
  epicGameMapping: 'common.notifications.cancelEpicGameMapping',
  xboxGameMapping: 'common.notifications.cancelXboxGameMapping',
  bulkRemoval: 'common.notifications.cancelBulkRemoval'
} as const;

export const NOTIFICATION_REGISTRY: NotificationRegistryEntry[] = [
  // ========== Log Processing ==========
  {
    type: 'log_processing',
    id: NOTIFICATION_IDS.LOG_PROCESSING,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.logProcessing,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/logs/process/status',
      isProcessing: (data: LogProcessingStatusResponse) => data.isProcessing && !data.silentMode,
      shouldSkip: (data: LogProcessingStatusResponse) => data.isProcessing && data.silentMode,
      createNotification: (data: LogProcessingStatusResponse) => ({
        message: formatLogProcessingRecoveryMessage(data.mbProcessed, data.mbTotal),
        detailMessage: formatLogProcessingRecoveryDetailMessage(data.entriesProcessed),
        progress: Math.min(ACTIVE_PROGRESS_PERCENT_CAP, data.percentComplete),
        details: {
          operationId: data.operationId,
          mbProcessed: data.mbProcessed,
          mbTotal: data.mbTotal,
          entriesProcessed: data.entriesProcessed
        }
      }),
      staleMessage: 'Log processing completed'
    } satisfies SimpleRecoveryConfig<LogProcessingStatusResponse>,
    events: {
      started: 'LogProcessingStarted',
      progress: 'LogProcessingProgress',
      complete: 'LogProcessingComplete'
    },
    started: {
      defaultMessage: 'Starting log processing...',
      getMessage: (event: LogProcessingStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.logProcessing.starting', event.context ?? {}),
      getDetails: (event: LogProcessingStartedEvent) => ({ operationId: event.operationId })
    },
    progress: {
      getMessage: (event: ProcessingProgressEvent) => formatLogProcessingMessage(event),
      getProgress: (event: ProcessingProgressEvent) =>
        Math.min(ACTIVE_PROGRESS_PERCENT_CAP, event.percentComplete),
      getStatus: (event: ProcessingProgressEvent) =>
        event.status?.toLowerCase() === 'completed' ? 'completed' : undefined,
      getCompletedMessage: (event: ProcessingProgressEvent) =>
        formatLogProcessingCompletionMessage(event.entriesSaved),
      getDetails: (event: ProcessingProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      // Translated, not a hardcoded English literal: this now actually reaches the card (the
      // completion handler never applied getSuccessMessage to an existing card before), so a
      // literal here would switch a localized card to English at the moment it finishes.
      getSuccessMessage: (event: LogProcessingCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.logProcessing.complete', event.context ?? {}),
      getDetailMessage: (event: LogProcessingCompleteEvent) =>
        formatLogProcessingDetailMessage(
          event.entriesProcessed,
          event.linesProcessed,
          event.elapsed
        )
    }
  },

  // ========== Log Removal ==========
  {
    type: 'log_removal',
    id: NOTIFICATION_IDS.LOG_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.logRemoval,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          {
            stageKey: 'signalr.logRemoval.starting.default',
            context: { service: 'steam' }
          },
          {
            stageKey: 'signalr.logRemoval.starting.multi',
            context: { service: 'steam', datasourceCount: 2 }
          },
          {
            stageKey: 'signalr.logRemoval.starting.single',
            context: { service: 'steam', datasourceName: 'access.log' }
          },
          {
            stageKey: 'signalr.logRemoval.processingDatasource',
            context: { service: 'steam', datasourceName: 'access.log' }
          },
          {
            stageKey: 'signalr.logRemoval.progressWithCount',
            context: { service: 'steam', linesRemoved: 12 }
          },
          { stageKey: 'signalr.logRemoval.removing', context: { service: 'steam' } },
          { stageKey: 'signalr.logRemoval.cleaningDatabase', context: { service: 'steam' } }
        ]
      },
      apiEndpoint: '/api/logs/remove/status',
      isProcessing: (data: LogRemovalStatusResponse) => data.isProcessing && Boolean(data.service),
      createNotification: (data: LogRemovalStatusResponse) => ({
        message: translateRecoveryStage(
          data.stageKey,
          {
            ...(data.context ?? {}),
            ...(data.service != null && { service: data.service }),
            ...(data.datasource != null && { datasourceName: data.datasource })
          },
          'signalr.logRemoval.recovering'
        ),
        progress: data.percentComplete ?? 0,
        details: {
          service: data.service ?? undefined,
          operationId: data.operationId ?? undefined,
          filesProcessed: data.filesProcessed,
          linesProcessed: data.linesProcessed,
          linesRemoved: data.linesRemoved
        }
      }),
      staleMessage: 'Log entry removal completed'
    } satisfies SimpleRecoveryConfig<LogRemovalStatusResponse>,
    events: {
      started: 'LogRemovalStarted',
      progress: 'LogRemovalProgress',
      complete: 'LogRemovalComplete'
    },
    started: {
      defaultMessage: 'Starting log removal...',
      getMessage: (event: LogRemovalStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.logRemoval.starting.default', event.context ?? {}),
      getDetails: (event: LogRemovalStartedEvent) => ({ operationId: event.operationId })
    },
    progress: {
      getMessage: (event: LogRemovalProgressEvent) => formatLogRemovalProgressMessage(event),
      getProgress: (event: LogRemovalProgressEvent) => event.percentComplete,
      getStatus: (event: LogRemovalProgressEvent) =>
        event.status === 'completed'
          ? 'completed'
          : event.status === 'failed'
            ? 'failed'
            : undefined,
      getCompletedMessage: (event: LogRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_COMPLETION_I18N_KEY, event.context ?? {}),
      getErrorMessage: (event: LogRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_FAILURE_I18N_KEY, event.context ?? {}),
      getDetails: (event: LogRemovalProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: LogRemovalCompleteEvent) => formatLogRemovalCompleteMessage(event),
      getSuccessDetails: (event: LogRemovalCompleteEvent, existing) => ({
        ...existing?.details,
        linesProcessed: event.linesProcessed
      }),
      useAnimationDelay: true
    }
  },

  // ========== Game Removal ==========
  {
    type: 'game_removal',
    id: NOTIFICATION_IDS.GAME_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.gameRemoval,
    // Recovered by the shared /api/cache/removals/active batch fetch (one GET
    // covering game/service/corruption/eviction removal) — NOT a simple config.
    recovery: { kind: 'cacheRemovalsBatch' },
    events: {
      started: 'GameRemovalStarted',
      progress: 'GameRemovalProgress',
      complete: 'GameRemovalComplete'
    },
    started: {
      defaultMessage: 'Starting game removal...',
      // Post-Phase-2 contract: GameRemovalStartedEvent carries a required i18n stageKey
      // (replaced free-text `message`) and scope-aware identity (`gameAppId` for Steam,
      // `epicAppId` for Epic - exactly one is non-null). Mirrors the eviction_removal
      // scope-aware pattern below.
      getMessage: (event: GameRemovalStartedEvent) =>
        i18n.t(event.stageKey, event.context ?? { gameName: event.gameName }),
      getDetails: (event: GameRemovalStartedEvent) => {
        const base = {
          operationId: event.operationId,
          gameName: event.gameName,
          stageKey: event.stageKey,
          cancelling: false
        };
        if (event.gameAppId !== null) {
          return { ...base, gameAppId: event.gameAppId };
        }
        if (event.epicAppId !== null) {
          return { ...base, epicAppId: event.epicAppId };
        }
        return base;
      }
    },
    progress: {
      getMessage: (event: GameRemovalProgressEvent) => formatGameRemovalProgressMessage(event),
      getProgress: (event: GameRemovalProgressEvent) => event.percentComplete,
      // GameRemovalProgress has no `status` field (dropped with the phase-label cleanup -
      // it never carried OperationStatus values anyway). Lifecycle transitions arrive via
      // the separate GameRemovalComplete event, so progress stays in `running` until then.
      getStatus: () => undefined,
      getCompletedMessage: (event: GameRemovalProgressEvent) =>
        i18n.t(
          event.stageKey ?? removalStageKey(classifyRemovalKind(event), 'complete'),
          event.context ?? {}
        ),
      getErrorMessage: (event: GameRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.gameRemove.error.fatal', event.context ?? {}),
      getDetails: (event: GameRemovalProgressEvent) => ({
        operationId: event.operationId,
        gameName: event.gameName,
        ...(event.gameAppId !== null && { gameAppId: event.gameAppId }),
        ...(event.epicAppId !== null && { epicAppId: event.epicAppId })
      })
    },
    complete: {
      getSuccessDetails: (event: GameRemovalCompleteEvent, existing) => ({
        ...existing?.details,
        // Seed operationId + scope identity from the event so the fast-completion create
        // path (no prior running slot) still produces a cancellable, scope-aware card.
        // gameAppId/epicAppId are scope-exclusive (exactly one non-null).
        operationId: event.operationId,
        ...(event.gameAppId !== null && { gameAppId: event.gameAppId }),
        ...(event.epicAppId !== null && { epicAppId: event.epicAppId }),
        gameName: event.gameName,
        filesDeleted: event.filesDeleted,
        bytesFreed: event.bytesFreed,
        logEntriesRemoved: event.logEntriesRemoved
      })
    }
  },

  // ========== Service Removal ==========
  {
    type: 'service_removal',
    id: NOTIFICATION_IDS.SERVICE_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.serviceRemoval,
    recovery: { kind: 'cacheRemovalsBatch' },
    events: {
      started: 'ServiceRemovalStarted',
      progress: 'ServiceRemovalProgress',
      complete: 'ServiceRemovalComplete'
    },
    started: {
      defaultMessage: 'Starting service removal...',
      getMessage: (event: ServiceRemovalStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.serviceRemove.starting.default', event.context ?? {}),
      getDetails: (event: ServiceRemovalStartedEvent) => ({ operationId: event.operationId })
    },
    progress: {
      getMessage: (event: ServiceRemovalProgressEvent) =>
        formatServiceRemovalProgressMessage(event),
      getProgress: (event: ServiceRemovalProgressEvent) => event.percentComplete,
      // See GameRemovalProgress - no `status` on this event either.
      getStatus: () => undefined,
      getCompletedMessage: (event: ServiceRemovalProgressEvent) =>
        i18n.t(event.stageKey, {
          name: event.serviceName,
          ...event.context
        }),
      getErrorMessage: (event: ServiceRemovalProgressEvent) =>
        i18n.t(event.stageKey, {
          name: event.serviceName,
          ...event.context
        }),
      getDetails: (event: ServiceRemovalProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessDetails: (event: ServiceRemovalCompleteEvent, existing) => ({
        ...existing?.details,
        // Seed operationId + service identity from the event so the fast-completion
        // create path (no prior running slot) still produces a cancellable, scope-aware
        // card. When `existing` is present these are merged after its details (event
        // values win, which is fine - they describe the same completed op).
        operationId: event.operationId,
        service: event.serviceName,
        filesDeleted: event.filesDeleted,
        bytesFreed: event.bytesFreed,
        logEntriesRemoved: event.logEntriesRemoved
      })
    }
  },

  // ========== Corruption Removal ==========
  {
    type: 'corruption_removal',
    id: NOTIFICATION_IDS.CORRUPTION_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.corruptionRemoval,
    recovery: { kind: 'cacheRemovalsBatch' },
    events: {
      started: 'CorruptionRemovalStarted',
      progress: 'CorruptionRemovalProgress',
      complete: 'CorruptionRemovalComplete'
    },
    started: {
      defaultMessage: 'Starting corruption removal...',
      getMessage: (event: CorruptionRemovalStartedEvent) =>
        formatCorruptionRemovalStartedMessage(event),
      getDetails: (event: CorruptionRemovalStartedEvent) => ({
        operationId: event.operationId,
        service: event.service,
        detectionMethod: event.detectionMethod
      })
    },
    progress: {
      getMessage: (event: CorruptionRemovalProgressEvent) =>
        prefixCorruptionRemovalService(
          i18n.t(event.stageKey ?? 'signalr.corruptionRemove.scanningFiles', event.context ?? {}),
          event.service,
          event.context
        ),
      getProgress: (event: CorruptionRemovalProgressEvent) => event.percentComplete,
      getStatus: (event: CorruptionRemovalProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: CorruptionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionRemove.success', event.context ?? {}),
      getErrorMessage: (event: CorruptionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionRemove.failed.generic', event.context ?? {}),
      getDetails: (event: CorruptionRemovalProgressEvent) => ({
        operationId: event.operationId,
        service: event.service,
        detectionMethod: event.detectionMethod
      })
    },
    complete: {
      getSuccessMessage: (event: CorruptionRemovalCompleteEvent) =>
        formatCorruptionRemovalCompleteMessage(event),
      getSuccessDetails: (event: CorruptionRemovalCompleteEvent) => ({
        service: event.service,
        detectionMethod: event.detectionMethod
      }),
      useAnimationDelay: true
    },
    onComplete: (removeNotification) => {
      removeNotification(NOTIFICATION_IDS.CORRUPTION_DETECTION);
      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION);
    }
  },

  // ========== Game Detection ==========
  {
    type: 'game_detection',
    id: NOTIFICATION_IDS.GAME_DETECTION,
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.gameDetection,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/games/detect/active',
      isProcessing: (data: GameDetectionStatusResponse) =>
        data.isProcessing && data.operation !== null,
      createNotification: (data: GameDetectionStatusResponse) => {
        // `isProcessing` guard above ensures `data.operation !== null` here.
        const op = data.operation!;
        return {
          message: translateStageKeyMessage(
            op.statusMessage,
            buildGameDetectionInterpolation(op.context, {
              totalGamesDetected: op.totalGamesDetected
            }),
            'signalr.gameDetect.starting.default'
          ),
          progress: op.percentComplete,
          details: {
            operationId: op.operationId,
            scanType: op.scanType
          }
        };
      },
      staleMessage: 'Game detection completed'
    } satisfies SimpleRecoveryConfig<GameDetectionStatusResponse>,
    events: {
      started: 'GameDetectionStarted',
      progress: 'GameDetectionProgress',
      complete: 'GameDetectionComplete'
    },
    started: {
      defaultMessage: 'Detecting games and services...',
      getMessage: (event: GameDetectionStartedEvent) => formatGameDetectionStartedMessage(event),
      getDetails: (event: GameDetectionStartedEvent) => ({
        operationId: event.operationId,
        scanType: event.scanType
      })
    },
    progress: {
      getMessage: (event: GameDetectionProgressEvent) => formatGameDetectionProgressMessage(event),
      getProgress: (event: GameDetectionProgressEvent) => event.percentComplete,
      getStatus: (event: GameDetectionProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: GameDetectionProgressEvent) =>
        i18n.t(
          event.stageKey ?? 'signalr.gameDetect.complete.default',
          buildGameDetectionInterpolation(event.context, {
            totalGamesDetected: event.gamesDetected
          })
        ),
      getErrorMessage: (event: GameDetectionProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_FAILURE_I18N_KEY, event.context ?? {}),
      getDetails: (event: GameDetectionProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: GameDetectionCompleteEvent) =>
        formatGameDetectionCompleteMessage(event),
      getSuccessDetails: (event: GameDetectionCompleteEvent, existing) => ({
        ...existing?.details,
        totalGamesDetected: event.totalGamesDetected,
        totalServicesDetected: event.totalServicesDetected
      }),
      getFailureMessage: (event: GameDetectionCompleteEvent) =>
        formatGameDetectionFailureMessage(event)
    }
  },

  // ========== Corruption Detection ==========
  {
    type: 'corruption_detection',
    id: NOTIFICATION_IDS.CORRUPTION_DETECTION,
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.corruptionDetection,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.corruptionDetect.startingStructuralFull', context: {} },
          { stageKey: 'signalr.corruptionDetect.startingStructuralIncremental', context: {} },
          { stageKey: 'signalr.corruptionDetect.startingStructural', context: {} },
          { stageKey: 'signalr.corruptionDetect.startingRepeatedMiss', context: {} },
          { stageKey: 'signalr.corruptionDetect.enumerating', context: { count: 0 } },
          { stageKey: 'signalr.corruptionDetect.buildingBaseline', context: {} },
          { stageKey: 'signalr.corruptionDetect.resumingIncremental', context: {} },
          { stageKey: 'signalr.corruptionDetect.scanningFull', context: {} },
          { stageKey: 'signalr.corruptionDetect.scanningIncremental', context: {} },
          { stageKey: 'signalr.corruptionDetect.scanningHeaders', context: {} },
          { stageKey: 'signalr.corruptionDetect.scanningLogs', context: {} }
        ]
      },
      apiEndpoint: '/api/cache/corruption/detect/status',
      isProcessing: (data: CorruptionDetectionStatusResponse) => data.isRunning,
      createNotification: (data: CorruptionDetectionStatusResponse) => {
        const presentation = formatCorruptionProgress(data);
        return {
          message: presentation.message,
          detailMessage: presentation.detailMessage,
          progress: data.percentComplete ?? 0,
          progressMode: presentation.progressMode,
          progressAriaValueText: presentation.progressAriaValueText,
          details: corruptionNotificationDetails(data)
        };
      },
      staleMessage: 'Corruption detection completed'
    } satisfies SimpleRecoveryConfig<CorruptionDetectionStatusResponse>,
    events: {
      started: 'CorruptionDetectionStarted',
      progress: 'CorruptionDetectionProgress',
      complete: 'CorruptionDetectionComplete'
    },
    started: {
      defaultMessage: 'Scanning for corrupted cache chunks...',
      getMessage: (event: CorruptionDetectionStartedEvent) =>
        formatCorruptionDetectionStartedMessage(event),
      getDetails: (event: CorruptionDetectionStartedEvent) => corruptionNotificationDetails(event)
    },
    progress: {
      getMessage: (event: CorruptionDetectionProgressEvent) =>
        formatCorruptionDetectionProgressMessage(event),
      getProgress: (event: CorruptionDetectionProgressEvent) => event.percentComplete,
      getDetailMessage: (event: CorruptionDetectionProgressEvent) =>
        formatCorruptionProgress(event).detailMessage,
      getProgressMode: (event: CorruptionDetectionProgressEvent) =>
        formatCorruptionProgress(event).progressMode,
      getProgressAriaValueText: (event: CorruptionDetectionProgressEvent) =>
        formatCorruptionProgress(event).progressAriaValueText,
      getStatus: (event: CorruptionDetectionProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: CorruptionDetectionProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionDetect.complete', event.context ?? {}),
      getErrorMessage: (event: CorruptionDetectionProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionDetect.failed', event.context ?? {}),
      getDetails: (event: CorruptionDetectionProgressEvent) => corruptionNotificationDetails(event)
    },
    complete: {
      getSuccessMessage: (event: CorruptionDetectionCompleteEvent) =>
        formatCorruptionDetectionCompleteMessage(event),
      getSuccessDetails: (event: CorruptionDetectionCompleteEvent) => ({
        ...corruptionNotificationDetails(event),
        detectionMethod: event.detectionMethod,
        detectionCounts: event.detectionCounts,
        coverage: event.coverage
      }),
      getFailureMessage: (event: CorruptionDetectionCompleteEvent) =>
        formatCorruptionDetectionFailureMessage(event)
    }
  },

  // ========== Cache Clearing ==========
  {
    type: 'cache_clearing',
    id: NOTIFICATION_IDS.CACHE_CLEARING,
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.cacheClearing,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.cacheClear.initializing', context: {} },
          { stageKey: 'signalr.cacheClear.starting', context: {} },
          {
            stageKey: 'signalr.cacheClear.progress',
            context: { processed: 1, totalDirs: 2, activeCount: 1 }
          }
        ]
      },
      apiEndpoint: '/api/cache/operations',
      isProcessing: (data: CacheOperationsResponse) =>
        data.isProcessing && Boolean(data.operations?.length),
      createNotification: (data: CacheOperationsResponse) => {
        const activeOp = data.operations?.[0];
        return {
          message: activeOp?.stageKey
            ? translateRecoveryStage(
                activeOp.stageKey,
                activeOp.context,
                'signalr.cacheClear.starting'
              )
            : (activeOp?.statusMessage ?? i18n.t('signalr.cacheClear.starting')),
          progress: activeOp?.percentComplete ?? 0,
          details: {
            operationId: activeOp?.operationId ?? activeOp?.id,
            filesDeleted: activeOp?.filesDeleted ?? 0,
            directoriesProcessed: activeOp?.directoriesProcessed ?? 0,
            bytesDeleted: activeOp?.bytesDeleted ?? 0
          }
        };
      },
      staleMessage: 'Cache clearing completed'
    } satisfies SimpleRecoveryConfig<CacheOperationsResponse>,
    events: {
      started: 'CacheClearingStarted',
      progress: 'CacheClearingProgress',
      complete: 'CacheClearingComplete'
    },
    started: {
      defaultMessage: 'Starting cache clearing...',
      getMessage: (event: CacheClearingStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.cacheClear.initializing', event.context ?? {}),
      getDetails: (event: CacheClearingStartedEvent) => ({ operationId: event.operationId })
    },
    progress: {
      getMessage: (event: CacheClearProgressEvent) => formatCacheClearProgressMessage(event),
      getProgress: (event: CacheClearProgressEvent) => event.percentComplete,
      getStatus: (event: CacheClearProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: CacheClearProgressEvent) =>
        event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : (event.statusMessage ?? i18n.t(GENERIC_COMPLETION_I18N_KEY)),
      getErrorMessage: (event: CacheClearProgressEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        event.statusMessage ??
        i18n.t(GENERIC_FAILURE_I18N_KEY),
      getDetails: (event: CacheClearProgressEvent) => ({
        operationId: event.operationId,
        filesDeleted: event.filesDeleted,
        directoriesProcessed: event.directoriesProcessed,
        bytesDeleted: event.bytesDeleted
      })
    },
    complete: {
      getSuccessMessage: (event: CacheClearCompleteEvent) => formatCacheClearCompleteMessage(event),
      getSuccessDetails: (event: CacheClearCompleteEvent, existing) => ({
        ...existing?.details,
        filesDeleted: event.filesDeleted,
        directoriesProcessed: event.directoriesProcessed
      }),
      getFailureMessage: (event: CacheClearCompleteEvent) => formatCacheClearFailureMessage(event)
    }
  },

  // ========== Data Import ==========
  {
    type: 'data_import',
    id: NOTIFICATION_IDS.DATA_IMPORT,
    storageKey: NOTIFICATION_STORAGE_KEYS.DATA_IMPORT,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.dataImport,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.dataImport.starting', context: {} },
          {
            stageKey: 'signalr.dataImport.progress',
            context: { processed: 10, total: 100 }
          }
        ]
      },
      apiEndpoint: '/api/migration/import/status',
      isProcessing: (data: DataImportStatusResponse) => data.isProcessing,
      createNotification: (data: DataImportStatusResponse) => ({
        message: translateRecoveryStage(data.stageKey, data.context, 'signalr.dataImport.starting'),
        // `??` (not `||`): backend field is `double?` - nullable. `??` preserves 0.
        progress: data.percentComplete ?? 0,
        details: {
          operationId: data.operationId ?? undefined
        }
      }),
      staleMessage: 'Data import completed'
    } satisfies SimpleRecoveryConfig<DataImportStatusResponse>,
    events: {
      started: 'DataImportStarted',
      progress: 'DataImportProgress',
      complete: 'DataImportComplete'
    },
    started: {
      defaultMessage: 'Starting data import...',
      getMessage: (event: DataImportStartedEvent) => formatDataImportStartedMessage(event),
      getDetails: (event: DataImportStartedEvent) => ({
        operationId: event.operationId
      })
    },
    progress: {
      getMessage: (event: DataImportProgressEvent) => formatDataImportProgressMessage(event),
      getProgress: (event: DataImportProgressEvent) => event.percentComplete,
      getStatus: (event: DataImportProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: DataImportProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_COMPLETION_I18N_KEY, event.context ?? {}),
      getErrorMessage: (event: DataImportProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_FAILURE_I18N_KEY, event.context ?? {}),
      getDetails: (event: DataImportProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: DataImportCompleteEvent) => formatDataImportCompleteMessage(event),
      // The summary line carries imported/skipped but never the ERROR count, and no renderer reads
      // the details for this type - so a failed record count was invisible. Put the breakdown on the
      // card's detail line.
      getDetailMessage: (event: DataImportCompleteEvent) =>
        formatDataImportCompleteDetailMessage(event),
      getSuccessDetails: (event: DataImportCompleteEvent, existing) => ({
        ...existing?.details,
        recordsImported: event.recordsImported,
        recordsSkipped: event.recordsSkipped,
        recordsErrors: event.recordsErrors,
        totalRecords: event.totalRecords
      }),
      getFailureMessage: (event: DataImportCompleteEvent) => formatDataImportFailureMessage(event)
    }
  },

  // ========== Eviction Scan ==========
  {
    type: 'eviction_scan',
    id: NOTIFICATION_IDS.EVICTION_SCAN,
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.evictionScan,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.evictionScan.scanning', context: {} },
          { stageKey: 'signalr.evictionScan.scanningFiles', context: { filesFound: 10 } },
          {
            stageKey: 'signalr.evictionScan.progress',
            context: { totalProcessed: 10, totalEstimate: 100 }
          },
          { stageKey: 'signalr.evictionScan.finalizing', context: {} },
          { stageKey: 'signalr.evictionScan.postProcessing', context: {} },
          { stageKey: 'signalr.evictionScan.refreshingSummary', context: {} },
          {
            stageKey: 'signalr.evictionScan.refreshingSummaryCounted',
            context: { filesChecked: 10, filesTotal: 100 }
          }
        ]
      },
      apiEndpoint: '/api/stats/eviction/scan/status',
      isProcessing: (data: EvictionScanStatusResponse) => data.isProcessing && !data.silentMode,
      shouldSkip: (data: EvictionScanStatusResponse) => data.isProcessing && data.silentMode,
      createNotification: (data: EvictionScanStatusResponse) => ({
        message: translateRecoveryStage(
          data.stageKey,
          data.context,
          'signalr.evictionScan.scanning'
        ),
        progress: data.percentComplete,
        details: {
          operationId: data.operationId ?? undefined
        }
      }),
      staleMessage: 'Eviction scan completed'
    } satisfies SimpleRecoveryConfig<EvictionScanStatusResponse>,
    events: {
      started: 'EvictionScanStarted',
      progress: 'EvictionScanProgress',
      complete: 'EvictionScanComplete'
    },
    started: {
      defaultMessage: 'Starting eviction scan...',
      getMessage: (event: EvictionScanStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionScan.scanning', event.context ?? {}),
      getDetails: (event: EvictionScanStartedEvent) => ({ operationId: event.operationId })
    },
    progress: {
      getMessage: (event: EvictionScanProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionScan.progress', event.context ?? {}),
      getProgress: (event: EvictionScanProgressEvent) =>
        Math.min(ACTIVE_PROGRESS_PERCENT_CAP, event.percentComplete),
      getStatus: (event: EvictionScanProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: EvictionScanProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionScan.complete', event.context ?? {}),
      getErrorMessage: (event: EvictionScanProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_FAILURE_I18N_KEY, event.context ?? {}),
      getDetails: (event: EvictionScanProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: EvictionScanCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionScan.complete', event.context ?? {}),
      getFailureMessage: (event: EvictionScanCompleteEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t(GENERIC_FAILURE_I18N_KEY)
    }
  },

  // ========== Cache File Scan (cache_size binary) ==========
  // Deliberately VISIBLE (never silent): the running card is what tells users why
  // other heavy cache operations are blocked while the minutes-long scan runs.
  {
    type: 'cache_size_scan',
    id: NOTIFICATION_IDS.CACHE_SIZE_SCAN,
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_SIZE_SCAN,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.cacheSizeScan,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.cacheSizeScan.starting', context: {} },
          {
            stageKey: 'signalr.cacheSizeScan.scanning',
            context: { directoriesScanned: 10, totalDirectories: 100, totalFiles: 1000 }
          },
          { stageKey: 'signalr.cacheSizeScan.sizing', context: {} },
          { stageKey: 'signalr.cacheSizeScan.counting', context: {} },
          {
            stageKey: 'signalr.cacheSizeScan.calibrating',
            context: { step: 1, totalSteps: 3 }
          }
        ]
      },
      apiEndpoint: '/api/cache/size/scan/status',
      isProcessing: (data: CacheSizeScanStatusResponse) => data.isProcessing,
      createNotification: (data: CacheSizeScanStatusResponse) => ({
        message: translateRecoveryStage(
          data.stageKey,
          data.context,
          'signalr.cacheSizeScan.starting'
        ),
        progress: data.percentComplete,
        details: {
          operationId: data.operationId ?? undefined
        }
      }),
      staleMessage: 'Cache file scan completed'
    } satisfies SimpleRecoveryConfig<CacheSizeScanStatusResponse>,
    events: {
      started: 'CacheSizeScanStarted',
      progress: 'CacheSizeScanProgress',
      complete: 'CacheSizeScanComplete'
    },
    started: {
      defaultMessage: 'Starting cache file scan...',
      getMessage: (event: CacheSizeScanStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.cacheSizeScan.starting', event.context ?? {}),
      getDetails: (event: CacheSizeScanStartedEvent) => ({ operationId: event.operationId })
    },
    progress: {
      getMessage: (event: CacheSizeScanProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.cacheSizeScan.scanning', event.context ?? {}),
      getProgress: (event: CacheSizeScanProgressEvent) =>
        Math.min(ACTIVE_PROGRESS_PERCENT_CAP, event.percentComplete),
      getStatus: (event: CacheSizeScanProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: CacheSizeScanProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.cacheSizeScan.complete', event.context ?? {}),
      getErrorMessage: (event: CacheSizeScanProgressEvent) =>
        i18n.t(event.stageKey ?? GENERIC_FAILURE_I18N_KEY, event.context ?? {}),
      getDetails: (event: CacheSizeScanProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: CacheSizeScanCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.cacheSizeScan.complete', event.context ?? {}),
      getFailureMessage: (event: CacheSizeScanCompleteEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t(GENERIC_FAILURE_I18N_KEY)
    }
  },

  // ========== Scheduled Prefill ==========
  {
    type: 'scheduled_prefill',
    id: NOTIFICATION_IDS.SCHEDULED_PREFILL,
    storageKey: NOTIFICATION_STORAGE_KEYS.SCHEDULED_PREFILL,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.scheduledPrefill,
    // The run's card persists via storageKey, so a terminal event missed while the page was
    // closed or reconnecting mid-run used to leave a ghost "Prefill in progress" card forever.
    // This endpoint stale-completes it (or re-seeds a card for a genuinely active run).
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/system/schedules/scheduledPrefill/run-status',
      isProcessing: (data: ScheduledPrefillRunStatusResponse) =>
        data.isRunning && data.showNotification !== false,
      shouldSkip: (data: ScheduledPrefillRunStatusResponse) =>
        data.isRunning && data.showNotification === false,
      createNotification: (data: ScheduledPrefillRunStatusResponse) => ({
        message: i18n.t('management.schedules.services.scheduledPrefill.events.started'),
        details: { operationId: data.operationId ?? undefined }
      }),
      staleMessage: 'Scheduled prefill completed'
    } satisfies SimpleRecoveryConfig<ScheduledPrefillRunStatusResponse>,
    events: {
      started: 'ScheduledPrefillStarted',
      progress: 'ScheduledPrefillProgress',
      complete: 'ScheduledPrefillCompleted'
    },
    started: {
      shouldDisplay: (event: ScheduledPrefillStartedEvent) => event.showNotification !== false,
      defaultMessage: 'Scheduled prefill started',
      getMessage: () => i18n.t('management.schedules.services.scheduledPrefill.events.started'),
      getDetails: (event: ScheduledPrefillStartedEvent) => ({ operationId: event.operationId }),
      replaceExisting: true,
      additionalIdsToRemove: [SCHEDULED_PREFILL_LEGACY_GENERIC_NOTIFICATION_ID]
    },
    progress: {
      shouldDisplay: (event: ScheduledPrefillProgressEvent) => event.showNotification !== false,
      getMessage: (event: ScheduledPrefillProgressEvent) => {
        const serviceKey =
          SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[event.serviceId] ?? event.serviceId;
        const serviceLabel = i18n.t(
          `management.schedules.services.scheduledPrefill.config.services.${serviceKey}`
        );

        if (event.stage === 'skipped') {
          return i18n.t('management.schedules.services.scheduledPrefill.events.skipped', {
            service: serviceLabel,
            reason: event.message
          });
        }

        if (event.stage === 'needs-login') {
          // Keep the backend's precise prerequisite (container stopped vs running-but-logged-out)
          // instead of collapsing every needs-login skip into the same generic line.
          return event.needsLoginReason
            ? i18n.t('management.schedules.services.scheduledPrefill.events.needsLoginWithReason', {
                service: serviceLabel,
                reason: event.needsLoginReason
              })
            : i18n.t('management.schedules.services.scheduledPrefill.events.needsLogin', {
                service: serviceLabel
              });
        }

        return i18n.t('management.schedules.services.scheduledPrefill.events.serviceProgress', {
          service: serviceLabel,
          message: event.message
        });
      },
      // Backend-computed run percent. It tracks the ACTIVE service only (games completed plus the
      // byte fraction of the game downloading right now), clamped 1-99 server-side; 100 comes from
      // the terminal Completed event.
      //
      // Deliberately NOT rounded: the percent divides the active game's fraction by the number of
      // games, so a big download moves it a fraction of a point at a time. Rounding to a whole
      // number pinned the bar in place and made a working prefill look frozen. The bar and its
      // "x.x%" label both read the fractional value, and getDetailMessage below carries the bytes.
      getProgress: (event: ScheduledPrefillProgressEvent) =>
        Math.max(1, event.percentComplete ?? 1),
      // Bytes of the game currently downloading. The bar alone is not enough on a multi-game run
      // (the run percent divides by the game count, so it crawls); this line moves on every tick of
      // a live download, which is what tells the user it is actually working.
      getDetailMessage: (event: ScheduledPrefillProgressEvent) =>
        formatScheduledPrefillDetailMessage(event),
      getStatus: () => undefined,
      getDetails: (event: ScheduledPrefillProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      shouldDisplay: (event: ScheduledPrefillCompletedEvent) => event.showNotification !== false,
      getSuccessMessage: () =>
        i18n.t('management.schedules.services.scheduledPrefill.events.completed'),
      // A stopped run is its own terminal, not a failure: the user caused it, so it must not read
      // as an error (and must not show the last service's progress line as the result).
      getCancelledMessage: () =>
        i18n.t('management.schedules.services.scheduledPrefill.events.cancelled'),
      getFailureMessage: (event: ScheduledPrefillCompletedEvent) =>
        event.error ?? i18n.t('management.schedules.services.scheduledPrefill.events.failed')
    },
    onComplete: (removeNotification) => {
      removeNotification(SCHEDULED_PREFILL_LEGACY_GENERIC_NOTIFICATION_ID);
    }
  },

  // ========== Eviction Removal ==========
  {
    type: 'eviction_removal',
    id: NOTIFICATION_IDS.EVICTION_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_REMOVAL,
    wiring: 'standard',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.evictionRemoval,
    // Scope-aware recovery lives inside the /api/cache/removals/active batch fn
    // (recoverEvictionRemovals). Marked as part of that batch.
    recovery: { kind: 'cacheRemovalsBatch' },
    events: {
      started: 'EvictionRemovalStarted',
      progress: 'EvictionRemovalProgress',
      complete: 'EvictionRemovalComplete'
    },
    started: {
      defaultMessage: 'Removing evicted game data...',
      getMessage: (event: EvictionRemovalStartedEvent) =>
        event.gameName
          ? i18n.t(REMOVING_GAME_I18N_KEY, { name: event.gameName })
          : i18n.t(event.stageKey ?? 'signalr.evictionRemove.starting.bulk', event.context ?? {}),
      // Scope → identifier-field mapping for eviction_removal (T8.3 load-bearing comment):
      //
      // eviction_removal has a 4-way identifier union depending on scope:
      //   steam   → details.gameAppId: number (Number(event.gameAppId)), details.steamAppId: string (raw)
      //             IMPORTANT: SignalR event's gameAppId arrives as STRING - must Number() before storing
      //             as details.gameAppId (typed as number). Also set steamAppId for parity with game_removal.
      //   epic    → details.epicAppId: string (= event.epicAppId, with event.gameAppId as legacy fallback)
      //             event.epicAppId is the dedicated field; event.gameAppId fallback handles pre-fix payloads.
      //   service → details.service: string (= context.key)
      //   bulk    → no entity identifier (scope/key are undefined); only operationId is set.
      //
      // Naming boundaries:
      //   SignalR (camelCase, global JsonNamingPolicy.CamelCase in Program.cs):
      //     event.operationId, event.gameAppId, event.epicAppId, event.gameName, event.context.scope, event.context.key
      //   REST /api/cache/removals/active (camelCase via same global policy on EvictionRemovalInfo):
      //     op.operationId, op.scope, op.key, op.gameName
      //   Both ingress points must map to the SAME details shape so recovery hydration
      //   (recoveryFactory.ts recoverEvictionRemovals) and SignalR live-start produce
      //   identical notification details. Any change here must be mirrored there.
      getDetails: (event: EvictionRemovalStartedEvent) => {
        const scope = (event.context?.scope as string | undefined)?.toLowerCase();
        const key = event.context?.key as string | undefined;
        const gameAppIdNum = event.gameAppId !== undefined ? Number(event.gameAppId) : undefined;
        return {
          operationId: event.operationId,
          ...(event.gameName !== undefined && { gameName: event.gameName }),
          ...(scope === 'steam' &&
            gameAppIdNum !== undefined &&
            !Number.isNaN(gameAppIdNum) && { gameAppId: gameAppIdNum }),
          ...(scope === 'epic' &&
            (event.epicAppId !== undefined || event.gameAppId !== undefined) && {
              epicAppId: event.epicAppId ?? event.gameAppId
            }),
          ...(scope === 'steam' &&
            event.gameAppId !== undefined && { steamAppId: event.gameAppId }),
          ...(scope === 'service' && key !== undefined && { service: key })
        };
      }
    },
    progress: {
      getMessage: (event: EvictionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.removingDownloads', event.context ?? {}),
      getProgress: (event: EvictionRemovalProgressEvent) => event.percentComplete || 0,
      getStatus: (event: EvictionRemovalProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: EvictionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.complete', event.context ?? {}),
      getErrorMessage: (event: EvictionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.failed', event.context ?? {}),
      // EvictionRemovalProgressEvent does NOT carry scope identity fields
      // (gameAppId, epicAppId, service, gameName are absent from the backend event).
      // Only operationId is available here. Scope identity is set by the started
      // handler and preserved by createStatusAwareProgressHandler's merge semantics
      // ({...n.details, ...eventDetails}). If the notification slot is ever
      // re-created from a progress tick alone, the scope identity would be lost —
      // but that can happen: createStatusAwareProgressHandler's running branch DOES
      // create a missing slot from a bare progress tick (fast-completion path). For
      // eviction_removal this means a bare tick would produce a slot without scope
      // identity. In practice the backend always emits EvictionRemovalStarted before
      // any progress tick, so there is always a prior started slot — but this is a
      // runtime guarantee, not a registry-level opt-out.
      getDetails: (event: EvictionRemovalProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: EvictionRemovalCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.complete', event.context ?? {}),
      getFailureMessage: (event: EvictionRemovalCompleteEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t('signalr.evictionRemove.failed')
    },
    onComplete: (removeNotification) => {
      removeNotification(NOTIFICATION_IDS.EVICTION_SCAN);
      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN);
    }
  },

  // ==========================================================================
  // Special-wiring entries (metadata-only)
  // --------------------------------------------------------------------------
  // These four types do NOT fit the standard Started->Progress->Complete loop.
  // Their SignalR handlers are hand-built in createSpecialCaseHandlers and wired
  // via SPECIAL_NOTIFICATION_CONTRACTS. They appear here ONLY to keep cancel +
  // recovery configured in one place per type. useNotificationHandlers skips
  // every wiring:'special' entry (no `events`/`started`/`progress`), so there is
  // no double-subscribe.
  // ==========================================================================

  // ========== Depot Mapping (special) ==========
  {
    type: 'depot_mapping',
    id: NOTIFICATION_IDS.DEPOT_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
    wiring: 'special',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.depotMapping,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/depots/rebuild/progress',
      isProcessing: (data: DepotRebuildProgressResponse) => data.isProcessing,
      createNotification: (data: DepotRebuildProgressResponse) => {
        const detailMessage = formatDepotMappingRecoveryDetailMessage({
          processedBatches: data.processedBatches,
          totalBatches: data.totalBatches,
          depotMappingsFound: data.depotMappingsFound
        });

        return {
          message: data.statusMessage,
          detailMessage,
          progress: data.progressPercent,
          details: {
            operationId: data.operationId,
            totalMappings: data.totalMappings,
            processedMappings: data.processedMappings,
            isLoggedOn: data.isLoggedOn,
            percentComplete: data.progressPercent
          }
        };
      },
      staleMessage: 'Depot mapping completed'
    } satisfies SimpleRecoveryConfig<DepotRebuildProgressResponse>
  },

  // ========== Database Reset (special) ==========
  {
    type: 'database_reset',
    id: NOTIFICATION_IDS.DATABASE_RESET,
    storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
    wiring: 'special',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.databaseReset,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.dbReset.starting', context: {} },
          { stageKey: 'signalr.dbReset.startingTables', context: { count: 2 } },
          {
            stageKey: 'signalr.dbReset.deleting',
            context: { tableName: 'Downloads', deletedRows: 10, totalRows: 100 }
          },
          {
            stageKey: 'signalr.dbReset.clearingLogEntries',
            context: { deleted: 10, total: 100, percent: 10 }
          },
          { stageKey: 'signalr.dbReset.clearedLogEntries', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedDownloads', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedClientStats', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedServiceStats', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedDepotMappings', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedGameDetections', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedUserPreferences', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedUserSessions', context: { count: 10 } },
          {
            stageKey: 'signalr.dbReset.clearedTable',
            context: { tableName: 'Events', count: 10 }
          },
          { stageKey: 'signalr.dbReset.optimizing', context: {} },
          { stageKey: 'signalr.dbReset.cleanup', context: {} },
          { stageKey: 'signalr.dbReset.failedExitCode', context: { exitCode: 1 } },
          { stageKey: 'signalr.dbReset.failed', context: { errorDetail: 'error' } },
          { stageKey: 'signalr.dbReset.error.fatal', context: { errorDetail: 'error' } }
        ]
      },
      apiEndpoint: '/api/database/reset-status',
      isProcessing: (data: DatabaseResetStatusResponse) => data.isProcessing,
      createNotification: (data: DatabaseResetStatusResponse) => ({
        message: translateRecoveryStage(data.stageKey, data.context, 'signalr.dbReset.starting'),
        // `??` (not `||`): backend field is `double?` - nullable. `??` preserves 0.
        progress: data.percentComplete ?? 0,
        // Always emit a defined details object so the deferred-cancel watchdog can
        // attach an operationId when it arrives via a later SignalR progress tick.
        // `?? undefined` normalises null→undefined (backend field is `string?`).
        details: { operationId: data.operationId ?? undefined }
      }),
      staleMessage: 'Database reset completed'
    } satisfies SimpleRecoveryConfig<DatabaseResetStatusResponse>
  },

  // ========== Epic Game Mapping (special) ==========
  {
    type: 'epic_game_mapping',
    id: NOTIFICATION_IDS.EPIC_GAME_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.EPIC_GAME_MAPPING,
    wiring: 'special',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.epicGameMapping,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/epic/game-mappings/schedule',
      isProcessing: (data: EpicGameMappingScheduleResponse) => data.isProcessing,
      createNotification: (data: EpicGameMappingScheduleResponse) => ({
        // `statusMessage` is C# `string?` - only populated when processing.
        // Fall back to i18n key when null/undefined (e.g. during idle recovery poll).
        message: data.statusMessage ?? i18n.t('signalr.epicMapping.starting'),
        // `progressPercent` is C# `double` (non-null) - no fallback needed.
        progress: data.progressPercent,
        details: {
          operationId: data.operationId ?? undefined
        }
      }),
      staleMessage: 'Epic game mapping completed'
    } satisfies SimpleRecoveryConfig<EpicGameMappingScheduleResponse>
  },

  // ========== Xbox Game Mapping (special) ==========
  // Mirrors epic_game_mapping but Xbox titles resolve automatically during Rust ingest, so there
  // is NO schedule/recovery endpoint - a missed in-flight resolve simply isn't re-surfaced on
  // refresh (recovery 'none'). Driven by XboxMappingProgress + XboxGameMappingsUpdated.
  {
    type: 'xbox_game_mapping',
    id: NOTIFICATION_IDS.XBOX_GAME_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.XBOX_GAME_MAPPING,
    wiring: 'special',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.xboxGameMapping,
    recovery: { kind: 'none' }
  },

  // ========== Steam Session Error (special; toast, no recovery, no cancel) ==========
  {
    type: 'steam_session_error' as NotificationType,
    id: NOTIFICATION_IDS.STEAM_SESSION_ERROR,
    storageKey: '',
    wiring: 'special',
    cancelKind: 'none',
    recovery: { kind: 'none' }
  },

  // ========== Bulk Removal (special; client-driven queue, no server op) ==========
  // Metadata-only entry: the bulk_removal notification is created/managed by the
  // always-mounted BulkRemovalProvider's useCancellableQueue, NOT by the standard
  // SignalR loop. It appears here ONLY so UniversalNotificationBar's cancel-config
  // loop is the single source for cancel wiring (cancelKind 'clientQueue' → the X
  // button flips a flag the provider's cascade effect observes). No SignalR events,
  // no recovery (the run loop survives in-app tab switches by construction).
  {
    type: 'bulk_removal',
    id: 'bulk_removal',
    storageKey: '',
    wiring: 'special',
    cancelKind: 'clientQueue',
    cancelTooltipKey: CANCEL_TOOLTIP.bulkRemoval,
    recovery: { kind: 'none' }
  }
];
