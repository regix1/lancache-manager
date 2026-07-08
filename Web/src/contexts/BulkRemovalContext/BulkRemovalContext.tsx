import React, { useCallback, useMemo, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { waitForSignalRCompletion } from '@contexts/notifications/waitForSignalRCompletion';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useCancellableQueue } from '@/hooks/useCancellableQueue';
import { finalizeBulkRemovalNotification } from '@components/features/management/game-detection/cacheRemovalHelpers';
import type {
  EvictionRemovalStartedEvent,
  EvictionRemovalCompleteEvent,
  EvictionRemovalProgressEvent
} from '@contexts/SignalRContext/types';
import {
  BulkRemovalContext,
  type BulkRemovalRunOptions,
  type BulkQueueEntry,
  type EvictedQueueEntry
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
              signal: ctx.signal,
              timeoutMs: 600_000
            });
            await ApiService.removeServiceFromCache(serviceName);
            const outcome = await waitPromise;
            if (outcome.timedOut) {
              // No completion within the window: count as a failure rather than a
              // silent success so the batch tally stays honest.
              throw new Error(`Service removal timed out for ${serviceName}`);
            }
          } else {
            const game = entry.game;
            const gameAppId = game.game_app_id;
            const gameName = game.game_name;
            const isEpic = game.service === 'epicgames' && !!gameName;
            // Named (Blizzard/Riot) games have game_app_id === 0, no Epic id, and a
            // non-Steam service. Their identity is (service, gameName) - every named game
            // shares gameAppId 0, so matching by gameAppId would collide and the Steam
            // removal endpoint (key=0) would 400. Mirror runTrackedGameRemoval.
            const isNamed =
              !isEpic && gameAppId === 0 && !!game.service && game.service !== 'steam';
            const epicAppId = game.epic_app_id ?? undefined;
            const namedService = isNamed ? game.service : undefined;
            let currentOperationId: string | null = null;
            const matchesGame = (payload?: {
              gameAppId?: number | null;
              epicAppId?: string | null;
              gameName?: string;
              service?: string | null;
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

              // Named games (Blizzard/Riot/Xbox) all carry gameAppId=null/0 and epicAppId=null
              // in their event payload; their identity is (service, gameName). Matching on
              // gameName alone lets a same-named game on a DIFFERENT named service (e.g. an Xbox
              // title sharing a name with a Blizzard one) cross-complete, so when the payload
              // carries a service it must also match.
              if (isNamed) {
                if (payload.gameName !== gameName) {
                  return false;
                }
                return payload.service == null || payload.service === namedService;
              }

              return payload.gameAppId === gameAppId;
            };
            const waitPromise = waitForSignalRCompletion<
              {
                gameAppId?: number | null;
                epicAppId?: string | null;
                gameName?: string;
                service?: string | null;
                operationId?: string;
              },
              {
                gameAppId?: number | null;
                epicAppId?: string | null;
                gameName?: string;
                service?: string | null;
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
              signal: ctx.signal,
              timeoutMs: 600_000
            });

            if (isEpic) {
              const response = await ApiService.removeEpicGameFromCache(gameName);
              currentOperationId = response.operationId;
              ctx.setOperationId(response.operationId);
            } else if (isNamed) {
              const response = await ApiService.removeNamedGameFromCache(game.service!, gameName);
              currentOperationId = response.operationId;
              ctx.setOperationId(response.operationId);
            } else {
              const response = await ApiService.removeGameFromCache(gameAppId);
              currentOperationId = response.operationId;
              ctx.setOperationId(response.operationId);
            }
            const outcome = await waitPromise;
            if (outcome.timedOut) {
              // No completion within the window: count as a failure rather than a
              // silent success so the batch tally stays honest.
              throw new Error(`Game removal timed out for ${gameName ?? gameAppId}`);
            }
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

  // --- Evicted-items queue -------------------------------------------------
  // Structurally the same sequential/cancellable pipeline as the cache queue
  // above, but each item is dispatched to the correct per-entity EVICTED
  // endpoint (steam/epic/named game, or service) and waits for that op's
  // EvictionRemovalComplete. The completion event carries only an operationId
  // (no entity identity), and the per-entity DELETE returns its operationId in
  // the response body, so matching is opId-based - no Started-event correlation.
  const evictedRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);

  const { run: runEvictedQueue, state: evictedState } = useCancellableQueue<EvictedQueueEntry>({
    onSettled: () => {
      const opts = evictedRunOptionsRef.current;
      opts?.onRunningChange?.(false);
      opts?.onSettled?.();
    }
  });

  const runEvictedRemoval = useCallback(
    async (items: EvictedQueueEntry[], options: BulkRemovalRunOptions): Promise<void> => {
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
            message: t('management.sections.data.evictionRemoveSelectedStarting', {
              total,
              defaultValue: 'Removing 0 of {{total}} evicted items...'
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
            message: t('management.sections.data.evictionRemoveSelectedProgress', {
              current: index,
              total,
              label,
              defaultValue: 'Removing {{current}} of {{total}} - {{label}}'
            }),
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          let operationId: string | null = null;
          const waitPromise = waitForSignalRCompletion<
            EvictionRemovalStartedEvent,
            EvictionRemovalCompleteEvent,
            EvictionRemovalProgressEvent
          >({
            signalR: { on, off },
            completeEvent: 'EvictionRemovalComplete',
            match: (payload) => operationId !== null && payload?.operationId === operationId,
            progressEvent: 'EvictionRemovalProgress',
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
            signal: ctx.signal,
            timeoutMs: 600_000
          });

          // Dispatch to the per-entity evicted endpoint. Identity logic mirrors
          // StorageSection.confirmPartialEvictedRemoval exactly: Epic games are
          // keyed by epic_app_id, named (Blizzard/Riot/Xbox) games by
          // (service, gameName), Steam games by game_app_id.
          if (entry.kind === 'service') {
            const response = await ApiService.removeEvictedForService(entry.service.service_name);
            operationId = response.operationId;
          } else {
            const game = entry.game;
            const isEpic = game.service === 'epicgames';
            const isNamed =
              !isEpic && game.game_app_id === 0 && !!game.service && game.service !== 'steam';
            if (isEpic) {
              if (!game.epic_app_id) {
                throw new Error(t('management.gameDetection.failedToRemoveGame'));
              }
              const response = await ApiService.removeEvictedForEpicGame(game.epic_app_id);
              operationId = response.operationId;
            } else if (isNamed) {
              const response = await ApiService.removeEvictedForNamedGame(
                game.service!,
                game.game_name
              );
              operationId = response.operationId;
            } else {
              const response = await ApiService.removeEvictedForGame(game.game_app_id);
              operationId = response.operationId;
            }
          }
          ctx.setOperationId(operationId);
          const outcome = await waitPromise;
          if (outcome.timedOut) {
            // No completion within the window: count as a failure rather than a
            // silent success so the batch tally stays honest.
            throw new Error('Evicted removal timed out');
          }
          // A completion that reports failure (e.g. locked files) must count as failed,
          // not succeeded. Exclude server-side cancels, which the queue's abort path owns.
          if (outcome.event && outcome.event.success === false && !outcome.event.cancelled) {
            throw new Error(outcome.event.error ?? 'Evicted removal failed');
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
              completeKey: 'management.sections.data.evictionRemoveSelectedComplete',
              completeDefaultValue: 'Removed {{count}} evicted items',
              partialFailureKey:
                'management.sections.data.evictionRemoveSelectedCompleteWithFailures',
              partialFailureDefaultValue: 'Removed {{count}} evicted items, but {{failed}} failed',
              cancelledKey: 'management.sections.data.evictionRemoveSelectedCancelled',
              cancelledDefaultValue: 'Evicted removal cancelled after {{count}} items',
              cancelledWithFailuresKey:
                'management.sections.data.evictionRemoveSelectedCancelledWithFailures',
              cancelledWithFailuresDefaultValue:
                'Evicted removal cancelled after {{count}} items, with {{failed}} failures'
            }
          });
        }
      });
    },
    [addNotification, updateNotification, runEvictedQueue, on, off, t]
  );

  const isEvictedRemovalRunning = evictedState.status === 'running';

  // Memoized so a parent re-render (NotificationsProvider updates on every
  // notification tick) does not hand consumers a fresh context object when
  // nothing they read has changed.
  const contextValue = useMemo(
    () => ({
      runCacheRemoval,
      isCacheRemovalRunning,
      runEvictedRemoval,
      isEvictedRemovalRunning
    }),
    [runCacheRemoval, isCacheRemovalRunning, runEvictedRemoval, isEvictedRemovalRunning]
  );

  return <BulkRemovalContext.Provider value={contextValue}>{children}</BulkRemovalContext.Provider>;
};
