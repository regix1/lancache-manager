import type {
  ProcessingProgressEvent,
  DepotMappingProgressEvent,
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
  DepotMappingStartedEvent,
  DataImportStartedEvent,
  DataImportProgressEvent,
  DataImportCompleteEvent,
  EpicMappingProgressEvent,
  EpicGameMappingsUpdatedEvent
} from '../SignalRContext/types';
import i18n from '@/i18n';

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

// ============================================================================
// Depot Mapping
// ============================================================================

/**
 * Formats the detail message for depot mapping recovery.
 * Includes optional mappings found count.
 * @param data - The recovery data from the API
 * @returns Formatted detail message or undefined
 */
export const formatDepotMappingRecoveryDetailMessage = (data: {
  processedBatches?: number;
  totalBatches?: number;
  depotMappingsFound?: number;
}): string | undefined => {
  if (data.processedBatches !== undefined && data.totalBatches !== undefined) {
    if (data.depotMappingsFound !== undefined) {
      return i18n.t('signalr.depotMapping.batchProgressWithMappings', {
        processedBatches: data.processedBatches.toLocaleString(),
        totalBatches: data.totalBatches.toLocaleString(),
        depotMappingsFound: data.depotMappingsFound.toLocaleString()
      });
    }
    return i18n.t('signalr.depotMapping.batchProgress', {
      processedBatches: data.processedBatches.toLocaleString(),
      totalBatches: data.totalBatches.toLocaleString()
    });
  }
  return undefined;
};

// ============================================================================
// Recovery Message Formatters (for recoveryFactory.ts)
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
 * @param entriesProcessed - Entries processed so far
 * @param totalLines - Total lines to process
 * @returns Formatted detail message string
 */
export const formatLogProcessingRecoveryDetailMessage = (
  entriesProcessed?: number,
  totalLines?: number
): string => {
  return i18n.t('signalr.logProcessing.recoveryDetail', {
    entriesProcessed: entriesProcessed?.toLocaleString() || '0',
    totalLines: totalLines?.toLocaleString() || '0'
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
    : i18n.t('signalr.generic.complete');
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
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.gameRemove.starting', {
        gameName: event.gameName,
        gameAppId: event.gameAppId
      });
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
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.corruptionRemove.starting', { service: event.service });
};

/**
 * Formats the success message for corruption removal completion.
 * @param event - The corruption removal complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCorruptionRemovalCompleteMessage = (
  event: CorruptionRemovalCompleteEvent
): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.corruptionRemove.success', { service: event.service });
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
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.gameDetect.scan.inProgress');
};

/**
 * Formats the success message for game detection completion.
 * @param event - The game detection complete event from SignalR
 * @returns Formatted success message string
 */
export const formatGameDetectionCompleteMessage = (event: GameDetectionCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
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
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.generic.failed');
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
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.corruptionDetect.starting');
};

/**
 * Formats the message for corruption detection progress.
 * @param event - The corruption detection progress event from SignalR
 * @returns Formatted message string
 */
export const formatCorruptionDetectionProgressMessage = (
  event: CorruptionDetectionProgressEvent
): string => {
  if (event.stageKey) {
    return i18n.t(event.stageKey, event.context ?? {});
  }
  return i18n.t('signalr.corruptionDetect.scanningLogs');
};

/**
 * Formats the success message for corruption detection completion.
 * @param event - The corruption detection complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCorruptionDetectionCompleteMessage = (
  event: CorruptionDetectionCompleteEvent
): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.corruptionDetect.complete', {
        count: event.totalServicesWithCorruption ?? 0
      });
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
export const formatDatabaseResetCompleteMessage = (event: DatabaseResetProgressEvent): string => {
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
    return `${base} (${event.directoriesProcessed}/${event.totalDirectories} directories)`;
  }

  return base;
};

/**
 * Formats the success message for cache clear completion.
 * @param event - The cache clear complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCacheClearCompleteMessage = (event: CacheClearCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.generic.complete');
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
    i18n.t('signalr.generic.failed')
  );
};

// ============================================================================
// Depot Mapping
// ============================================================================

/**
 * Formats the message for depot mapping started.
 * @param event - The depot mapping started event from SignalR
 * @returns Formatted message string
 */
export const formatDepotMappingStartedMessage = (event: DepotMappingStartedEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.depotMapping.github.downloading');
};

/**
 * Formats the message for depot mapping progress.
 * Falls back to existing notification message if event has no message.
 * @param event - The depot mapping progress event from SignalR
 * @param existingMessage - Optional existing notification message for fallback
 * @returns Formatted message string
 */
export const formatDepotMappingProgressMessage = (
  event: DepotMappingProgressEvent,
  existingMessage?: string
): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : (existingMessage ??
        i18n.t('signalr.depotMapping.applyingToDownloads', { processed: 0, totalDownloads: 0 }));
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
export const formatDataImportCompleteMessage = (event: DataImportCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.generic.complete');
};

/**
 * Formats the failure message for data import.
 * @param event - The data import complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatDataImportFailureMessage = (event: DataImportCompleteEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.generic.failed');
};

// ============================================================================
// Epic Game Mapping
// ============================================================================

/**
 * Formats the progress message for Epic game mapping.
 * @param event - The Epic mapping progress event from SignalR
 * @returns Formatted progress message string
 */
export const formatEpicMappingProgressMessage = (event: EpicMappingProgressEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.epicMapping.starting');
};

/**
 * Formats the completion message for Epic game mapping progress.
 * @param event - The Epic mapping progress event from SignalR
 * @returns Formatted completion message string
 */
export const formatEpicMappingCompleteMessage = (event: EpicMappingProgressEvent): string => {
  return event.stageKey
    ? i18n.t(event.stageKey, event.context ?? {})
    : i18n.t('signalr.epicMapping.gamesDiscovered', { gamesDiscovered: event.gamesDiscovered });
};

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
    parts.push(`${event.newGames} new game${event.newGames !== 1 ? 's' : ''} discovered`);
  }
  if (event.updatedGames > 0) {
    parts.push(`${event.updatedGames} game${event.updatedGames !== 1 ? 's' : ''} updated`);
  }
  const detail = parts.join(', ');
  return detail ? `${detail}, ${event.totalGames} total` : `${event.totalGames} total`;
};
