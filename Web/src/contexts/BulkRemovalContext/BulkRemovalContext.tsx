import React, { useCallback, useMemo, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { waitForSignalRCompletion } from '@contexts/notifications/waitForSignalRCompletion';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useCancellableQueue } from '@/hooks/useCancellableQueue';
import { finalizeBulkRemovalNotification } from '@components/features/management/game-detection/cacheRemovalHelpers';
import {
  BulkRemovalContext,
  type BulkRemovalRunOptions,
  type BulkQueueEntry
} from './BulkRemovalContext.types';

interface BulkRemovalProviderProps {
  children: ReactNode;
}

/**
 * Inputs for {@link updateBulkProgress}. Strongly typed (no loose lambda capture)
 * so both bulk-removal pipelines share one progress-mapping implementation.
 */
interface BulkProgressUpdate {
  /** The bulk notification id, or null before openNotification has run. */
  bulkNotifId: string | null;
  /** 1-based index of the item currently in flight. */
  currentIndex: number;
  /** Total number of items in the run. */
  total: number;
  /** Inner per-item percent (0-100) from the current item's SignalR progress. */
  inner: number;
  updateNotification: (id: string, updates: { progress: number }) => void;
}

/**
 * Maps a per-item inner percent (0-100) onto the overall bulk-removal progress
 * bar and pushes it to the bulk notification. Shared by the evicted-items and
 * full-cache pipelines (their progress maths were byte-identical).
 */
function updateBulkProgress({
  bulkNotifId,
  currentIndex,
  total,
  inner,
  updateNotification
}: BulkProgressUpdate): void {
  if (!bulkNotifId) return;
  const clamped = Math.min(100, Math.max(0, inner));
  const overall = Math.min(100, ((currentIndex - 1 + clamped / 100) / total) * 100);
  updateNotification(bulkNotifId, { progress: Math.floor(overall) });
}

/** Inputs for {@link pollOperationUntilDone}. */
interface PollOperationArgs {
  operationId: string;
  /** Aborts the poll loop between probes (user cancel). */
  signal: AbortSignal;
  /** Receives the operation's inner percent (0-100) on each probe. */
  onPercent: (percent: number) => void;
}

/**
 * Awaits completion of a SILENT per-entity removal by polling the operation
 * tracker. Silent ops emit no SignalR events by design, so the old
 * waitForSignalRCompletion approach cannot see them (and previously burned a
 * 120s timeout per item). Resolves when the tracker no longer reports the
 * operation as active; rejects only on the safety cap.
 */
async function pollOperationUntilDone({
  operationId,
  signal,
  onPercent
}: PollOperationArgs): Promise<void> {
  const POLL_INTERVAL_MS = 500;
  const MAX_POLLS = 1200; // 10 minutes - safety cap so a stuck op cannot hang the queue forever

  for (let i = 0; i < MAX_POLLS; i += 1) {
    if (signal.aborted) return;
    const status = await ApiService.getOperationStatus(operationId);
    if (!status.active) return;
    onPercent(status.percentComplete ?? 0);
    await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));
  }
  throw new Error(`Operation ${operationId} did not finish within the polling window`);
}

/**
 * App-root provider that owns the two sequential bulk-removal queues (evicted
 * items + full cache). Because it is mounted near the top of the provider tree
 * and never unmounts, the queue run loop survives in-app tab switches by
 * construction — there is no unmount-abort path to misfire on a Management-tab
 * navigation.
 *
 * Both queues are PRE-BAKED here: the i18n strings, the per-item ApiService
 * selection, and the `waitForSignalRCompletion` plumbing all live in this file
 * (moved verbatim from StorageSection / GameCacheDetector). Callers only supply
 * the item list and the per-run options (`onSettled` refresh, inline `onProgress`,
 * and `onRunningChange`). The evicted pipeline runs per-item removals SILENT
 * (one bulk notification; completion via status polling; disk-summary refresh
 * deferred to the last item), while the cache pipeline still consumes per-item
 * SignalR events.
 */
export const BulkRemovalProvider: React.FC<BulkRemovalProviderProps> = ({ children }) => {
  const { t } = useTranslation();
  const { addNotification, updateNotification } = useNotifications();
  const { on, off } = useSignalR();

  // Per-run options are captured at run() time but the hook-level onSettled is
  // instantiation-time, so we stash the current run's options in refs that the
  // instantiation-time onSettled reads. This is what keeps each caller's
  // post-settle refresh (StorageSection.fetchEvictedItems /
  // GameCacheDetector.onDataRefresh) alive across the provider hoist.
  const evictedRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);
  const cacheRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);

  const { run: runEvictedQueue, state: evictedState } = useCancellableQueue<BulkQueueEntry>({
    onSettled: () => {
      const opts = evictedRunOptionsRef.current;
      opts?.onRunningChange?.(false);
      opts?.onSettled?.();
    }
  });

  const { run: runCacheQueue, state: cacheState } = useCancellableQueue<BulkQueueEntry>({
    onSettled: () => {
      const opts = cacheRunOptionsRef.current;
      opts?.onRunningChange?.(false);
      opts?.onSettled?.();
    }
  });

  const runEvictedRemoval = useCallback(
    async (items: BulkQueueEntry[], options: BulkRemovalRunOptions): Promise<void> => {
      const total = items.length;
      if (total === 0) return;

      evictedRunOptionsRef.current = options;
      options.onRunningChange?.(true);

      let bulkNotifId: string | null = null;
      let currentIndex = 0;

      await runEvictedQueue({
        items,
        openNotification: () => {
          const id = addNotification({
            type: 'bulk_removal',
            status: 'running',
            message: t('management.sections.data.evictionRemoveAllStarting', {
              total,
              defaultValue: 'Removing 0 of {{total}} evicted items...'
            }),
            progress: 0,
            // No operationId → handleCancel special-cases bulk_removal (sets cancelling=true)
            details: {}
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label =
            entry.kind === 'service'
              ? entry.service.service_name
              : (entry.game.game_name ?? entry.game.service ?? String(entry.game.game_app_id));

          options.onProgress?.({ current: index, total, label });
          updateNotification(notifId, {
            message: t('management.sections.data.evictionRemoveAllProgress', {
              current: index,
              total,
              label
            }),
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          if (entry.kind === 'service') {
            const { operationId } = await ApiService.removeEvictedForService(
              entry.service.service_name,
              { silent: true, deferRefresh: currentIndex < total }
            );
            ctx.setOperationId(operationId);
            await pollOperationUntilDone({
              operationId,
              signal: ctx.signal,
              onPercent: (percent) =>
                updateBulkProgress({
                  bulkNotifId,
                  currentIndex,
                  total,
                  inner: percent,
                  updateNotification
                })
            });
          } else {
            const game = entry.game;
            const isEpic = game.service === 'epicgames' && !!game.epic_app_id;
            const { operationId } = isEpic
              ? await ApiService.removeEvictedForEpicGame(game.epic_app_id!, {
                  silent: true,
                  deferRefresh: currentIndex < total
                })
              : await ApiService.removeEvictedForGame(game.game_app_id, {
                  silent: true,
                  deferRefresh: currentIndex < total
                });
            ctx.setOperationId(operationId);
            await pollOperationUntilDone({
              operationId,
              signal: ctx.signal,
              onPercent: (percent) =>
                updateBulkProgress({
                  bulkNotifId,
                  currentIndex,
                  total,
                  inner: percent,
                  updateNotification
                })
            });
          }
        },
        finalize: ({ id, succeeded, failed, cancelled, total: finalizeTotal }) => {
          finalizeBulkRemovalNotification({
            id,
            succeeded,
            failed,
            total: finalizeTotal,
            cancelled,
            t,
            updateNotification,
            text: {
              completeKey: 'management.sections.data.evictionRemoveAllComplete',
              completeDefaultValue: 'Removed {{count}} evicted items',
              partialFailureKey: 'management.sections.data.evictionRemoveAllCompleteWithFailures',
              partialFailureDefaultValue: 'Removed {{count}} evicted items, but {{failed}} failed',
              cancelledKey: 'management.sections.data.evictionRemoveAllCancelled',
              cancelledDefaultValue: 'Bulk removal cancelled after {{count}} items',
              cancelledWithFailuresKey:
                'management.sections.data.evictionRemoveAllCancelledWithFailures',
              cancelledWithFailuresDefaultValue:
                'Bulk removal cancelled after {{count}} items, with {{failed}} failures'
            }
          });
        }
      });
    },
    [addNotification, updateNotification, runEvictedQueue, t]
  );

  const runCacheRemoval = useCallback(
    async (items: BulkQueueEntry[], options: BulkRemovalRunOptions): Promise<void> => {
      const total = items.length;
      if (total === 0) return;

      cacheRunOptionsRef.current = options;
      options.onRunningChange?.(true);

      let bulkNotifId: string | null = null;
      let currentIndex = 0;

      await runCacheQueue({
        items,
        openNotification: () => {
          const id = addNotification({
            type: 'bulk_removal',
            status: 'running',
            message: t('management.sections.data.gameCacheRemoveAllStarting', {
              total,
              defaultValue: 'Removing 0 of {{total}} cached items...'
            }),
            progress: 0,
            // No operationId → handleCancel special-cases bulk_removal
            details: {}
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label =
            entry.kind === 'service'
              ? entry.service.service_name
              : (entry.game.game_name ?? String(entry.game.game_app_id));
          options.onProgress?.({ current: index, total, label });
          updateNotification(notifId, {
            message: t('management.sections.data.gameCacheRemoveAllProgress', {
              current: index,
              total,
              label
            }),
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          if (entry.kind === 'service') {
            const serviceName = entry.service.service_name;
            let operationId: string | null = null;
            const waitPromise = waitForSignalRCompletion<
              { serviceName?: string; operationId?: string },
              { serviceName?: string },
              { operationId?: string; percentComplete?: number }
            >({
              signalR: { on, off },
              completeEvent: 'ServiceRemovalComplete',
              startedEvent: 'ServiceRemovalStarted',
              match: (payload) => payload?.serviceName === serviceName,
              onStartedCapture: (payload) =>
                payload?.serviceName === serviceName && typeof payload.operationId === 'string'
                  ? { opId: payload.operationId }
                  : null,
              onOperationIdCaptured: (opId) => {
                operationId = opId;
                ctx.setOperationId(opId);
              },
              progressEvent: 'ServiceRemovalProgress',
              onProgress: (payload) => {
                if (!operationId || payload?.operationId !== operationId) return;
                updateBulkProgress({
                  bulkNotifId,
                  currentIndex,
                  total,
                  inner: payload.percentComplete ?? 0,
                  updateNotification
                });
              },
              signal: ctx.signal
            });
            await ApiService.removeServiceFromCache(serviceName);
            await waitPromise;
          } else {
            const game = entry.game;
            const gameAppId = game.game_app_id;
            const gameName = game.game_name;
            const isEpic = game.service === 'epicgames' && !!gameName;
            const epicAppId = game.epic_app_id ?? undefined;
            let currentOperationId: string | null = null;
            const matchesGame = (payload?: {
              gameAppId?: number | null;
              epicAppId?: string | null;
              gameName?: string;
              operationId?: string;
            }): boolean => {
              if (!payload) {
                return false;
              }

              if (currentOperationId) {
                return payload.operationId === currentOperationId;
              }

              if (isEpic) {
                if (epicAppId && payload.epicAppId === epicAppId) {
                  return true;
                }

                return payload.gameName === gameName;
              }

              return payload.gameAppId === gameAppId;
            };
            const waitPromise = waitForSignalRCompletion<
              {
                gameAppId?: number | null;
                epicAppId?: string | null;
                gameName?: string;
                operationId?: string;
              },
              {
                gameAppId?: number | null;
                epicAppId?: string | null;
                gameName?: string;
                operationId?: string;
              },
              { operationId?: string; percentComplete?: number }
            >({
              signalR: { on, off },
              completeEvent: 'GameRemovalComplete',
              startedEvent: 'GameRemovalStarted',
              match: matchesGame,
              onStartedCapture: (payload) =>
                matchesGame(payload) && typeof payload.operationId === 'string'
                  ? { opId: payload.operationId }
                  : null,
              onOperationIdCaptured: (opId) => {
                currentOperationId = opId;
                ctx.setOperationId(opId);
              },
              progressEvent: 'GameRemovalProgress',
              onProgress: (payload) => {
                if (!currentOperationId || payload?.operationId !== currentOperationId) return;
                updateBulkProgress({
                  bulkNotifId,
                  currentIndex,
                  total,
                  inner: payload.percentComplete ?? 0,
                  updateNotification
                });
              },
              signal: ctx.signal
            });

            if (isEpic) {
              const response = await ApiService.removeEpicGameFromCache(gameName);
              currentOperationId = response.operationId;
              ctx.setOperationId(response.operationId);
            } else {
              const response = await ApiService.removeGameFromCache(gameAppId);
              currentOperationId = response.operationId;
              ctx.setOperationId(response.operationId);
            }
            await waitPromise;
          }
        },
        finalize: ({ id, succeeded, failed, cancelled, total: finalizeTotal }) => {
          finalizeBulkRemovalNotification({
            id,
            succeeded,
            failed,
            total: finalizeTotal,
            cancelled,
            t,
            updateNotification,
            text: {
              completeKey: 'management.sections.data.gameCacheRemoveAllComplete',
              completeDefaultValue: 'Removed {{count}} cached items',
              partialFailureKey: 'management.sections.data.gameCacheRemoveAllCompleteWithFailures',
              partialFailureDefaultValue: 'Removed {{count}} cached items, but {{failed}} failed',
              cancelledKey: 'management.sections.data.gameCacheRemoveAllCancelled',
              cancelledDefaultValue: 'Bulk removal cancelled after {{count}} items',
              cancelledWithFailuresKey:
                'management.sections.data.gameCacheRemoveAllCancelledWithFailures',
              cancelledWithFailuresDefaultValue:
                'Bulk removal cancelled after {{count}} items, with {{failed}} failures'
            }
          });
        }
      });
    },
    [addNotification, updateNotification, runCacheQueue, on, off, t]
  );

  const isEvictedRemovalRunning = evictedState.status === 'running';
  const isCacheRemovalRunning = cacheState.status === 'running';

  // Memoized so a parent re-render (NotificationsProvider updates on every
  // notification tick) does not hand consumers a fresh context object when
  // nothing they read has changed.
  const contextValue = useMemo(
    () => ({
      runEvictedRemoval,
      isEvictedRemovalRunning,
      runCacheRemoval,
      isCacheRemovalRunning
    }),
    [runEvictedRemoval, isEvictedRemovalRunning, runCacheRemoval, isCacheRemovalRunning]
  );

  return <BulkRemovalContext.Provider value={contextValue}>{children}</BulkRemovalContext.Provider>;
};
