import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  DownloadTotals,
  ServiceFilterOption,
  SparklineDataResponse,
  HourlyActivityResponse,
  CacheSnapshotResponse
} from '../../types';
import type { DashboardBatchResponse } from './types';

/**
 * The dashboard state slices owned by the batch endpoint. Detection is applied
 * separately by the provider (it builds lookup maps), so it is tracked for
 * failure reporting only and never materialized here.
 */
export interface DashboardSlices {
  cacheInfo: CacheInfo | null;
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;
  latestDownloads: Download[];
  downloadTotals: DownloadTotals | null;
  filteredDownloadTotals: DownloadTotals | null;
  serviceOptions: ServiceFilterOption[];
  clientOptions: string[];
  sparklines: SparklineDataResponse | null;
  hourlyActivity: HourlyActivityResponse | null;
  cacheSnapshot: CacheSnapshotResponse | null;
}

interface ApplyBatchMeta {
  /** Range key of the fetch that produced this batch. */
  rangeKey: string;
  /** Range key of the currently displayed data (null before the first apply). */
  previousRangeKey: string | null;
}

interface ApplyBatchResult {
  next: DashboardSlices;
  hadPartialFailure: boolean;
  failedSectionKeys: (keyof DashboardBatchResponse)[];
}

/**
 * Identity of a fetch's time window and download selection. Live mode sends no
 * start/end params so its key is stable; rolling ranges are anchored and
 * minute-quantized upstream. The service and client filters belong here because
 * the batch applies them server-side: without them a failed filtered sub-query
 * would count as the same range and carry the previous selection's figures
 * forward under the newly chosen label.
 */
export function buildRangeKey(
  startTime?: number,
  endTime?: number,
  eventId?: number,
  service?: string,
  client?: string
): string {
  return `${startTime ?? ''}|${endTime ?? ''}|${eventId ?? ''}|${service ?? ''}|${client ?? ''}`;
}

/**
 * Applies a batch response to the previous slices under the wire contract:
 * null = the sub-query failed server-side, while an empty collection or a
 * cacheSnapshot with hasData:false is a successful result (live mode returns
 * hasData:false rather than null) and always applies.
 *
 * A failed time-range-dependent section keeps the previous value only when the
 * fetch targets the same range as the displayed data; on a range change the
 * previous value belongs to a different window, so the slice clears instead of
 * mixing data across ranges. Cache info is not range-dependent and keeps the
 * previous value on failure at any range.
 */
export function applyDashboardBatchResponse(
  prev: DashboardSlices,
  batch: DashboardBatchResponse,
  meta: ApplyBatchMeta
): ApplyBatchResult {
  const failedSectionKeys: (keyof DashboardBatchResponse)[] = [];
  const sameRange = meta.previousRangeKey === meta.rangeKey;

  const resolveSection = <T>(
    key: keyof DashboardBatchResponse,
    value: T | null | undefined,
    previousValue: T,
    clearedValue: T
  ): T => {
    if (value !== null && value !== undefined) {
      return value;
    }
    failedSectionKeys.push(key);
    return sameRange ? previousValue : clearedValue;
  };

  const cacheInfo = resolveSection('cache', batch.cache, prev.cacheInfo, prev.cacheInfo);

  if (batch.detection === null || batch.detection === undefined) {
    failedSectionKeys.push('detection');
  }

  const next: DashboardSlices = {
    cacheInfo,
    clientStats: resolveSection('clients', batch.clients, prev.clientStats, []),
    serviceStats: resolveSection('services', batch.services, prev.serviceStats, []),
    dashboardStats: resolveSection('dashboard', batch.dashboard, prev.dashboardStats, null),
    latestDownloads: resolveSection(
      'recentDownloads',
      batch.recentDownloads,
      prev.latestDownloads,
      []
    ),
    downloadTotals: resolveSection(
      'downloadTotals',
      batch.downloadTotals,
      prev.downloadTotals,
      null
    ),
    filteredDownloadTotals: resolveSection(
      'filteredDownloadTotals',
      batch.filteredDownloadTotals,
      prev.filteredDownloadTotals,
      null
    ),
    serviceOptions: resolveSection('serviceOptions', batch.serviceOptions, prev.serviceOptions, []),
    clientOptions: resolveSection('clientOptions', batch.clientOptions, prev.clientOptions, []),
    sparklines: resolveSection('sparklines', batch.sparklines, prev.sparklines, null),
    hourlyActivity: resolveSection(
      'hourlyActivity',
      batch.hourlyActivity,
      prev.hourlyActivity,
      null
    ),
    cacheSnapshot: resolveSection('cacheSnapshot', batch.cacheSnapshot, prev.cacheSnapshot, null)
  };

  return { next, hadPartialFailure: failedSectionKeys.length > 0, failedSectionKeys };
}
