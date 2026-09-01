import { SERVICES } from '../utils/constants';
import { getServiceFilterKey } from '../utils/serviceDisplayName';
import type {
  RetroDownloadDto,
  RetroDownloadQueryParams,
  RetroDownloadResponse
} from '../services/api.service';
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
  SparklineDataResponse,
  CacheSnapshotResponse,
  ClientGroup,
  DownloadSpeedSnapshot,
  GameSpeedInfo,
  ClientSpeedInfo
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

/** The label the server gives unmapped Steam content, and the only name the hide-unknown and
 *  group-unknown controls act on. */
const MOCK_RETRO_UNKNOWN_NAME = 'Unknown/Other';

/** What one mock retro row downloads. */
interface MockRetroSource {
  service: string;
  appName: string;
  steamAppId: number | null;
  epicAppId: string | null;
  sizeBytes: number;
}

/**
 * The content the mock retro rows are built from. It deliberately carries every shape the retro
 * controls act on: named Steam titles, unmapped Steam content under the Unknown/Other label, one
 * title logged under two services so merging across services has something to merge, the
 * xbox/xboxlive alias pair that folds into one service while wsus keeps its own, and the
 * service-only rows that carry no title at all.
 */
const MOCK_RETRO_SOURCES: MockRetroSource[] = [
  ...STEAM_GAMES.slice(0, 16).map((game) => ({
    service: 'steam',
    appName: game.name,
    steamAppId: parseInt(game.appId, 10),
    epicAppId: null,
    sizeBytes: game.size
  })),
  {
    service: 'steam',
    appName: MOCK_RETRO_UNKNOWN_NAME,
    steamAppId: null,
    epicAppId: null,
    sizeBytes: 3 * GIGABYTE
  },
  {
    service: 'steam',
    appName: MOCK_RETRO_UNKNOWN_NAME,
    steamAppId: null,
    epicAppId: null,
    sizeBytes: 12 * GIGABYTE
  },
  {
    service: 'epicgames',
    appName: 'Fortnite',
    steamAppId: null,
    epicAppId: 'fortnite',
    sizeBytes: 30 * GIGABYTE
  },
  {
    service: 'epicgames',
    appName: 'Rocket League',
    steamAppId: null,
    epicAppId: 'rocket-league',
    sizeBytes: 20 * GIGABYTE
  },
  {
    service: 'blizzard',
    appName: 'Rocket League',
    steamAppId: null,
    epicAppId: null,
    sizeBytes: 20 * GIGABYTE
  },
  {
    service: 'blizzard',
    appName: 'blizzard',
    steamAppId: null,
    epicAppId: null,
    sizeBytes: 40 * GIGABYTE
  },
  { service: 'wsus', appName: 'wsus', steamAppId: null, epicAppId: null, sizeBytes: 6 * GIGABYTE },
  { service: 'xbox', appName: 'xbox', steamAppId: null, epicAppId: null, sizeBytes: 18 * GIGABYTE },
  {
    service: 'xboxlive',
    appName: 'xboxlive',
    steamAppId: null,
    epicAppId: null,
    sizeBytes: 9 * GIGABYTE
  },
  { service: 'riot', appName: 'riot', steamAppId: null, epicAppId: null, sizeBytes: 15 * GIGABYTE },
  {
    service: 'origin',
    appName: 'origin',
    steamAppId: null,
    epicAppId: null,
    sizeBytes: 22 * GIGABYTE
  }
];

/** Localhost joins the rotation so the hide-localhost control has rows to remove. */
const MOCK_RETRO_CLIENTS = [...CLIENT_IPS, '127.0.0.1'];

/** A generated retro row, plus the two facts the query filters on that the wire shape omits. */
interface MockRetroRow extends RetroDownloadDto {
  isActive: boolean;
  eventIds: number[];
}

/** What a merged bucket stands for, which decides its name, its service and whether it can carry
 *  a depot and an app id. Mirrors the endpoint's own three kinds. */
type MockRetroBucketKind = 'game' | 'service' | 'unknown';

/** The server's test for a resolved title: not blank, not the Unknown/Other label, not just the
 *  service name repeated. */
function isRealMockGameName(appName: string, service: string): boolean {
  return (
    appName.trim().length > 0 &&
    appName !== MOCK_RETRO_UNKNOWN_NAME &&
    appName.toLowerCase() !== service.toLowerCase()
  );
}

/**
 * The rows behind mock mode's retro page, one per depot-and-client group, which is the level the
 * endpoint aggregates to before it filters and merges.
 */
function buildMockRetroRows(now: Date): MockRetroRow[] {
  const eventSpecs = mockEventSpecs(now);
  const rows: MockRetroRow[] = [];
  const total = MOCK_RETRO_SOURCES.length * 3;

  for (let index = 0; index < total; index++) {
    const source = MOCK_RETRO_SOURCES[index % MOCK_RETRO_SOURCES.length];
    const clientIp = MOCK_RETRO_CLIENTS[index % MOCK_RETRO_CLIENTS.length];
    // Every seventh row is placed inside an event window, so the event filter and the header's
    // time filter agree about which rows belong to an event instead of each seeing a different set.
    const eventSpec =
      index % 7 === 3 ? eventSpecs[Math.floor(index / 7) % eventSpecs.length] : null;
    const startTime = eventSpec
      ? new Date(eventSpec.start.getTime() + ((index % 5) + 1) * 15 * 60 * 1000)
      : new Date(now.getTime() - Math.pow(index / total, 2) * 720 * 60 * 60 * 1000);

    const requestCount = 1 + (index % 4);
    const isActive = index % 19 === 5;
    // A completed zero-byte session never reaches this endpoint - the server drops it before the
    // query runs - so the metadata control only has rows to remove while a running one is listed.
    const isZeroByte = isActive && index % 38 === 5;
    const totalBytes = isZeroByte
      ? 0
      : index % 23 === 7
        ? 512 * 1024
        : Math.floor(source.sizeBytes * (0.3 + (index % 7) / 10));
    // Three of every six rows land each side of the 50% line the hit/miss control splits on.
    const cacheHitBytes = Math.floor(totalBytes * (0.15 + (index % 6) * 0.14));
    const cacheMissBytes = totalBytes - cacheHitBytes;
    const durationMs = (2 + (index % 25)) * 60 * 1000;
    const endTime = new Date(startTime.getTime() + durationMs);
    const evictedCount =
      index % 13 === 4 ? requestCount : index % 13 === 9 && requestCount > 1 ? 1 : 0;
    const depotId = source.steamAppId !== null ? source.steamAppId + 1 : null;

    rows.push({
      id:
        depotId !== null
          ? `depot-${depotId}-${clientIp}`
          : `no-depot-${source.service}-${clientIp}-${index + 1}`,
      startTimeUtc: startTime.toISOString(),
      lastStartTimeUtc: new Date(
        startTime.getTime() + (requestCount - 1) * 60 * 1000
      ).toISOString(),
      endTimeUtc: endTime.toISOString(),
      depotId,
      appName: source.appName,
      steamAppId: source.steamAppId,
      epicAppId: source.epicAppId,
      service: source.service,
      datasource: 'Default',
      clientIp,
      averageBytesPerSecond: totalBytes / (durationMs / 1000),
      cacheHitBytes,
      cacheMissBytes,
      cacheHitPercent: totalBytes > 0 ? (cacheHitBytes * 100) / totalBytes : 0,
      totalBytes,
      requestCount,
      downloadIds: Array.from({ length: requestCount }, (_, member) => index * 10 + member + 1),
      clientIps: [clientIp],
      depotIds: depotId !== null ? [depotId] : [],
      isEvicted: evictedCount === requestCount,
      isPartiallyEvicted: evictedCount > 0 && evictedCount !== requestCount,
      primaryDownload: null,
      hasRealGameName: false,
      groupType: '',
      isActive,
      eventIds: eventSpecs
        .filter((spec) => startTime >= spec.start && startTime <= spec.end)
        .map((spec) => spec.id)
    });
  }

  return rows;
}

/**
 * The row-level filters, in the order the endpoint applies them: the ones it pushes into SQL
 * before grouping, then the ones it runs over the grouped rows.
 */
function filterMockRetroRows(
  rows: MockRetroRow[],
  params: RetroDownloadQueryParams
): MockRetroRow[] {
  const clientIps =
    params.client && params.client !== 'all'
      ? params.client
          .split(',')
          .map((ip) => ip.trim())
          .filter((ip) => ip.length > 0)
      : null;
  const serviceKey =
    params.service && params.service !== 'all' ? getServiceFilterKey(params.service) : null;
  const startMs = params.startTime !== undefined ? params.startTime * 1000 : null;
  const endMs = params.endTime !== undefined ? params.endTime * 1000 : null;
  const searchTerm = params.search ? params.search.toLowerCase() : null;

  let filtered = rows.filter((row) => {
    if (!params.includeActive && row.isActive) return false;
    // Hiding evicted rows drops the fully evicted ones; a partly evicted row keeps the members
    // that are still cached, so its badge goes with the members that were dropped.
    if (params.hideEvicted && row.isEvicted) return false;
    if (params.hideLocalhost && (row.clientIp === '127.0.0.1' || row.clientIp === '::1'))
      return false;
    if (serviceKey !== null && getServiceFilterKey(row.service) !== serviceKey) return false;
    if (clientIps !== null && !clientIps.includes(row.clientIp)) return false;
    if (params.showZeroBytes === false && row.totalBytes === 0) return false;
    if (params.hideSmallFiles && row.totalBytes < 1048576) return false;
    if (params.eventId !== undefined && !row.eventIds.includes(params.eventId)) return false;
    if (startMs !== null || endMs !== null) {
      const startedAt = new Date(row.startTimeUtc).getTime();
      if (startMs !== null && startedAt < startMs) return false;
      if (endMs !== null && startedAt > endMs) return false;
    }
    return true;
  });

  if (params.hideEvicted) {
    filtered = filtered.map((row) =>
      row.isPartiallyEvicted ? { ...row, isPartiallyEvicted: false } : row
    );
  }

  if (searchTerm !== null) {
    filtered = filtered.filter(
      (row) =>
        row.appName.toLowerCase().includes(searchTerm) ||
        row.service.toLowerCase().includes(searchTerm) ||
        (row.depotId !== null && String(row.depotId).includes(searchTerm)) ||
        (row.steamAppId !== null && String(row.steamAppId).includes(searchTerm)) ||
        row.clientIp.includes(searchTerm)
    );
  }

  if (params.hideUnknown) {
    filtered = filtered.filter((row) => row.appName !== MOCK_RETRO_UNKNOWN_NAME);
  }

  if (params.hitMiss === 'hit') {
    filtered = filtered.filter((row) => row.cacheHitPercent >= 50);
  } else if (params.hitMiss === 'miss') {
    filtered = filtered.filter((row) => row.cacheHitPercent < 50);
  }

  return filtered;
}

/**
 * The newest download behind a row, which is what a collapsed grouped row draws its session
 * details from. A row stands for `requestCount` downloads, so one of them carries its share.
 */
function mockRetroPrimaryDownload(row: MockRetroRow): Download {
  const cacheHitBytes = Math.floor(row.cacheHitBytes / row.requestCount);
  const cacheMissBytes = Math.floor(row.cacheMissBytes / row.requestCount);
  const totalBytes = cacheHitBytes + cacheMissBytes;
  return {
    id: row.downloadIds[row.downloadIds.length - 1],
    service: row.service,
    clientIp: row.clientIp,
    startTimeUtc: row.lastStartTimeUtc,
    endTimeUtc: row.isActive ? null : row.endTimeUtc,
    cacheHitBytes,
    cacheMissBytes,
    totalBytes,
    cacheHitPercent: totalBytes > 0 ? (cacheHitBytes * 100) / totalBytes : 0,
    isActive: row.isActive,
    gameName: isRealMockGameName(row.appName, row.service) ? row.appName : undefined,
    gameAppId: row.steamAppId ?? undefined,
    depotId: row.depotId ?? undefined,
    epicAppId: row.epicAppId ?? undefined,
    datasource: row.datasource,
    averageBytesPerSecond: row.averageBytesPerSecond,
    isEvicted: row.isEvicted
  };
}

/** The merge key and bucket kind one row falls into, following the endpoint's own cascade. */
function mockRetroMergeKey(
  row: MockRetroRow,
  params: RetroDownloadQueryParams
): { key: string; kind: MockRetroBucketKind } {
  const normalizedService = getServiceFilterKey(row.service);

  if (params.groupByService) {
    return { key: normalizedService, kind: 'game' };
  }
  if (params.mergeAcrossServices) {
    if (row.steamAppId) return { key: `game-appid-${row.steamAppId}`, kind: 'game' };
    if (isRealMockGameName(row.appName, row.service))
      return { key: `game-${row.appName}`, kind: 'game' };
    if (params.groupUnknownGames && row.appName === MOCK_RETRO_UNKNOWN_NAME)
      return { key: 'unknown-other', kind: 'unknown' };
    return { key: `service-${normalizedService}`, kind: 'service' };
  }
  if (row.steamAppId) return { key: `${normalizedService}-app-${row.steamAppId}`, kind: 'game' };
  if (row.epicAppId) return { key: `${normalizedService}-epic-${row.epicAppId}`, kind: 'game' };
  if (row.appName !== '' && row.appName !== row.service)
    return { key: `${normalizedService}-name-${row.appName.toLowerCase()}`, kind: 'game' };
  return { key: `${normalizedService}-unknown`, kind: 'game' };
}

/** Folds the filtered rows into the buckets the grouping controls ask for. */
function mergeMockRetroRows(
  rows: MockRetroRow[],
  params: RetroDownloadQueryParams
): MockRetroRow[] {
  const buckets = new Map<string, MockRetroRow[]>();
  const kinds = new Map<string, MockRetroBucketKind>();
  const order: string[] = [];

  for (const row of rows) {
    const { key, kind } = mockRetroMergeKey(row, params);
    const bucket = buckets.get(key);
    if (bucket) {
      bucket.push(row);
      continue;
    }
    buckets.set(key, [row]);
    kinds.set(key, kind);
    order.push(key);
  }

  return order.map((key) => {
    const bucket = buckets.get(key) as MockRetroRow[];
    const kind = kinds.get(key) as MockRetroBucketKind;
    const first = bucket[0];
    const displayService = getServiceFilterKey(first.service);
    // A game bucket names the service its title was logged under; a service bucket has to show
    // the folded name or it would be labeled with one alias of the several it holds.
    const bucketService =
      params.mergeAcrossServices && kind === 'game' ? first.service : displayService;
    // A service-level bucket spans many depots and titles, so a member's depot and app id would
    // be misleading on it.
    const hideRowIdentity = Boolean(params.groupByService) || kind !== 'game';
    const cacheHitBytes = bucket.reduce((sum, row) => sum + row.cacheHitBytes, 0);
    const cacheMissBytes = bucket.reduce((sum, row) => sum + row.cacheMissBytes, 0);
    const totalBytes = bucket.reduce((sum, row) => sum + row.totalBytes, 0);
    const weightedSpeed = bucket.reduce(
      (sum, row) => sum + row.averageBytesPerSecond * row.totalBytes,
      0
    );
    const newest = bucket.reduce((latest, row) =>
      row.lastStartTimeUtc > latest.lastStartTimeUtc ? row : latest
    );

    return {
      id: key,
      startTimeUtc: bucket.reduce(
        (earliest, row) => (row.startTimeUtc < earliest ? row.startTimeUtc : earliest),
        first.startTimeUtc
      ),
      lastStartTimeUtc: newest.lastStartTimeUtc,
      endTimeUtc: bucket.reduce(
        (latest, row) => (row.endTimeUtc > latest ? row.endTimeUtc : latest),
        first.endTimeUtc
      ),
      depotId: hideRowIdentity ? null : first.depotId,
      appName:
        kind === 'unknown'
          ? MOCK_RETRO_UNKNOWN_NAME
          : kind === 'service'
            ? bucketService
            : params.groupByService
              ? displayService
              : first.appName,
      steamAppId: hideRowIdentity ? null : first.steamAppId,
      epicAppId: hideRowIdentity ? null : first.epicAppId,
      service: kind === 'unknown' ? 'unknown' : bucketService,
      datasource: first.datasource,
      clientIp: first.clientIp,
      averageBytesPerSecond: totalBytes > 0 ? weightedSpeed / totalBytes : 0,
      cacheHitBytes,
      cacheMissBytes,
      cacheHitPercent: totalBytes > 0 ? (cacheHitBytes * 100) / totalBytes : 0,
      totalBytes,
      requestCount: bucket.reduce((sum, row) => sum + row.requestCount, 0),
      downloadIds: bucket.flatMap((row) => row.downloadIds),
      clientIps: Array.from(new Set(bucket.flatMap((row) => row.clientIps))),
      depotIds: Array.from(new Set(bucket.flatMap((row) => row.depotIds))),
      isEvicted: bucket.every((row) => row.isEvicted),
      isPartiallyEvicted:
        bucket.some((row) => row.isEvicted || row.isPartiallyEvicted) &&
        !bucket.every((row) => row.isEvicted),
      // The collapsed row is drawn from the newest member, and the endpoint sends that member only
      // when the buckets were keyed across services, which is when the grouped views ask for it.
      primaryDownload: params.mergeAcrossServices ? mockRetroPrimaryDownload(newest) : null,
      hasRealGameName:
        kind === 'unknown' || bucket.some((row) => isRealMockGameName(row.appName, row.service)),
      groupType: params.mergeAcrossServices ? (kind === 'game' ? 'game' : 'content') : '',
      isActive: bucket.some((row) => row.isActive),
      eventIds: Array.from(new Set(bucket.flatMap((row) => row.eventIds)))
    };
  });
}

/** Orders the rows the way the endpoint's sort switch does, including the frequency bucketing
 *  that puts single-download groups last. */
function sortMockRetroRows(rows: MockRetroRow[], params: RetroDownloadQueryParams): MockRetroRow[] {
  const sort = params.sort ?? 'latest';
  // These five impose their own full order, so frequency never buckets under them.
  const bucketByFrequency =
    Boolean(params.groupByFrequency) &&
    !['service', 'alphabetical', 'efficiency', 'efficiency-low', 'sessions'].includes(sort);
  const chronological = (row: MockRetroRow): string =>
    params.mergeAcrossServices ? row.lastStartTimeUtc : row.endTimeUtc;

  const within = (a: MockRetroRow, b: MockRetroRow): number => {
    switch (sort) {
      case 'oldest':
        return a.startTimeUtc.localeCompare(b.startTimeUtc);
      case 'largest':
        return b.totalBytes - a.totalBytes;
      case 'smallest':
        return a.totalBytes - b.totalBytes;
      case 'efficiency':
        return b.cacheHitPercent - a.cacheHitPercent;
      case 'efficiency-low':
        return a.cacheHitPercent - b.cacheHitPercent;
      case 'sessions':
        return b.requestCount - a.requestCount;
      case 'alphabetical':
        return a.appName.toLowerCase().localeCompare(b.appName.toLowerCase());
      case 'service':
        return (
          a.service.localeCompare(b.service) || chronological(b).localeCompare(chronological(a))
        );
      default:
        return chronological(b).localeCompare(chronological(a));
    }
  };

  return [...rows].sort((a, b) => {
    if (bucketByFrequency) {
      const bucketA = a.requestCount === 1 ? 1 : 0;
      const bucketB = b.requestCount === 1 ? 1 : 0;
      if (bucketA !== bucketB) return bucketA - bucketB;
    }
    return within(a, b);
  });
}

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

  /**
   * The live speed snapshot mock mode serves: three Steam titles downloading on three clients, in
   * the shape SpeedContext hands its readers, so the Downloads Active tab and the Dashboard's live
   * strip and active-download count show something without a socket or a poll behind them.
   */
  /**
   * A week of hourly cache-size samples for the Cache Growth Trend widget, ending on the used size
   * `generateMockData` reports so the widget and the stat cards agree.
   */
  /**
   * The nicknamed client groups behind the mock client rows, so the Downloads client dropdown and
   * the Clients page label the generated addresses with the same names their totals were folded
   * under instead of with a real network's nicknames.
   */
  static generateMockClientGroups(): ClientGroup[] {
    const createdAtUtc = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString();
    return MOCK_CLIENT_GROUPS.map((group) => ({
      id: group.id,
      nickname: group.nickname,
      separateMemberRows: group.separateMemberRows,
      memberIps: [...group.memberIps],
      createdAtUtc
    }));
  }

  /** The downloads tagged to one mock event, for the list an expanded event opens. */
  static generateMockEventDownloads(eventId: number): Download[] {
    return buildMockRetroRows(new Date())
      .filter((row) => row.eventIds.includes(eventId))
      .map(mockRetroPrimaryDownload);
  }

  static generateMockCacheSnapshot(): CacheSnapshotResponse {
    return {
      hasData: true,
      startUsedSize: 1320000000000,
      endUsedSize: 1450000000000,
      averageUsedSize: 1390000000000,
      totalCacheSize: 2000000000000,
      snapshotCount: 168,
      isEstimate: false,
      nextSnapshotUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString()
    };
  }

  static generateMockSpeedSnapshot(): DownloadSpeedSnapshot {
    const windowSeconds = 10;
    const gameSpeeds: GameSpeedInfo[] = STEAM_GAMES.slice(0, 3).map((game, index) => {
      const bytesPerSecond = (90 - index * 25) * 1024 * 1024;
      const totalBytes = bytesPerSecond * windowSeconds;
      const cacheHitBytes = Math.floor(totalBytes * (0.85 - index * 0.25));
      return {
        depotId: parseInt(game.appId, 10) + 1,
        gameName: game.name,
        gameAppId: parseInt(game.appId, 10),
        service: 'steam',
        clientIp: CLIENT_IPS[index],
        bytesPerSecond,
        totalBytes,
        requestCount: 12 + index * 7,
        cacheHitBytes,
        cacheMissBytes: totalBytes - cacheHitBytes,
        cacheHitPercent: (cacheHitBytes / totalBytes) * 100
      };
    });

    const clientSpeeds: ClientSpeedInfo[] = gameSpeeds.map((game, index) => ({
      clientIp: CLIENT_IPS[index],
      bytesPerSecond: game.bytesPerSecond,
      totalBytes: game.totalBytes,
      activeGames: 1,
      cacheHitBytes: game.cacheHitBytes,
      cacheMissBytes: game.cacheMissBytes
    }));

    return {
      timestampUtc: new Date().toISOString(),
      totalBytesPerSecond: gameSpeeds.reduce((sum, game) => sum + game.bytesPerSecond, 0),
      gameSpeeds,
      clientSpeeds,
      windowSeconds,
      entriesInWindow: gameSpeeds.reduce((sum, game) => sum + game.requestCount, 0),
      hasActiveDownloads: true
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
        const missed: (number | null)[] = [];
        for (let index = 0; index < maxBuckets; index++) {
          if (index >= eventBuckets) {
            served.push(null);
            saved.push(null);
            missed.push(null);
            continue;
          }

          const progress = eventBuckets === 1 ? 1 : index / (eventBuckets - 1);
          const point = profileWave(spec.profile, progress);
          served.push(point.served);
          saved.push(point.saved);
          missed.push(point.served - point.saved);
        }

        return {
          eventId: spec.id,
          name: spec.name,
          colorIndex: spec.colorIndex,
          served,
          saved,
          missed
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
   * One page of `/api/downloads/retro`, generated in the browser so mock mode can serve the
   * Downloads and Retro views without a request. It runs the endpoint's own order - the row
   * filters, then the group filters, then the merge, then the sort, then the page - so every
   * control on those pages changes what comes back here the way it does against a live server.
   */
  static generateMockRetroData(params: RetroDownloadQueryParams): RetroDownloadResponse {
    const pageSize = Math.min(Math.max(params.pageSize, 1), 200);
    const page = Math.max(params.page, 1);

    const filtered = filterMockRetroRows(buildMockRetroRows(new Date()), params);
    const merged =
      params.groupByService || params.groupByGame ? mergeMockRetroRows(filtered, params) : filtered;
    const ordered = sortMockRetroRows(merged, params);

    const totalItems = ordered.length;
    const start = (page - 1) * pageSize;

    return {
      // The two fields the rows carry for filtering are not on the wire, so they come off here.
      items: ordered
        .slice(start, start + pageSize)
        .map(({ isActive: _isActive, eventIds: _eventIds, ...item }) => item),
      totalItems,
      // Every row carries the downloads it stands for, so the download count is a sum over the
      // whole filtered list rather than a second pass.
      totalDownloads: ordered.reduce((sum, row) => sum + row.requestCount, 0),
      totalPages: Math.max(1, Math.ceil(totalItems / pageSize)),
      currentPage: page,
      pageSize
    };
  }
}

export default MockDataService;
