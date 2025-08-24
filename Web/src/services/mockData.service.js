import { SERVICES } from '../utils/constants';

class MockDataService {
  static generateMockData() {
    const clients = ['192.168.1.100', '192.168.1.101', '192.168.1.102', '192.168.1.103', '192.168.1.104'];
    
    // Generate cache info
    const cacheInfo = {
      totalCacheSize: 2000000000000, // 2TB
      usedCacheSize: 1450000000000, // 1.45TB
      freeCacheSize: 550000000000,
      usagePercent: 72.5,
      totalFiles: 48293,
      serviceSizes: {
        steam: 650000000000,
        epic: 320000000000,
        origin: 180000000000,
        blizzard: 150000000000,
        wsus: 100000000000,
        riot: 50000000000
      }
    };

    // Generate downloads
    const downloads = [];
    const now = new Date();
    for (let i = 0; i < 50; i++) {
      const service = SERVICES[Math.floor(Math.random() * SERVICES.length)];
      const client = clients[Math.floor(Math.random() * clients.length)];
      const cacheHitBytes = Math.floor(Math.random() * 5000000000);
      const cacheMissBytes = Math.floor(Math.random() * 1000000000);
      
      downloads.push({
        id: i + 1,
        service,
        clientIp: client,
        startTime: new Date(now - Math.random() * 86400000).toISOString(),
        endTime: new Date(now - Math.random() * 82800000).toISOString(),
        cacheHitBytes,
        cacheMissBytes,
        totalBytes: cacheHitBytes + cacheMissBytes,
        cacheHitPercent: (cacheHitBytes / (cacheHitBytes + cacheMissBytes)) * 100,
        isActive: i < 3
      });
    }

    // Generate client stats
    const clientStats = clients.map(ip => {
      const hitBytes = Math.floor(Math.random() * 100000000000);
      const missBytes = Math.floor(Math.random() * 20000000000);
      return {
        clientIp: ip,
        totalCacheHitBytes: hitBytes,
        totalCacheMissBytes: missBytes,
        totalBytes: hitBytes + missBytes,
        cacheHitPercent: (hitBytes / (hitBytes + missBytes)) * 100,
        totalDownloads: Math.floor(Math.random() * 100) + 10,
        lastSeen: new Date(now - Math.random() * 3600000).toISOString()
      };
    });

    // Generate service stats
    const serviceStats = SERVICES.map(service => {
      const hitBytes = cacheInfo.serviceSizes[service] * 0.8 || Math.floor(Math.random() * 500000000000);
      const missBytes = cacheInfo.serviceSizes[service] * 0.2 || Math.floor(Math.random() * 100000000000);
      return {
        service,
        totalCacheHitBytes: hitBytes,
        totalCacheMissBytes: missBytes,
        totalBytes: hitBytes + missBytes,
        cacheHitPercent: (hitBytes / (hitBytes + missBytes)) * 100,
        totalDownloads: Math.floor(Math.random() * 200) + 50,
        lastActivity: new Date(now - Math.random() * 7200000).toISOString()
      };
    });

    return {
      cacheInfo,
      activeDownloads: downloads.filter(d => d.isActive),
      latestDownloads: downloads,
      clientStats,
      serviceStats
    };
  }

  static generateRealtimeUpdate() {
    const clients = ['192.168.1.100', '192.168.1.101', '192.168.1.102', '192.168.1.103', '192.168.1.104'];
    
    const cacheHitBytes = Math.floor(Math.random() * 500000000);
    const cacheMissBytes = Math.floor(Math.random() * 100000000);
    
    return {
      id: Date.now(),
      service: SERVICES[Math.floor(Math.random() * SERVICES.length)],
      clientIp: clients[Math.floor(Math.random() * clients.length)],
      startTime: new Date().toISOString(),
      endTime: new Date().toISOString(),
      cacheHitBytes,
      cacheMissBytes,
      totalBytes: cacheHitBytes + cacheMissBytes,
      cacheHitPercent: (cacheHitBytes / (cacheHitBytes + cacheMissBytes)) * 100,
      isActive: true
    };
  }
}

export default MockDataService;