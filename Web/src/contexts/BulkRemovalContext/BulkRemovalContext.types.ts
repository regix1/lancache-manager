import { createContext } from 'react';
import type { GameCacheInfo, ServiceCacheInfo } from '../../types';

/**
 * Queue entry for a bulk-removal queue. One entry per game or service; the
 * provider selects the per-item ApiService call from `kind`. Used by both the
 * evicted-items removal queue (StorageSection) and the full cache-removal queue
 * (GameCacheDetector) — they share the same entry shape; only the per-item
 * ApiService selection inside the provider differs.
 */
export type BulkQueueEntry =
  | { kind: 'service'; service: ServiceCacheInfo }
  | { kind: 'game'; game: GameCacheInfo };

/**
 * Per-run options threaded into a bulk-removal entry point. `onSettled` is the
 * caller-supplied post-settle refresh (StorageSection's `fetchEvictedItems`,
 * GameCacheDetector's `onDataRefresh`). It must survive the provider hoist —
 * the provider lives at app root and never unmounts, so the refresh callback is
 * captured per-run rather than at provider instantiation time.
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
 * Context surface for the app-root bulk-removal provider. Both queues are
 * pre-baked inside the provider (i18n + ApiService selection +
 * waitForSignalRCompletion live there); callers only pass the item list and the
 * per-run options. The run loop survives in-app tab switches because the
 * provider never unmounts.
 */
interface BulkRemovalContextType {
  runEvictedRemoval: (items: BulkQueueEntry[], options: BulkRemovalRunOptions) => Promise<void>;
  isEvictedRemovalRunning: boolean;
  runCacheRemoval: (items: BulkQueueEntry[], options: BulkRemovalRunOptions) => Promise<void>;
  isCacheRemovalRunning: boolean;
}

export const BulkRemovalContext = createContext<BulkRemovalContextType | undefined>(undefined);
