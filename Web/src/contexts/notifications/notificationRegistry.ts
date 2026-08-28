/**
 * Declarative notification registry.
 * Each entry describes the full lifecycle (started -> progress -> complete) of a
 * notification type, including the SignalR event names, handler configurations,
 * cancel wiring (cancelKind + tooltip), and recovery wiring.
 *
 * An entry that declares `events` is subscribed by the {@link useNotificationHandlers}
 * loop, phase by phase, so an announcement whose single event is already terminal
 * declares `events.complete` alone. An entry that declares none is metadata-only: its
 * card is created by client code, and it appears here ONLY so cancel + recovery live
 * in one config surface per type.
 */

import type { NotificationRegistryEntry, SimpleRecoveryConfig } from './types';
import type {
  CacheOperationsResponse,
  CacheSizeScanStatusResponse,
  CorruptionDetectionStatusResponse,
  DatabaseResetStatusResponse,
  DataImportStatusResponse,
  EvictionScanStatusResponse,
  GameDetectionStatusResponse,
  LogProcessingStatusResponse,
  LogRemovalStatusResponse,
  ScheduledPrefillRunStatusResponse
} from './recoveryStatusResponses';
import { corruptionNotificationDetails, formatCorruptionProgress } from './corruptionProgress';
import {
  ACTIVE_PROGRESS_PERCENT_CAP,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  NOTIFICATION_IDS,
  NOTIFICATION_STORAGE_KEYS,
  REMOVING_GAME_I18N_KEY,
  STEAM_ERROR_DISMISS_DELAY_MS
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
  formatCorruptionDetectionCompleteMessage,
  formatCorruptionDetectionFailureMessage,
  formatCacheClearProgressMessage,
  formatCacheClearCompleteMessage,
  formatCacheClearFailureMessage,
  formatDataImportStartedMessage,
  formatDataImportProgressMessage,
  formatDataImportCompleteMessage,
  formatDataImportFailureMessage,
  formatDatabaseResetProgressMessage,
  formatDatabaseResetCompleteMessage,
  formatEpicGameMappingsUpdatedMessage,
  formatXboxGameMappingsUpdatedMessage
} from './detailMessageFormatters';
import {
  buildScheduledRunEntry,
  buildMappingOperationEntry,
  buildStandardOperationEntry,
  cappedProgress,
  errorOrStageKeyMessage,
  operationIdDetails,
  stageKeyMessage
} from './registryEntries';
import { translateRecoveryStage, translateStageKeyMessage } from '@utils/stageKeyMessage';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { storage } from '@utils/storage';
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
  DatabaseResetStartedEvent,
  DatabaseResetProgressEvent,
  CacheClearingStartedEvent,
  CacheClearProgressEvent,
  CacheClearCompleteEvent,
  DataImportStartedEvent,
  DataImportProgressEvent,
  DataImportCompleteEvent,
  EvictionScanStartedEvent,
  EvictionScanProgressEvent,
  EvictionScanCompleteEvent,
  CacheSizeScanStartedEvent,
  CacheSizeScanProgressEvent,
  CacheSizeScanCompleteEvent,
  EvictionRemovalStartedEvent,
  EvictionRemovalProgressEvent,
  EvictionRemovalCompleteEvent,
  ScheduledPrefillStartedEvent,
  ScheduledPrefillProgressEvent,
  ScheduledPrefillCompletedEvent,
  DepotMappingStartedEvent,
  DepotMappingProgressEvent,
  DepotMappingCompleteEvent,
  EpicMappingStartedEvent,
  EpicMappingProgressEvent,
  EpicMappingCompleteEvent,
  XboxMappingStartedEvent,
  XboxMappingProgressEvent,
  XboxMappingCompleteEvent,
  BattleNetMappingStartedEvent,
  BattleNetMappingProgressEvent,
  BattleNetMappingCompleteEvent,
  RiotMappingStartedEvent,
  RiotMappingProgressEvent,
  RiotMappingCompleteEvent,
  EpicGameMappingsUpdatedEvent,
  XboxGameMappingsUpdatedEvent,
  SteamSessionErrorEvent
} from '../SignalRContext/types';

/**
 * Terminal `DatabaseResetComplete` SignalR payload (camelCase, mirrors the backend
 * `SignalRNotifications.DatabaseResetComplete` record). Emitted exactly once via
 * `OperationInfo.OnTerminalEmit` on the normal success/error path AND the universal
 * force-kill/cancel path. Declared here because `DatabaseResetStartedEvent` and
 * `DatabaseResetProgressEvent` have SignalR contract types but the terminal payload
 * does not; it satisfies the completion config's `{ success; stageKey?; context?;
 * message?; cancelled? }` constraint.
 */
interface DatabaseResetCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  status?: string;
  cancelled?: boolean;
  error?: string;
  context?: Record<string, string | number | boolean>;
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
  context: Record<string, string | number | boolean | null> | undefined
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
  battleNetGameMapping: 'common.notifications.cancelBattleNetGameMapping',
  riotGameMapping: 'common.notifications.cancelRiotGameMapping',
  bulkRemoval: 'common.notifications.cancelBulkRemoval',
  prefillLogin: 'common.notifications.cancelPrefillLogin'
} as const;

export const NOTIFICATION_REGISTRY: NotificationRegistryEntry[] = [
  // ========== Log Processing ==========
  buildStandardOperationEntry<
    LogProcessingStartedEvent,
    ProcessingProgressEvent,
    LogProcessingCompleteEvent
  >({
    type: 'log_processing',
    id: NOTIFICATION_IDS.LOG_PROCESSING,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING,
    eventPrefix: 'LogProcessing',
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
      staleMessageKey: 'signalr.logProcessing.stale'
    } satisfies SimpleRecoveryConfig<LogProcessingStatusResponse>,
    started: {
      defaultMessage: 'Starting log processing...',
      getMessage: stageKeyMessage('signalr.logProcessing.starting')
    },
    progress: {
      getMessage: (event: ProcessingProgressEvent) => formatLogProcessingMessage(event),
      getProgress: cappedProgress,
      // Not the shared three-status pattern: this pipeline reports a capitalized status.
      getStatus: (event: ProcessingProgressEvent) =>
        event.status?.toLowerCase() === 'completed' ? 'completed' : undefined,
      getCompletedMessage: (event: ProcessingProgressEvent) =>
        formatLogProcessingCompletionMessage(event.entriesSaved)
    },
    complete: {
      // Translated, not a hardcoded English literal: this now actually reaches the card (the
      // completion handler never applied getSuccessMessage to an existing card before), so a
      // literal here would switch a localized card to English at the moment it finishes.
      getSuccessMessage: stageKeyMessage('signalr.logProcessing.complete'),
      getDetailMessage: (event: LogProcessingCompleteEvent) =>
        formatLogProcessingDetailMessage(
          event.entriesProcessed,
          event.linesProcessed,
          event.elapsed
        )
    }
  }),

  // ========== Log Removal ==========
  buildStandardOperationEntry<
    LogRemovalStartedEvent,
    LogRemovalProgressEvent,
    LogRemovalCompleteEvent
  >({
    type: 'log_removal',
    id: NOTIFICATION_IDS.LOG_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
    eventPrefix: 'LogRemoval',
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
      staleMessageKey: 'signalr.logRemoval.stale'
    } satisfies SimpleRecoveryConfig<LogRemovalStatusResponse>,
    started: {
      defaultMessage: 'Starting log removal...',
      getMessage: stageKeyMessage('signalr.logRemoval.starting.default')
    },
    progress: {
      getMessage: (event: LogRemovalProgressEvent) => formatLogRemovalProgressMessage(event),
      getProgress: (event: LogRemovalProgressEvent) => event.percentComplete,
      // Not the shared three-status pattern: this pipeline does report a cancelled terminal,
      // and the shared getter folds cancelled into failed, which would paint a stopped run red.
      getStatus: (event: LogRemovalProgressEvent) =>
        event.status === 'completed'
          ? 'completed'
          : event.status === 'failed'
            ? 'failed'
            : undefined,
      getCompletedMessage: stageKeyMessage(GENERIC_COMPLETION_I18N_KEY),
      getErrorMessage: stageKeyMessage(GENERIC_FAILURE_I18N_KEY)
    },
    complete: {
      getSuccessMessage: (event: LogRemovalCompleteEvent) => formatLogRemovalCompleteMessage(event),
      getSuccessDetails: (event: LogRemovalCompleteEvent, existing) => ({
        ...existing?.details,
        linesProcessed: event.linesProcessed
      }),
      useAnimationDelay: true
    }
  }),

  // ========== Game Removal ==========
  buildStandardOperationEntry<
    GameRemovalStartedEvent,
    GameRemovalProgressEvent,
    GameRemovalCompleteEvent
  >({
    type: 'game_removal',
    id: NOTIFICATION_IDS.GAME_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
    eventPrefix: 'GameRemoval',
    cancelTooltipKey: CANCEL_TOOLTIP.gameRemoval,
    // Recovered by the shared /api/cache/removals/active batch fetch (one GET
    // covering game/service/corruption/eviction removal) — NOT a simple config.
    recovery: { kind: 'cacheRemovalsBatch' },
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
      getErrorMessage: stageKeyMessage('signalr.gameRemove.error.fatal'),
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
  }),

  // ========== Service Removal ==========
  buildStandardOperationEntry<
    ServiceRemovalStartedEvent,
    ServiceRemovalProgressEvent,
    ServiceRemovalCompleteEvent
  >({
    type: 'service_removal',
    id: NOTIFICATION_IDS.SERVICE_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
    eventPrefix: 'ServiceRemoval',
    cancelTooltipKey: CANCEL_TOOLTIP.serviceRemoval,
    recovery: { kind: 'cacheRemovalsBatch' },
    started: {
      defaultMessage: 'Starting service removal...',
      getMessage: stageKeyMessage('signalr.serviceRemove.starting.default')
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
        })
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
  }),

  // ========== Corruption Removal ==========
  buildStandardOperationEntry<
    CorruptionRemovalStartedEvent,
    CorruptionRemovalProgressEvent,
    CorruptionRemovalCompleteEvent
  >({
    type: 'corruption_removal',
    id: NOTIFICATION_IDS.CORRUPTION_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
    eventPrefix: 'CorruptionRemoval',
    cancelTooltipKey: CANCEL_TOOLTIP.corruptionRemoval,
    recovery: { kind: 'cacheRemovalsBatch' },
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
      getCompletedMessage: stageKeyMessage('signalr.corruptionRemove.success'),
      getErrorMessage: stageKeyMessage('signalr.corruptionRemove.failed.generic'),
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
      storage.removeItem(NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION);
    }
  }),

  // ========== Game Detection ==========
  buildStandardOperationEntry<
    GameDetectionStartedEvent,
    GameDetectionProgressEvent,
    GameDetectionCompleteEvent
  >({
    type: 'game_detection',
    id: NOTIFICATION_IDS.GAME_DETECTION,
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
    eventPrefix: 'GameDetection',
    cancelTooltipKey: CANCEL_TOOLTIP.gameDetection,
    silentRunGate: true,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/games/detect/active',
      isProcessing: (data: GameDetectionStatusResponse) =>
        data.isProcessing && data.operation !== null && data.showNotification !== false,
      // A silent automatic run still emits its terminal (display-gated). Skip recovery so a page
      // reload mid-run does not resurrect a visible card that the silent terminal can never clear.
      shouldSkip: (data: GameDetectionStatusResponse) =>
        data.isProcessing && data.operation !== null && data.showNotification === false,
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
      staleMessageKey: 'signalr.gameDetect.stale'
    } satisfies SimpleRecoveryConfig<GameDetectionStatusResponse>,
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
      getCompletedMessage: (event: GameDetectionProgressEvent) =>
        i18n.t(
          event.stageKey ?? 'signalr.gameDetect.complete.default',
          buildGameDetectionInterpolation(event.context, {
            totalGamesDetected: event.gamesDetected
          })
        ),
      getErrorMessage: stageKeyMessage(GENERIC_FAILURE_I18N_KEY)
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
  }),

  // ========== Corruption Detection ==========
  buildStandardOperationEntry<
    CorruptionDetectionStartedEvent,
    CorruptionDetectionProgressEvent,
    CorruptionDetectionCompleteEvent
  >({
    type: 'corruption_detection',
    id: NOTIFICATION_IDS.CORRUPTION_DETECTION,
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
    eventPrefix: 'CorruptionDetection',
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
      staleMessageKey: 'signalr.corruptionDetect.stale'
    } satisfies SimpleRecoveryConfig<CorruptionDetectionStatusResponse>,
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
      getCompletedMessage: stageKeyMessage('signalr.corruptionDetect.complete'),
      getErrorMessage: stageKeyMessage('signalr.corruptionDetect.failed'),
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
  }),

  // ========== Cache Clearing ==========
  buildStandardOperationEntry<
    CacheClearingStartedEvent,
    CacheClearProgressEvent,
    CacheClearCompleteEvent
  >({
    type: 'cache_clearing',
    id: NOTIFICATION_IDS.CACHE_CLEARING,
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
    eventPrefix: 'CacheClearing',
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
      staleMessageKey: 'signalr.cacheClear.stale'
    } satisfies SimpleRecoveryConfig<CacheOperationsResponse>,
    started: {
      defaultMessage: 'Starting cache clearing...',
      getMessage: stageKeyMessage('signalr.cacheClear.initializing')
    },
    progress: {
      getMessage: (event: CacheClearProgressEvent) => formatCacheClearProgressMessage(event),
      getProgress: (event: CacheClearProgressEvent) => event.percentComplete,
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
      getFailureMessage: (event: CacheClearCompleteEvent) => formatCacheClearFailureMessage(event),
      // Without this the card falls through to the server's own English sentence, which no
      // locale ever translates.
      getCancelledMessage: () => i18n.t('signalr.cacheClear.cancelled')
    }
  }),

  // ========== Data Import ==========
  buildStandardOperationEntry<
    DataImportStartedEvent,
    DataImportProgressEvent,
    DataImportCompleteEvent
  >({
    type: 'data_import',
    id: NOTIFICATION_IDS.DATA_IMPORT,
    storageKey: NOTIFICATION_STORAGE_KEYS.DATA_IMPORT,
    eventPrefix: 'DataImport',
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
      staleMessageKey: 'signalr.dataImport.stale'
    } satisfies SimpleRecoveryConfig<DataImportStatusResponse>,
    started: {
      defaultMessage: 'Starting data import...',
      getMessage: (event: DataImportStartedEvent) => formatDataImportStartedMessage(event)
    },
    progress: {
      getMessage: (event: DataImportProgressEvent) => formatDataImportProgressMessage(event),
      getProgress: (event: DataImportProgressEvent) => event.percentComplete,
      getCompletedMessage: stageKeyMessage(GENERIC_COMPLETION_I18N_KEY),
      getErrorMessage: stageKeyMessage(GENERIC_FAILURE_I18N_KEY)
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
      getFailureMessage: (event: DataImportCompleteEvent) => formatDataImportFailureMessage(event),
      // Without this the card falls through to the server's own English sentence, which no
      // locale ever translates.
      getCancelledMessage: () => i18n.t('signalr.dataImport.cancelled')
    }
  }),

  // ========== Eviction Scan ==========
  buildStandardOperationEntry<
    EvictionScanStartedEvent,
    EvictionScanProgressEvent,
    EvictionScanCompleteEvent
  >({
    type: 'eviction_scan',
    id: NOTIFICATION_IDS.EVICTION_SCAN,
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN,
    eventPrefix: 'EvictionScan',
    cancelTooltipKey: CANCEL_TOOLTIP.evictionScan,
    silentRunGate: true,
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
      staleMessageKey: 'signalr.evictionScan.stale'
    } satisfies SimpleRecoveryConfig<EvictionScanStatusResponse>,
    started: {
      defaultMessage: 'Starting eviction scan...',
      getMessage: stageKeyMessage('signalr.evictionScan.scanning')
    },
    progress: {
      getMessage: stageKeyMessage('signalr.evictionScan.progress'),
      getProgress: cappedProgress,
      getCompletedMessage: stageKeyMessage('signalr.evictionScan.complete'),
      getErrorMessage: stageKeyMessage(GENERIC_FAILURE_I18N_KEY)
    },
    complete: {
      getSuccessMessage: stageKeyMessage('signalr.evictionScan.complete'),
      getFailureMessage: errorOrStageKeyMessage(GENERIC_FAILURE_I18N_KEY),
      getCancelledMessage: stageKeyMessage('signalr.evictionScan.cancelled')
    }
  }),

  // ========== Cache File Scan (cache_size binary) ==========
  // Deliberately VISIBLE (never silent): the running card is what tells users why
  // other heavy cache operations are blocked while the minutes-long scan runs.
  buildStandardOperationEntry<
    CacheSizeScanStartedEvent,
    CacheSizeScanProgressEvent,
    CacheSizeScanCompleteEvent
  >({
    type: 'cache_size_scan',
    id: NOTIFICATION_IDS.CACHE_SIZE_SCAN,
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_SIZE_SCAN,
    eventPrefix: 'CacheSizeScan',
    cancelTooltipKey: CANCEL_TOOLTIP.cacheSizeScan,
    silentRunGate: true,
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
      isProcessing: (data: CacheSizeScanStatusResponse) =>
        data.isProcessing && data.showNotification !== false,
      // A silent automatic scan still emits its terminal (display-gated). Skip recovery so a page
      // reload mid-run does not resurrect a visible card that the silent terminal can never clear.
      shouldSkip: (data: CacheSizeScanStatusResponse) =>
        data.isProcessing && data.showNotification === false,
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
      staleMessageKey: 'signalr.cacheSizeScan.stale'
    } satisfies SimpleRecoveryConfig<CacheSizeScanStatusResponse>,
    started: {
      defaultMessage: 'Starting cache file scan...',
      getMessage: stageKeyMessage('signalr.cacheSizeScan.starting')
    },
    progress: {
      getMessage: stageKeyMessage('signalr.cacheSizeScan.scanning'),
      getProgress: cappedProgress,
      getCompletedMessage: stageKeyMessage('signalr.cacheSizeScan.complete'),
      getErrorMessage: stageKeyMessage(GENERIC_FAILURE_I18N_KEY)
    },
    complete: {
      getSuccessMessage: stageKeyMessage('signalr.cacheSizeScan.complete'),
      getFailureMessage: errorOrStageKeyMessage(GENERIC_FAILURE_I18N_KEY),
      getCancelledMessage: stageKeyMessage('signalr.cacheSizeScan.cancelled')
    }
  }),

  // ========== Scheduled Prefill ==========
  buildStandardOperationEntry<
    ScheduledPrefillStartedEvent,
    ScheduledPrefillProgressEvent,
    ScheduledPrefillCompletedEvent
  >({
    type: 'scheduled_prefill',
    id: NOTIFICATION_IDS.SCHEDULED_PREFILL,
    storageKey: NOTIFICATION_STORAGE_KEYS.SCHEDULED_PREFILL,
    eventPrefix: 'ScheduledPrefill',
    // This pipeline's terminal event is `...Completed`, not the `...Complete` the
    // other operations emit.
    completeEvent: 'ScheduledPrefillCompleted',
    cancelTooltipKey: CANCEL_TOOLTIP.scheduledPrefill,
    silentRunGate: true,
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
      staleMessageKey: 'signalr.scheduledPrefill.stale'
    } satisfies SimpleRecoveryConfig<ScheduledPrefillRunStatusResponse>,
    started: {
      defaultMessage: 'Scheduled prefill started',
      getMessage: () => i18n.t('management.schedules.services.scheduledPrefill.events.started'),
      replaceExisting: true
    },
    progress: {
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
      // byte fraction of the game downloading right now), clamped 1-99 by ComputeRunPercent; 100
      // comes from the terminal Completed event. Keep only the null fallback here instead of
      // duplicating the backend's clamp.
      //
      // Deliberately NOT rounded: the percent divides the active game's fraction by the number of
      // games, so a big download moves it a fraction of a point at a time. Rounding to a whole
      // number pinned the bar in place and made a working prefill look frozen. The bar and its
      // "x.x%" label both read the fractional value, and getDetailMessage below carries the bytes.
      getProgress: (event: ScheduledPrefillProgressEvent) => event.percentComplete ?? 1,
      // Bytes of the game currently downloading. The bar alone is not enough on a multi-game run
      // (the run percent divides by the game count, so it crawls); this line moves on every tick of
      // a live download, which is what tells the user it is actually working.
      getDetailMessage: (event: ScheduledPrefillProgressEvent) =>
        formatScheduledPrefillDetailMessage(event),
      // The run's terminal arrives as its own event, so progress never completes the card.
      getStatus: () => undefined
    },
    complete: {
      getSuccessMessage: () =>
        i18n.t('management.schedules.services.scheduledPrefill.events.completed'),
      // A stopped run is its own terminal, not a failure: the user caused it, so it must not read
      // as an error (and must not show the last service's progress line as the result).
      getCancelledMessage: () =>
        i18n.t('management.schedules.services.scheduledPrefill.events.cancelled'),
      getFailureMessage: (event: ScheduledPrefillCompletedEvent) =>
        event.error ?? i18n.t('management.schedules.services.scheduledPrefill.events.failed')
    }
  }),

  // ========== Eviction Removal ==========
  buildStandardOperationEntry<
    EvictionRemovalStartedEvent,
    EvictionRemovalProgressEvent,
    EvictionRemovalCompleteEvent
  >({
    type: 'eviction_removal',
    id: NOTIFICATION_IDS.EVICTION_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_REMOVAL,
    eventPrefix: 'EvictionRemoval',
    cancelTooltipKey: CANCEL_TOOLTIP.evictionRemoval,
    silentRunGate: true,
    // Scope-aware recovery lives inside the /api/cache/removals/active batch fn
    // (recoverEvictionRemovals). Marked as part of that batch.
    recovery: { kind: 'cacheRemovalsBatch' },
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
      //   (recovery.ts recoverEvictionRemovals) and SignalR live-start produce
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
      getMessage: stageKeyMessage('signalr.evictionRemove.removingDownloads'),
      getProgress: (event: EvictionRemovalProgressEvent) => event.percentComplete || 0,
      getCompletedMessage: stageKeyMessage('signalr.evictionRemove.complete'),
      getErrorMessage: stageKeyMessage('signalr.evictionRemove.failed'),
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
      // runtime guarantee, not a registry-level opt-out. Spelled out rather than left
      // to the builder default so the reasoning above stays attached to the field.
      getDetails: operationIdDetails
    },
    complete: {
      getSuccessMessage: stageKeyMessage('signalr.evictionRemove.complete'),
      getFailureMessage: errorOrStageKeyMessage('signalr.evictionRemove.failed')
    },
    onComplete: (removeNotification) => {
      removeNotification(NOTIFICATION_IDS.EVICTION_SCAN);
      storage.removeItem(NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN);
    }
  }),

  // ========== Scheduled service runs (standard, built by factory) ==========
  buildScheduledRunEntry({
    type: 'log_rotation',
    id: NOTIFICATION_IDS.LOG_ROTATION,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_ROTATION,
    serviceKey: 'logRotation',
    eventPrefix: 'LogRotation',
    i18nBase: 'signalr.scheduledRun.logRotation',
    countable: false,
    defaultMessage: 'Starting log rotation...',
    staleMessageKey: 'signalr.scheduledRun.logRotation.complete'
  }),
  buildScheduledRunEntry({
    type: 'game_image_fetch',
    id: NOTIFICATION_IDS.GAME_IMAGE_FETCH,
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_IMAGE_FETCH,
    serviceKey: 'gameImageFetch',
    eventPrefix: 'GameImageFetch',
    i18nBase: 'signalr.scheduledRun.gameImageFetch',
    countable: true,
    defaultMessage: 'Starting game image fetch...',
    staleMessageKey: 'signalr.scheduledRun.gameImageFetch.complete'
  }),
  buildScheduledRunEntry({
    type: 'cache_snapshot',
    id: NOTIFICATION_IDS.CACHE_SNAPSHOT,
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_SNAPSHOT,
    serviceKey: 'cacheSnapshot',
    eventPrefix: 'CacheSnapshot',
    i18nBase: 'signalr.scheduledRun.cacheSnapshot',
    countable: false,
    defaultMessage: 'Starting cache snapshot...',
    staleMessageKey: 'signalr.scheduledRun.cacheSnapshot.complete'
  }),
  buildScheduledRunEntry({
    type: 'operation_history_cleanup',
    id: NOTIFICATION_IDS.OPERATION_HISTORY_CLEANUP,
    storageKey: NOTIFICATION_STORAGE_KEYS.OPERATION_HISTORY_CLEANUP,
    serviceKey: 'operationHistoryCleanup',
    eventPrefix: 'OperationHistoryCleanup',
    i18nBase: 'signalr.scheduledRun.operationHistoryCleanup',
    countable: true,
    defaultMessage: 'Starting operation history cleanup...',
    staleMessageKey: 'signalr.scheduledRun.operationHistoryCleanup.complete'
  }),
  buildScheduledRunEntry({
    type: 'performance_optimization',
    id: NOTIFICATION_IDS.PERFORMANCE_OPTIMIZATION,
    storageKey: NOTIFICATION_STORAGE_KEYS.PERFORMANCE_OPTIMIZATION,
    serviceKey: 'performanceOptimization',
    eventPrefix: 'PerformanceOptimization',
    i18nBase: 'signalr.scheduledRun.performanceOptimization',
    countable: false,
    defaultMessage: 'Starting performance optimization...',
    staleMessageKey: 'signalr.scheduledRun.performanceOptimization.complete'
  }),
  buildScheduledRunEntry({
    type: 'dashboard_cache_warmer',
    id: NOTIFICATION_IDS.DASHBOARD_CACHE_WARMER,
    storageKey: NOTIFICATION_STORAGE_KEYS.DASHBOARD_CACHE_WARMER,
    serviceKey: 'dashboardCacheWarmer',
    eventPrefix: 'DashboardCacheWarmer',
    i18nBase: 'signalr.scheduledRun.dashboardCacheWarmer',
    countable: true,
    defaultMessage: 'Warming dashboard cache...',
    staleMessageKey: 'signalr.scheduledRun.dashboardCacheWarmer.complete'
  }),

  // ==========================================================================
  // Scheduled mapping operations
  // ==========================================================================
  buildMappingOperationEntry<
    DepotMappingStartedEvent,
    DepotMappingProgressEvent,
    DepotMappingCompleteEvent
  >({
    type: 'depot_mapping',
    id: NOTIFICATION_IDS.DEPOT_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
    serviceKey: 'depotMapping',
    eventPrefix: 'DepotMapping',
    i18nBase: 'signalr.depotMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.depotMapping,
    defaultMessage: 'Starting depot mapping...',
    staleMessageKey: 'signalr.depotMapping.stale',
    recoveryCases: [
      { stageKey: 'signalr.depotMapping.starting', context: {} },
      {
        stageKey: 'signalr.depotMapping.batchProgress',
        context: { processedBatches: 1, totalBatches: 2 }
      },
      { stageKey: 'signalr.depotMapping.saving', context: {} },
      { stageKey: 'signalr.depotMapping.resolvingOrphans', context: {} },
      { stageKey: 'signalr.depotMapping.importing', context: {} },
      { stageKey: 'signalr.depotMapping.applyingToDownloads', context: {} },
      { stageKey: 'signalr.depotMapping.finalized', context: { updated: 1 } },
      { stageKey: 'signalr.depotMapping.cancelled', context: {} },
      { stageKey: 'signalr.depotMapping.failed', context: {} },
      { stageKey: 'signalr.depotMapping.github.downloading', context: {} },
      { stageKey: 'signalr.depotMapping.github.complete', context: {} },
      { stageKey: 'signalr.depotMapping.github.failed', context: {} }
    ]
  }),
  buildMappingOperationEntry<
    EpicMappingStartedEvent,
    EpicMappingProgressEvent,
    EpicMappingCompleteEvent
  >({
    type: 'epic_game_mapping',
    id: NOTIFICATION_IDS.EPIC_GAME_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.EPIC_GAME_MAPPING,
    serviceKey: 'epicMapping',
    eventPrefix: 'EpicMapping',
    i18nBase: 'signalr.epicMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.epicGameMapping,
    defaultMessage: 'Starting Epic game mapping...',
    staleMessageKey: 'signalr.epicMapping.stale',
    recoveryCases: [
      { stageKey: 'signalr.epicMapping.starting', context: {} },
      { stageKey: 'signalr.epicMapping.fetchingGames', context: {} },
      { stageKey: 'signalr.epicMapping.refreshingCdn', context: {} },
      { stageKey: 'signalr.epicMapping.checkingFreeGames', context: {} },
      { stageKey: 'signalr.epicMapping.applyingMappings', context: {} },
      { stageKey: 'signalr.epicMapping.completed', context: {} },
      { stageKey: 'signalr.epicMapping.skippedNotSignedIn', context: {} },
      { stageKey: 'signalr.epicMapping.cancelled', context: {} },
      { stageKey: 'signalr.epicMapping.failed', context: {} }
    ]
  }),
  buildMappingOperationEntry<
    XboxMappingStartedEvent,
    XboxMappingProgressEvent,
    XboxMappingCompleteEvent
  >({
    type: 'xbox_game_mapping',
    id: NOTIFICATION_IDS.XBOX_GAME_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.XBOX_GAME_MAPPING,
    serviceKey: 'xboxMapping',
    eventPrefix: 'XboxMapping',
    i18nBase: 'signalr.xboxMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.xboxGameMapping,
    defaultMessage: 'Starting Xbox game mapping...',
    staleMessageKey: 'signalr.xboxMapping.stale',
    recoveryCases: [
      { stageKey: 'signalr.xboxMapping.starting', context: {} },
      { stageKey: 'signalr.xboxMapping.collecting', context: {} },
      { stageKey: 'signalr.xboxMapping.resolving', context: {} },
      { stageKey: 'signalr.xboxMapping.backfilling', context: {} },
      { stageKey: 'signalr.xboxMapping.completed', context: {} },
      { stageKey: 'signalr.xboxMapping.skippedNotSignedIn', context: {} },
      { stageKey: 'signalr.xboxMapping.cancelled', context: {} },
      { stageKey: 'signalr.xboxMapping.failed', context: {} }
    ]
  }),
  buildMappingOperationEntry<
    BattleNetMappingStartedEvent,
    BattleNetMappingProgressEvent,
    BattleNetMappingCompleteEvent
  >({
    type: 'battle_net_game_mapping',
    id: NOTIFICATION_IDS.BATTLE_NET_GAME_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.BATTLE_NET_GAME_MAPPING,
    serviceKey: 'battleNetMapping',
    eventPrefix: 'BattleNetMapping',
    i18nBase: 'signalr.battleNetMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.battleNetGameMapping,
    defaultMessage: 'Starting Battle.net game mapping...',
    staleMessageKey: 'signalr.battleNetMapping.completed',
    recoveryCases: [
      { stageKey: 'signalr.battleNetMapping.starting', context: {} },
      { stageKey: 'signalr.battleNetMapping.resolving', context: {} },
      { stageKey: 'signalr.battleNetMapping.saving', context: {} },
      { stageKey: 'signalr.battleNetMapping.completed', context: {} },
      { stageKey: 'signalr.battleNetMapping.skippedNothingResolved', context: {} },
      { stageKey: 'signalr.battleNetMapping.cancelled', context: {} },
      { stageKey: 'signalr.battleNetMapping.failed', context: {} }
    ]
  }),
  buildMappingOperationEntry<
    RiotMappingStartedEvent,
    RiotMappingProgressEvent,
    RiotMappingCompleteEvent
  >({
    type: 'riot_game_mapping',
    id: NOTIFICATION_IDS.RIOT_GAME_MAPPING,
    storageKey: NOTIFICATION_STORAGE_KEYS.RIOT_GAME_MAPPING,
    serviceKey: 'riotMapping',
    eventPrefix: 'RiotMapping',
    i18nBase: 'signalr.riotMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.riotGameMapping,
    defaultMessage: 'Starting Riot game mapping...',
    staleMessageKey: 'signalr.riotMapping.completed',
    recoveryCases: [
      { stageKey: 'signalr.riotMapping.starting', context: {} },
      { stageKey: 'signalr.riotMapping.resolving', context: {} },
      { stageKey: 'signalr.riotMapping.completed', context: {} },
      { stageKey: 'signalr.riotMapping.skippedNothingResolved', context: {} },
      { stageKey: 'signalr.riotMapping.cancelled', context: {} },
      { stageKey: 'signalr.riotMapping.failed', context: {} }
    ]
  }),

  // ========== Database Reset ==========
  buildStandardOperationEntry<
    DatabaseResetStartedEvent,
    DatabaseResetProgressEvent,
    DatabaseResetCompleteEvent
  >({
    type: 'database_reset',
    id: NOTIFICATION_IDS.DATABASE_RESET,
    storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
    eventPrefix: 'DatabaseReset',
    cancelTooltipKey: CANCEL_TOOLTIP.databaseReset,
    // The reset is already marked as processing before its operationId is registered, so a page
    // load in that window recovers a running card with no id (see createNotification below). The
    // id then arrives on a progress tick, which is exactly what a deferred cancel needs, so the
    // card keeps its X through that window.
    allowsDeferredCancel: true,
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: 'signalr.dbReset.starting', context: {} },
          { stageKey: 'signalr.dbReset.startingTables', context: { count: 2 } },
          {
            stageKey: 'signalr.dbReset.clearingLogEntries',
            context: { deleted: 10, total: 100, percent: 10 }
          },
          { stageKey: 'signalr.dbReset.clearedLogEntries', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedDownloads', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedDepotMappings', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedGameDetections', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedUserPreferences', context: { count: 10 } },
          { stageKey: 'signalr.dbReset.clearedUserSessions', context: { count: 10 } },
          {
            stageKey: 'signalr.dbReset.clearedTable',
            context: { tableName: 'Events', count: 10 }
          },
          { stageKey: 'signalr.dbReset.cleanup', context: {} },
          { stageKey: 'signalr.dbReset.failed', context: { errorDetail: 'error' } }
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
      staleMessageKey: 'signalr.dbReset.stale'
    } satisfies SimpleRecoveryConfig<DatabaseResetStatusResponse>,
    started: {
      defaultMessage: 'Starting database reset...',
      getMessage: stageKeyMessage('signalr.dbReset.starting')
    },
    progress: {
      getMessage: formatDatabaseResetProgressMessage,
      getProgress: (event: DatabaseResetProgressEvent) => event.percentComplete || 0,
      getCompletedMessage: formatDatabaseResetCompleteMessage,
      getErrorMessage: (event: DatabaseResetProgressEvent) =>
        event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : i18n.t(GENERIC_FAILURE_I18N_KEY),
      supportFastCompletion: true
    },
    // The terminal DatabaseResetComplete event is idempotent with the legacy
    // progress-status completion: whichever arrives first wins and the other is a
    // no-op, because the completion handler only acts on a still-running card.
    complete: {
      getSuccessMessage: formatDatabaseResetCompleteMessage,
      getSuccessDetails: operationIdDetails,
      getFailureMessage: (event: DatabaseResetCompleteEvent) =>
        event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : i18n.t(GENERIC_FAILURE_I18N_KEY),
      getCancelledMessage: (event: DatabaseResetCompleteEvent) =>
        event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : i18n.t('signalr.dbReset.cancelled'),
      getCancelledDetails: operationIdDetails
    }
  }),

  // ==========================================================================
  // Completion-only entries
  // --------------------------------------------------------------------------
  // One event carries the whole lifecycle: there is no run to open a card for and
  // no progress to report, so these declare `events.complete` alone and state the
  // outcome their event always means. They persist nothing and recover nothing -
  // an announcement that was missed while the tab was closed is simply gone.
  // ==========================================================================

  // ========== Epic catalog update (one-shot toast) ==========
  {
    type: 'epic_catalog_update',
    id: NOTIFICATION_IDS.EPIC_GAME_MAPPING_UPDATE,
    storageKey: '',
    cancelKind: 'none',
    recovery: { kind: 'none' },
    events: { complete: 'EpicGameMappingsUpdated' },
    complete: {
      succeeded: true,
      shouldDisplay: (event: EpicGameMappingsUpdatedEvent) =>
        Boolean(event.newGames || event.updatedGames),
      getSuccessMessage: () => i18n.t('notifications.epicGameMappingsUpdated.title'),
      getDetailMessage: formatEpicGameMappingsUpdatedMessage
    }
  },

  // ========== Xbox catalog update (one-shot toast) ==========
  {
    type: 'xbox_catalog_update',
    id: NOTIFICATION_IDS.XBOX_GAME_MAPPING_UPDATE,
    storageKey: '',
    cancelKind: 'none',
    recovery: { kind: 'none' },
    events: { complete: 'XboxGameMappingsUpdated' },
    complete: {
      succeeded: true,
      // The same event announces a single download's game being resolved, carrying neither count.
      // Without this gate that emission renders a card reporting nothing was added. [13]
      shouldDisplay: (event: XboxGameMappingsUpdatedEvent) =>
        Boolean(event.newMappings || event.newPatterns),
      getSuccessMessage: () => i18n.t('notifications.xboxGameMappingsUpdated.title'),
      getDetailMessage: formatXboxGameMappingsUpdatedMessage
    }
  },

  // ========== Steam session error (one-shot error toast) ==========
  // Both lines come off the event: the title from the key the emitter mapped from its error type,
  // the detail from the event's own stage key, the same reading every operation card uses. Keeping
  // the error-type mapping on the emitter leaves one copy of it rather than two that can disagree.
  // The card stays twice as long as the shared default because a dropped Steam session is
  // something a person has to act on. [33]
  {
    type: 'steam_session_error',
    id: NOTIFICATION_IDS.STEAM_SESSION_ERROR,
    storageKey: '',
    cancelKind: 'none',
    recovery: { kind: 'none' },
    events: { complete: 'SteamSessionError' },
    complete: {
      succeeded: false,
      dismissDelayMs: STEAM_ERROR_DISMISS_DELAY_MS,
      getFailureMessage: (event: SteamSessionErrorEvent) =>
        i18n.t(event.titleStageKey ?? 'signalr.steamSession.errorTitle.generic'),
      getDetailMessage: (event: SteamSessionErrorEvent) =>
        event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : i18n.t('signalr.steamSession.disconnected', {
              result: event.result ?? i18n.t('common.unknown')
            })
    }
  },

  // ==========================================================================
  // Metadata-only entries
  // --------------------------------------------------------------------------
  // These types declare no lifecycle events, so the handler loop skips them.
  // Their cards are created by client code; the entries appear here ONLY to keep
  // cancel + recovery configured in one place per type.
  // ==========================================================================

  // ========== Bulk Removal (client-driven queue, no server op) ==========
  // Metadata-only entry: the bulk_removal notification is created/managed by the
  // always-mounted BulkRemovalProvider's useBatchQueue, NOT by the standard
  // SignalR loop. It appears here ONLY so UniversalNotificationBar's cancel-config
  // loop is the single source for cancel wiring (cancelKind 'clientQueue' → the X
  // button flips a flag the provider's cascade effect observes). No SignalR events,
  // no recovery (the run loop survives in-app tab switches by construction).
  {
    type: 'bulk_removal',
    id: 'bulk_removal',
    storageKey: '',
    cancelKind: 'clientQueue',
    cancelTooltipKey: CANCEL_TOOLTIP.bulkRemoval,
    recovery: { kind: 'none' }
  },

  // ========== Prefill Login (card raised by the login hooks) ==========
  // Metadata-only entry, same reason as bulk_removal above: the card is created by
  // usePrefillSteamAuth / usePersistentXboxAuth while a daemon sign-in waits on the
  // person, and this entry exists so UniversalNotificationBar's cancel-config loop is
  // the single source for its cancel wiring. cancelKind 'serverOp' → the X posts to
  // /api/operations/{id}/cancel using details.operationId, which the login challenge
  // carries; a card without one simply shows no X. No SignalR events (the hooks own
  // the card's whole life) and no recovery (a reload ends the browser side of the
  // sign-in, and the server's own sweep ends the daemon side).
  {
    type: 'prefill_login',
    id: 'prefill_login',
    storageKey: '',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.prefillLogin,
    recovery: { kind: 'none' }
  }
];
