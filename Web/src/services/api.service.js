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
      console.error('getCacheInfo error:', error);
      throw error;
    }
  }

  static async getActiveDownloads(signal) {
    try {
      const res = await fetch(`${API_BASE}/downloads/active`, { signal });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getActiveDownloads error:', error);
      throw error;
    }
  }

  static async getLatestDownloads(signal) {
    try {
      const res = await fetch(`${API_BASE}/downloads/latest`, { signal });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getLatestDownloads error:', error);
      throw error;
    }
  }

  static async getClientStats(signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/clients`, { signal });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getClientStats error:', error);
      throw error;
    }
  }

  static async getServiceStats(signal) {
    try {
      const res = await fetch(`${API_BASE}/stats/services`, { signal });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('getServiceStats error:', error);
      throw error;
    }
  }

  static async clearCache(service = null) {
    try {
      const url = service 
        ? `${API_BASE}/management/cache?service=${service}`
        : `${API_BASE}/management/cache`;
      const res = await fetch(url, { 
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(30000) // 30 second timeout
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('clearCache error:', error);
      throw error;
    }
  }

  static async resetDatabase() {
    try {
      const res = await fetch(`${API_BASE}/management/database`, { 
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(30000)
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
        signal: AbortSignal.timeout(30000)
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
        signal: AbortSignal.timeout(60000) // 60 second timeout for this longer operation
      });
      return await this.handleResponse(res);
    } catch (error) {
      console.error('processAllLogs error:', error);
      throw error;
    }
  }
}

export default ApiService;