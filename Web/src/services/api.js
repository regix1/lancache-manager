import axios from 'axios';

const api = axios.create({
  baseURL: '/api',
  timeout: 10000,
});

// Request interceptor
api.interceptors.request.use(
  (config) => {
    // Add any auth headers if needed
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

// Response interceptor
api.interceptors.response.use(
  (response) => response.data,
  (error) => {
    console.error('API Error:', error);
    return Promise.reject(error);
  }
);

export default {
  // Downloads
  async getLatestDownloads(count = 50) {
    return api.get('/downloads/latest', { params: { count } });
  },

  async getActiveDownloads() {
    return api.get('/downloads/active');
  },

  // Stats
  async getClientStats() {
    return api.get('/stats/clients');
  },

  async getServiceStats() {
    return api.get('/stats/services');
  },

  // Management
  async getCacheInfo() {
    return api.get('/management/cache-info');
  },

  async clearCache(service = null) {
    return api.post('/management/clear-cache', { service });
  },

  async resetDatabase() {
    return api.post('/management/reset-database');
  },
};