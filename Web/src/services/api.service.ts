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
  DashboardStats
} from '../types';

class ApiService {
  static async handleResponse<T>(response: Response): Promise<T> {
    // Handle 401 Unauthorized
    if (response.status === 401) {
      authService.handleUnauthorized();
      const error = await response.text().catch(() => '');
      throw new Error(`Authentication required: ${error || 'Please provide API key'}`);
    }

    if (!response.ok) {
      const error = await response.text().catch(() => '');
      throw new Error(`HTTP ${response.status}: ${error || response.statusText}`);
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
      } else {
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
      } else {
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
      const actualCount = count === 'unlimited' ? 9999 : count;
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
      } else {
        console.error('getLatestDownloads error:', error);
      }
      throw error;
    }
  }

  static async getClientStats(signal?: AbortSignal, startTime?: number, endTime?: number): Promise<ClientStat[]> {
    try {
      let url = `${API_BASE}/stats/clients`;
      const params = new URLSearchParams();
      if (startTime) params.append('startTime', startTime.toString());
      if (endTime) params.append('endTime', endTime.toString());
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
      if (startTime) params.append('startTime', startTime.toString());
      if (endTime) params.append('endTime', endTime.toString());
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
      } else {
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
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(10000)
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
    } catch (error) {
      console.error('cancelCacheClear error:', error);
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
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(60000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('resetDatabase error:', error);
      throw error;
    }
  }

  // Reset log position (requires auth)
  static async resetLogPosition(position: 'top' | 'bottom' = 'bottom'): Promise<any> {
    try {
      const res = await fetch(`${API_BASE}/management/reset-logs?position=${position}`, {
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(60000)
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
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(120000)
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
        body: JSON.stringify({ service }),
        signal: AbortSignal.timeout(120000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('removeServiceFromLogs error:', error);
      throw error;
    }
  }

  // Get counts of log entries per service
  static async getServiceLogCounts(): Promise<Record<string, number>> {
    try {
      const res = await fetch(`${API_BASE}/management/logs/service-counts`, {
        signal: AbortSignal.timeout(300000), // 5 minute timeout for large log files
        headers: this.getHeaders()
      });
      return await this.handleResponse<Record<string, number>>(res);
    } catch (error) {
      console.error('getServiceLogCounts error:', error);
      throw error;
    }
  }

  // Get configuration info
  static async getConfig(): Promise<Config> {
    const res = await fetch(`${API_BASE}/management/config`, {
      signal: AbortSignal.timeout(300000), // 5 minute timeout for large log files
      headers: this.getHeaders()
    });
    return await this.handleResponse<Config>(res);
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
}

export default ApiService;
