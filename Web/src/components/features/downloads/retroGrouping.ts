import { formatDateTime, isFromDifferentYear } from '@utils/formatters';
import type { RetroDownloadDto } from '@services/api.service';
import type { Download as DownloadType, DownloadGroup } from '../../../types';

export type RetroSortOrder =
  | 'recent'
  | 'latest' // accepted as alias of recent (API / legacy)
  | 'oldest'
  | 'largest'
  | 'smallest'
  | 'service'
  | 'efficiency'
  | 'efficiency-low'
  | 'sessions'
  | 'alphabetical';

// Data for one rendered retro row: a depot+client group (or a whole game when
// grouping by game). Produced either client-side by groupByDepot or by mapping
// server DTOs via mapDtoToDepotGroupedData.
export interface DepotGroupedData {
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
  depotsSet: Set<number>;
  datasource?: string;
  averageBytesPerSecond: number;
  downloadIds: number[]; // Track original download IDs for event associations
  isEvicted?: boolean;
  isPartiallyEvicted?: boolean;
}

/**
 * Format a time range with consistent year display
 * If either date is from a different year than now, both dates show the year
 * @param startTimeUtc - Start time
 * @param endTimeUtc - End time
 * @param forceYear - If true, always include year in both dates (for measurement)
 */
export const formatTimeRange = (
  startTimeUtc: string,
  endTimeUtc: string,
  forceYear = false
): string => {
  // Check if either date needs the year displayed
  const needsYear =
    forceYear || isFromDifferentYear(startTimeUtc) || isFromDifferentYear(endTimeUtc);

  const startTime = formatDateTime(startTimeUtc, needsYear);
  const endTime = formatDateTime(endTimeUtc, needsYear);

  return startTime === endTime ? startTime : `${startTime} - ${endTime}`;
};

/**
 * Same range as formatTimeRange but split into stacked display lines so the
 * timestamp column never truncates: ["start", null] when both ends match,
 * otherwise ["start", "→ end"].
 */
export const formatTimeRangeLines = (
  startTimeUtc: string,
  endTimeUtc: string
): [string, string | null] => {
  const needsYear = isFromDifferentYear(startTimeUtc) || isFromDifferentYear(endTimeUtc);

  const startTime = formatDateTime(startTimeUtc, needsYear);
  const endTime = formatDateTime(endTimeUtc, needsYear);

  return startTime === endTime ? [startTime, null] : [startTime, `→ ${endTime}`];
};

// Helper to check if item is a DownloadGroup
const isDownloadGroup = (item: DownloadType | DownloadGroup): item is DownloadGroup => {
  return 'downloads' in item;
};

// Build the grouping key for a single download row.
// - depot mode: one row per (depot, client) - matches historical retro behavior.
// - game mode: one row per (service, gameAppId|epicAppId|gameName) - collapses
//   every depot and every client for the same game into a single row.
const buildGroupKey = (download: DownloadType, groupByGame: boolean): string => {
  if (groupByGame) {
    const gameId =
      download.gameAppId != null
        ? `app-${download.gameAppId}`
        : download.epicAppId
          ? `epic-${download.epicAppId}`
          : download.gameName && download.gameName !== download.service
            ? `name-${download.gameName.toLowerCase()}`
            : // No resolved game identity (e.g. WSUS/Windows Update, unmapped depots):
              // collapse every such row for this service into one per-service bucket so
              // grouping by game groups them together instead of one row per download.
              'unknown';
    return `game-${download.service}-${gameId}`;
  }
  return download.depotId
    ? `depot-${download.depotId}-${download.clientIp}`
    : `no-depot-${download.service}-${download.clientIp}-${download.id}`;
};

// Group items for retro view display (client-side path).
// See buildGroupKey for the two supported grouping modes.
export const groupByDepot = (
  items: (DownloadType | DownloadGroup)[],
  sortOrder: RetroSortOrder = 'recent',
  groupByGame = false
): DepotGroupedData[] => {
  const depotGroups: Record<
    string,
    DepotGroupedData & {
      _weightedSpeedSum: number;
      _speedBytesSum: number;
      _hasEvicted: boolean;
      _hasNonEvicted: boolean;
    }
  > = {};

  const ingest = (download: DownloadType) => {
    const depotKey = buildGroupKey(download, groupByGame);

    if (!depotGroups[depotKey]) {
      depotGroups[depotKey] = {
        id: depotKey,
        service: download.service,
        gameName: download.gameName || download.service,
        gameAppId: download.gameAppId || null,
        epicAppId: download.epicAppId || null,
        depotId: download.depotId || null,
        clientIp: download.clientIp,
        startTimeUtc: download.startTimeUtc,
        endTimeUtc: download.endTimeUtc || download.startTimeUtc,
        cacheHitBytes: 0,
        cacheMissBytes: 0,
        totalBytes: 0,
        requestCount: 0,
        clientsSet: new Set<string>(),
        depotsSet: new Set<number>(),
        datasource: download.datasource,
        averageBytesPerSecond: 0,
        downloadIds: [],
        isEvicted: false,
        isPartiallyEvicted: false,
        _hasEvicted: false,
        _hasNonEvicted: false,
        _weightedSpeedSum: 0,
        _speedBytesSum: 0
      };
    }

    const group = depotGroups[depotKey];
    // Track eviction across all downloads in the group
    if (download.isEvicted) {
      group._hasEvicted = true;
    } else {
      group._hasNonEvicted = true;
    }
    group.downloadIds.push(download.id);
    group.cacheHitBytes += download.cacheHitBytes || 0;
    group.cacheMissBytes += download.cacheMissBytes || 0;
    group.totalBytes += download.totalBytes || 0;
    group.requestCount += 1;
    group.clientsSet.add(download.clientIp);
    if (download.depotId) group.depotsSet.add(download.depotId);

    const speed = download.averageBytesPerSecond;
    const bytes = download.totalBytes || 0;
    if (speed > 0 && bytes > 0) {
      group._weightedSpeedSum += speed * bytes;
      group._speedBytesSum += bytes;
    }

    if (download.startTimeUtc < group.startTimeUtc) {
      group.startTimeUtc = download.startTimeUtc;
    }
    const endTime = download.endTimeUtc || download.startTimeUtc;
    if (endTime > group.endTimeUtc) {
      group.endTimeUtc = endTime;
    }
  };

  items.forEach((item) => {
    if (isDownloadGroup(item)) {
      item.downloads.forEach((download) => ingest(download));
    } else {
      ingest(item);
    }
  });

  const grouped = Object.values(depotGroups).map((group) => {
    const { _weightedSpeedSum, _speedBytesSum, _hasEvicted, _hasNonEvicted, ...cleanGroup } = group;
    cleanGroup.averageBytesPerSecond = _speedBytesSum > 0 ? _weightedSpeedSum / _speedBytesSum : 0;
    cleanGroup.isEvicted = _hasEvicted && !_hasNonEvicted;
    cleanGroup.isPartiallyEvicted = _hasEvicted && _hasNonEvicted;
    return cleanGroup as DepotGroupedData;
  });

  return grouped.sort((a, b) => {
    switch (sortOrder) {
      case 'oldest':
        return new Date(a.startTimeUtc).getTime() - new Date(b.startTimeUtc).getTime();
      case 'largest':
        return b.totalBytes - a.totalBytes;
      case 'smallest':
        return a.totalBytes - b.totalBytes;
      case 'service': {
        const serviceCompare = a.service.localeCompare(b.service);
        if (serviceCompare !== 0) return serviceCompare;
        return new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime();
      }
      case 'efficiency': {
        const aEff = a.totalBytes > 0 ? (a.cacheHitBytes / a.totalBytes) * 100 : 0;
        const bEff = b.totalBytes > 0 ? (b.cacheHitBytes / b.totalBytes) * 100 : 0;
        return bEff - aEff;
      }
      case 'efficiency-low': {
        const aEffLow = a.totalBytes > 0 ? (a.cacheHitBytes / a.totalBytes) * 100 : 0;
        const bEffLow = b.totalBytes > 0 ? (b.cacheHitBytes / b.totalBytes) * 100 : 0;
        return aEffLow - bEffLow;
      }
      case 'sessions':
        return b.requestCount - a.requestCount;
      case 'alphabetical':
        return a.gameName.localeCompare(b.gameName);
      case 'recent':
      case 'latest':
      default:
        return new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime();
    }
  });
};

/**
 * Map server-paginated RetroDownloadDto rows into the in-memory DepotGroupedData
 * shape used by the retro row renderer. The server already groups by
 * (depotId, clientIp), sorts, and paginates, so no further regrouping is
 * required when `serverMode` is active.
 */
export const mapDtoToDepotGroupedData = (dto: RetroDownloadDto): DepotGroupedData => {
  // Prefer server-provided arrays (populated for both merged and non-merged rows).
  // Defensive fallback to singular fields for staged-deploy safety (remove once backend ships).
  const clientsSet = new Set<string>(dto.clientIps ?? [dto.clientIp]);
  const depotsSet = new Set<number>(
    (dto.depotIds ?? (dto.depotId != null ? [dto.depotId] : [])).filter((d) => d != null)
  );
  return {
    id: dto.id,
    service: dto.service,
    gameName: dto.appName,
    gameAppId: dto.steamAppId,
    epicAppId: dto.epicAppId,
    depotId: dto.depotId,
    clientIp: dto.clientIp,
    startTimeUtc: dto.startTimeUtc,
    endTimeUtc: dto.endTimeUtc,
    cacheHitBytes: dto.cacheHitBytes,
    cacheMissBytes: dto.cacheMissBytes,
    totalBytes: dto.totalBytes,
    requestCount: dto.requestCount,
    clientsSet,
    depotsSet,
    datasource: dto.datasource,
    averageBytesPerSecond: dto.averageBytesPerSecond,
    downloadIds: dto.downloadIds,
    isEvicted: false,
    isPartiallyEvicted: false
  };
};

// Hit-rate classification shared by the row renderer (gauge color, accent
// stripe) and the column auto-fit measurement (gauge label width).
export type EfficiencyTier = 'success' | 'warning' | 'error';

export const efficiencyTier = (percent: number): EfficiencyTier => {
  if (percent >= 90) return 'success';
  if (percent >= 50) return 'warning';
  return 'error';
};
