/**
 * Declarative notification registry.
 * Each entry describes what a notification type's per-type SignalR events say
 * (started -> progress -> complete): the event names, the getters that turn an event
 * into a card's text, detail line and percent, the cancel wiring (cancelKind +
 * tooltip), and the detail recovery wiring. A run card itself opens and ends on the
 * server's run rows, never on these events.
 *
 * An entry that declares `events` is subscribed by the {@link useNotificationHandlers}
 * loop, phase by phase, so an announcement whose single event is already terminal
 * declares `events.complete` alone. An entry that declares none is metadata-only: its
 * card is created by client code, and it appears here ONLY so cancel + recovery live
 * in one config surface per type.
 */

import type { NotificationRegistryEntry, SimpleRecoveryConfig, UnifiedNotification } from './types';
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
  REMOVING_GAME_I18N_KEY
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
  detectionErrorDetail,
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
  skippedOrStageKeyMessage,
  stageKeyMessage
} from './registryEntries';
import {
  hasUnresolvedInterpolation,
  translateRecoveryStage,
  translateStageKeyMessage
} from '@utils/stageKeyMessage';
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
  DatabaseResetStartedEvent,
  DatabaseResetProgressEvent,
  DatabaseResetCompleteEvent,
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
// Scheduled prefill: one card per platform run
// ============================================================================

/** The platform's display name, as the Schedules page spells it. */
function scheduledPrefillServiceLabel(serviceId: string, scheduleName?: string | null): string {
  const serviceKey = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[serviceId] ?? serviceId;
  const service = i18n.t(
    `management.schedules.services.scheduledPrefill.config.services.${serviceKey}`
  );
  return scheduleName
    ? i18n.t('management.schedules.services.scheduledPrefill.events.schedule', {
        service,
        name: scheduleName
      })
    : service;
}

function scheduledPrefillDetails(
  event: Pick<
    ScheduledPrefillCompletedEvent,
    | 'operationId'
    | 'serviceId'
    | 'scheduleId'
    | 'scheduleName'
    | 'runOperationId'
    | 'eventEpoch'
    | 'eventSequence'
    | 'daemonInstanceId'
  > & { stage?: string; recovering?: boolean }
): UnifiedNotification['details'] {
  return {
    ...(event.eventEpoch != null ? { eventEpoch: event.eventEpoch } : {}),
    ...(event.eventSequence != null ? { eventSequence: event.eventSequence } : {}),
    ...(event.daemonInstanceId != null ? { daemonInstanceId: event.daemonInstanceId } : {}),
    ...(event.stage != null
      ? { stage: event.stage, recovering: event.recovering ?? event.stage === 'recovering' }
      : {}),
    ...(event.operationId != null ? { operationId: event.operationId } : {}),
    ...(event.serviceId != null ? { service: event.serviceId } : {}),
    ...(event.scheduleId != null ? { scheduleId: event.scheduleId } : {}),
    ...(event.scheduleName != null ? { scheduleName: event.scheduleName } : {}),
    ...(event.runOperationId != null ? { runOperationId: event.runOperationId } : {})
  };
}

/**
 * The one sentence a service's card shows while it runs. The progress event and the run-status
 * response both describe a service the same way, so both compose their line here.
 *
 * The backend names the sentence with `stageKey` and sends the English it composed as `message` /
 * `needsLoginReason`. A daemon's own text arrives with no key and passes through as it did before.
 */
/**
 * Translate a scheduled-prefill stage key, falling back to the English sentence sent beside it when
 * the key's placeholders cannot be filled.
 *
 * Two producers send the key WITHOUT the values it interpolates: the run-status endpoint recovery
 * rebuilds a card from (`ScheduledPrefillRunServiceStatus` carries Message and StageKey but no
 * context) and the per-service terminal event (`ScheduledPrefillCompleted` likewise). Translating
 * `signalr.scheduledPrefill.runningWithCounts` without them put a literal
 * "Prefill in progress ({{completed}} of {{total}} games)" on the card. The English beside the key
 * is that same line already filled in, so it is what the reader gets instead. [35]
 */
function scheduledPrefillSentence(
  stageKey: string | null | undefined,
  stageContext: Record<string, string | number | boolean | null> | null | undefined,
  english: string | null | undefined,
  fallbackKey?: string
): string {
  const translated = translateStageKeyMessage(
    stageKey ?? english,
    stageContext ?? undefined,
    fallbackKey
  );
  if (!hasUnresolvedInterpolation(translated)) {
    return translated;
  }

  return translateStageKeyMessage(english, undefined, fallbackKey);
}

function scheduledPrefillServiceMessage(service: {
  serviceId: string;
  scheduleName?: string | null;
  stage: string;
  message?: string | null;
  stageKey?: string | null;
  stageContext?: Record<string, string | number | boolean | null> | null;
  needsLoginReason?: string | null;
}): string {
  const serviceLabel = scheduledPrefillServiceLabel(service.serviceId, service.scheduleName);
  const sentence = (english: string | null | undefined): string =>
    scheduledPrefillSentence(service.stageKey, service.stageContext, english);

  if (service.stage === 'skipped') {
    return i18n.t('management.schedules.services.scheduledPrefill.events.skipped', {
      service: serviceLabel,
      reason: sentence(service.message)
    });
  }

  if (service.stage === 'needs-login') {
    // Keep the backend's precise prerequisite (container stopped vs running-but-logged-out)
    // instead of collapsing every needs-login skip into the same generic line.
    return service.needsLoginReason
      ? i18n.t('management.schedules.services.scheduledPrefill.events.needsLoginWithReason', {
          service: serviceLabel,
          reason: sentence(service.needsLoginReason)
        })
      : i18n.t('management.schedules.services.scheduledPrefill.events.needsLogin', {
          service: serviceLabel
        });
  }

  return i18n.t('management.schedules.services.scheduledPrefill.events.serviceProgress', {
    service: serviceLabel,
    message: sentence(service.message)
  });
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
    eventPrefix: 'LogProcessing',
    cancelTooltipKey: CANCEL_TOOLTIP.logProcessing,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/logs/process/status',
      isProcessing: (data: LogProcessingStatusResponse) => data.isProcessing,
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
      })
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
        event.status.toLowerCase() === 'completed' ? 'completed' : undefined,
      getCompletedMessage: (event: ProcessingProgressEvent) =>
        formatLogProcessingCompletionMessage(event.entriesSaved)
    },
    complete: {
      // Translated, not a hardcoded English literal: a literal here would switch a localized card
      // to English at the moment it finishes.
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
      })
    } satisfies SimpleRecoveryConfig<LogRemovalStatusResponse>,
    started: {
      defaultMessage: 'Starting log removal...',
      getMessage: stageKeyMessage('signalr.logRemoval.starting.default'),
      // The service drives the Log Removal row's busy spinner. The Started event carries it only
      // inside its stage context (RustLogRemovalService sends no top-level Service on this event).
      getDetails: (event: LogRemovalStartedEvent) => ({
        operationId: event.operationId ?? undefined,
        service: event.context?.service as string | undefined
      })
    },
    progress: {
      getMessage: (event: LogRemovalProgressEvent) => formatLogRemovalProgressMessage(event),
      getProgress: (event: LogRemovalProgressEvent) => event.percentComplete,
      getDetails: (event: LogRemovalProgressEvent) => ({
        ...operationIdDetails(event),
        service: event.service
      }),
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
      })
    }
  }),

  // ========== Game Removal ==========
  buildStandardOperationEntry<
    GameRemovalStartedEvent,
    GameRemovalProgressEvent,
    GameRemovalCompleteEvent
  >({
    type: 'game_removal',
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
          ...(event.service != null && { service: event.service }),
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
        ...(event.epicAppId !== null && { epicAppId: event.epicAppId }),
        ...(event.service != null && { service: event.service })
      })
    },
    complete: {
      getSuccessDetails: (event: GameRemovalCompleteEvent, existing) => ({
        ...existing?.details,
        // Scope identity from the event, so a card first seen at its end (after a reload) still
        // names its game. gameAppId/epicAppId are scope-exclusive (exactly one non-null).
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
    eventPrefix: 'ServiceRemoval',
    cancelTooltipKey: CANCEL_TOOLTIP.serviceRemoval,
    recovery: { kind: 'cacheRemovalsBatch' },
    started: {
      defaultMessage: 'Starting service removal...',
      getMessage: stageKeyMessage('signalr.serviceRemove.starting.default'),
      // The service is what keeps that service's remove button busy while the run is live.
      getDetails: (event: ServiceRemovalStartedEvent) => ({
        ...operationIdDetails(event),
        service: event.serviceName
      })
    },
    progress: {
      getMessage: (event: ServiceRemovalProgressEvent) =>
        formatServiceRemovalProgressMessage(event),
      getProgress: (event: ServiceRemovalProgressEvent) => event.percentComplete,
      getDetails: (event: ServiceRemovalProgressEvent) => ({
        ...operationIdDetails(event),
        service: event.serviceName
      }),
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
        // Service identity from the event, merged after the card's own details (event values
        // win, which is fine - they describe the same completed op).
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
      })
    }
  }),

  // ========== Game Detection ==========
  buildStandardOperationEntry<
    GameDetectionStartedEvent,
    GameDetectionProgressEvent,
    GameDetectionCompleteEvent
  >({
    type: 'game_detection',
    eventPrefix: 'GameDetection',
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
            parentOperationId: op.parentOperationId,
            scanType: op.scanType
          }
        };
      }
    } satisfies SimpleRecoveryConfig<GameDetectionStatusResponse>,
    started: {
      defaultMessage: 'Detecting games and services...',
      getMessage: (event: GameDetectionStartedEvent) => formatGameDetectionStartedMessage(event),
      getDetails: (event: GameDetectionStartedEvent) => ({
        operationId: event.operationId,
        parentOperationId: event.parentOperationId,
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
      // The terminal handler resolves a declined run through the success path too, and that
      // payload carries no counts, so merging it would write undefined over the numbers the
      // card is already showing.
      getSuccessDetails: (event: GameDetectionCompleteEvent, existing) =>
        event.status === 'skipped'
          ? existing?.details
          : {
              ...existing?.details,
              operationId: event.operationId,
              parentOperationId: event.parentOperationId,
              totalGamesDetected: event.totalGamesDetected,
              totalServicesDetected: event.totalServicesDetected
            },
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
      }
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
      }
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
      })
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
    eventPrefix: 'EvictionScan',
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
          { stageKey: 'signalr.evictionScan.refreshingSummary', context: {} }
        ]
      },
      apiEndpoint: '/api/stats/eviction/scan/status',
      isProcessing: (data: EvictionScanStatusResponse) => data.isProcessing,
      createNotification: (data: EvictionScanStatusResponse) => ({
        message: translateRecoveryStage(
          data.stageKey,
          data.context,
          'signalr.evictionScan.scanning'
        ),
        progress: data.percentComplete,
        detailMessage: detectionErrorDetail(data),
        details: {
          operationId: data.operationId ?? undefined
        }
      })
    } satisfies SimpleRecoveryConfig<EvictionScanStatusResponse>,
    started: {
      defaultMessage: 'Starting eviction scan...',
      getMessage: stageKeyMessage('signalr.evictionScan.scanning')
    },
    progress: {
      getMessage: stageKeyMessage('signalr.evictionScan.progress'),
      getDetailMessage: detectionErrorDetail,
      getProgress: cappedProgress,
      getCompletedMessage: stageKeyMessage('signalr.evictionScan.complete'),
      getErrorMessage: stageKeyMessage(GENERIC_FAILURE_I18N_KEY)
    },
    complete: {
      getSuccessMessage: skippedOrStageKeyMessage<EvictionScanCompleteEvent>(
        'signalr.evictionScan.complete'
      ),
      getDetailMessage: detectionErrorDetail,
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
    eventPrefix: 'CacheSizeScan',
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
      })
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
      getSuccessMessage: skippedOrStageKeyMessage<CacheSizeScanCompleteEvent>(
        'signalr.cacheSizeScan.complete'
      ),
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
    // Run-level events name the run-level container, which has no run row, so they reach no card;
    // each platform's run has its own row and its own card.
    eventPrefix: 'ScheduledPrefill',
    // This pipeline's terminal event is `...Completed`, not the `...Complete` the
    // other operations emit.
    completeEvent: 'ScheduledPrefillCompleted',
    cancelTooltipKey: CANCEL_TOOLTIP.scheduledPrefill,
    // The run-status response names each platform's current event series (`eventEpoch`), which is
    // how a card adopts a restarted daemon's events.
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'dedicated' },
      apiEndpoint: '/api/system/schedules/scheduledPrefill/run-status',
      isProcessing: (data: ScheduledPrefillRunStatusResponse) => data.isRunning,
      // One result per service still running, each on its own operation, so a reload mid-run
      // fills in every platform card the run has.
      recoverCards: (data: ScheduledPrefillRunStatusResponse) =>
        data.services.map((service) => ({
          message: scheduledPrefillServiceMessage(service),
          progress: service.percentComplete ?? undefined,
          detailMessage: formatScheduledPrefillDetailMessage({
            ...service,
            message: service.message ?? ''
          }),
          details: scheduledPrefillDetails({ ...service, runOperationId: data.operationId })
        }))
    } satisfies SimpleRecoveryConfig<ScheduledPrefillRunStatusResponse>,
    started: {
      defaultMessage: 'Scheduled prefill started',
      getMessage: (event: ScheduledPrefillStartedEvent) =>
        event.stage && event.message
          ? scheduledPrefillServiceMessage({
              ...event,
              serviceId: event.serviceId ?? '',
              stage: event.stage
            })
          : i18n.t('management.schedules.services.scheduledPrefill.events.started', {
              service: scheduledPrefillServiceLabel(event.serviceId ?? '', event.scheduleName)
            }),
      getDetails: scheduledPrefillDetails
    },
    progress: {
      getDetails: scheduledPrefillDetails,
      getMessage: (event: ScheduledPrefillProgressEvent) => scheduledPrefillServiceMessage(event),
      // Backend-computed run percent. It tracks the ACTIVE service only (games completed plus the
      // byte fraction of the game downloading right now), clamped 1-99 by ComputeRunPercent; 100
      // comes from the terminal Completed event. Unknown totals deliberately leave this undefined
      // so the notification uses its existing indeterminate presentation rather than inventing a
      // denominator.
      //
      // Deliberately NOT rounded: the percent divides the active game's fraction by the number of
      // games, so a big download moves it a fraction of a point at a time. Rounding to a whole
      // number pinned the bar in place and made a working prefill look frozen. The bar and its
      // "x.x%" label both read the fractional value, and getDetailMessage below carries the bytes.
      getProgress: (event: ScheduledPrefillProgressEvent) => event.percentComplete ?? undefined,
      // Bytes of the game currently downloading. The bar alone is not enough on a multi-game run
      // (the run percent divides by the game count, so it crawls); this line moves on every tick of
      // a live download, which is what tells the user it is actually working.
      getDetailMessage: (event: ScheduledPrefillProgressEvent) =>
        formatScheduledPrefillDetailMessage(event),
      // The run's terminal arrives as its own event, so progress never completes the card.
      getStatus: () => undefined
    },
    complete: {
      // A skipped service keeps the line its own progress already put on the card: that sentence
      // names the service and the precise prerequisite, and the terminal has nothing better to say.
      getSuccessMessage: (event: ScheduledPrefillCompletedEvent, existing) =>
        event.stage && event.message && event.stageKey
          ? scheduledPrefillServiceMessage({
              ...event,
              serviceId: event.serviceId ?? '',
              stage: event.stage
            })
          : event.status === 'skipped'
            ? scheduledPrefillServiceMessage({
                serviceId: event.serviceId ?? '',
                scheduleName: event.scheduleName ?? existing?.details?.scheduleName,
                stage: 'skipped',
                message: event.error,
                stageKey: event.stageKey
              })
            : // A finished service's last progress line IS its result sentence, and it is the only
              // place the reason lives: the terminal event sends no stageKey and no error on success,
              // so "everything was already cached (0 bytes)" exists nowhere else. Overwriting it with
              // the bare "<service> completed" left the user unable to tell a successful no-op from a
              // silent failure. Same shape as the skipped arm above. The generic sentence still covers
              // a card that never saw that progress line, such as one rebuilt after a reload. [31]
              (existing?.message ??
              i18n.t('management.schedules.services.scheduledPrefill.events.completed', {
                service: scheduledPrefillServiceLabel(event.serviceId ?? '', event.scheduleName)
              })),
      // A stopped service is its own terminal, not a failure: the user caused it, so it must not
      // read as an error (and must not show its last progress line as the result).
      getCancelledMessage: (event: ScheduledPrefillCompletedEvent) =>
        i18n.t('management.schedules.services.scheduledPrefill.events.cancelled', {
          service: scheduledPrefillServiceLabel(event.serviceId ?? '', event.scheduleName)
        }),
      getSuccessDetails: scheduledPrefillDetails,
      getCancelledDetails: scheduledPrefillDetails,
      // The bytes line is a LIVE counter for the game downloading right now, so a finished card
      // kept showing a number that had stopped moving. The result sentence above already carries
      // the run's total, so the terminal drops the line rather than restating it. [32]
      getDetailMessage: () => undefined,
      getFailureMessage: (event: ScheduledPrefillCompletedEvent) =>
        i18n.t('management.schedules.services.scheduledPrefill.events.failed', {
          service: scheduledPrefillServiceLabel(event.serviceId ?? '', event.scheduleName),
          reason: scheduledPrefillSentence(
            event.stageKey,
            undefined,
            event.error,
            GENERIC_FAILURE_I18N_KEY
          )
        })
    }
  }),

  // ========== Eviction Removal ==========
  buildStandardOperationEntry<
    EvictionRemovalStartedEvent,
    EvictionRemovalProgressEvent,
    EvictionRemovalCompleteEvent
  >({
    type: 'eviction_removal',
    eventPrefix: 'EvictionRemoval',
    cancelTooltipKey: CANCEL_TOOLTIP.evictionRemoval,
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
      //             details.service: 'epicgames', so only the Epic game of that name reads as busy.
      //   named   → details.service: string (= context.key, the lowercase service) + details.gameName
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
          ...((scope === 'service' || scope === 'named') && key !== undefined && { service: key }),
          ...(scope === 'epic' && { service: 'epicgames' })
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
      // event and kept by the run store, which merges each event's details key by key
      // over the card's. A card seen first mid-run gets it from detail recovery
      // (recovery.ts recoverEvictionRemovals). Spelled out rather than left to the
      // builder default so the reasoning above stays attached to the field.
      getDetails: operationIdDetails
    },
    complete: {
      getSuccessMessage: stageKeyMessage('signalr.evictionRemove.complete'),
      getFailureMessage: errorOrStageKeyMessage('signalr.evictionRemove.failed')
    }
  }),

  // ========== Scheduled service runs (standard, built by factory) ==========
  buildScheduledRunEntry({
    type: 'log_rotation',
    cancellable: false,
    serviceKey: 'logRotation',
    eventPrefix: 'LogRotation',
    i18nBase: 'signalr.scheduledRun.logRotation',
    countable: false,
    defaultMessage: 'Starting log rotation...'
  }),
  buildScheduledRunEntry({
    type: 'game_image_fetch',
    cancellable: true,
    serviceKey: 'gameImageFetch',
    eventPrefix: 'GameImageFetch',
    i18nBase: 'signalr.scheduledRun.gameImageFetch',
    countable: true,
    defaultMessage: 'Starting game image fetch...'
  }),
  buildScheduledRunEntry({
    type: 'cache_snapshot',
    cancellable: true,
    serviceKey: 'cacheSnapshot',
    eventPrefix: 'CacheSnapshot',
    i18nBase: 'signalr.scheduledRun.cacheSnapshot',
    countable: false,
    defaultMessage: 'Starting cache snapshot...'
  }),
  buildScheduledRunEntry({
    type: 'operation_history_cleanup',
    cancellable: true,
    serviceKey: 'operationHistoryCleanup',
    eventPrefix: 'OperationHistoryCleanup',
    i18nBase: 'signalr.scheduledRun.operationHistoryCleanup',
    countable: true,
    defaultMessage: 'Starting operation history cleanup...'
  }),
  buildScheduledRunEntry({
    type: 'dashboard_cache_warmer',
    cancellable: true,
    serviceKey: 'dashboardCacheWarmer',
    eventPrefix: 'DashboardCacheWarmer',
    i18nBase: 'signalr.scheduledRun.dashboardCacheWarmer',
    countable: true,
    defaultMessage: 'Warming dashboard cache...'
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
    serviceKey: 'depotMapping',
    eventPrefix: 'DepotMapping',
    i18nBase: 'signalr.depotMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.depotMapping,
    defaultMessage: 'Starting depot mapping...',
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
    serviceKey: 'epicMapping',
    eventPrefix: 'EpicMapping',
    i18nBase: 'signalr.epicMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.epicGameMapping,
    defaultMessage: 'Starting Epic game mapping...',
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
    serviceKey: 'xboxMapping',
    eventPrefix: 'XboxMapping',
    i18nBase: 'signalr.xboxMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.xboxGameMapping,
    defaultMessage: 'Starting Xbox game mapping...',
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
    serviceKey: 'battleNetMapping',
    eventPrefix: 'BattleNetMapping',
    i18nBase: 'signalr.battleNetMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.battleNetGameMapping,
    defaultMessage: 'Starting Battle.net game mapping...',
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
    serviceKey: 'riotMapping',
    eventPrefix: 'RiotMapping',
    i18nBase: 'signalr.riotMapping',
    cancelTooltipKey: CANCEL_TOOLTIP.riotGameMapping,
    defaultMessage: 'Starting Riot game mapping...',
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
    eventPrefix: 'DatabaseReset',
    cancelTooltipKey: CANCEL_TOOLTIP.databaseReset,
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
        // The reset is marked as processing before its operationId is registered, so this can be
        // missing; the result then fills the one live reset run.
        // `?? undefined` normalises null→undefined (backend field is `string?`).
        details: { operationId: data.operationId ?? undefined }
      })
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
          : i18n.t(GENERIC_FAILURE_I18N_KEY)
    },
    // The terminal DatabaseResetComplete event and the legacy progress-status completion
    // both only fill in the finished card's text; the run row decides when the card ends.
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
  // outcome their event always means. They recover nothing - an announcement that
  // was missed while the tab was closed is simply gone.
  // ==========================================================================

  // ========== Epic catalog update (one-shot toast) ==========
  {
    type: 'epic_catalog_update',
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
    cancelKind: 'none',
    recovery: { kind: 'none' },
    events: { complete: 'XboxGameMappingsUpdated' },
    complete: {
      succeeded: true,
      // The same event announces a single download's game being resolved, carrying neither count.
      // Without this gate that emission renders a card reporting nothing was added.
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
  // The card stays until it is closed, like a failed run, because a dropped Steam session is
  // something a person has to act on.
  {
    type: 'steam_session_error',
    cancelKind: 'none',
    recovery: { kind: 'none' },
    events: { complete: 'SteamSessionError' },
    complete: {
      succeeded: false,
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
    cancelKind: 'clientQueue',
    cancelTooltipKey: CANCEL_TOOLTIP.bulkRemoval,
    recovery: { kind: 'none' }
  },

  // ========== Prefill Login (card drawn from the sign-in's run row) ==========
  // Unlike bulk_removal, this card is not created by client code: the server's run row
  // for the sign-in opens and ends it, in the browser whose session started the sign-in
  // only. The entry exists so UniversalNotificationBar's cancel-config loop is the single
  // source for its cancel wiring. cancelKind 'serverOp' → the X posts to
  // /api/operations/{id}/cancel using the row's operation id. No SignalR events (the row
  // carries everything the card says) and no recovery (the run list redraws it after a
  // reload).
  {
    type: 'prefill_login',
    cancelKind: 'serverOp',
    cancelTooltipKey: CANCEL_TOOLTIP.prefillLogin,
    recovery: { kind: 'none' }
  }
];
