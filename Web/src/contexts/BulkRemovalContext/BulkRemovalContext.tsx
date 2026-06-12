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
 * bar and pushes it to the bulk notification.
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

/**
 * App-root provider that owns the sequential full-cache bulk-removal queue.
 * Because it is mounted near the top of the provider tree and never unmounts,
 * the queue run loop survives in-app tab switches by construction — there is
 * no unmount-abort path to misfire on a Management-tab navigation.
 *
 * The queue is PRE-BAKED here: the i18n strings, the per-item ApiService
 * selection, and the `waitForSignalRCompletion` plumbing all live in this file
 * (moved verbatim from GameCacheDetector). Callers only supply the item list
 * and the per-run options (`onSettled` refresh, inline `onProgress`, and
 * `onRunningChange`).
 *
 * The evicted-items "Remove All" no longer queues per-entity removals here:
 * it calls the batched DELETE /api/cache/evicted endpoint (one log rewrite
 * pass + one DB transaction server-side) and its progress/cancel/recovery flow
 * through the standard eviction_removal notification.
 */
export const BulkRemovalProvider: React.FC<BulkRemovalProviderProps> = ({ children }) => {
  const { t } = useTranslation();
  const { addNotification, updateNotification } = useNotifications();
  const { on, off } = useSignalR();

  // Per-run options are captured at run() time but the hook-level onSettled is
  // instantiation-time, so we stash the current run's options in a ref that the
  // instantiation-time onSettled reads. This is what keeps the caller's
  // post-settle refresh (GameCacheDetector.onDataRefresh) alive across the
  // provider hoist.
  const cacheRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);

  const { run: runCacheQueue, state: cacheState } = useCancellableQueue<BulkQueueEntry>({
    onSettled: () => {
      const opts = cacheRunOptionsRef.current;
      opts?.onRunningChange?.(false);
      opts?.onSettled?.();
    }
  });

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

  const isCacheRemovalRunning = cacheState.status === 'running';

  // Memoized so a parent re-render (NotificationsProvider updates on every
  // notification tick) does not hand consumers a fresh context object when
  // nothing they read has changed.
  const contextValue = useMemo(
    () => ({
      runCacheRemoval,
      isCacheRemovalRunning
    }),
    [runCacheRemoval, isCacheRemovalRunning]
  );

  return <BulkRemovalContext.Provider value={contextValue}>{children}</BulkRemovalContext.Provider>;
};
