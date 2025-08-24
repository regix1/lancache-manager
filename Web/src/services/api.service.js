import { API_BASE } from '../utils/constants';

class ApiService {
  static async getCacheInfo() {
    const res = await fetch(`${API_BASE}/management/cache`);
    if (!res.ok) throw new Error('Failed to fetch cache info');
    return res.json();
  }

  static async getActiveDownloads() {
    const res = await fetch(`${API_BASE}/downloads/active`);
    if (!res.ok) throw new Error('Failed to fetch active downloads');
    return res.json();
  }

  static async getLatestDownloads() {
    const res = await fetch(`${API_BASE}/downloads/latest`);
    if (!res.ok) throw new Error('Failed to fetch latest downloads');
    return res.json();
  }

  static async getClientStats() {
    const res = await fetch(`${API_BASE}/stats/clients`);
    if (!res.ok) throw new Error('Failed to fetch client stats');
    return res.json();
  }

  static async getServiceStats() {
    const res = await fetch(`${API_BASE}/stats/services`);
    if (!res.ok) throw new Error('Failed to fetch service stats');
    return res.json();
  }

  static async clearCache(service = null) {
    const url = service 
      ? `${API_BASE}/management/cache?service=${service}`
      : `${API_BASE}/management/cache`;
    const res = await fetch(url, { method: 'DELETE' });
    if (!res.ok) throw new Error('Failed to clear cache');
    return res.json();
  }

  static async resetDatabase() {
    const res = await fetch(`${API_BASE}/management/database`, { method: 'DELETE' });
    if (!res.ok) throw new Error('Failed to reset database');
    return res.json();
  }

  static async resetLogPosition() {
    const res = await fetch(`${API_BASE}/management/reset-logs`, { method: 'POST' });
    if (!res.ok) throw new Error('Failed to reset log position');
    return res.json();
  }

  static async processAllLogs() {
    const res = await fetch(`${API_BASE}/management/process-all-logs`, { method: 'POST' });
    if (!res.ok) throw new Error('Failed to start processing all logs');
    return res.json();
  }
}

export default ApiService;