import { createContext } from 'react';
import type { GameCacheInfo, ServiceCacheInfo } from '../../types';

/**
 * Queue entry for the full cache-removal queue (GameCacheDetector). One entry
 * per game or service; the provider selects the per-item ApiService call from
 * `kind`.
 */
export type BulkQueueEntry =
  | { kind: 'service'; service: ServiceCacheInfo }
  | { kind: 'game'; game: GameCacheInfo };

/**
 * Queue entry for the evicted-items removal queue (StorageSection "Remove
 * Selected"). Structurally identical to {@link BulkQueueEntry} - one entry per
 * evicted game or service - but routed to the per-entity EVICTED endpoints
 * (removeEvictedForGame / removeEvictedForEpicGame / removeEvictedForNamedGame /
 * removeEvictedForService) instead of the full cache-removal endpoints. Aliased
 * rather than re-declared so the two unions can never drift apart.
 */
export type EvictedQueueEntry = BulkQueueEntry;

/**
 * Per-run options threaded into a bulk-removal entry point. `onSettled` is the
 * caller-supplied post-settle refresh (GameCacheDetector's `onDataRefresh`).
 * It must survive the provider hoist — the provider lives at app root and
 * never unmounts, so the refresh callback is captured per-run rather than at
 * provider instantiation time.
 */
export interface BulkRemovalRunOptions {
  /** Called once the queue settles (success, cancel, or error). */
  onSettled?: () => void;
  /**
   * Called whenever the in-flight item index advances, so the initiating
   * component can mirror inline progress (e.g. GameCacheDetector's bottom-right
   * toast). Receives the 1-based index, total, and the current item label.
   */
  onProgress?: (progress: { current: number; total: number; label: string }) => void;
  /** Flips true when the queue starts and false once it settles. */
  onRunningChange?: (running: boolean) => void;
}

/**
 * Context surface for the app-root bulk-removal provider. The cache queue is
 * pre-baked inside the provider (i18n + ApiService selection +
 * waitForSignalRCompletion live there); callers only pass the item list and the
 * per-run options. The run loop survives in-app tab switches because the
 * provider never unmounts.
 *
 * The evicted-items "Remove All" no longer lives here: it calls the batched
 * DELETE /api/cache/evicted endpoint and flows through the standard
 * eviction_removal notification (progress, cancel, page-refresh recovery).
 */
interface BulkRemovalContextType {
  runCacheRemoval: (items: BulkQueueEntry[], options: BulkRemovalRunOptions) => Promise<void>;
  isCacheRemovalRunning: boolean;
  /**
   * Sequential queue for the evicted-items "Remove Selected" batch. Dispatches
   * each entry to the correct per-entity evicted endpoint and waits for its
   * EvictionRemovalComplete before advancing. Shares the same run-options shape,
   * seeded bulk_removal card, and finalize transition as {@link runCacheRemoval}.
   */
  runEvictedRemoval: (items: EvictedQueueEntry[], options: BulkRemovalRunOptions) => Promise<void>;
  isEvictedRemovalRunning: boolean;
}

export const BulkRemovalContext = createContext<BulkRemovalContextType | undefined>(undefined);
