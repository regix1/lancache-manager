import { API_BASE } from '../utils/constants';
import { isAbortError } from '../utils/error';
import type {
  CacheInfo,
  CacheSizeInfo,
  Download,
  ClientStat,
  ServiceStat,

  ProcessingStatus,
  ClearCacheResponse,
  MessageResponse,
  Config,
  DashboardStats,
  HourlyActivityResponse,
  CacheGrowthResponse,
  SparklineDataResponse,
  CacheSnapshotResponse,
  CorruptedChunkDetail,

  GameCacheInfo,
  ServiceCacheInfo,
  DatasourceLogPosition,
  DatasourceServiceCounts,
  Event,
  CreateEventRequest,
  UpdateEventRequest,
  DownloadSpeedSnapshot,
  SpeedHistorySnapshot,
  ClientGroup,
  CreateClientGroupRequest,
  UpdateClientGroupRequest,
  StatsExclusionsResponse
} from '../types';

// Response types for API operations
interface ApiErrorData {
  code?: string;
  message?: string;
  error?: string;
  details?: string;
  suggestion?: string;
}

interface OperationResponse {
  message?: string;
  success?: boolean;
  status?: string;
  // Log processing specific
  logSizeMB?: number;
  remainingMB?: number;
  resume?: boolean;
  estimatedTimeMinutes?: number;
  // Depot rebuild specific
  rebuildInProgress?: boolean;
  started?: boolean;
  requiresFullScan?: boolean;
  changeGap?: number;
  estimatedApps?: number;
}



interface PicsStatus {
  isScanning: boolean;
  scanProgress?: number;
  totalDepots?: number;
  lastScanTime?: string;
  nextScanIn?: number | string | { totalSeconds?: number; totalHours?: number };
  // Additional status properties
  jsonFile?: { exists: boolean; totalMappings?: number };
  database?: { totalMappings?: number };
  steamKit2?: { isReady: boolean; isRebuildRunning?: boolean };
  rebuildInProgress?: boolean;
}

class ApiService {
  static async handleResponse<T>(response: Response): Promise<T> {
    // Handle 401 Unauthorized - dispatch event to trigger auth refresh
    if (response.status === 401) {
      window.dispatchEvent(new Event('auth-state-changed'));
      let errorData: ApiErrorData | null = null;
      try {
        const text = await response.text();
        errorData = text ? JSON.parse(text) : null;
      } catch {
        // Not JSON
      }
      throw new Error(errorData?.message || 'Authentication required');
    }

    // Handle 403 Forbidden - guest trying admin operation
    if (response.status === 403) {
      console.warn('403 Forbidden: Access denied. User may lack required permissions.');
      let errorData: ApiErrorData | null = null;
      try {
        const text = await response.text();
        errorData = text ? JSON.parse(text) : null;
      } catch {
        // Not JSON
      }
      throw new Error(errorData?.message || errorData?.error || 'Access denied');
    }

    // Handle 499 Client Closed Request (user cancelled the operation)
    // This is not an error - just throw a cancellation signal
    if (response.status === 499) {
      const error = new Error('Request cancelled');
      error.name = 'AbortError';
      throw error;
    }

    if (!response.ok) {
      const errorText = await response.text().catch(() => '');

      // Try to parse error as JSON for structured error messages
      let errorData = null;
      try {
        errorData = errorText ? JSON.parse(errorText) : null;
      } catch (parseError) {
        // Not JSON, use default error format
      }

      // If we have a structured error with helpful fields, use them
      if (errorData) {
        if (errorData.message && errorData.details && errorData.suggestion) {
          // Full structured error
          throw new Error(
            `${errorData.message}\n\n${errorData.details}\n\n${errorData.suggestion}`
          );
        } else if (errorData.message) {
          // Just a message field
          throw new Error(errorData.message);
        } else if (errorData.error) {
          // Legacy error field
          throw new Error(errorData.error);
        }
      }

      // Default error format
      throw new Error(`HTTP ${response.status}: ${errorText || response.statusText}`);
    }

    // Check if response has content before parsing JSON
    const text = await response.text();
    if (!text || text.trim() === '') {
      return {} as T; // Return empty object for empty responses
    }

    try {
      return JSON.parse(text);
    } catch (e) {
      console.error('Failed to parse JSON response:', text);
      throw new Error('Invalid JSON response from server');
    }
  }

  // Helper to add headers to all requests
  private static getHeaders(additionalHeaders: Record<string, string> = {}): HeadersInit {
    return {
      ...additionalHeaders
    };
  }

  // Helper to get fetch options with credentials for session cookies
  static getFetchOptions(options: RequestInit = {}): RequestInit {
    return {
      ...options,
      credentials: 'include', // Important: include HttpOnly session cookies
      headers: {
        ...this.getHeaders(),
        ...(options.headers || {})
      }
    };
  }

  static async getCacheInfo(signal?: AbortSignal): Promise<CacheInfo> {
    try {
      const res = await fetch(`${API_BASE}/cache`, this.getFetchOptions({ signal }));
      return await this.handleResponse<CacheInfo>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCacheInfo error:', error);
      }
      throw error;
    }
  }

  static async getLatestDownloads(
    signal?: AbortSignal,
    count: number | 'unlimited' = 'unlimited',
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    cacheBust?: number
  ): Promise<Download[]> {
    try {
      const actualCount = count === 'unlimited' ? 2147483647 : count;
      let url = `${API_BASE}/downloads/latest`;
      const params = new URLSearchParams();
      params.append('count', actualCount.toString());
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<Download[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getLatestDownloads error:', error);
      }
      throw error;
    }
  }

  static async getClientStats(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    includeExcluded?: boolean,
    cacheBust?: number
  ): Promise<ClientStat[]> {
    try {
      let url = `${API_BASE}/stats/clients`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (includeExcluded) params.append('includeExcluded', 'true');
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<ClientStat[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getClientStats error:', error);
      }
      throw error;
    }
  }

  static async getStatsExclusions(signal?: AbortSignal): Promise<StatsExclusionsResponse> {
    try {
      const res = await fetch(`${API_BASE}/stats/exclusions`, this.getFetchOptions({ signal }));
      return await this.handleResponse<StatsExclusionsResponse>(res);
    } catch (error: unknown) {
      {
        console.error('getStatsExclusions error:', error);
      }
      throw error;
    }
  }

  static async updateStatsExclusions(ips: string[]): Promise<StatsExclusionsResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/stats/exclusions`,
        this.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ips })
        })
      );
      return await this.handleResponse<StatsExclusionsResponse>(res);
    } catch (error: unknown) {
      {
        console.error('updateStatsExclusions error:', error);
      }
      throw error;
    }
  }

  static async getServiceStats(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    cacheBust?: number
  ): Promise<ServiceStat[]> {
    try {
      let url = `${API_BASE}/stats/services`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<ServiceStat[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getServiceStats error:', error);
      }
      throw error;
    }
  }

  // Dashboard aggregated stats
  static async getDashboardStats(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    cacheBust?: number
  ): Promise<DashboardStats> {
    try {
      let url = `${API_BASE}/stats/dashboard`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<DashboardStats>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getDashboardStats error:', error);
      }
      throw error;
    }
  }

  // Hourly activity data for Peak Usage Hours widget
  static async getHourlyActivity(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventId?: number
  ): Promise<HourlyActivityResponse> {
    try {
      let url = `${API_BASE}/stats/hourly-activity`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventId) params.append('eventId', eventId.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<HourlyActivityResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getHourlyActivity error:', error);
      }
      throw error;
    }
  }

  // Cache growth data for Cache Growth widget
  static async getCacheGrowth(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    interval = 'daily',
    actualCacheSize?: number,
    eventId?: number
  ): Promise<CacheGrowthResponse> {
    try {
      let url = `${API_BASE}/stats/cache-growth`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      params.append('interval', interval);
      // Pass actual cache size to detect deletions and calculate net growth
      if (actualCacheSize && actualCacheSize > 0) {
        params.append('actualCacheSize', actualCacheSize.toString());
      }
      if (eventId) params.append('eventId', eventId.toString());
      url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<CacheGrowthResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCacheGrowth error:', error);
      }
      throw error;
    }
  }

  // Sparkline data for dashboard stat cards
  static async getSparklineData(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventId?: number
  ): Promise<SparklineDataResponse> {
    try {
      let url = `${API_BASE}/stats/sparklines`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventId) params.append('eventId', eventId.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<SparklineDataResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getSparklineData error:', error);
      }
      throw error;
    }
  }

  // Historical cache size snapshots for displaying used space in time ranges
  static async getCacheSnapshot(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number
  ): Promise<CacheSnapshotResponse> {
    try {
      let url = `${API_BASE}/stats/cache-snapshot`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<CacheSnapshotResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCacheSnapshot error:', error);
      }
      throw error;
    }
  }

  // Start async cache clearing operation for all datasources (requires auth)
  static async clearAllCache(): Promise<ClearCacheResponse> {
    try {
      const res = await fetch(`${API_BASE}/cache`, this.getFetchOptions({
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' }
        // No timeout - Rust backend handles efficiently
      }));
      return await this.handleResponse<ClearCacheResponse>(res);
    } catch (error) {
      console.error('clearAllCache error:', error);
      throw error;
    }
  }

  // Start async cache clearing operation for a specific datasource (requires auth)
  static async clearDatasourceCache(datasourceName: string): Promise<ClearCacheResponse> {
    try {
      const res = await fetch(`${API_BASE}/cache/datasources/${encodeURIComponent(datasourceName)}`, this.getFetchOptions({
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' }
      }));
      return await this.handleResponse<ClearCacheResponse>(res);
    } catch (error) {
      console.error('clearDatasourceCache error:', error);
      throw error;
    }
  }

  // Get status of cache clearing operation
  

  // Reset selected database tables (requires auth)
  static async resetSelectedTables(tableNames: string[]): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/database/tables`, this.getFetchOptions({
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tables: tableNames })
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error) {
      console.error('resetSelectedTables error:', error);
      throw error;
    }
  }

  // Reset log position (requires auth) - all datasources
  static async resetLogPosition(position: 'top' | 'bottom' = 'bottom'): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/logs/position`, this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reset: true, position: position === 'top' ? 0 : null })
        // No timeout - may need to read entire log file to count lines
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('resetLogPosition error:', error);
      throw error;
    }
  }

  // Reset log position for a specific datasource (requires auth)
  static async resetDatasourceLogPosition(datasourceName: string, position: 'top' | 'bottom' = 'bottom'): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/logs/position/${encodeURIComponent(datasourceName)}`, this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ position: position === 'top' ? 0 : null })
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('resetDatasourceLogPosition error:', error);
      throw error;
    }
  }

  // Get log positions for all datasources
  static async getLogPositions(): Promise<DatasourceLogPosition[]> {
    try {
      const res = await fetch(`${API_BASE}/logs/positions`, this.getFetchOptions({
        signal: AbortSignal.timeout(10000)
      }));
      return await this.handleResponse<DatasourceLogPosition[]>(res);
    } catch (error: unknown) {
      console.error('getLogPositions error:', error);
      throw error;
    }
  }

  // Process all logs (requires auth) - all datasources
  static async processAllLogs(): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/logs/process`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }
        // No timeout - Rust log processor handles large files efficiently
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('processAllLogs error:', error);
      throw error;
    }
  }

  // Process logs for a specific datasource (requires auth)
  static async processDatasourceLogs(datasourceName: string): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/logs/process/${encodeURIComponent(datasourceName)}`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('processDatasourceLogs error:', error);
      throw error;
    }
  }

  static async getProcessingStatus(): Promise<ProcessingStatus> {
    try {
      const res = await fetch(`${API_BASE}/logs/process/status`, this.getFetchOptions({
        signal: AbortSignal.timeout(5000)
      }));
      return await this.handleResponse<ProcessingStatus>(res);
    } catch (error) {
      console.error('getProcessingStatus error:', error);
      throw error;
    }
  }

  // Get log removal status
  

  // Get counts of log entries per service, grouped by datasource
  static async getServiceLogCountsByDatasource(): Promise<DatasourceServiceCounts[]> {
    try {
      const res = await fetch(`${API_BASE}/logs/service-counts/by-datasource`, this.getFetchOptions({
        // No timeout - can take time for large log files
      }));
      return await this.handleResponse<DatasourceServiceCounts[]>(res);
    } catch (error) {
      console.error('getServiceLogCountsByDatasource error:', error);
      throw error;
    }
  }

  // Remove specific service entries from a specific datasource's logs (requires auth)
  static async removeServiceFromDatasourceLogs(datasourceName: string, service: string): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/datasources/${encodeURIComponent(datasourceName)}/services/${encodeURIComponent(service)}`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('removeServiceFromDatasourceLogs error:', error);
      throw error;
    }
  }

  // Delete entire log file for a datasource (requires auth)
  static async deleteLogFile(datasourceName: string): Promise<MessageResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/datasources/${encodeURIComponent(datasourceName)}/file`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<MessageResponse>(res);
    } catch (error: unknown) {
      console.error('deleteLogFile error:', error);
      throw error;
    }
  }

  // Get count of LogEntries in database (not log files)
  static async getDatabaseLogEntriesCount(): Promise<number> {
    try {
      const res = await fetch(`${API_BASE}/database/log-entries-count`, this.getFetchOptions());
      const data = await this.handleResponse<{ count: number }>(res);
      return data.count;
    } catch (error) {
      console.error('getDatabaseLogEntriesCount error:', error);
      throw error;
    }
  }

  // Get configuration info
  static async getConfig(): Promise<Config> {
    const res = await fetch(`${API_BASE}/system/config`, this.getFetchOptions({
      // No timeout - can take time for large log file scanning
    }));
    return await this.handleResponse<Config>(res);
  }

  // Get directory write permissions and docker socket availability
  static async getDirectoryPermissions(): Promise<{
    cache: { path: string; exists: boolean; writable: boolean; readOnly: boolean };
    logs: { path: string; exists: boolean; writable: boolean; readOnly: boolean };
    dockerSocket: { available: boolean };
  }> {
    const res = await fetch(`${API_BASE}/system/permissions`, this.getFetchOptions());
    return await this.handleResponse(res);
  }

  // PICS/Depot related endpoints (consolidated from GameInfoController)
  static async getPicsStatus(signal?: AbortSignal): Promise<PicsStatus> {
    try {
      const res = await fetch(`${API_BASE}/depots/status`, this.getFetchOptions({ signal }));
      return await this.handleResponse<PicsStatus>(res);
    } catch (error: unknown) {
      console.error('getPicsStatus error:', error);
      throw error;
    }
  }


  static async triggerSteamKitRebuild(incremental = false, signal?: AbortSignal): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/depots/rebuild?incremental=${incremental}`, this.getFetchOptions({
        method: 'POST',
        signal,
        headers: { 'Content-Type': 'application/json' }
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('triggerSteamKitRebuild error:', error);
      throw error;
    }
  }

  static async cancelSteamKitRebuild(signal?: AbortSignal): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/depots/rebuild`, this.getFetchOptions({
        method: 'DELETE',
        signal,
        headers: { 'Content-Type': 'application/json' }
      }));
      if (!res.ok) {
        throw new Error(`Failed to cancel scan: ${res.statusText}`);
      }
    } catch (error) {
      console.error('cancelSteamKitRebuild error:', error);
      throw error;
    }
  }

  static async checkIncrementalViability(signal?: AbortSignal): Promise<{ viable: boolean; reason?: string; willTriggerFullScan?: boolean }> {
    try {
      const res = await fetch(`${API_BASE}/depots/rebuild/check-incremental`, this.getFetchOptions({
        method: 'GET',
        signal
      }));
      return await this.handleResponse<{ viable: boolean; reason?: string }>(res);
    } catch (error: unknown) {
      console.error('checkIncrementalViability error:', error);
      throw error;
    }
  }

  static async downloadPrecreatedDepotData(signal?: AbortSignal): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/depots/import?source=github`, this.getFetchOptions({
        method: 'POST',
        signal
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        console.error('downloadPrecreatedDepotData error:', error);
      }
      throw error;
    }
  }

  static async applyDepotMappings(signal?: AbortSignal): Promise<OperationResponse> {
    try {
      const res = await fetch(`${API_BASE}/depots`, this.getFetchOptions({
        method: 'PATCH',
        signal,
        headers: { 'Content-Type': 'application/json' }
      }));
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('applyDepotMappings error:', error);
      throw error;
    }
  }

  // Set cache clearing delete mode (requires auth)
  static async setCacheDeleteMode(
    deleteMode: string
  ): Promise<{ message: string; deleteMode: string }> {
    const res = await fetch(`${API_BASE}/system/cache-delete-mode`, this.getFetchOptions({
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ deleteMode })
    }));
    return await this.handleResponse<{ message: string; deleteMode: string }>(res);
  }

  // Check if rsync is available on the system
  static async isRsyncAvailable(): Promise<{ available: boolean }> {
    try {
      const res = await fetch(`${API_BASE}/system/rsync/available`, this.getFetchOptions());
      return await this.handleResponse<{ available: boolean }>(res);
    } catch (error) {
      console.error('isRsyncAvailable error:', error);
      throw error;
    }
  }

  // Get cache size with deletion time estimates
  static async getCacheSize(datasource?: string): Promise<CacheSizeInfo> {
    try {
      const url = datasource
        ? `${API_BASE}/cache/size?datasource=${encodeURIComponent(datasource)}`
        : `${API_BASE}/cache/size`;
      // No timeout - cache size calculation can take a very long time for large caches
      const res = await fetch(url, this.getFetchOptions());
      return await this.handleResponse<CacheSizeInfo>(res);
    } catch (error) {
      console.error('getCacheSize error:', error);
      throw error;
    }
  }


  // Get cached corruption detection results (returns immediately without running a scan)
  static async getCachedCorruptionDetection(): Promise<{
    hasCachedResults: boolean;
    corruptionCounts?: Record<string, number>;
    totalServicesWithCorruption?: number;
    totalCorruptedChunks?: number;
    lastDetectionTime?: string;
  }> {
    try {
      const res = await fetch(`${API_BASE}/cache/corruption/cached`, this.getFetchOptions({
        signal: AbortSignal.timeout(30000) // 30 seconds for large datasets
      }));
      return await this.handleResponse<{
        hasCachedResults: boolean;
        corruptionCounts?: Record<string, number>;
        totalServicesWithCorruption?: number;
        totalCorruptedChunks?: number;
        lastDetectionTime?: string;
      }>(res);
    } catch (error) {
      console.error('getCachedCorruptionDetection error:', error);
      throw error;
    }
  }

  // Start background corruption detection scan
  static async startCorruptionDetection(threshold: number = 3, compareToCacheLogs: boolean = true): Promise<{ operationId: string; message: string; status: string }> {
    try {
      const res = await fetch(`${API_BASE}/cache/corruption/detect?threshold=${threshold}&compareToCacheLogs=${compareToCacheLogs}`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }
      }));
      return await this.handleResponse<{ operationId: string; message: string; status: string }>(res);
    } catch (error) {
      console.error('startCorruptionDetection error:', error);
      throw error;
    }
  }

  // Get corruption detection status


  // Remove corrupted chunks for a specific service (requires auth)
  static async removeCorruptedChunks(
    service: string,
    threshold: number = 3,
    compareToCacheLogs: boolean = true
  ): Promise<{ message: string; service: string }> {
    try {
      const res = await fetch(`${API_BASE}/cache/services/${encodeURIComponent(service)}/corruption?threshold=${threshold}&compareToCacheLogs=${compareToCacheLogs}`, this.getFetchOptions({
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' }
        // No timeout - Rust corruption remover handles large operations efficiently
      }));
      return await this.handleResponse<{ message: string; service: string }>(res);
    } catch (error) {
      console.error('removeCorruptedChunks error:', error);
      throw error;
    }
  }

  // Get detailed corruption information for a specific service
  static async getCorruptionDetails(
    service: string,
    forceRefresh = false,
    threshold: number = 3,
    compareToCacheLogs: boolean = true
  ): Promise<CorruptedChunkDetail[]> {
    try {
      const params = new URLSearchParams();
      if (forceRefresh) params.set('forceRefresh', 'true');
      params.set('threshold', String(threshold));
      params.set('compareToCacheLogs', String(compareToCacheLogs));
      const url = `${API_BASE}/cache/services/${encodeURIComponent(service)}/corruption?${params.toString()}`;
      const res = await fetch(url, this.getFetchOptions({
        // No timeout - wait for backend to complete analysis (could take several minutes for large logs)
      }));
      return await this.handleResponse<CorruptedChunkDetail[]>(res);
    } catch (error) {
      console.error('getCorruptionDetails error:', error);
      throw error;
    }
  }

  // Start game cache detection as background operation
  static async startGameCacheDetection(forceRefresh: boolean = false): Promise<{ operationId: string }> {
    try {
      const url = `${API_BASE}/games/detect${forceRefresh ? '?forceRefresh=true' : ''}`;
      const res = await fetch(url, this.getFetchOptions({
        method: 'POST',
        signal: AbortSignal.timeout(10000) // Short timeout since it returns immediately
      }));
      return await this.handleResponse<{ operationId: string }>(res);
    } catch (error) {
      console.error('startGameCacheDetection error:', error);
      throw error;
    }
  }

  // Get cached game detection results from database (if available)
  static async getCachedGameDetection(): Promise<{
    hasCachedResults: boolean;
    games?: GameCacheInfo[];
    services?: ServiceCacheInfo[];
    totalGamesDetected?: number;
    totalServicesDetected?: number;
    lastDetectionTime?: string;
  }> {
    try {
      const res = await fetch(`${API_BASE}/games/detect/cached`, this.getFetchOptions({
        signal: AbortSignal.timeout(30000) // 30 seconds for large datasets
      }));
      return await this.handleResponse<{
        hasCachedResults: boolean;
        games?: GameCacheInfo[];
        services?: ServiceCacheInfo[];
        totalGamesDetected?: number;
        totalServicesDetected?: number;
        lastDetectionTime?: string;
      }>(res);
    } catch (error) {
      console.error('getCachedGameDetection error:', error);
      throw error;
    }
  }

  // Remove all cache files for a specific game (fire-and-forget, requires auth)
  static async removeGameFromCache(
    gameAppId: string
  ): Promise<{ message: string; gameAppId: string; status: string }> {
    try {
      const res = await fetch(`${API_BASE}/games/${gameAppId}`, this.getFetchOptions({
        method: 'DELETE'
        // Returns immediately with 202 Accepted - removal happens in background
      }));
      return await this.handleResponse<{ message: string; gameAppId: string; status: string }>(res);
    } catch (error) {
      console.error('removeGameFromCache error:', error);
      throw error;
    }
  }

  // Remove all cache files for a specific service (fire-and-forget, requires auth)
  static async removeServiceFromCache(
    serviceName: string
  ): Promise<{ message: string; serviceName: string; status: string }> {
    try {
      const res = await fetch(`${API_BASE}/cache/services/${encodeURIComponent(serviceName)}`, this.getFetchOptions({
        method: 'DELETE'
        // Returns immediately with 202 Accepted - removal happens in background
      }));
      return await this.handleResponse<{ message: string; serviceName: string; status: string }>(res);
    } catch (error) {
      console.error('removeServiceFromCache error:', error);
      throw error;
    }
  }

  // Get active cache operations (for recovery on page load)
  // Note: Used by NotificationsContext for operation recovery
  


  // Get all active removal operations (games, services, corruption)
  // Used for universal recovery on page refresh
  

  // Set guest session duration configuration
  static async setGuestSessionDuration(
    durationHours: number
  ): Promise<{ success: boolean; durationHours: number; message: string }> {
    try {
      const res = await fetch(`${API_BASE}/auth/guest/config/duration`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ durationHours })
      }));
      return await this.handleResponse<{
        success: boolean;
        durationHours: number;
        message: string;
      }>(res);
    } catch (error) {
      console.error('setGuestSessionDuration error:', error);
      throw error;
    }
  }

  // ==================== Events API ====================

  // Get all events
  static async getEvents(signal?: AbortSignal): Promise<Event[]> {
    try {
      const res = await fetch(`${API_BASE}/events`, this.getFetchOptions({ signal }));
      return await this.handleResponse<Event[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getEvents error:', error);
      }
      throw error;
    }
  }

  // Get currently active events
  static async getActiveEvents(signal?: AbortSignal): Promise<Event[]> {
    try {
      const res = await fetch(`${API_BASE}/events/active`, this.getFetchOptions({ signal }));
      return await this.handleResponse<Event[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getActiveEvents error:', error);
      }
      throw error;
    }
  }

  // Get a single event by ID
  

  // Create a new event
  static async createEvent(data: CreateEventRequest): Promise<Event> {
    try {
      const res = await fetch(`${API_BASE}/events`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data)
      }));
      return await this.handleResponse<Event>(res);
    } catch (error) {
      console.error('createEvent error:', error);
      throw error;
    }
  }

  // Update an existing event
  static async updateEvent(id: number, data: UpdateEventRequest): Promise<Event> {
    try {
      const res = await fetch(`${API_BASE}/events/${id}`, this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data)
      }));
      return await this.handleResponse<Event>(res);
    } catch (error) {
      console.error('updateEvent error:', error);
      throw error;
    }
  }

  // Delete an event
  static async deleteEvent(id: number): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/events/${id}`, this.getFetchOptions({
        method: 'DELETE'
      }));
      if (!res.ok) {
        const errorText = await res.text().catch(() => '');
        throw new Error(`HTTP ${res.status}: ${errorText || res.statusText}`);
      }
    } catch (error) {
      console.error('deleteEvent error:', error);
      throw error;
    }
  }

  // Get downloads for an event
  

  // ==================== Downloads with Associations ====================

  // Get a single download with its events
  

  // Get events for multiple downloads in a single batch request
  static async getBatchDownloadEvents(
    downloadIds: number[],
    signal?: AbortSignal
  ): Promise<Record<number, { events: Array<{ id: number; name: string; colorIndex: number; autoTagged: boolean }> }>> {
    if (downloadIds.length === 0) {
      return {};
    }
    try {
      // IMPORTANT: use getFetchOptions() to include credentials for HttpOnly session cookies.
      const res = await fetch(`${API_BASE}/downloads/batch-download-events`, this.getFetchOptions({
        signal,
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ downloadIds })
      }));
      return await this.handleResponse(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getBatchDownloadEvents error:', error);
      }
      throw error;
    }
  }

  // =====================================================
  // Real-time Download Speed API
  // =====================================================

  // Get current download speeds snapshot
  static async getCurrentSpeeds(signal?: AbortSignal): Promise<DownloadSpeedSnapshot> {
    try {
      const res = await fetch(`${API_BASE}/speeds/current`, this.getFetchOptions({ signal }));
      return await this.handleResponse<DownloadSpeedSnapshot>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCurrentSpeeds error:', error);
      }
      throw error;
    }
  }

  // Get historical download speeds for a time period
  static async getSpeedHistory(minutes: number = 60, signal?: AbortSignal): Promise<SpeedHistorySnapshot> {
    try {
      const res = await fetch(`${API_BASE}/speeds/history?minutes=${minutes}`, this.getFetchOptions({ signal }));
      return await this.handleResponse<SpeedHistorySnapshot>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getSpeedHistory error:', error);
      }
      throw error;
    }
  }

  // ==================== Client Groups API ====================

  // Get all client groups
  static async getClientGroups(signal?: AbortSignal): Promise<ClientGroup[]> {
    try {
      const res = await fetch(`${API_BASE}/client-groups`, this.getFetchOptions({ signal }));
      return await this.handleResponse<ClientGroup[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getClientGroups error:', error);
      }
      throw error;
    }
  }

  // Get a single client group by ID
  

  // Create a new client group
  static async createClientGroup(data: CreateClientGroupRequest): Promise<ClientGroup> {
    try {
      const res = await fetch(`${API_BASE}/client-groups`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data)
      }));
      return await this.handleResponse<ClientGroup>(res);
    } catch (error) {
      console.error('createClientGroup error:', error);
      throw error;
    }
  }

  // Update an existing client group
  static async updateClientGroup(id: number, data: UpdateClientGroupRequest): Promise<ClientGroup> {
    try {
      const res = await fetch(`${API_BASE}/client-groups/${id}`, this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data)
      }));
      return await this.handleResponse<ClientGroup>(res);
    } catch (error) {
      console.error('updateClientGroup error:', error);
      throw error;
    }
  }

  // Delete a client group
  static async deleteClientGroup(id: number): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/client-groups/${id}`, this.getFetchOptions({
        method: 'DELETE'
      }));
      if (!res.ok) {
        const errorText = await res.text().catch(() => '');
        throw new Error(`HTTP ${res.status}: ${errorText || res.statusText}`);
      }
    } catch (error) {
      console.error('deleteClientGroup error:', error);
      throw error;
    }
  }

  // Add a member (IP) to a client group
  static async addClientGroupMember(groupId: number, clientIp: string): Promise<ClientGroup> {
    try {
      const res = await fetch(`${API_BASE}/client-groups/${groupId}/members`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ clientIp })
      }));
      return await this.handleResponse<ClientGroup>(res);
    } catch (error) {
      console.error('addClientGroupMember error:', error);
      throw error;
    }
  }

  // Remove a member (IP) from a client group
  static async removeClientGroupMember(groupId: number, clientIp: string): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/client-groups/${groupId}/members/${encodeURIComponent(clientIp)}`, this.getFetchOptions({
        method: 'DELETE'
      }));
      if (!res.ok) {
        const errorText = await res.text().catch(() => '');
        throw new Error(`HTTP ${res.status}: ${errorText || res.statusText}`);
      }
    } catch (error) {
      console.error('removeClientGroupMember error:', error);
      throw error;
    }
  }

  // Get IP to group mapping for efficient lookups
  

  // =====================================================
  // Universal Operation Cancellation APIs
  // =====================================================

  // Cancel any operation by ID
  static async cancelOperation(operationId: string): Promise<{ message: string }> {
    try {
      const res = await fetch(`${API_BASE}/operations/${operationId}/cancel`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(5000)
      }));
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('cancelOperation error:', error);
      throw error;
    }
  }

  // =====================
  // Prefill Admin APIs
  // =====================

  // Get prefill sessions (paginated)
  static async getPrefillSessions(
    page = 1,
    pageSize = 20,
    status?: string,
    signal?: AbortSignal
  ): Promise<PrefillSessionsResponse> {
    try {
      const params = new URLSearchParams({ page: page.toString(), pageSize: pageSize.toString() });
      if (status) params.set('status', status);
      const res = await fetch(`${API_BASE}/prefill-admin/sessions?${params}`, this.getFetchOptions({ signal }));
      return await this.handleResponse<PrefillSessionsResponse>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillSessions error:', error);
      throw error;
    }
  }

  // Get active prefill sessions (in-memory)
  static async getActivePrefillSessions(signal?: AbortSignal): Promise<DaemonSessionDto[]> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/sessions/active`, this.getFetchOptions({ signal }));
      return await this.handleResponse<DaemonSessionDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getActivePrefillSessions error:', error);
      throw error;
    }
  }

  // Get prefill history for a specific session
  static async getPrefillSessionHistory(
    sessionId: string,
    signal?: AbortSignal
  ): Promise<PrefillHistoryEntryDto[]> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/sessions/${sessionId}/history`, this.getFetchOptions({ signal }));
      return await this.handleResponse<PrefillHistoryEntryDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillSessionHistory error:', error);
      throw error;
    }
  }

  // Terminate a specific prefill session
  static async terminatePrefillSession(
    sessionId: string,
    reason?: string,
    force = false
  ): Promise<{ message: string }> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/sessions/${sessionId}/terminate`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason, force })
      }));
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('terminatePrefillSession error:', error);
      throw error;
    }
  }

  // Terminate all prefill sessions
  static async terminateAllPrefillSessions(reason?: string, force = true): Promise<{ message: string }> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/sessions/terminate-all`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason, force })
      }));
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('terminateAllPrefillSessions error:', error);
      throw error;
    }
  }

  // Get Steam user bans
  static async getSteamBans(includeLifted = false, signal?: AbortSignal): Promise<BannedSteamUserDto[]> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/bans?includeLifted=${includeLifted}`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<BannedSteamUserDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getSteamBans error:', error);
      throw error;
    }
  }

  // Ban a Steam user by session ID
  static async banSteamUserBySession(
    sessionId: string,
    reason?: string,
    expiresAt?: string
  ): Promise<BannedSteamUserDto> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/bans/by-session/${sessionId}`, this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason, expiresAt })
      }));
      return await this.handleResponse<BannedSteamUserDto>(res);
    } catch (error: unknown) {
      console.error('banSteamUserBySession error:', error);
      throw error;
    }
  }

  // Ban a Steam user by username
  

  // Lift a Steam user ban
  static async liftSteamBan(banId: number): Promise<{ message: string }> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/bans/${banId}/lift`, this.getFetchOptions({
        method: 'POST'
      }));
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('liftSteamBan error:', error);
      throw error;
    }
  }

  // =====================
  // Prefill Cache APIs
  // =====================

  // Get all cached apps
  static async getPrefillCachedApps(signal?: AbortSignal): Promise<CachedAppDto[]> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/cache`, this.getFetchOptions({ signal }));
      return await this.handleResponse<CachedAppDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillCachedApps error:', error);
      throw error;
    }
  }

  // Check cache status for apps via daemon (verifies manifests/build versions)
  static async getPrefillCacheStatus(
    sessionId: string,
    appIds: string[],
    serviceBasePath: string = 'steam-daemon',
    signal?: AbortSignal
  ): Promise<PrefillCacheStatusDto> {
    try {
      const res = await fetch(
        `${API_BASE}/${serviceBasePath}/sessions/${sessionId}/cache-status`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ appIds }),
          signal
        })
      );
      return await this.handleResponse<PrefillCacheStatusDto>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillCacheStatus error:', error);
      throw error;
    }
  }

  // Check which apps are cached
  

  // Clear cache for a specific app
  

  // Clear entire prefill cache
  static async clearAllPrefillCache(): Promise<{ message: string }> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/cache`, this.getFetchOptions({
        method: 'DELETE'
      }));
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('clearAllPrefillCache error:', error);
      throw error;
    }
  }
}

// Prefill admin types
export interface PrefillSessionDto {
  id: number;
  sessionId: string;
  containerId?: string;
  containerName?: string;
  steamUsername?: string;
  status: string;
  isAuthenticated: boolean;
  isPrefilling: boolean;
  createdAtUtc: string;
  endedAtUtc?: string;
  expiresAtUtc: string;
  terminationReason?: string;
  terminatedBy?: string;
  isLive: boolean;
}

export interface DnsTestResult {
  domain: string;
  resolvedIps?: string[];
  isPrivateIp: boolean;
  success: boolean;
  error?: string;
}

export interface NetworkDiagnostics {
  internetConnectivity: boolean;
  internetConnectivityError?: string;
  internetConnectivityIpv4?: boolean | null;
  internetConnectivityIpv4Error?: string;
  internetConnectivityIpv6?: boolean | null;
  internetConnectivityIpv6Error?: string;
  dnsResults: DnsTestResult[];
  testedAt: string;
  /** True if container uses host networking - steam-prefill will detect lancache via localhost/gateway fallback */
  useHostNetworking?: boolean;
}

export interface DaemonSessionDto {
  id: string;
  userId: string;
  containerName: string;
  status: string;
  authState: string;
  isPrefilling: boolean;
  createdAt: string;
  endedAt?: string;
  expiresAt: string;
  timeRemainingSeconds: number;
  // Client info for admin visibility
  ipAddress?: string;
  operatingSystem?: string;
  browser?: string;
  lastSeenAt: string;
  steamUsername?: string;
  // Current prefill progress info for admin visibility
  currentAppId?: string;
  currentAppName?: string;
  // Total bytes transferred during this session
  totalBytesTransferred?: number;
  // Network diagnostics results
  networkDiagnostics?: NetworkDiagnostics;
}

interface PrefillSessionsResponse {
  sessions: PrefillSessionDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface BannedSteamUserDto {
  id: number;
  username: string;
  banReason?: string;
  bannedAtUtc: string;
  bannedBy?: string;
  expiresAtUtc?: string;
  isLifted: boolean;
  liftedAtUtc?: string;
  liftedBy?: string;
  isActive: boolean;
}

export interface PrefillHistoryEntryDto {
  id: number;
  sessionId: string;
  appId: string;
  appName?: string;
  startedAtUtc: string;
  completedAtUtc?: string;
  bytesDownloaded: number;
  totalBytes: number;
  status: string;
  errorMessage?: string;
}

interface CachedAppDto {
  appId: string;
  appName?: string;
  depotCount: number;
  totalBytes: number;
  cachedAtUtc: string;
  cachedBy?: string;
}

interface PrefillCacheStatusDto {
  upToDateAppIds: string[];
  outdatedAppIds: string[];
  message?: string;
}



export default ApiService;
