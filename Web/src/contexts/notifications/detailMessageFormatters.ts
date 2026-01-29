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
  GameDetectionCompleteEvent,
  CorruptionDetectionStartedEvent,
  CorruptionDetectionProgressEvent,
  CorruptionDetectionCompleteEvent,
  DatabaseResetProgressEvent,
  CacheClearProgressEvent,
  CacheClearCompleteEvent,
  DepotMappingStartedEvent
} from '../SignalRContext/types';

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
  const mbProcessed = event.mbProcessed?.toFixed(1) || '0';
  const mbTotal = event.mbTotal?.toFixed(1) || '0';
  return `Processing: ${mbProcessed} MB of ${mbTotal} MB`;
};

/**
 * Formats the detail message for log processing progress.
 * Shows entries processed of total entries.
 * @param event - The processing progress event from SignalR
 * @returns Formatted detail message string
 */
export const formatLogProcessingDetailMessage = (event: ProcessingProgressEvent): string => {
  const entriesProcessed = event.entriesProcessed?.toLocaleString() || '0';
  const totalLines = event.totalLines?.toLocaleString() || '0';
  return `${entriesProcessed} of ${totalLines} entries`;
};

/**
 * Formats the completion detail message for log processing.
 * @param entriesProcessed - Number of entries successfully processed
 * @returns Formatted completion message
 */
export const formatLogProcessingCompletionMessage = (entriesProcessed?: number): string => {
  return `Successfully processed ${entriesProcessed?.toLocaleString() || 0} entries`;
};

/**
 * Formats the fast processing completion message.
 * @param event - The fast processing complete event
 * @returns Formatted completion message with timing info
 */
export const formatFastProcessingCompletionMessage = (
  entriesProcessed?: number,
  linesProcessed?: number,
  elapsed?: number
): string => {
  return `Successfully processed ${entriesProcessed?.toLocaleString() || 0} entries from ${linesProcessed?.toLocaleString() || 0} lines in ${elapsed?.toFixed(1) || 0} minutes.`;
};

// ============================================================================
// Depot Mapping
// ============================================================================

/**
 * Formats the detail message for depot mapping progress.
 * Shows either batches progress or mappings/downloads progress.
 * @param event - The depot mapping progress event from SignalR
 * @returns Formatted detail message or undefined if no progress data
 */
export const formatDepotMappingDetailMessage = (
  event: DepotMappingProgressEvent
): string | undefined => {
  if (event.processedBatches !== undefined && event.totalBatches !== undefined) {
    return `${event.processedBatches.toLocaleString()} / ${event.totalBatches.toLocaleString()} batches...`;
  }
  if (event.processedMappings !== undefined && event.totalMappings !== undefined) {
    return `${event.processedMappings.toLocaleString()} / ${event.totalMappings.toLocaleString()} downloads...`;
  }
  return undefined;
};

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
    const batchesStr = `${data.processedBatches.toLocaleString()} / ${data.totalBatches.toLocaleString()} batches`;
    if (data.depotMappingsFound !== undefined) {
      return `${batchesStr} • ${data.depotMappingsFound.toLocaleString()} mappings found`;
    }
    return batchesStr;
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
  const processed = mbProcessed?.toFixed(1) || '0';
  const total = mbTotal?.toFixed(1) || '0';
  return `Processing: ${processed} MB of ${total} MB`;
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
  const entries = entriesProcessed?.toLocaleString() || '0';
  const total = totalLines?.toLocaleString() || '0';
  return `${entries} of ${total} entries`;
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
    return `Removing ${event.service} entries (${linesRemoved.toLocaleString()} removed)...`;
  }
  return event.message || `Removing ${event.service} entries...`;
};

/**
 * Formats the success message for log removal completion.
 * @param event - The log removal complete event from SignalR
 * @returns Formatted success message string
 */
export const formatLogRemovalCompleteMessage = (event: LogRemovalCompleteEvent): string => {
  return event.message || `Successfully removed ${event.service} entries`;
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
  return event.message || `Removing ${event.gameName}...`;
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
  return event.message || `Removing ${event.serviceName} cache...`;
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
  return event.message || `Removing corrupted chunks for ${event.service}...`;
};

/**
 * Formats the success message for corruption removal completion.
 * @param event - The corruption removal complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCorruptionRemovalCompleteMessage = (
  event: CorruptionRemovalCompleteEvent
): string => {
  return event.message || `Successfully removed corrupted chunks for ${event.service}`;
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
  return event.message || 'Detecting games and services in cache...';
};

/**
 * Formats the success message for game detection completion.
 * @param event - The game detection complete event from SignalR
 * @returns Formatted success message string
 */
export const formatGameDetectionCompleteMessage = (event: GameDetectionCompleteEvent): string => {
  return event.message || 'Game detection completed';
};

/**
 * Formats the failure message for game detection.
 * @param event - The game detection complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatGameDetectionFailureMessage = (event: GameDetectionCompleteEvent): string => {
  return event.message || 'Game detection failed';
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
  return event.message || 'Scanning for corrupted cache chunks...';
};

/**
 * Formats the message for corruption detection progress.
 * @param event - The corruption detection progress event from SignalR
 * @returns Formatted message string
 */
export const formatCorruptionDetectionProgressMessage = (
  event: CorruptionDetectionProgressEvent
): string => {
  if (event.message) {
    return event.message;
  }
  if (event.totalFiles && event.filesProcessed !== undefined) {
    return `Scanning file ${event.filesProcessed + 1}/${event.totalFiles}...`;
  }
  return 'Scanning for corrupted cache chunks...';
};

/**
 * Formats the success message for corruption detection completion.
 * @param event - The corruption detection complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCorruptionDetectionCompleteMessage = (
  event: CorruptionDetectionCompleteEvent
): string => {
  return event.message || 'Corruption scan completed';
};

/**
 * Formats the failure message for corruption detection.
 * @param event - The corruption detection complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatCorruptionDetectionFailureMessage = (
  event: CorruptionDetectionCompleteEvent
): string => {
  return event.message || 'Corruption scan failed';
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
  return event.message || 'Resetting database...';
};

/**
 * Formats the completion message for database reset.
 * @param event - The database reset progress event from SignalR
 * @returns Formatted completion message string
 */
export const formatDatabaseResetCompleteMessage = (event: DatabaseResetProgressEvent): string => {
  return event.message || 'Database reset completed';
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
  return event.statusMessage || 'Clearing cache...';
};

/**
 * Formats the success message for cache clear completion.
 * @param event - The cache clear complete event from SignalR
 * @returns Formatted success message string
 */
export const formatCacheClearCompleteMessage = (event: CacheClearCompleteEvent): string => {
  return event.message || 'Cache cleared successfully';
};

/**
 * Formats the failure message for cache clear.
 * @param event - The cache clear complete event from SignalR
 * @returns Formatted failure message string
 */
export const formatCacheClearFailureMessage = (event: CacheClearCompleteEvent): string => {
  return event.error || event.message || 'Cache clear failed';
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
  return event.message || 'Starting depot mapping scan...';
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
  return event.message || existingMessage || 'Scanning depot mappings...';
};
