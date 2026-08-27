/**
 * Wire shapes for the unmapped-cache section. They live beside the component rather than
 * inside it because a `.tsx` may only export React components.
 */

/**
 * Bumped whenever a stored scan's shape changes; the API's own
 * `UnmappedScanReport.SupportedContractVersion` is the other half of the pair. A snapshot
 * written by another version is refused rather than read: a tab left open across an app
 * upgrade holds the previous bundle and keeps talking to the new API, and a
 * half-understood snapshot would name the wrong files deletable.
 */
export const UNMAPPED_CONTRACT_VERSION = 1;

/** The saved scan snapshot, returned without running anything. */
export interface CachedUnmappedScanResponse {
  hasCachedResults: boolean;
  contractVersion: number;
  scanId: string | null;
  lastScanTime: string | null;
  totalFiles: number;
  totalBytes: number;
  services: UnmappedServiceRow[];
}

/**
 * One service's share of the orphaned files. One list rather than parallel count maps, so a
 * service cannot arrive carrying a file count without a byte total.
 */
export interface UnmappedServiceRow {
  service: string;
  fileCount: number;
  totalBytes: number;
}

/**
 * One orphaned cache file. `url` is the upstream address recovered from the file's own
 * stored nginx key and is the only thing that says what a hash-named file holds; it is
 * null when that key could not be read, and then the path is all there is to show.
 */
export interface UnmappedCacheFile {
  id: string;
  path: string;
  url: string | null;
  sizeBytes: number;
}
