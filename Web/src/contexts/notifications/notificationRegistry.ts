/**
 * Declarative notification registry.
 * Each entry describes the full lifecycle (started -> progress -> complete) of a standard
 * notification type, including the SignalR event names and handler configurations.
 *
 * Types NOT in this registry (they stay manual in NotificationsContext.tsx):
 *   - depot_mapping: special completion handler with animation/cancellation logic
 *   - database_reset: no separate complete event (uses status-aware progress only)
 *   - epic_game_mapping: progress-only with custom EpicGameMappingsUpdated handler
 *   - SteamSessionError: custom one-shot error notification
 *   - EpicGameMappingsUpdated: custom one-shot notification
 */

import type { NotificationRegistryEntry } from './types';
import { NOTIFICATION_IDS, NOTIFICATION_STORAGE_KEYS } from './constants';
import i18n from '@/i18n';
import {
  formatLogProcessingMessage,
  formatLogProcessingCompletionMessage,
  formatLogProcessingDetailMessage,
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
  formatDataImportFailureMessage
} from './detailMessageFormatters';

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
  EvictionScanProgressEvent,
  EvictionScanCompleteEvent,
  EvictionRemovalStartedEvent,
  EvictionRemovalProgressEvent,
  EvictionRemovalCompleteEvent
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

export const NOTIFICATION_REGISTRY: NotificationRegistryEntry[] = [
  // ========== Log Processing ==========
  {
    type: 'log_processing',
    id: NOTIFICATION_IDS.LOG_PROCESSING,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING,
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
      getProgress: (event: ProcessingProgressEvent) => Math.min(99.9, event.percentComplete),
      getStatus: (event: ProcessingProgressEvent) =>
        event.status?.toLowerCase() === 'completed' ? 'completed' : undefined,
      getCompletedMessage: (event: ProcessingProgressEvent) =>
        formatLogProcessingCompletionMessage(event.entriesSaved),
      getDetails: (event: ProcessingProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: () => 'Log processing completed',
      getDetailMessage: (event: LogProcessingCompleteEvent) =>
        formatLogProcessingDetailMessage(
          event.entriesProcessed,
          event.linesProcessed,
          event.elapsed
        ),
      supportFastCompletion: true,
      getFastCompletionId: () => NOTIFICATION_IDS.LOG_PROCESSING
    }
  },

  // ========== Log Removal ==========
  {
    type: 'log_removal',
    id: NOTIFICATION_IDS.LOG_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
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
        i18n.t(event.stageKey ?? 'signalr.generic.complete', event.context ?? {}),
      getErrorMessage: (event: LogRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.generic.failed', event.context ?? {}),
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
    events: {
      started: 'GameRemovalStarted',
      progress: 'GameRemovalProgress',
      complete: 'GameRemovalComplete'
    },
    started: {
      defaultMessage: 'Starting game removal...',
      // Post-Phase-2 contract: GameRemovalStartedEvent carries a required i18n stageKey
      // (replaced free-text `message`) and scope-aware identity (`gameAppId` for Steam,
      // `epicAppId` for Epic — exactly one is non-null). Mirrors the eviction_removal
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
      // GameRemovalProgress has no `status` field (dropped with the phase-label cleanup —
      // it never carried OperationStatus values anyway). Lifecycle transitions arrive via
      // the separate GameRemovalComplete event, so progress stays in `running` until then.
      getStatus: () => undefined,
      getCompletedMessage: (event: GameRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.gameRemove.complete', event.context ?? {}),
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
      // See GameRemovalProgress — no `status` on this event either.
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
        service: event.service
      })
    },
    progress: {
      getMessage: (event: CorruptionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionRemove.scanningFiles', event.context ?? {}),
      getProgress: (event: CorruptionRemovalProgressEvent) => event.percentComplete,
      getStatus: (event: CorruptionRemovalProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: CorruptionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionRemove.success', event.context ?? {}),
      getErrorMessage: (event: CorruptionRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionRemove.failed.generic', event.context ?? {}),
      getDetails: (event: CorruptionRemovalProgressEvent) => ({
        operationId: event.operationId,
        service: event.service
      })
    },
    complete: {
      getSuccessMessage: (event: CorruptionRemovalCompleteEvent) =>
        formatCorruptionRemovalCompleteMessage(event),
      getSuccessDetails: (event: CorruptionRemovalCompleteEvent) => ({ service: event.service }),
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
        i18n.t(event.stageKey ?? 'signalr.gameDetect.complete.default', event.context ?? {}),
      getErrorMessage: (event: GameDetectionProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.generic.failed', event.context ?? {}),
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
        formatGameDetectionFailureMessage(event),
      supportFastCompletion: true,
      getFastCompletionId: () => NOTIFICATION_IDS.GAME_DETECTION
    }
  },

  // ========== Corruption Detection ==========
  {
    type: 'corruption_detection',
    id: NOTIFICATION_IDS.CORRUPTION_DETECTION,
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
    events: {
      started: 'CorruptionDetectionStarted',
      progress: 'CorruptionDetectionProgress',
      complete: 'CorruptionDetectionComplete'
    },
    started: {
      defaultMessage: 'Scanning for corrupted cache chunks...',
      getMessage: (event: CorruptionDetectionStartedEvent) =>
        formatCorruptionDetectionStartedMessage(event),
      getDetails: (event: CorruptionDetectionStartedEvent) => ({
        operationId: event.operationId
      })
    },
    progress: {
      getMessage: (event: CorruptionDetectionProgressEvent) =>
        formatCorruptionDetectionProgressMessage(event),
      getProgress: (event: CorruptionDetectionProgressEvent) => event.percentComplete,
      getStatus: (event: CorruptionDetectionProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: CorruptionDetectionProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionDetect.complete', event.context ?? {}),
      getErrorMessage: (event: CorruptionDetectionProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.corruptionDetect.failed', event.context ?? {}),
      getDetails: (event: CorruptionDetectionProgressEvent) => ({
        operationId: event.operationId
      })
    },
    complete: {
      getSuccessMessage: (event: CorruptionDetectionCompleteEvent) =>
        formatCorruptionDetectionCompleteMessage(event),
      getFailureMessage: (event: CorruptionDetectionCompleteEvent) =>
        formatCorruptionDetectionFailureMessage(event),
      supportFastCompletion: true,
      getFastCompletionId: () => NOTIFICATION_IDS.CORRUPTION_DETECTION
    }
  },

  // ========== Cache Clearing ==========
  {
    type: 'cache_clearing',
    id: NOTIFICATION_IDS.CACHE_CLEARING,
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
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
          : (event.statusMessage ?? i18n.t('signalr.generic.complete')),
      getErrorMessage: (event: CacheClearProgressEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        event.statusMessage ??
        i18n.t('signalr.generic.failed'),
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
        i18n.t(event.stageKey ?? 'signalr.generic.complete', event.context ?? {}),
      getErrorMessage: (event: DataImportProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.generic.failed', event.context ?? {}),
      getDetails: (event: DataImportProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: DataImportCompleteEvent) => formatDataImportCompleteMessage(event),
      getSuccessDetails: (event: DataImportCompleteEvent, existing) => ({
        ...existing?.details,
        recordsImported: event.recordsImported,
        recordsSkipped: event.recordsSkipped,
        recordsErrors: event.recordsErrors,
        totalRecords: event.totalRecords
      }),
      getFailureMessage: (event: DataImportCompleteEvent) => formatDataImportFailureMessage(event),
      supportFastCompletion: true,
      getFastCompletionId: () => NOTIFICATION_IDS.DATA_IMPORT
    }
  },

  // ========== Eviction Scan ==========
  {
    type: 'eviction_scan',
    id: NOTIFICATION_IDS.EVICTION_SCAN,
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN,
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
      getProgress: (event: EvictionScanProgressEvent) => event.percentComplete,
      getStatus: (event: EvictionScanProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: EvictionScanProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionScan.complete', event.context ?? {}),
      getErrorMessage: (event: EvictionScanProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.generic.failed', event.context ?? {}),
      getDetails: (event: EvictionScanProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: EvictionScanCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionScan.complete', event.context ?? {}),
      getFailureMessage: (event: EvictionScanCompleteEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t('signalr.generic.failed'),
      supportFastCompletion: true,
      getFastCompletionId: () => NOTIFICATION_IDS.EVICTION_SCAN
    }
  },

  // ========== Eviction Removal ==========
  {
    type: 'eviction_removal',
    id: NOTIFICATION_IDS.EVICTION_REMOVAL,
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_REMOVAL,
    events: {
      started: 'EvictionRemovalStarted',
      progress: 'EvictionRemovalProgress',
      complete: 'EvictionRemovalComplete'
    },
    started: {
      defaultMessage: 'Removing evicted game data...',
      getMessage: (event: EvictionRemovalStartedEvent) =>
        event.gameName
          ? i18n.t('management.gameDetection.removingGame', { name: event.gameName })
          : i18n.t(event.stageKey ?? 'signalr.evictionRemove.starting.bulk', event.context ?? {}),
      // Scope → identifier-field mapping for eviction_removal (T8.3 load-bearing comment):
      //
      // eviction_removal has a 4-way identifier union depending on scope:
      //   steam   → details.gameAppId: number (Number(event.gameAppId)), details.steamAppId: string (raw)
      //             IMPORTANT: SignalR event's gameAppId arrives as STRING — must Number() before storing
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
      getDetails: (event: EvictionRemovalProgressEvent) => ({ operationId: event.operationId })
    },
    complete: {
      getSuccessMessage: (event: EvictionRemovalCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.complete', event.context ?? {}),
      getCancelledMessage: (event: EvictionRemovalCompleteEvent) =>
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.cancelled', event.context ?? {}),
      getCancelledDetails: (event: EvictionRemovalCompleteEvent) => ({
        operationId: event.operationId
      }),
      getFailureMessage: (event: EvictionRemovalCompleteEvent) =>
        event.error ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t('signalr.evictionRemove.failed'),
      supportFastCompletion: true,
      getFastCompletionId: () => NOTIFICATION_IDS.EVICTION_REMOVAL
    },
    onComplete: (removeNotification) => {
      removeNotification(NOTIFICATION_IDS.EVICTION_SCAN);
      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN);
    }
  }
];
