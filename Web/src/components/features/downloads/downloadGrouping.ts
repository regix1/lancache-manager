import { getServiceDisplayName } from '@utils/serviceDisplayName';
import type { Download, DownloadGroup } from '../../../types';
import { isResolvedGameName } from './liveDownloadPreviews';

/**
 * Wraps one download in the group shape the row and card renderers accept, so an individual
 * download goes through the same render path as a real group. Every field here is part of that
 * contract: the `individual-` id keys the row and drives expand/collapse, the service name is the
 * fallback title when the game is still unidentified, and the single-item list, client set and
 * repeated start timestamp keep session lists and time labels correct for a group of one. The
 * membership answers collapse to that one row: it is either evicted or it is not, never partially.
 */
const toSingleDownloadGroup = (download: Download): DownloadGroup => {
  const totalBytes = download.totalBytes;

  return {
    id: `individual-${download.id}`,
    name: download.gameName || getServiceDisplayName(download.service),
    type: 'game',
    service: download.service,
    downloads: [download],
    downloadIds: [download.id],
    totalBytes,
    totalDownloaded: totalBytes,
    cacheHitBytes: download.cacheHitBytes,
    cacheMissBytes: download.cacheMissBytes,
    clientsSet: new Set([download.clientIp]),
    firstSeen: download.startTimeUtc,
    lastSeen: download.startTimeUtc,
    count: 1,
    isEvicted: download.isEvicted,
    isPartiallyEvicted: false,
    hasRealGameName: isResolvedGameName(download.gameName, download.service)
  };
};

/**
 * Reads a row as a group. A real group is already in that shape and passes through untouched; a
 * lone download is wrapped, so both reach the row and card renderers the same way. `downloads` is
 * the field only groups carry, so it is what tells the two apart.
 */
export const toGroup = (item: Download | DownloadGroup): DownloadGroup =>
  'downloads' in item ? (item as DownloadGroup) : toSingleDownloadGroup(item as Download);

/**
 * Share of bytes served from cache, for either a single download or a whole group. A zero total
 * reports zero percent instead of dividing by zero. Display rounding stays in `formatPercent`.
 */
export const cacheHitPercent = (cacheHitBytes: number, totalBytes: number): number =>
  totalBytes > 0 ? (cacheHitBytes / totalBytes) * 100 : 0;
