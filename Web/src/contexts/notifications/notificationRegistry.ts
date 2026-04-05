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
      getProgress: (event: ProcessingProgressEvent) => Math.min(99.9, event.percentComplete || 0),
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
      getProgress: (event: LogRemovalProgressEvent) => event.percentComplete || 0,
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
      getMessage: (event: GameRemovalStartedEvent) =>
        i18n.t(event.stageKey ?? 'signalr.gameRemove.starting', event.context ?? {}),
      getDetails: (event: GameRemovalStartedEvent) => ({
        operationId: event.operationId,
        gameAppId: event.gameAppId,
        gameName: event.gameName
      })
    },
    progress: {
      getMessage: (event: GameRemovalProgressEvent) => formatGameRemovalProgressMessage(event),
      getProgress: (event: GameRemovalProgressEvent) => event.percentComplete || 0,
      getStatus: (event: GameRemovalProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: GameRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.gameRemove.complete', event.context ?? {}),
      getErrorMessage: (event: GameRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.gameRemove.error.fatal', event.context ?? {}),
      getDetails: (event: GameRemovalProgressEvent) => ({
        operationId: event.operationId,
        gameAppId: event.gameAppId,
        gameName: event.gameName
      })
    },
    complete: {
      getSuccessDetails: (event: GameRemovalCompleteEvent, existing) => ({
        ...existing?.details,
        gameAppId: event.gameAppId,
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
      getProgress: (event: ServiceRemovalProgressEvent) => event.percentComplete || 0,
      getStatus: (event: ServiceRemovalProgressEvent) => standardGetStatus(event),
      getCompletedMessage: (event: ServiceRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.serviceRemove.success', {
          name: event.serviceName,
          ...event.context
        }),
      getErrorMessage: (event: ServiceRemovalProgressEvent) =>
        i18n.t(event.stageKey ?? 'signalr.serviceRemove.failed.generic', {
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
      getProgress: (event: CorruptionRemovalProgressEvent) => event.percentComplete || 0,
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
      getProgress: (event: GameDetectionProgressEvent) => event.percentComplete || 0,
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
      getProgress: (event: CorruptionDetectionProgressEvent) => event.percentComplete || 0,
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
      getProgress: (event: CacheClearProgressEvent) => event.percentComplete || 0,
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
      getProgress: (event: DataImportProgressEvent) => event.percentComplete || 0,
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
      getProgress: (event: EvictionScanProgressEvent) => event.percentComplete || 0,
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
        i18n.t(event.stageKey ?? 'signalr.evictionRemove.starting.bulk', event.context ?? {}),
      getDetails: (event: EvictionRemovalStartedEvent) => ({ operationId: event.operationId })
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
