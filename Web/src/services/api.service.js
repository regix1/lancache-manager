import { API_BASE } from '../utils/constants';
import authService from './auth.service';

class ApiService {
  static async handleResponse(response) {
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
  static getHeaders(additionalHeaders = {}) {
    return {
      ...authService.getAuthHeaders(),
      ...additionalHeaders
    };
  }

  static async getCacheInfo(signal) {
    try {
      const res = await fetch(`${API_BASE}/management/cache`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getCacheInfo request aborted (timeout)');
      } else {
        console.error('getCacheInfo error:', error);
      }
      throw error;
    }
  }

  static async getActiveDownloads(signal) {
    try {
      const res = await fetch(`${API_BASE}/downloads/active`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getActiveDownloads request aborted (timeout)');
      } else {
        console.error('getActiveDownloads error:', error);
      }
      throw error;
    }
  }

  static async getLatestDownloads(signal, count = 50) {
    try {
      const actualCount = count === 'unlimited' ? 9999 : count;
      const res = await fetch(`${API_BASE}/downloads/latest?count=${actualCount}`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getLatestDownloads request aborted (timeout)');
      } else {
        console.error('getLatestDownloads error:', error);
      }
      throw error;
    }
  }

  static async getClientStats(signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/clients`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getClientStats request aborted (timeout)');
      } else {
        console.error('getClientStats error:', error);
      }
      throw error;
    }
  }

  static async getServiceStats(signal, since = null) {
    try {
      const url = since 
        ? `${API_BASE}/stats/services?since=${since}`
        : `${API_BASE}/stats/services`;
      const res = await fetch(url, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getServiceStats request aborted (timeout)');
      } else {
        console.error('getServiceStats error:', error);
      }
      throw error;
    }
  }

  // NEW: Dashboard aggregated stats
  static async getDashboardStats(period = '24h', signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/dashboard?period=${period}`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getDashboardStats request aborted (timeout)');
      } else {
        console.error('getDashboardStats error:', error);
      }
      throw error;
    }
  }

  // NEW: Cache effectiveness stats
  static async getCacheEffectiveness(period = '24h', signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/cache-effectiveness?period=${period}`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getCacheEffectiveness request aborted (timeout)');
      } else {
        console.error('getCacheEffectiveness error:', error);
      }
      throw error;
    }
  }

  // NEW: Timeline stats
  static async getTimelineStats(period = '24h', interval = 'hourly', signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/timeline?period=${period}&interval=${interval}`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getTimelineStats request aborted (timeout)');
      } else {
        console.error('getTimelineStats error:', error);
      }
      throw error;
    }
  }

  // NEW: Bandwidth saved stats
  static async getBandwidthSaved(period = 'all', signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/bandwidth-saved?period=${period}`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getBandwidthSaved request aborted (timeout)');
      } else {
        console.error('getBandwidthSaved error:', error);
      }
      throw error;
    }
  }

  // NEW: Top games stats
  static async getTopGames(limit = 10, period = '7d', signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/top-games?limit=${limit}&period=${period}`, { 
        signal,
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      if (error.name === 'AbortError') {
        console.log('getTopGames request aborted (timeout)');
      } else {
        console.error('getTopGames error:', error);
      }
      throw error;
    }
  }

  // Start async cache clearing operation (requires auth)
  static async clearAllCache() {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-all`, { 
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(10000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('clearAllCache error:', error);
      throw error;
    }
  }

  // Get status of cache clearing operation
  static async getCacheClearStatus(operationId) {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-status/${operationId}`, { 
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getCacheClearStatus error:', error);
      throw error;
    }
  }

  // Cancel cache clearing operation (requires auth)
  static async cancelCacheClear(operationId) {
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
  static async getActiveCacheOperations() {
    try {
      const res = await fetch(`${API_BASE}/management/cache/active-operations`, { 
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getActiveCacheOperations error:', error);
      throw error;
    }
  }

  // Legacy method for compatibility
  static async clearCache(service = null) {
    if (service) {
      return await this.removeServiceFromLogs(service);
    } else {
      return await this.clearAllCache();
    }
  }

  // Reset database (requires auth)
  static async resetDatabase() {
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
  static async resetLogPosition() {
    try {
      const res = await fetch(`${API_BASE}/management/reset-logs`, { 
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
  static async processAllLogs() {
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
  static async cancelProcessing() {
    try {
      const res = await fetch(`${API_BASE}/management/cancel-processing`, { 
        method: 'POST',
        headers: this.getHeaders({ 'Content-Type': 'application/json' }),
        signal: AbortSignal.timeout(10000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('cancelProcessing error:', error);
      throw error;
    }
  }

  static async getProcessingStatus() {
    try {
      const res = await fetch(`${API_BASE}/management/processing-status`, { 
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getProcessingStatus error:', error);
      throw error;
    }
  }

  // Remove specific service entries from log file (requires auth)
  static async removeServiceFromLogs(service) {
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
  static async getServiceLogCounts() {
    try {
      const res = await fetch(`${API_BASE}/management/logs/service-counts`, { 
        signal: AbortSignal.timeout(30000),
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getServiceLogCounts error:', error);
      throw error;
    }
  }

  // Get configuration info
  static async getConfig() {
    try {
      const res = await fetch(`${API_BASE}/management/config`, { 
        signal: AbortSignal.timeout(5000),
        headers: this.getHeaders()
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getConfig error:', error);
      // Return defaults if API fails
      return {
        cachePath: '/cache',
        logPath: '/logs/access.log',
        services: ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']
      };
    }
  }
}

export default ApiService;