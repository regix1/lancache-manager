import { formatTimestamp, type ReaderClock } from '@utils/dateTimeFormat';
import { isFromDifferentYear } from '@utils/formatters';
import type { RetroDownloadDto } from '@services/api.service';

// Data for one rendered retro row: a depot+client group (or a whole game when
// grouping by game). Produced by mapping server DTOs via
// mapDtoToDepotGroupedData - grouping, sorting and pagination all happen in the
// API so the browser never holds the full download set.
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
 * A time range split into stacked display lines so the timestamp column never
 * truncates: ["start", null] when both ends match, otherwise ["start", "→ end"].
 * If either date is from a different year than now, both dates show the year.
 */
export const formatTimeRangeLines = (
  startTimeUtc: string,
  endTimeUtc: string,
  clock: ReaderClock
): [string, string | null] => {
  const needsYear = isFromDifferentYear(startTimeUtc) || isFromDifferentYear(endTimeUtc);

  const startTime = formatTimestamp(startTimeUtc, { ...clock, forceYear: needsYear });
  const endTime = formatTimestamp(endTimeUtc, { ...clock, forceYear: needsYear });

  return startTime === endTime ? [startTime, null] : [startTime, `→ ${endTime}`];
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
    isEvicted: dto.isEvicted,
    isPartiallyEvicted: dto.isPartiallyEvicted
  };
};
