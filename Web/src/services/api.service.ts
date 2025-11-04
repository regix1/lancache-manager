import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import type {
  CacheInfo,
  Download,
  ClientStat,
  ServiceStat,
  CacheClearStatus,
  ProcessingStatus,
  ClearCacheResponse,
  Config,
  DashboardStats,
  CorruptedChunkDetail,
  GameDetectionStatus,
  GameCacheInfo
  // GameCacheRemovalReport // No longer used - game removal is fire-and-forget
} from '../types';

class ApiService {
  // Helper to check if error is a guest session revoked error (don't log these)
  private static isGuestSessionError(error: any): boolean {
    return error?.message?.includes('guest session') || error?.message?.includes('Session revoked');
  }

  static async handleResponse<T>(response: Response): Promise<T> {
    // Handle 401 Unauthorized
    if (response.status === 401) {
      // Try to parse JSON error response
      let errorData: any = null;
      try {
        const text = await response.text();
        errorData = text ? JSON.parse(text) : null;
      } catch {
        // Not JSON, continue with default handling
      }

      // Check if it's a guest session revoked error
      if (errorData?.code === 'GUEST_SESSION_REVOKED') {
        // Guest session was revoked - just expire guest mode without calling handleUnauthorized
        // This prevents the page reload loop
        authService.expireGuestMode();
        throw new Error(errorData.message || 'Your guest session has been revoked');
      }

      // Log authentication failure for diagnostics
      console.error('[API] 401 Unauthorized - API key may be invalid or backend was restarted with new key', {
        url: response.url,
        errorMessage: errorData?.message,
        hasStoredApiKey: !!authService['apiKey']
      });

      // For other 401 errors, use standard handling
      authService.handleUnauthorized();
      throw new Error(errorData?.message || 'Authentication required');
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
          throw new Error(`${errorData.message}\n\n${errorData.details}\n\n${errorData.suggestion}`);
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
    return response.json();
  }

  // Helper to add auth headers to all requests
  static getHeaders(additionalHeaders: Record<string, string> = {}): HeadersInit {
    return {
      ...authService.getAuthHeaders(),
      ...additionalHeaders
    };
  }

  static async getCacheInfo(signal?: AbortSignal): Promise<CacheInfo> {
    try {
      const res = await fetch(`${API_BASE}/management/cache`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse<CacheInfo>(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
        // Silently ignore abort errors
      } else if (!this.isGuestSessionError(error)) {
        console.error('getCacheInfo error:', error);
      }
      throw error;
    }
  }

  static async getActiveDownloads(signal?: AbortSignal): Promise<Download[]> {
    try {
      const res = await fetch(`${API_BASE}/downloads/active`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse<Download[]>(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else if (!this.isGuestSessionError(error)) {
        console.error('getActiveDownloads error:', error);
      }
      throw error;
    }
  }

  static async getLatestDownloads(
    signal?: AbortSignal,
    count: number | 'unlimited' = 'unlimited',
    startTime?: number,
    endTime?: number
  ): Promise<Download[]> {
    try {
      const actualCount = count === 'unlimited' ? 2147483647 : count;
      let url = `${API_BASE}/downloads/latest?count=${actualCount}`;
      if (startTime) url += `&startTime=${startTime}`;
      if (endTime) url += `&endTime=${endTime}`;
      const res = await fetch(url, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse<Download[]>(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else if (!this.isGuestSessionError(error)) {
        console.error('getLatestDownloads error:', error);
      }
      throw error;
    }
  }

  static async getClientStats(signal?: AbortSignal, startTime?: number, endTime?: number): Promise<ClientStat[]> {
    try {
      let url = `${API_BASE}/stats/clients`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse<ClientStat[]>(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else {
        console.error('getClientStats error:', error);
      }
      throw error;
    }
  }

  static async getServiceStats(
    signal?: AbortSignal,
    since: string | null = null,
    startTime?: number,
    endTime?: number
  ): Promise<ServiceStat[]> {
    try {
      let url = `${API_BASE}/stats/services`;
      const params = new URLSearchParams();
      if (since) params.append('since', since);
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse<ServiceStat[]>(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else {
        console.error('getServiceStats error:', error);
      }
      throw error;
    }
  }

  // Dashboard aggregated stats
  static async getDashboardStats(period = '24h', signal?: AbortSignal): Promise<DashboardStats> {
    try {
      const res = await fetch(`${API_BASE}/stats/dashboard?period=${period}`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse<DashboardStats>(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else if (!this.isGuestSessionError(error)) {
        console.error('getDashboardStats error:', error);
      }
      throw error;
    }
  }

  // Cache effectiveness stats
  static async getCacheEffectiveness(period = '24h', signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/stats/cache-effectiveness?period=${period}`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else {
        console.error('getCacheEffectiveness error:', error);
      }
      throw error;
    }
  }

  // Timeline stats
  static async getTimelineStats(
    period = '24h',
    interval = 'hourly',
    signal?: AbortSignal
  ): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/stats/timeline?period=${period}&interval=${interval}`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else {
        console.error('getTimelineStats error:', error);
      }
      throw error;
    }
  }

  static async postProcessDepotMappings(): Promise<{ message?: string; mappingsProcessed?: number }> {
    const res = await fetch(`${API_BASE}/management/post-process-depot-mappings`, {
      method: 'POST',
      headers: this.getHeaders({ 'Content-Type': 'application/json' })
    });
    return this.handleResponse(res);
  }

  // Bandwidth saved stats
  static async getBandwidthSaved(period = 'all', signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/stats/bandwidth-saved?period=${period}`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else {
        console.error('getBandwidthSaved error:', error);
      }
      throw error;
    }
  }

  // Top games stats
  static async getTopGames(limit = 10, period = '7d', signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/stats/top-games?limit=${limit}&period=${period}`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error: any) {
      if (error.name === 'AbortError') {
      } else {
        console.error('getTopGames error:', error);
      }
      throw error;
    }
  }

  // Start async cache clearing operation (requires auth)
  static async clearAllCache(): Promise<ClearCacheResponse> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-all`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
        // No timeout - Rust backend handles efficiently
      });
      return await this.handleResponse<ClearCacheResponse>(res);
    } catch (error) {
      console.error('clearAllCache error:', error);
      throw error;
    }
  }

  // Get status of cache clearing operation
  static async getCacheClearStatus(operationId: string): Promise<CacheClearStatus> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-status/${operationId}`, {
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse<CacheClearStatus>(res);
    } catch (error) {
      console.error('getCacheClearStatus error:', error);
      throw error;
    }
  }

  // Cancel cache clearing operation (requires auth)
  static async cancelCacheClear(operationId: string): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-cancel/${operationId}`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(5000)
      });
      return await this.handleResponse(res);
    } catch (error: any) {
      // Suppress logging for "operation not found" errors (expected when operation already completed)
      if (!error?.message?.includes('Operation not found') && !error?.message?.includes('already completed')) {
        console.error('cancelCacheClear error:', error);
      }
      throw error;
    }
  }

  // Get all active cache clear operations
  static async getActiveCacheOperations(): Promise<any[]> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/active-operations`, {
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse<any[]>(res);
    } catch (error) {
      console.error('getActiveCacheOperations error:', error);
      throw error;
    }
  }

  // Get database reset status
  static async getDatabaseResetStatus(): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/database/reset-status`, {
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse<any>(res);
    } catch (error) {
      console.error('getDatabaseResetStatus error:', error);
      throw error;
    }
  }

  // Legacy method for compatibility
  static async clearCache(service: string | null = null): Promise<any> {
    if (service) {
      return await this.removeServiceFromLogs(service);
    } else {
      return await this.clearAllCache();
    }
  }

  // Reset database (requires auth)
  static async resetDatabase(): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/database`, {
        method: 'DELETE',
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
        // No timeout - Rust backend handles efficiently
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('resetDatabase error:', error);
      throw error;
    }
  }

  // Reset selected database tables (requires auth)
  static async resetSelectedTables(tableNames: string[]): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/database/reset-selected`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify(tableNames)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('resetSelectedTables error:', error);
      throw error;
    }
  }

  // Reset log position (requires auth)
  static async resetLogPosition(position: 'top' | 'bottom' = 'bottom'): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/reset-logs?position=${position}`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
        // No timeout - may need to read entire log file to count lines
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('resetLogPosition error:', error);
      throw error;
    }
  }

  // Process all logs (requires auth)
  static async processAllLogs(): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/process-all-logs`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
        // No timeout - Rust log processor handles large files efficiently
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('processAllLogs error:', error);
      throw error;
    }
  }

  // Cancel processing (requires auth)
  static async cancelProcessing(): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/cancel-processing`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(3000) // Short timeout since endpoint returns immediately
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('cancelProcessing error:', error);
      // Treat timeout as success since cancellation was initiated
      if (error instanceof DOMException && error.name === 'TimeoutError') {
        console.log('Cancel request timed out - treating as success');
        return { message: 'Log processing cancelled' };
      }
      throw error;
    }
  }

  static async getProcessingStatus(): Promise<ProcessingStatus> {
    try {
      const res = await fetch(`${API_BASE}/management/processing-status`, {
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse<ProcessingStatus>(res);
    } catch (error) {
      console.error('getProcessingStatus error:', error);
      throw error;
    }
  }

  // Remove specific service entries from log file (requires auth)
  static async removeServiceFromLogs(service: string): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/logs/remove-service`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ service })
        // No timeout - Rust log filtering handles large files efficiently
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('removeServiceFromLogs error:', error);
      throw error;
    }
  }

  // Get log removal status
  static async getLogRemovalStatus(): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/logs/remove-status`, {
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getLogRemovalStatus error:', error);
      throw error;
    }
  }

  // Get counts of log entries per service (from log files)
  static async getServiceLogCounts(forceRefresh: boolean = false): Promise<Record<string, number>> {
    try {
      const url = `${API_BASE}/management/logs/service-counts${forceRefresh ? '?forceRefresh=true' : ''}`;
      const res = await fetch(url, {
        // No timeout - can take hours for massive log files
        headers: this.getHeaders()
      });
      return await this.handleResponse<Record<string, number>>(res);
    } catch (error) {
      console.error('getServiceLogCounts error:', error);
      throw error;
    }
  }

  // Get count of LogEntries in database (not log files)
  static async getDatabaseLogEntriesCount(): Promise<number> {
    try {
      const res = await fetch(`${API_BASE}/management/database/log-entries-count`, {
        headers: this.getHeaders()
      });
      const data = await this.handleResponse<{ count: number }>(res);
      return data.count;
    } catch (error) {
      console.error('getDatabaseLogEntriesCount error:', error);
      throw error;
    }
  }

  // Get configuration info
  static async getConfig(): Promise<Config> {
    const res = await fetch(`${API_BASE}/management/config`, {
      // No timeout - can take time for large log file scanning
      headers: this.getHeaders()
    });
    return await this.handleResponse<Config>(res);
  }

  // Get directory write permissions
  static async getDirectoryPermissions(): Promise<{
    cache: { path: string; writable: boolean; readOnly: boolean };
    logs: { path: string; writable: boolean; readOnly: boolean };
  }> {
    const res = await fetch(`${API_BASE}/management/directory-permissions`, {
      headers: this.getHeaders()
    });
    return await this.handleResponse(res);
  }

  // PICS/GameInfo related endpoints
  static async getPicsStatus(signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/gameinfo/pics-status`, {
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getPicsStatus error:', error);
      throw error;
    }
  }

  static async downloadPrecreatedPicsData(signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/gameinfo/download-precreated-data`, {
        method: 'POST',
        signal,
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('downloadPrecreatedPicsData error:', error);
      throw error;
    }
  }

  static async triggerSteamKitRebuild(incremental = false, signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/gameinfo/steamkit/rebuild?incremental=${incremental}`, {
        method: 'POST',
        signal,
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('triggerSteamKitRebuild error:', error);
      throw error;
    }
  }

  static async cancelSteamKitRebuild(signal?: AbortSignal): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/gameinfo/steamkit/cancel`, {
        method: 'POST',
        signal,
        headers: this.getHeaders({ 'Content-Type': 'application/json' })
      });
      if (!res.ok) {
        throw new Error(`Failed to cancel scan: ${res.statusText}`);
      }
    } catch (error) {
      console.error('cancelSteamKitRebuild error:', error);
      throw error;
    }
  }

  static async checkIncrementalViability(signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/gameinfo/steamkit/check-incremental`, {
        method: 'GET',
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('checkIncrementalViability error:', error);
      throw error;
    }
  }

  static async downloadPrecreatedDepotData(signal?: AbortSignal): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/gameinfo/download-precreated-data`, {
        method: 'POST',
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('downloadPrecreatedDepotData error:', error);
      throw error;
    }
  }

  // Get cache clearing thread count
  static async getCacheThreadCount(): Promise<{ threadCount: number }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/thread-count`, {
        headers: this.getHeaders()
      });
      return await this.handleResponse<{ threadCount: number }>(res);
    } catch (error) {
      console.error('getCacheThreadCount error:', error);
      throw error;
    }
  }

  // Set cache clearing thread count (requires auth)
  static async setCacheThreadCount(threadCount: number): Promise<{ message: string; threadCount: number }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/thread-count`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ threadCount })
      });
      return await this.handleResponse<{ message: string; threadCount: number }>(res);
    } catch (error) {
      console.error('setCacheThreadCount error:', error);
      throw error;
    }
  }

  // Get cache clearing delete mode
  static async getCacheDeleteMode(): Promise<{ deleteMode: string }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/delete-mode`, {
        headers: this.getHeaders()
      });
      return await this.handleResponse<{ deleteMode: string }>(res);
    } catch (error) {
      console.error('getCacheDeleteMode error:', error);
      throw error;
    }
  }

  // Set cache clearing delete mode (requires auth)
  static async setCacheDeleteMode(deleteMode: string): Promise<{ message: string; deleteMode: string }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/delete-mode`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ deleteMode })
      });
      return await this.handleResponse<{ message: string; deleteMode: string }>(res);
    } catch (error) {
      console.error('setCacheDeleteMode error:', error);
      throw error;
    }
  }

  // Get system CPU count
  static async getSystemCpuCount(): Promise<{ cpuCount: number }> {
    try {
      const res = await fetch(`${API_BASE}/management/system/cpu-count`, {
        headers: this.getHeaders()
      });
      return await this.handleResponse<{ cpuCount: number }>(res);
    } catch (error) {
      console.error('getSystemCpuCount error:', error);
      throw error;
    }
  }

  // Check if rsync is available on the system
  static async isRsyncAvailable(): Promise<{ available: boolean }> {
    try {
      const res = await fetch(`${API_BASE}/management/system/rsync-available`, {
        headers: this.getHeaders()
      });
      return await this.handleResponse<{ available: boolean }>(res);
    } catch (error) {
      console.error('isRsyncAvailable error:', error);
      throw error;
    }
  }

  // Clear all depot mappings from database (requires auth)
  static async clearDepotMappings(): Promise<{ message: string; count: number }> {
    try {
      const res = await fetch(`${API_BASE}/management/depot-mappings`, {
        method: 'DELETE',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(30000)
      });
      return await this.handleResponse<{ message: string; count: number }>(res);
    } catch (error) {
      console.error('clearDepotMappings error:', error);
      throw error;
    }
  }

  // Get corruption summary (counts of corrupted chunks per service)
  static async getCorruptionSummary(forceRefresh: boolean = false): Promise<Record<string, number>> {
    try {
      const url = `${API_BASE}/management/corruption/summary${forceRefresh ? '?forceRefresh=true' : ''}`;
      const res = await fetch(url, {
        // No timeout - can take hours for massive log files
        headers: this.getHeaders()
      });
      return await this.handleResponse<Record<string, number>>(res);
    } catch (error) {
      console.error('getCorruptionSummary error:', error);
      throw error;
    }
  }

  // Remove corrupted chunks for a specific service (requires auth)
  static async removeCorruptedChunks(service: string): Promise<{ message: string; service: string }> {
    try {
      const res = await fetch(`${API_BASE}/management/corruption/remove`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ service })
        // No timeout - Rust corruption remover handles large operations efficiently
      });
      return await this.handleResponse<{ message: string; service: string }>(res);
    } catch (error) {
      console.error('removeCorruptedChunks error:', error);
      throw error;
    }
  }

  // Get detailed corruption information for a specific service
  static async getCorruptionDetails(service: string, forceRefresh: boolean = false): Promise<CorruptedChunkDetail[]> {
    try {
      const url = `${API_BASE}/management/corruption/details/${encodeURIComponent(service)}${forceRefresh ? '?forceRefresh=true' : ''}`;
      const res = await fetch(url, {
        headers: this.getHeaders()
        // No timeout - wait for backend to complete analysis (could take several minutes for large logs)
      });
      return await this.handleResponse<CorruptedChunkDetail[]>(res);
    } catch (error) {
      console.error('getCorruptionDetails error:', error);
      throw error;
    }
  }

  // Start game cache detection as background operation
  static async startGameCacheDetection(): Promise<{ operationId: string }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/detect-games`, {
        method: 'POST',
        headers: this.getHeaders(),
        signal: AbortSignal.timeout(10000) // Short timeout since it returns immediately
      });
      return await this.handleResponse<{ operationId: string }>(res);
    } catch (error) {
      console.error('startGameCacheDetection error:', error);
      throw error;
    }
  }

  // Get status of game cache detection operation
  static async getGameDetectionStatus(operationId: string): Promise<GameDetectionStatus> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/detect-games/${operationId}`, {
        headers: this.getHeaders(),
        signal: AbortSignal.timeout(30000) // 30 seconds - status check can be slow with many games
      });
      return await this.handleResponse<GameDetectionStatus>(res);
    } catch (error) {
      console.error('getGameDetectionStatus error:', error);
      throw error;
    }
  }

  // Get active game cache detection operation (if any)
  static async getActiveGameDetection(): Promise<{ hasActiveOperation: boolean; operation?: GameDetectionStatus }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/detect-games-active`, {
        headers: this.getHeaders(),
        signal: AbortSignal.timeout(5000)
      });
      return await this.handleResponse<{ hasActiveOperation: boolean; operation?: GameDetectionStatus }>(res);
    } catch (error) {
      console.error('getActiveGameDetection error:', error);
      throw error;
    }
  }

  // Get cached game detection results from database (if available)
  static async getCachedGameDetection(): Promise<{ hasCachedResults: boolean; games?: GameCacheInfo[]; totalGamesDetected?: number }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/detect-games-cached`, {
        headers: this.getHeaders(),
        signal: AbortSignal.timeout(30000) // 30 seconds for large datasets
      });
      return await this.handleResponse<{ hasCachedResults: boolean; games?: GameCacheInfo[]; totalGamesDetected?: number }>(res);
    } catch (error) {
      console.error('getCachedGameDetection error:', error);
      throw error;
    }
  }

  // Remove all cache files for a specific game (fire-and-forget, requires auth)
  static async removeGameFromCache(gameAppId: number): Promise<{ message: string; gameAppId: number; status: string }> {
    try {
      const res = await fetch(`${API_BASE}/management/cache/game/${gameAppId}`, {
        method: 'DELETE',
        headers: this.getHeaders()
        // Returns immediately with 202 Accepted - removal happens in background
      });
      return await this.handleResponse<{ message: string; gameAppId: number; status: string }>(res);
    } catch (error) {
      console.error('removeGameFromCache error:', error);
      throw error;
    }
  }

  // Get guest session duration configuration
  static async getGuestSessionDuration(): Promise<{ durationHours: number }> {
    try {
      const res = await fetch(`${API_BASE}/auth/guest/config/duration`, {
        headers: this.getHeaders()
      });
      return await this.handleResponse<{ durationHours: number }>(res);
    } catch (error) {
      console.error('getGuestSessionDuration error:', error);
      throw error;
    }
  }

  // Set guest session duration configuration
  static async setGuestSessionDuration(durationHours: number): Promise<{ success: boolean; durationHours: number; message: string }> {
    try {
      const res = await fetch(`${API_BASE}/auth/guest/config/duration`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ durationHours })
      });
      return await this.handleResponse<{ success: boolean; durationHours: number; message: string }>(res);
    } catch (error) {
      console.error('setGuestSessionDuration error:', error);
      throw error;
    }
  }
}

export default ApiService;
