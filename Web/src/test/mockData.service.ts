import { SERVICES } from '../utils/constants';
import type {
  Download,
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  HourlyActivityResponse,
  HourlyActivityItem,
  GameDetectionSummary,
  ServiceDetectionSummary,
  Event,
  EventCompareResponse,
  SparklineDataResponse
} from '../types';
import type { CachedDetectionResponse } from '../contexts/DashboardDataContext/types';

interface MockData {
  cacheInfo: CacheInfo;
  latestDownloads: Download[];
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats;
}

interface GameInfo {
  appId: string;
  name: string;
  size: number;
}

type MockEventProfile = 'lan' | 'lanHot' | 'evening' | 'midday' | 'live';

interface MockEventSpec {
  id: number;
  name: string;
  description: string;
  colorIndex: number;
  start: Date;
  end: Date;
  profile: MockEventProfile;
}

const GIGABYTE = 1024 * 1024 * 1024;

function hoursFrom(now: Date, hours: number): Date {
  return new Date(now.getTime() + hours * 60 * 60 * 1000);
}

function toEvent(spec: MockEventSpec): Event {
  const start = spec.start.toISOString();
  const end = spec.end.toISOString();
  return {
    id: spec.id,
    name: spec.name,
    description: spec.description,
    startTimeUtc: start,
    endTimeUtc: end,
    colorIndex: spec.colorIndex,
    createdAtUtc: start
  };
}

function mockEventSpecs(now: Date): MockEventSpec[] {
  return [
    {
      id: 9001,
      name: 'LAN Party 2024',
      description: 'First year at the warehouse. Cache was still cold.',
      colorIndex: 1,
      start: hoursFrom(now, -(52 * 7 * 24 + 48)),
      end: hoursFrom(now, -(52 * 7 * 24)),
      profile: 'lan'
    },
    {
      id: 9002,
      name: 'LAN Party 2025',
      description: 'Same weekend a year later. Higher hit rate after the first party.',
      colorIndex: 2,
      start: hoursFrom(now, -(14 * 24 + 48)),
      end: hoursFrom(now, -(14 * 24)),
      profile: 'lanHot'
    },
    {
      id: 9003,
      name: 'Summer LAN',
      description: 'Saturday daytime session.',
      colorIndex: 3,
      start: hoursFrom(now, -(28 * 24 + 12)),
      end: hoursFrom(now, -(28 * 24)),
      profile: 'midday'
    },
    {
      id: 9004,
      name: 'Friday Night Fights',
      description: 'Eight-hour evening session.',
      colorIndex: 4,
      start: hoursFrom(now, -(3 * 24 + 8)),
      end: hoursFrom(now, -(3 * 24)),
      profile: 'evening'
    },
    {
      id: 9005,
      name: 'Open House',
      description: 'In-progress session so the compare chart has a live overlay.',
      colorIndex: 5,
      start: hoursFrom(now, -3),
      end: hoursFrom(now, 3),
      profile: 'live'
    }
  ];
}

/**
 * Mirrors `SparklineBuckets.ResolveMinutes` in the API, which mock mode never reaches. Change the
 * thresholds and widths in both places together.
 */
function resolveMockBucketMinutes(rangeHours: number): number {
  if (rangeHours <= 2) {
    return 15;
  }
  if (rangeHours <= 13) {
    return 30;
  }
  if (rangeHours <= 25) {
    return 60;
  }
  if (rangeHours <= 240) {
    return 180;
  }
  return 1440;
}

function profileWave(profile: MockEventProfile, t: number): { served: number; saved: number } {
  let peak = 0.45;
  let amp = 40;
  let hit = 0.75;
  switch (profile) {
    case 'lan':
      peak = 0.42;
      amp = 90;
      hit = 0.62;
      break;
    case 'lanHot':
      peak = 0.38;
      amp = 130;
      hit = 0.88;
      break;
    case 'evening':
      peak = 0.65;
      amp = 35;
      hit = 0.8;
      break;
    case 'midday':
      peak = 0.5;
      amp = 22;
      hit = 0.7;
      break;
    case 'live':
      peak = 0.85;
      amp = 18;
      hit = 0.9;
      break;
  }

  let bell = Math.exp(-(((t - peak) * 3.2) ** 2));
  if (profile === 'lan' || profile === 'lanHot') {
    const saturday = Math.exp(-(((t - 0.55) * 5) ** 2));
    bell = Math.max(bell, saturday * 0.7);
  }

  const served = (0.08 + bell * 0.92) * amp * GIGABYTE;
  return { served, saved: served * hit };
}

// Services the demo library is spread across, cycled by index so the split is stable between
// renders instead of reshuffling. Steam repeats because a real cache is mostly Steam, and because
// anything that groups or filters by service needs one clearly dominant group to look right.
// Every entry is a key from SERVICES, so display names resolve the same way as live data.
const MOCK_DETECTION_SERVICES = [
  'steam',
  'steam',
  'epicgames',
  'steam',
  'xbox',
  'blizzard',
  'steam',
  'riot',
  'origin',
  'steam',
  'wsus'
];

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
  { appId: '730', name: 'Counter-Strike 2', size: 35 * 1024 * 1024 * 1024 },
  { appId: '570', name: 'Dota 2', size: 40 * 1024 * 1024 * 1024 },
  { appId: '440', name: 'Team Fortress 2', size: 25 * 1024 * 1024 * 1024 },
  { appId: '271590', name: 'Grand Theft Auto V', size: 95 * 1024 * 1024 * 1024 },
  { appId: '1172470', name: 'Apex Legends', size: 70 * 1024 * 1024 * 1024 },
  { appId: '1245620', name: 'ELDEN RING', size: 50 * 1024 * 1024 * 1024 },
  { appId: '1086940', name: "Baldur's Gate 3", size: 120 * 1024 * 1024 * 1024 },
  { appId: '1091500', name: 'Cyberpunk 2077', size: 70 * 1024 * 1024 * 1024 },
  { appId: '1174180', name: 'Red Dead Redemption 2', size: 120 * 1024 * 1024 * 1024 },
  { appId: '1085660', name: 'Destiny 2', size: 105 * 1024 * 1024 * 1024 },
  { appId: '578080', name: 'PUBG: BATTLEGROUNDS', size: 40 * 1024 * 1024 * 1024 },
  { appId: '292030', name: 'The Witcher 3: Wild Hunt', size: 50 * 1024 * 1024 * 1024 },
  { appId: '1716740', name: 'Starfield', size: 140 * 1024 * 1024 * 1024 },
  { appId: '2358720', name: 'Black Myth: Wukong', size: 130 * 1024 * 1024 * 1024 },
  { appId: '2050650', name: 'Resident Evil 4', size: 60 * 1024 * 1024 * 1024 },
  { appId: '883710', name: 'Resident Evil 2', size: 26 * 1024 * 1024 * 1024 },
  // Popular multiplayer games
  { appId: '381210', name: 'Dead by Daylight', size: 45 * 1024 * 1024 * 1024 },
  { appId: '252490', name: 'Rust', size: 25 * 1024 * 1024 * 1024 },
  { appId: '230410', name: 'Warframe', size: 55 * 1024 * 1024 * 1024 },
  { appId: '892970', name: 'Valheim', size: 1.5 * 1024 * 1024 * 1024 },
  { appId: '322330', name: "Don't Starve Together", size: 3 * 1024 * 1024 * 1024 },
  { appId: '550', name: 'Left 4 Dead 2', size: 13 * 1024 * 1024 * 1024 },
  { appId: '632360', name: 'Risk of Rain 2', size: 3 * 1024 * 1024 * 1024 },
  { appId: '1599340', name: 'Lost Ark', size: 80 * 1024 * 1024 * 1024 },
  { appId: '438100', name: 'VRChat', size: 2 * 1024 * 1024 * 1024 },
  // Indie favorites
  { appId: '367520', name: 'Hollow Knight', size: 9 * 1024 * 1024 * 1024 },
  { appId: '413150', name: 'Stardew Valley', size: 0.5 * 1024 * 1024 * 1024 },
  { appId: '105600', name: 'Terraria', size: 0.5 * 1024 * 1024 * 1024 },
  { appId: '1868140', name: 'DAVE THE DIVER', size: 4 * 1024 * 1024 * 1024 },
  // Other popular titles
  { appId: '546560', name: 'Half-Life: Alyx', size: 67 * 1024 * 1024 * 1024 },
  { appId: '4000', name: "Garry's Mod", size: 5 * 1024 * 1024 * 1024 },
  { appId: '812140', name: "Assassin's Creed Odyssey", size: 100 * 1024 * 1024 * 1024 },
  { appId: '238960', name: 'Path of Exile', size: 40 * 1024 * 1024 * 1024 },
  { appId: '1938090', name: 'Call of Duty', size: 150 * 1024 * 1024 * 1024 }
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

// Mirrors /api/stats/clients group folding: nicknamed rows, multi-IP collapse, and the
// per-IP rows a nickname reports when it is set to separate rows. Both row modes are
// represented on multi-IP nicknames so either one can be seen without a live server.
interface MockClientGroup {
  id: number;
  nickname: string;
  memberIps: string[];
  separateMemberRows: boolean;
}

const MOCK_CLIENT_GROUPS: MockClientGroup[] = [
  {
    id: 1,
    nickname: 'Living Room',
    memberIps: ['192.168.1.100', '192.168.1.101'],
    separateMemberRows: false
  },
  { id: 2, nickname: 'Office PC', memberIps: ['10.0.0.50'], separateMemberRows: false },
  {
    id: 3,
    nickname: 'Lab Bench',
    memberIps: ['10.0.0.51', '10.0.0.52'],
    separateMemberRows: true
  }
];

class MockDataService {
  static generateMockData(downloadCount: number | 'unlimited' = 'unlimited'): MockData {
    const clients = CLIENT_IPS;

    const steamGames = STEAM_GAMES;

    // Generate cache info
    const cacheInfo = {
      totalCacheSize: 2000000000000, // 2TB (configured size)
      configuredCacheSize: 2000000000000, // 2TB
      driveCapacity: 4000000000000, // 4TB (physical drive)
      usedCacheSize: 1450000000000, // 1.45TB
      freeCacheSize: 550000000000,
      usagePercent: 72.5,
      totalFiles: 48293 + (typeof downloadCount === 'number' ? downloadCount : 500) * 100,
      hasCacheScan: true,
      serviceSizes: {
        steam: 650000000000,
        epicgames: 320000000000,
        origin: 180000000000,
        blizzard: 150000000000,
        wsus: 100000000000,
        riot: 50000000000,
        xbox: 80000000000
      } as Record<string, number>
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
          cacheHitBytes: 0,
          cacheMissBytes: 0,
          totalBytes: 0,
          cacheHitPercent: 0,
          isActive: false,
          gameName: undefined,
          averageBytesPerSecond: 0,
          isEvicted: false
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
          gameAppId = parseInt(game.appId, 10);
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

        // ~8% of older downloads are evicted (only non-active, older ones)
        const isEvicted = !!(hoursAgo > 200 && Math.random() < 0.08);

        download = {
          id: i + 1,
          service,
          clientIp: client,
          startTimeUtc: startTime.toISOString(),
          endTimeUtc: endTime.toISOString(),
          cacheHitBytes,
          cacheMissBytes,
          totalBytes,
          cacheHitPercent: (cacheHitBytes / totalBytes) * 100,
          isActive: i < 3 && hoursAgo < 0.5, // First 3 recent downloads are active
          gameName,
          gameAppId,
          averageBytesPerSecond: durationMs > 0 ? totalBytes / (durationMs / 1000) : 0,
          isEvicted
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
      (a, b) => new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
    );

    // Generate client stats based on actual download activity, then fold nicknamed
    // groups the same way ClientStatsAggregationHelper does for the live API.
    const ipToGroup = new Map(
      MOCK_CLIENT_GROUPS.flatMap((g) => g.memberIps.map((ip) => [ip, g] as const))
    );
    const foldedGroups = new Map<number, ClientStat>();
    const separatedMembers: ClientStat[] = [];
    const ungrouped: ClientStat[] = [];

    for (const ip of clients) {
      const activity = clientActivity[ip];
      if (!activity) continue;

      const totalBytes = activity.totalCacheHitBytes + activity.totalCacheMissBytes;
      if (totalBytes <= 0) continue;

      const lastSeenIso = activity.lastSeen.toISOString();
      const group = ipToGroup.get(ip);

      if (!group) {
        ungrouped.push({
          clientIp: ip,
          displayName: undefined,
          groupId: undefined,
          isGrouped: false,
          groupMemberIps: undefined,
          totalCacheHitBytes: activity.totalCacheHitBytes,
          totalCacheMissBytes: activity.totalCacheMissBytes,
          totalBytes,
          cacheHitPercent: (activity.totalCacheHitBytes / totalBytes) * 100,
          totalDownloads: activity.totalDownloads,
          lastActivityUtc: lastSeenIso
        });
        continue;
      }

      if (group.separateMemberRows) {
        // The nickname labels the row, but the row speaks for one machine: it is not itself
        // a group row and carries no member list, so member counts and IP lists stay
        // truthful downstream. Totals are this address alone, never the nickname's sum.
        separatedMembers.push({
          clientIp: ip,
          displayName: group.nickname,
          groupId: group.id,
          isGrouped: false,
          groupMemberIps: undefined,
          totalCacheHitBytes: activity.totalCacheHitBytes,
          totalCacheMissBytes: activity.totalCacheMissBytes,
          totalBytes,
          cacheHitPercent: (activity.totalCacheHitBytes / totalBytes) * 100,
          totalDownloads: activity.totalDownloads,
          lastActivityUtc: lastSeenIso
        });
        continue;
      }

      const existing = foldedGroups.get(group.id);
      if (!existing) {
        foldedGroups.set(group.id, {
          clientIp: ip,
          displayName: group.nickname,
          groupId: group.id,
          isGrouped: true,
          groupMemberIps: [...group.memberIps],
          totalCacheHitBytes: activity.totalCacheHitBytes,
          totalCacheMissBytes: activity.totalCacheMissBytes,
          totalBytes,
          cacheHitPercent: (activity.totalCacheHitBytes / totalBytes) * 100,
          totalDownloads: activity.totalDownloads,
          lastActivityUtc: lastSeenIso
        });
        continue;
      }

      existing.totalCacheHitBytes += activity.totalCacheHitBytes;
      existing.totalCacheMissBytes += activity.totalCacheMissBytes;
      existing.totalBytes += totalBytes;
      existing.totalDownloads += activity.totalDownloads;
      existing.cacheHitPercent =
        existing.totalBytes > 0 ? (existing.totalCacheHitBytes / existing.totalBytes) * 100 : 0;
      if (new Date(lastSeenIso) > new Date(existing.lastActivityUtc)) {
        existing.lastActivityUtc = lastSeenIso;
        existing.clientIp = ip;
      }
    }

    // Combined rows fold before the ranking, so a nickname spread over several addresses
    // ranks on its summed traffic while separated members rank one by one.
    const clientStats: ClientStat[] = [
      ...foldedGroups.values(),
      ...separatedMembers,
      ...ungrouped
    ].sort((a, b) => b.totalBytes - a.totalBytes);

    // Generate service stats
    const serviceStats = SERVICES.map((service) => {
      const serviceDownloads = downloads.filter((d) => d.service === service);
      const hitBytes = serviceDownloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
      const missBytes = serviceDownloads.reduce((sum, d) => sum + d.cacheMissBytes, 0);

      const lastActivity =
        serviceDownloads[0]?.startTimeUtc ||
        new Date(now.getTime() - Math.random() * 7200000).toISOString();

      return {
        service,
        totalCacheHitBytes: hitBytes || cacheInfo.serviceSizes[service] * 0.8,
        totalCacheMissBytes: missBytes || cacheInfo.serviceSizes[service] * 0.2,
        totalBytes: hitBytes + missBytes || cacheInfo.serviceSizes[service],
        cacheHitPercent: hitBytes + missBytes > 0 ? (hitBytes / (hitBytes + missBytes)) * 100 : 80,
        totalDownloads: serviceDownloads.length,
        lastActivityUtc: lastActivity
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
        cacheHitBytes: 0,
        cacheMissBytes: 0,
        totalBytes: 0,
        cacheHitPercent: 0,
        isActive: false,
        averageBytesPerSecond: 0,
        isEvicted: false
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
      cacheHitBytes,
      cacheMissBytes,
      totalBytes,
      cacheHitPercent: (cacheHitBytes / totalBytes) * 100,
      isActive: true,
      gameName: game.name,
      gameAppId: parseInt(game.appId, 10),
      averageBytesPerSecond: 0,
      isEvicted: false
    };
  }

  /**
   * Generate mock hourly activity data for PeakUsageHours widget
   */
  static generateMockHourlyActivity(): HourlyActivityResponse {
    const daysInPeriod = 7;

    // Generate realistic hourly distribution - more activity in afternoon/evening
    const hours: HourlyActivityItem[] = Array.from({ length: 24 }, (_, hour) => {
      // Activity pattern: low at night, peak in afternoon/evening
      let baseActivity: number;
      if (hour >= 0 && hour < 6) {
        baseActivity = 0.1 + Math.random() * 0.15; // Night: 10-25%
      } else if (hour >= 6 && hour < 12) {
        baseActivity = 0.3 + Math.random() * 0.2; // Morning: 30-50%
      } else if (hour >= 12 && hour < 18) {
        baseActivity = 0.7 + Math.random() * 0.3; // Afternoon: 70-100%
      } else {
        baseActivity = 0.5 + Math.random() * 0.35; // Evening: 50-85%
      }

      const downloads = Math.floor(baseActivity * 100 * daysInPeriod);
      const avgDownloads = downloads / daysInPeriod;
      const bytesPerDownload = (2 + Math.random() * 8) * 1024 * 1024 * 1024; // 2-10 GB average
      const bytesServed = Math.floor(downloads * bytesPerDownload);
      const avgBytesServed = bytesServed / daysInPeriod;
      const cacheHitRatio = 0.7 + Math.random() * 0.25; // 70-95% hit rate

      return {
        hour,
        downloads,
        avgDownloads,
        bytesServed,
        avgBytesServed,
        cacheHitBytes: Math.floor(bytesServed * cacheHitRatio),
        cacheMissBytes: Math.floor(bytesServed * (1 - cacheHitRatio))
      };
    });

    // Find peak hour (highest downloads)
    const peakHour = hours.reduce(
      (max, h) => (h.downloads > max.downloads ? h : max),
      hours[0]
    ).hour;
    const totalDownloads = hours.reduce((sum, h) => sum + h.downloads, 0);
    const totalBytesServed = hours.reduce((sum, h) => sum + h.bytesServed, 0);

    const now = Math.floor(Date.now() / 1000);
    const periodStart = now - daysInPeriod * 24 * 60 * 60;

    return {
      hours,
      peakHour,
      totalDownloads,
      totalBytesServed,
      daysInPeriod,
      periodStart,
      periodEnd: now,
      period: '7d'
    };
  }

  static generateMockSparklines(startTime?: number, endTime?: number): SparklineDataResponse {
    const nowSec = Math.floor(Date.now() / 1000);
    const end = endTime ?? nowSec;
    const start = startTime ?? end - 24 * 3600;
    const rangeHours = Math.max((end - start) / 3600, 0.25);
    const bucketMinutes = resolveMockBucketMinutes(rangeHours);
    const bucketSec = bucketMinutes * 60;
    const count = Math.max(1, Math.ceil((end - start) / bucketSec));
    const bucketStarts: number[] = [];
    const bandwidthSaved: number[] = [];
    const totalServed: number[] = [];
    const addedToCache: number[] = [];
    const cacheHitRatio: number[] = [];

    for (let i = count - 1; i >= 0; i--) {
      const startSec = end - i * bucketSec;
      const hour = new Date(startSec * 1000).getUTCHours();
      let activity = 0.15;
      if (hour >= 12 && hour < 18) {
        activity = 0.85;
      } else if (hour >= 18 && hour < 23) {
        activity = 0.65;
      } else if (hour >= 8 && hour < 12) {
        activity = 0.4;
      }

      const served = activity * 25 * GIGABYTE;
      const saved = served * 0.78;
      bucketStarts.push(startSec);
      totalServed.push(served);
      bandwidthSaved.push(saved);
      addedToCache.push(served - saved);
      cacheHitRatio.push(78);
    }

    return {
      bandwidthSaved: { data: bandwidthSaved, trend: 'up' },
      cacheHitRatio: { data: cacheHitRatio, trend: 'stable' },
      totalServed: { data: totalServed, trend: 'up' },
      addedToCache: { data: addedToCache, trend: 'stable' },
      period: startTime == null ? 'all' : 'filtered',
      bucketMinutes,
      bucketStarts
    };
  }

  static generateMockEvents(): Event[] {
    return mockEventSpecs(new Date()).map(toEvent);
  }

  static generateMockEventCompare(eventIds: number[]): EventCompareResponse {
    const specs = mockEventSpecs(new Date());
    const selected = eventIds
      .filter((id, index, all) => Number.isInteger(id) && all.indexOf(id) === index)
      .slice(0, 8)
      .map((id) => specs.find((spec) => spec.id === id))
      .filter((spec): spec is MockEventSpec => spec !== undefined);

    if (selected.length === 0) {
      return { bucketMinutes: 60, elapsedMinutes: [], series: [] };
    }

    const longestHours = Math.max(
      ...selected.map((spec) => Math.max((spec.end.getTime() - spec.start.getTime()) / 3600000, 0))
    );
    const bucketMinutes = resolveMockBucketMinutes(longestHours);
    const bucketMs = bucketMinutes * 60 * 1000;
    const maxBuckets = Math.max(
      ...selected.map((spec) =>
        Math.max(1, Math.ceil((spec.end.getTime() - spec.start.getTime()) / bucketMs))
      )
    );
    const elapsedMinutes = Array.from({ length: maxBuckets }, (_, index) => index * bucketMinutes);

    return {
      bucketMinutes,
      elapsedMinutes,
      series: selected.map((spec) => {
        const eventBuckets = Math.max(
          1,
          Math.ceil((spec.end.getTime() - spec.start.getTime()) / bucketMs)
        );
        const served: (number | null)[] = [];
        const saved: (number | null)[] = [];
        for (let index = 0; index < maxBuckets; index++) {
          if (index >= eventBuckets) {
            served.push(null);
            saved.push(null);
            continue;
          }

          const progress = eventBuckets === 1 ? 1 : index / (eventBuckets - 1);
          const point = profileWave(spec.profile, progress);
          served.push(point.served);
          saved.push(point.saved);
        }

        return {
          eventId: spec.id,
          name: spec.name,
          colorIndex: spec.colorIndex,
          served,
          saved
        };
      })
    };
  }

  /**
   * Generate mock game detection data matching the CachedDetectionResponse shape.
   * The game_app_id values match STEAM_GAMES appIds so the detectionLookup Map
   * can resolve "on disk" sizes for mock downloads.
   */
  static generateMockGameDetection(): CachedDetectionResponse {
    const games: GameDetectionSummary[] = STEAM_GAMES.map((game, index) => {
      const appId = parseInt(game.appId, 10);
      // Simulate on-disk size as 70-100% of full game size (some updates not fully cached)
      const totalSizeBytes = Math.floor(game.size * (0.7 + Math.random() * 0.3));
      const filesCount = Math.max(1, Math.floor(totalSizeBytes / (64 * 1024 * 1024))); // ~64MB per file

      return {
        game_app_id: appId,
        game_name: game.name,
        cache_files_found: filesCount,
        total_size_bytes: totalSizeBytes,
        service: MOCK_DETECTION_SERVICES[index % MOCK_DETECTION_SERVICES.length],
        image_url: undefined
      };
    });

    // Built from the games rather than hardcoded, so the per-service totals always agree with the
    // rows they summarise.
    const totalsByService = new Map<string, { files: number; bytes: number }>();
    for (const game of games) {
      const service = game.service ?? 'steam';
      const running = totalsByService.get(service) ?? { files: 0, bytes: 0 };
      running.files += game.cache_files_found;
      running.bytes += game.total_size_bytes;
      totalsByService.set(service, running);
    }

    const services: ServiceDetectionSummary[] = [...totalsByService.entries()].map(
      ([serviceName, totals]) => ({
        service_name: serviceName,
        cache_files_found: totals.files,
        total_size_bytes: totals.bytes,
        is_evicted: false,
        evicted_downloads_count: 0
      })
    );

    const totalSizeBytes = games.reduce((s, g) => s + g.total_size_bytes, 0);

    return {
      hasCachedResults: true,
      games,
      services,
      totalGamesDetected: games.length,
      totalServicesDetected: services.length,
      lastDetectionTime: new Date().toISOString(),
      games_on_disk_bytes: totalSizeBytes,
      games_on_disk_count: games.length
    };
  }

  /**
   * Generate mock retro view data (grouped by depot + client, matching the /api/downloads/retro shape)
   */
  static generateMockRetroData(
    options: {
      page?: number;
      pageSize?: number;
      sortOrder?: string;
      service?: string;
      client?: string;
      search?: string;
      hideLocalhost?: boolean;
      hideMetadata?: boolean;
      hideSmallFiles?: boolean;
      hideUnknownGames?: boolean;
    } = {}
  ): {
    items: {
      id: string;
      service: string;
      gameName: string;
      gameAppId: number | null;
      epicAppId: string | null;
      depotId: number | null;
      clientIp: string;
      startTimeUtc: string;
      endTimeUtc: string;
      cacheHitBytes: number;
      cacheMissBytes: number;
      totalBytes: number;
      requestCount: number;
      clientsSet: Set<string>;
      datasource: string;
      averageBytesPerSecond: number;
      downloadIds: number[];
    }[];
    totalItems: number;
    totalPages: number;
    currentPage: number;
    pageSize: number;
  } {
    const {
      page = 1,
      pageSize = 20,
      sortOrder = 'latest',
      service: filterService,
      client: filterClient,
      search,
      hideLocalhost = false,
      hideMetadata = false,
      hideSmallFiles = false,
      hideUnknownGames = false
    } = options;

    const now = new Date();
    const allItems: {
      id: string;
      service: string;
      gameName: string;
      gameAppId: number | null;
      epicAppId: string | null;
      depotId: number | null;
      clientIp: string;
      startTimeUtc: string;
      endTimeUtc: string;
      cacheHitBytes: number;
      cacheMissBytes: number;
      totalBytes: number;
      requestCount: number;
      clientsSet: Set<string>;
      datasource: string;
      averageBytesPerSecond: number;
      downloadIds: number[];
    }[] = [];

    // Generate ~80 grouped depot entries using real Steam games
    for (let i = 0; i < 80; i++) {
      const game = STEAM_GAMES[i % STEAM_GAMES.length];
      const clientIp = CLIENT_IPS[i % CLIENT_IPS.length];
      const depotId = parseInt(game.appId, 10) + 1;

      const hoursAgo = Math.pow(i / 80, 2) * 2160;
      const startTime = new Date(
        now.getTime() - hoursAgo * 60 * 60 * 1000 - Math.random() * 3600000
      );
      const durationMs = (1 + Math.random() * 30) * 60 * 1000;
      const endTime = new Date(startTime.getTime() + durationMs);

      const cacheHitRatio = Math.min(0.95, 0.1 + (hoursAgo / 2160) * 0.85);
      const totalBytes = Math.floor(game.size * (0.3 + Math.random() * 0.7));
      const cacheHitBytes = Math.floor(totalBytes * cacheHitRatio);
      const cacheMissBytes = totalBytes - cacheHitBytes;
      const requestCount = 1 + Math.floor(Math.random() * 10);
      const speed = totalBytes > 0 ? totalBytes / (durationMs / 1000) : 0;

      allItems.push({
        id: `depot-${depotId}-${clientIp}`,
        service: 'steam',
        gameName: game.name,
        gameAppId: parseInt(game.appId, 10),
        epicAppId: null,
        depotId,
        clientIp,
        startTimeUtc: startTime.toISOString(),
        endTimeUtc: endTime.toISOString(),
        cacheHitBytes,
        cacheMissBytes,
        totalBytes,
        requestCount,
        clientsSet: new Set([clientIp]),
        datasource: 'Default',
        averageBytesPerSecond: speed,
        downloadIds: Array.from({ length: requestCount }, (_, j) => i * 10 + j + 1)
      });
    }

    // Apply filters
    let filtered = allItems;
    if (filterService && filterService !== 'all') {
      filtered = filtered.filter((item) => item.service === filterService);
    }
    if (filterClient && filterClient !== 'all') {
      filtered = filtered.filter((item) => item.clientIp === filterClient);
    }
    if (search) {
      const q = search.toLowerCase();
      filtered = filtered.filter(
        (item) =>
          item.gameName.toLowerCase().includes(q) ||
          item.clientIp.includes(q) ||
          String(item.depotId).includes(q)
      );
    }
    if (hideLocalhost) {
      filtered = filtered.filter(
        (item) => item.clientIp !== '127.0.0.1' && item.clientIp !== '::1'
      );
    }
    if (hideMetadata) {
      filtered = filtered.filter((item) => item.totalBytes > 0);
    }
    if (hideSmallFiles) {
      filtered = filtered.filter((item) => item.totalBytes === 0 || item.totalBytes >= 1048576);
    }
    if (hideUnknownGames) {
      filtered = filtered.filter((item) => item.gameName && item.gameName !== item.service);
    }

    // Sort
    filtered.sort((a, b) => {
      switch (sortOrder) {
        case 'oldest':
          return new Date(a.startTimeUtc).getTime() - new Date(b.startTimeUtc).getTime();
        case 'largest':
          return b.totalBytes - a.totalBytes;
        case 'smallest':
          return a.totalBytes - b.totalBytes;
        case 'efficiency': {
          const aEff = a.totalBytes > 0 ? a.cacheHitBytes / a.totalBytes : 0;
          const bEff = b.totalBytes > 0 ? b.cacheHitBytes / b.totalBytes : 0;
          return bEff - aEff;
        }
        case 'efficiency-low': {
          const aEff = a.totalBytes > 0 ? a.cacheHitBytes / a.totalBytes : 0;
          const bEff = b.totalBytes > 0 ? b.cacheHitBytes / b.totalBytes : 0;
          return aEff - bEff;
        }
        case 'sessions':
          return b.requestCount - a.requestCount;
        case 'alphabetical':
          return a.gameName.localeCompare(b.gameName);
        case 'latest':
        default:
          return new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime();
      }
    });

    // Paginate
    const totalItems = filtered.length;
    const effectivePageSize = pageSize >= 10000 ? totalItems : pageSize;
    const totalPages = Math.max(1, Math.ceil(totalItems / effectivePageSize));
    const start = (page - 1) * effectivePageSize;
    const items = filtered.slice(start, start + effectivePageSize);

    return {
      items,
      totalItems,
      totalPages,
      currentPage: page,
      pageSize: effectivePageSize
    };
  }
}

export default MockDataService;
