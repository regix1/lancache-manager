import { API_BASE } from '../utils/constants';

class ApiService {
  static async handleResponse(response) {
    if (!response.ok) {
      const error = await response.text().catch(() => '');
      throw new Error(`HTTP ${response.status}: ${error || response.statusText}`);
    }
    return response.json();
  }

  static async getCacheInfo(signal) {
    try {
      const res = await fetch(`${API_BASE}/management/cache`, { signal });
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
      const res = await fetch(`${API_BASE}/downloads/active`, { signal });
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

  static async getLatestDownloads(signal) {
    try {
      const res = await fetch(`${API_BASE}/downloads/latest`, { signal });
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
      const res = await fetch(`${API_BASE}/stats/clients`, { signal });
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

  static async getServiceStats(signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/services`, { signal });
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

  // Start async cache clearing operation
  static async clearAllCache() {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-all`, { 
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(10000) // 10 second timeout to start operation
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
        signal: AbortSignal.timeout(5000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getCacheClearStatus error:', error);
      throw error;
    }
  }

  // Cancel cache clearing operation
  static async cancelCacheClear(operationId) {
    try {
      const res = await fetch(`${API_BASE}/management/cache/clear-cancel/${operationId}`, { 
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
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
        signal: AbortSignal.timeout(5000)
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

  static async resetDatabase() {
    try {
      const res = await fetch(`${API_BASE}/management/database`, { 
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(60000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('resetDatabase error:', error);
      throw error;
    }
  }

  static async resetLogPosition() {
    try {
      const res = await fetch(`${API_BASE}/management/reset-logs`, { 
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(60000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('resetLogPosition error:', error);
      throw error;
    }
  }

  static async processAllLogs() {
    try {
      const res = await fetch(`${API_BASE}/management/process-all-logs`, { 
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(120000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('processAllLogs error:', error);
      throw error;
    }
  }

  static async cancelProcessing() {
    try {
      const res = await fetch(`${API_BASE}/management/cancel-processing`, { 
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
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
        signal: AbortSignal.timeout(5000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getProcessingStatus error:', error);
      throw error;
    }
  }

  // Remove specific service entries from log file
  static async removeServiceFromLogs(service) {
    try {
      const res = await fetch(`${API_BASE}/management/logs/remove-service`, { 
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ service }),
        signal: AbortSignal.timeout(120000) // 2 minutes for large logs
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
        signal: AbortSignal.timeout(30000) // 30 seconds
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getServiceLogCounts error:', error);
      throw error;
    }
  }

  // Get configuration info (cache path, services list, etc.)
  static async getConfig() {
    try {
      const res = await fetch(`${API_BASE}/management/config`, { 
        signal: AbortSignal.timeout(5000)
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getConfig error:', error);
      // Return defaults if API fails
      return {
        cachePath: '/mnt/cache/cache',
        logPath: '/logs/access.log',
        services: ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']
      };
    }
  }
}

export default ApiService;