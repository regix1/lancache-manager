import axios from 'axios';

const api = {
  async getLatestDownloads(count = 20) {
    const response = await axios.get(`/api/downloads/latest?count=${count}`);
    return response.data;
  },

  async getClientStats() {
    const response = await axios.get('/api/stats/clients');
    return response.data;
  },

  async getServiceStats() {
    const response = await axios.get('/api/stats/services');
    return response.data;
  },

  async getCacheInfo() {
    const response = await axios.get('/api/management/cache-info');
    return response.data;
  },

  async clearCache(service = null) {
    const response = await axios.post('/api/management/clear-cache', { service });
    return response.data;
  },

  async resetDatabase() {
    const response = await axios.post('/api/management/reset-database');
    return response.data;
  }
};

export default api;