import type {
  ProcessingProgressEvent,
  DepotMappingProgressEvent,
  GameRemovalProgressEvent,
  ServiceRemovalProgressEvent,
  LogRemovalProgressEvent,
  CacheClearProgressEvent,
  DatabaseResetProgressEvent
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
// Log Removal
// ============================================================================

/**
 * Formats the message for log removal progress.
 * Shows service name and optionally lines removed count.
 * @param event - The log removal progress event from SignalR
 * @returns Formatted message string
 */
export const formatLogRemovalMessage = (event: LogRemovalProgressEvent): string => {
  const linesRemoved = event.linesRemoved || 0;
  if (linesRemoved > 0) {
    return `Removing ${event.service} entries (${linesRemoved.toLocaleString()} removed)...`;
  }
  return event.message || `Removing ${event.service} entries...`;
};

// ============================================================================
// Game Removal
// ============================================================================

/**
 * Formats the message for game removal progress.
 * @param event - The game removal progress event from SignalR
 * @returns Formatted message string
 */
export const formatGameRemovalMessage = (event: GameRemovalProgressEvent): string => {
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
export const formatServiceRemovalMessage = (event: ServiceRemovalProgressEvent): string => {
  return event.message || `Removing ${event.serviceName} cache...`;
};

// ============================================================================
// Cache Clearing
// ============================================================================

/**
 * Formats the message for cache clear progress.
 * @param event - The cache clear progress event from SignalR
 * @returns Formatted message string
 */
export const formatCacheClearMessage = (event: CacheClearProgressEvent): string => {
  return event.statusMessage || 'Clearing cache...';
};

// ============================================================================
// Database Reset
// ============================================================================

/**
 * Formats the message for database reset progress.
 * @param event - The database reset progress event from SignalR
 * @returns Formatted message string
 */
export const formatDatabaseResetMessage = (event: DatabaseResetProgressEvent): string => {
  return event.message || 'Resetting database...';
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
