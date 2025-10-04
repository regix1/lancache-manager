import { SERVICES } from '../utils/constants';

interface MockData {
  cacheInfo: any;
  activeDownloads: any[];
  latestDownloads: any[];
  clientStats: any[];
  serviceStats: any[];
  dashboardStats: any;
}

interface GameInfo {
  name: string;
  size: number;
}

class MockDataService {
  static generateMockData(downloadCount: number | 'unlimited' = 'unlimited'): MockData {
    const clients = [
      '192.168.1.100',
      '192.168.1.101',
      '192.168.1.102',
      '192.168.1.103',
      '192.168.1.104',
      '192.168.1.105',
      '192.168.1.106',
      '192.168.1.107',
      '192.168.1.108',
      '192.168.1.109',
      '192.168.1.110',
      '192.168.1.111',
      '10.0.0.50',
      '10.0.0.51',
      '10.0.0.52',
      '10.0.0.53',
      '127.0.0.1'
    ];

    const steamGames: GameInfo[] = [
      { name: 'Counter-Strike 2', size: 30 * 1024 * 1024 * 1024 },
      { name: 'Dota 2', size: 35 * 1024 * 1024 * 1024 },
      { name: 'Team Fortress 2', size: 25 * 1024 * 1024 * 1024 },
      { name: 'Grand Theft Auto V', size: 95 * 1024 * 1024 * 1024 },
      { name: 'Apex Legends', size: 60 * 1024 * 1024 * 1024 },
      { name: 'Dead by Daylight', size: 45 * 1024 * 1024 * 1024 },
      { name: 'Marvel Rivals', size: 55 * 1024 * 1024 * 1024 },
      { name: 'Path of Exile', size: 40 * 1024 * 1024 * 1024 },
      { name: 'Warframe', size: 50 * 1024 * 1024 * 1024 },
      { name: 'Destiny 2', size: 105 * 1024 * 1024 * 1024 },
      { name: 'Rust', size: 35 * 1024 * 1024 * 1024 },
      { name: 'Valheim', size: 1 * 1024 * 1024 * 1024 },
      { name: 'Unknown Steam Game', size: 15 * 1024 * 1024 * 1024 }
    ];

    // Generate cache info
    const cacheInfo = {
      totalCacheSize: 2000000000000, // 2TB
      usedCacheSize: 1450000000000, // 1.45TB
      freeCacheSize: 550000000000,
      usagePercent: 72.5,
      totalFiles: 48293 + (typeof downloadCount === 'number' ? downloadCount : 500) * 100,
      serviceSizes: {
        steam: 650000000000,
        epic: 320000000000,
        origin: 180000000000,
        blizzard: 150000000000,
        wsus: 100000000000,
        riot: 50000000000
      }
    };

    // Generate downloads with realistic patterns
    const downloads: any[] = [];
    const now = new Date();

    // Calculate the actual count - if "unlimited", generate a large dataset
    const actualCount = downloadCount === 'unlimited' ? 500 : downloadCount;

    // Track client activity for accurate stats
    const clientActivity: Record<string, any> = {};

    for (let i = 0; i < actualCount; i++) {
      const service = SERVICES[Math.floor(Math.random() * SERVICES.length)];
      const client = clients[Math.floor(Math.random() * clients.length)];

      // 30% chance of metadata/zero-byte download
      const isMetadata = Math.random() < 0.3;

      // Time distribution - more recent downloads at the top
      // Spread over 90 days instead of just 7 for better "all time" data
      const hoursAgo = Math.pow(i / actualCount, 2) * 2160; // Up to 90 days ago, exponentially distributed
      const startTime = new Date(
        now.getTime() - hoursAgo * 60 * 60 * 1000 - Math.random() * 3600000
      );

      let download: any;

      if (isMetadata) {
        // Metadata download
        const endTime = new Date(startTime.getTime() + Math.random() * 5000); // 0-5 seconds
        download = {
          id: i + 1,
          service,
          clientIp: client,
          startTime: startTime.toISOString(),
          endTime: endTime.toISOString(),
          cacheHitBytes: 0,
          cacheMissBytes: 0,
          totalBytes: 0,
          cacheHitPercent: 0,
          isActive: false,
          gameName: null
        };
      } else {
        // Regular download
        let gameName = null;
        let totalBytes: number;

        if (service === 'steam' && Math.random() < 0.7) {
          // 70% chance of identifiable Steam game
          const game = steamGames[Math.floor(Math.random() * steamGames.length)];
          gameName = game.name;
          // Vary the size a bit (80-100% of full game size)
          totalBytes = Math.floor(game.size * (0.8 + Math.random() * 0.2));
        } else {
          // Generic content
          totalBytes = Math.floor(Math.random() * 50 * 1024 * 1024 * 1024); // Up to 50GB
        }

        // Cache hit ratio varies by age - older downloads have better cache hit
        const cacheHitRatio = Math.min(0.95, 0.1 + (hoursAgo / 2160) * 0.85);
        const cacheHitBytes = Math.floor(totalBytes * cacheHitRatio);
        const cacheMissBytes = totalBytes - cacheHitBytes;

        // Duration based on size and whether it's cached
        const downloadSpeed = cacheHitRatio > 0.8 ? 500 * 1024 * 1024 : 50 * 1024 * 1024; // 500MB/s cached, 50MB/s uncached
        const durationMs = (totalBytes / downloadSpeed) * 1000;
        const endTime = new Date(startTime.getTime() + durationMs);

        download = {
          id: i + 1,
          service,
          clientIp: client,
          startTime: startTime.toISOString(),
          endTime: endTime.toISOString(),
          cacheHitBytes,
          cacheMissBytes,
          totalBytes,
          cacheHitPercent: (cacheHitBytes / totalBytes) * 100,
          isActive: i < 3 && hoursAgo < 0.5, // First 3 recent downloads are active
          gameName,
          gameAppId:
            gameName && gameName !== 'Unknown Steam Game'
              ? 200000 + Math.floor(Math.random() * 2000000)
              : null
        };
      }

      // Track client activity
      if (!clientActivity[client]) {
        clientActivity[client] = {
          totalCacheHitBytes: 0,
          totalCacheMissBytes: 0,
          totalDownloads: 0,
          lastActivityLocal: startTime
        };
      }

      clientActivity[client].totalCacheHitBytes += download.cacheHitBytes || 0;
      clientActivity[client].totalCacheMissBytes += download.cacheMissBytes || 0;
      clientActivity[client].totalDownloads += 1;

      // Update last seen if this is more recent
      if (startTime > clientActivity[client].lastSeen) {
        clientActivity[client].lastSeen = startTime;
      }

      downloads.push(download);
    }

    // Sort by start time (most recent first)
    downloads.sort((a, b) => new Date(b.startTimeLocal).getTime() - new Date(a.startTimeLocal).getTime());

    // Generate client stats based on actual download activity
    const clientStats = clients
      .map((ip) => {
        const activity = clientActivity[ip];

        if (activity) {
          // Use actual data from downloads
          const totalBytes = activity.totalCacheHitBytes + activity.totalCacheMissBytes;
          return {
            clientIp: ip,
            totalCacheHitBytes: activity.totalCacheHitBytes,
            totalCacheMissBytes: activity.totalCacheMissBytes,
            totalBytes: totalBytes,
            cacheHitPercent: totalBytes > 0 ? (activity.totalCacheHitBytes / totalBytes) * 100 : 0,
            totalDownloads: activity.totalDownloads,
            lastActivityLocal: activity.lastSeen.toISOString()
          };
        } else {
          // Client had no downloads - return zeros
          return {
            clientIp: ip,
            totalCacheHitBytes: 0,
            totalCacheMissBytes: 0,
            totalBytes: 0,
            cacheHitPercent: 0,
            totalDownloads: 0,
            lastActivityLocal: null
          };
        }
      })
      .filter((client) => client.totalBytes > 0); // Only include clients with activity

    // Generate service stats
    const serviceStats = SERVICES.map((service) => {
      const serviceDownloads = downloads.filter((d) => d.service === service);
      const hitBytes = serviceDownloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
      const missBytes = serviceDownloads.reduce((sum, d) => sum + d.cacheMissBytes, 0);

      return {
        service,
        totalCacheHitBytes: hitBytes || cacheInfo.serviceSizes[service] * 0.8,
        totalCacheMissBytes: missBytes || cacheInfo.serviceSizes[service] * 0.2,
        totalBytes: hitBytes + missBytes || cacheInfo.serviceSizes[service],
        cacheHitPercent: hitBytes + missBytes > 0 ? (hitBytes / (hitBytes + missBytes)) * 100 : 80,
        totalDownloads: serviceDownloads.length,
        lastActivityLocal:
          serviceDownloads[0]?.startTimeLocal ||
          new Date(now.getTime() - Math.random() * 7200000).toISOString()
      };
    });

    // Generate dashboard stats
    const totalCacheHit = downloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
    const totalCacheMiss = downloads.reduce((sum, d) => sum + d.cacheMissBytes, 0);
    const totalBytes = totalCacheHit + totalCacheMiss;
    const topServiceStat = serviceStats.reduce((max, stat) =>
      stat.totalBytes > max.totalBytes ? stat : max, serviceStats[0]);

    const dashboardStats = {
      totalBandwidthSaved: totalCacheHit,
      totalAddedToCache: totalCacheMiss,
      totalServed: totalBytes,
      cacheHitRatio: totalBytes > 0 ? (totalCacheHit / totalBytes) * 100 : 0,
      activeDownloads: downloads.filter((d) => d.isActive).length,
      uniqueClients: clientStats.length,
      topService: topServiceStat?.service || 'steam',
      period: {
        duration: 'all',
        since: null,
        bandwidthSaved: totalCacheHit,
        addedToCache: totalCacheMiss,
        totalServed: totalBytes,
        hitRatio: totalBytes > 0 ? (totalCacheHit / totalBytes) * 100 : 0,
        downloads: downloads.length
      },
      serviceBreakdown: serviceStats.map(stat => ({
        service: stat.service,
        bytes: stat.totalBytes,
        percentage: totalBytes > 0 ? (stat.totalBytes / totalBytes) * 100 : 0
      })).sort((a, b) => b.bytes - a.bytes),
      lastUpdated: now
    };

    return {
      cacheInfo,
      activeDownloads: downloads.filter((d) => d.isActive),
      latestDownloads: downloads,
      clientStats,
      serviceStats,
      dashboardStats
    };
  }

  static generateRealtimeUpdate(): any {
    const clients = [
      '192.168.1.100',
      '192.168.1.101',
      '192.168.1.102',
      '192.168.1.103',
      '192.168.1.104',
      '192.168.1.105',
      '192.168.1.106',
      '192.168.1.107'
    ];

    const isMetadata = Math.random() < 0.2;

    if (isMetadata) {
      return {
        id: Date.now(),
        service: SERVICES[Math.floor(Math.random() * SERVICES.length)],
        clientIp: clients[Math.floor(Math.random() * clients.length)],
        startTime: new Date().toISOString(),
        endTime: new Date().toISOString(),
        cacheHitBytes: 0,
        cacheMissBytes: 0,
        totalBytes: 0,
        cacheHitPercent: 0,
        isActive: false
      };
    }

    const cacheHitBytes = Math.floor(Math.random() * 500000000);
    const cacheMissBytes = Math.floor(Math.random() * 100000000);

    return {
      id: Date.now(),
      service: SERVICES[Math.floor(Math.random() * SERVICES.length)],
      clientIp: clients[Math.floor(Math.random() * clients.length)],
      startTime: new Date().toISOString(),
      endTime: null,
      cacheHitBytes,
      cacheMissBytes,
      totalBytes: cacheHitBytes + cacheMissBytes,
      cacheHitPercent: (cacheHitBytes / (cacheHitBytes + cacheMissBytes)) * 100,
      isActive: true,
      gameName: Math.random() < 0.5 ? 'Counter-Strike 2' : null
    };
  }
}

export default MockDataService;
