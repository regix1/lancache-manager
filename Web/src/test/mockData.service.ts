import { SERVICES } from '../utils/constants';
import type { Download, CacheInfo, ClientStat, ServiceStat, DashboardStats } from '../types';

interface MockData {
  cacheInfo: CacheInfo;
  latestDownloads: Download[];
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats;
}

interface GameInfo {
  appId: number;
  name: string;
  size: number;
}

// Type for tracking client activity during mock data generation
interface ClientActivityTracker {
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalDownloads: number;
  lastSeen: Date;
}

// Real Steam games with actual app IDs for proper banner/image display
const STEAM_GAMES: GameInfo[] = [
  // Popular AAA titles
  { appId: 730, name: 'Counter-Strike 2', size: 35 * 1024 * 1024 * 1024 },
  { appId: 570, name: 'Dota 2', size: 40 * 1024 * 1024 * 1024 },
  { appId: 440, name: 'Team Fortress 2', size: 25 * 1024 * 1024 * 1024 },
  { appId: 271590, name: 'Grand Theft Auto V', size: 95 * 1024 * 1024 * 1024 },
  { appId: 1172470, name: 'Apex Legends', size: 70 * 1024 * 1024 * 1024 },
  { appId: 1245620, name: 'ELDEN RING', size: 50 * 1024 * 1024 * 1024 },
  { appId: 1086940, name: "Baldur's Gate 3", size: 120 * 1024 * 1024 * 1024 },
  { appId: 1091500, name: 'Cyberpunk 2077', size: 70 * 1024 * 1024 * 1024 },
  { appId: 1174180, name: 'Red Dead Redemption 2', size: 120 * 1024 * 1024 * 1024 },
  { appId: 1085660, name: 'Destiny 2', size: 105 * 1024 * 1024 * 1024 },
  { appId: 578080, name: 'PUBG: BATTLEGROUNDS', size: 40 * 1024 * 1024 * 1024 },
  { appId: 292030, name: 'The Witcher 3: Wild Hunt', size: 50 * 1024 * 1024 * 1024 },
  { appId: 1716740, name: 'Starfield', size: 140 * 1024 * 1024 * 1024 },
  { appId: 2358720, name: 'Black Myth: Wukong', size: 130 * 1024 * 1024 * 1024 },
  { appId: 2050650, name: 'Resident Evil 4', size: 60 * 1024 * 1024 * 1024 },
  { appId: 883710, name: 'Resident Evil 2', size: 26 * 1024 * 1024 * 1024 },
  // Popular multiplayer games
  { appId: 381210, name: 'Dead by Daylight', size: 45 * 1024 * 1024 * 1024 },
  { appId: 252490, name: 'Rust', size: 25 * 1024 * 1024 * 1024 },
  { appId: 230410, name: 'Warframe', size: 55 * 1024 * 1024 * 1024 },
  { appId: 892970, name: 'Valheim', size: 1.5 * 1024 * 1024 * 1024 },
  { appId: 322330, name: "Don't Starve Together", size: 3 * 1024 * 1024 * 1024 },
  { appId: 550, name: 'Left 4 Dead 2', size: 13 * 1024 * 1024 * 1024 },
  { appId: 632360, name: 'Risk of Rain 2', size: 3 * 1024 * 1024 * 1024 },
  { appId: 1599340, name: 'Lost Ark', size: 80 * 1024 * 1024 * 1024 },
  { appId: 438100, name: 'VRChat', size: 2 * 1024 * 1024 * 1024 },
  // Indie favorites
  { appId: 367520, name: 'Hollow Knight', size: 9 * 1024 * 1024 * 1024 },
  { appId: 413150, name: 'Stardew Valley', size: 0.5 * 1024 * 1024 * 1024 },
  { appId: 105600, name: 'Terraria', size: 0.5 * 1024 * 1024 * 1024 },
  { appId: 1868140, name: 'DAVE THE DIVER', size: 4 * 1024 * 1024 * 1024 },
  // Other popular titles
  { appId: 546560, name: 'Half-Life: Alyx', size: 67 * 1024 * 1024 * 1024 },
  { appId: 4000, name: "Garry's Mod", size: 5 * 1024 * 1024 * 1024 },
  { appId: 812140, name: "Assassin's Creed Odyssey", size: 100 * 1024 * 1024 * 1024 },
  { appId: 238960, name: 'Path of Exile', size: 40 * 1024 * 1024 * 1024 },
  { appId: 1938090, name: 'Call of Duty', size: 150 * 1024 * 1024 * 1024 }
];

// Client IPs simulating a LAN environment
const CLIENT_IPS = [
  '192.168.1.100',
  '192.168.1.101',
  '192.168.1.102',
  '192.168.1.103',
  '192.168.1.104',
  '192.168.1.105',
  '192.168.1.106',
  '192.168.1.107',
  '192.168.1.110',
  '192.168.1.115',
  '10.0.0.50',
  '10.0.0.51',
  '10.0.0.52'
];

class MockDataService {
  static generateMockData(downloadCount: number | 'unlimited' = 'unlimited'): MockData {
    const clients = CLIENT_IPS;

    const steamGames = STEAM_GAMES;

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
    const downloads: Download[] = [];
    const now = new Date();

    // Calculate the actual count - if "unlimited", generate a large dataset
    const actualCount = downloadCount === 'unlimited' ? 500 : downloadCount;

    // Track client activity for accurate stats
    const clientActivity: Record<string, ClientActivityTracker> = {};

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

      let download: Download;

      if (isMetadata) {
        // Metadata download
        const endTime = new Date(startTime.getTime() + Math.random() * 5000); // 0-5 seconds
        download = {
          id: i + 1,
          service,
          clientIp: client,
          startTimeUtc: startTime.toISOString(),
          endTimeUtc: endTime.toISOString(),
          startTimeLocal: startTime.toISOString(),
          endTimeLocal: endTime.toISOString(),
          cacheHitBytes: 0,
          cacheMissBytes: 0,
          totalBytes: 0,
          cacheHitPercent: 0,
          isActive: false,
          gameName: undefined
        };
      } else {
        // Regular download
        let gameName: string | undefined;
        let gameAppId: number | undefined;
        let totalBytes: number;

        if (service === 'steam' && Math.random() < 0.85) {
          // 85% chance of identifiable Steam game
          const game = steamGames[Math.floor(Math.random() * steamGames.length)];
          gameName = game.name;
          gameAppId = game.appId;
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
          startTimeUtc: startTime.toISOString(),
          endTimeUtc: endTime.toISOString(),
          startTimeLocal: startTime.toISOString(),
          endTimeLocal: endTime.toISOString(),
          cacheHitBytes,
          cacheMissBytes,
          totalBytes,
          cacheHitPercent: (cacheHitBytes / totalBytes) * 100,
          isActive: i < 3 && hoursAgo < 0.5, // First 3 recent downloads are active
          gameName,
          gameAppId
        };
      }

      // Track client activity
      if (!clientActivity[client]) {
        clientActivity[client] = {
          totalCacheHitBytes: 0,
          totalCacheMissBytes: 0,
          totalDownloads: 0,
          lastSeen: startTime
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
    downloads.sort(
      (a, b) => new Date(b.startTimeLocal).getTime() - new Date(a.startTimeLocal).getTime()
    );

    // Generate client stats based on actual download activity
    const clientStats: ClientStat[] = clients
      .map((ip) => {
        const activity = clientActivity[ip];

        if (activity) {
          // Use actual data from downloads
          const totalBytes = activity.totalCacheHitBytes + activity.totalCacheMissBytes;
          const lastSeenIso = activity.lastSeen.toISOString();
          return {
            clientIp: ip,
            displayName: undefined,
            groupId: undefined,
            isGrouped: false,
            groupMemberIps: undefined,
            totalCacheHitBytes: activity.totalCacheHitBytes,
            totalCacheMissBytes: activity.totalCacheMissBytes,
            totalBytes: totalBytes,
            cacheHitPercent: totalBytes > 0 ? (activity.totalCacheHitBytes / totalBytes) * 100 : 0,
            totalDownloads: activity.totalDownloads,
            lastActivityUtc: lastSeenIso,
            lastActivityLocal: lastSeenIso
          } as ClientStat;
        } else {
          // Client had no downloads - return null to filter out
          return null;
        }
      })
      .filter((client): client is ClientStat => client !== null && client.totalBytes > 0);

    // Generate service stats
    const serviceStats = SERVICES.map((service) => {
      const serviceDownloads = downloads.filter((d) => d.service === service);
      const hitBytes = serviceDownloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
      const missBytes = serviceDownloads.reduce((sum, d) => sum + d.cacheMissBytes, 0);

      const lastActivity =
        serviceDownloads[0]?.startTimeLocal ||
        new Date(now.getTime() - Math.random() * 7200000).toISOString();

      return {
        service,
        totalCacheHitBytes: hitBytes || cacheInfo.serviceSizes[service] * 0.8,
        totalCacheMissBytes: missBytes || cacheInfo.serviceSizes[service] * 0.2,
        totalBytes: hitBytes + missBytes || cacheInfo.serviceSizes[service],
        cacheHitPercent: hitBytes + missBytes > 0 ? (hitBytes / (hitBytes + missBytes)) * 100 : 80,
        totalDownloads: serviceDownloads.length,
        lastActivityUtc: lastActivity,
        lastActivityLocal: lastActivity
      };
    });

    // Generate dashboard stats
    const totalCacheHit = downloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
    const totalCacheMiss = downloads.reduce((sum, d) => sum + d.cacheMissBytes, 0);
    const totalBytes = totalCacheHit + totalCacheMiss;
    const topServiceStat = serviceStats.reduce(
      (max, stat) => (stat.totalBytes > max.totalBytes ? stat : max),
      serviceStats[0]
    );

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
      serviceBreakdown: serviceStats
        .map((stat) => ({
          service: stat.service,
          bytes: stat.totalBytes,
          percentage: totalBytes > 0 ? (stat.totalBytes / totalBytes) * 100 : 0
        }))
        .sort((a, b) => b.bytes - a.bytes),
      lastUpdated: now
    };

    return {
      cacheInfo,
      latestDownloads: downloads,
      clientStats,
      serviceStats,
      dashboardStats
    };
  }

  static generateRealtimeUpdate(): Download {
    const isMetadata = Math.random() < 0.15;
    const nowIso = new Date().toISOString();

    if (isMetadata) {
      return {
        id: Date.now(),
        service: SERVICES[Math.floor(Math.random() * SERVICES.length)],
        clientIp: CLIENT_IPS[Math.floor(Math.random() * CLIENT_IPS.length)],
        startTimeUtc: nowIso,
        endTimeUtc: nowIso,
        startTimeLocal: nowIso,
        endTimeLocal: nowIso,
        cacheHitBytes: 0,
        cacheMissBytes: 0,
        totalBytes: 0,
        cacheHitPercent: 0,
        isActive: false
      };
    }

    // Pick a random real game for realistic updates
    const game = STEAM_GAMES[Math.floor(Math.random() * STEAM_GAMES.length)];
    const cacheHitRatio = 0.7 + Math.random() * 0.25;
    const totalBytes = Math.floor(game.size * (0.1 + Math.random() * 0.9));
    const cacheHitBytes = Math.floor(totalBytes * cacheHitRatio);
    const cacheMissBytes = totalBytes - cacheHitBytes;

    return {
      id: Date.now(),
      service: 'steam',
      clientIp: CLIENT_IPS[Math.floor(Math.random() * CLIENT_IPS.length)],
      startTimeUtc: nowIso,
      endTimeUtc: null,
      startTimeLocal: nowIso,
      endTimeLocal: null,
      cacheHitBytes,
      cacheMissBytes,
      totalBytes,
      cacheHitPercent: (cacheHitBytes / totalBytes) * 100,
      isActive: true,
      gameName: game.name,
      gameAppId: game.appId
    };
  }
}

export default MockDataService;
