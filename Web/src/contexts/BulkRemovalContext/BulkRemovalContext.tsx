import React, { useCallback, useMemo, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { FAILED_TO_REMOVE_GAME_I18N_KEY } from '@contexts/notifications/constants';
import {
  settleBatchItem,
  waitForSignalRCompletion
} from '@contexts/notifications/waitForSignalRCompletion';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useBatchQueue } from '@/hooks/useBatchQueue';
import { finalizeBulkRemovalNotification } from '@components/features/management/game-detection/cacheRemovalHelpers';
import {
  classifyGameFromCacheInfo,
  matchesGameRemovalComplete,
  matchesGameRemovalIdentity
} from '@components/features/management/game-detection/gameRemovalEntity';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import type {
  EvictionRemovalStartedEvent,
  EvictionRemovalCompleteEvent,
  EvictionRemovalProgressEvent,
  GameRemovalProgressEvent,
  LogRemovalStartedEvent,
  LogRemovalCompleteEvent,
  LogRemovalProgressEvent,
  ServiceRemovalStartedEvent,
  ServiceRemovalCompleteEvent,
  ServiceRemovalProgressEvent
} from '@contexts/SignalRContext/types';
import type { OperationStatus } from '@/types/operations';
import {
  BulkRemovalContext,
  type BulkRemovalRunOptions,
  type BulkQueueEntry,
  type EvictedQueueEntry,
  type LogBatchEntry
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
  const { addNotification, updateNotification, events } = useNotifications();
  const { on, off } = useSignalR();

  // Per-run options are captured at run() time but the hook-level onSettled is
  // instantiation-time, so we stash the current run's options in a ref that the
  // instantiation-time onSettled reads. This is what keeps the caller's
  // post-settle refresh (GameCacheDetector.onDataRefresh) alive across the
  // provider hoist.
  const cacheRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);

  const { run: runCacheQueue, state: cacheState } = useBatchQueue<BulkQueueEntry>({
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
      // While an item is parked behind another operation, the wait-queue handler replaces this
      // card's text with the blocker's name. Keeping the item's own line here is what lets the
      // card go back to naming the item the moment the queue promotes it.
      let currentItemMessage = '';
      const restoreItemMessage = (): void => {
        if (bulkNotifId && currentItemMessage) {
          // Back to running as well as back to the item's own line: the card turned purple while
          // this item sat in the queue, and the promotion is what makes it a live removal again.
          updateNotification(bulkNotifId, { status: 'running', message: currentItemMessage });
        }
      };

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
            // A cache run queues both service and game entries, so its per-item
            // cards can be either type.
            details: { itemTypes: ['service_removal', 'game_removal'] }
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label =
            entry.kind === 'service' ? entry.service.service_name : entry.game.game_name;
          options.onProgress?.({ current: index, total, label });
          currentItemMessage = t('management.sections.data.gameCacheRemoveAllProgress', {
            current: index,
            total,
            label
          });
          updateNotification(notifId, {
            message: currentItemMessage,
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          if (entry.kind === 'service') {
            const serviceName = entry.service.service_name;
            let operationId: string | null = null;
            const waitPromise = waitForSignalRCompletion<
              ServiceRemovalStartedEvent,
              ServiceRemovalCompleteEvent,
              ServiceRemovalProgressEvent
            >({
              signalR: { on, off },
              events,
              completeEvent: 'ServiceRemovalComplete',
              startedEvent: 'ServiceRemovalStarted',
              match: (payload) => !!operationId && payload.operationId === operationId,
              // Until promotion rebinds it below, operationId still holds the waiting op's id,
              // which is exactly what a waiting-complete for this item carries.
              waitingOperationId: () => operationId,
              onStartedCapture: (payload) =>
                payload.serviceName === serviceName ? { opId: payload.operationId } : null,
              onOperationIdCaptured: (opId, ownsCancellation = true) => {
                operationId = opId;
                ctx.setOperationId(ownsCancellation ? opId : null);
                if (!events.current.waiting.has(opId)) restoreItemMessage();
              },
              progressEvent: 'ServiceRemovalProgress',
              onProgress: (payload) => {
                if (!operationId || payload.operationId !== operationId) return;
                updateBulkProgress({
                  bulkNotifId,
                  currentIndex,
                  total,
                  inner: payload.percentComplete,
                  updateNotification
                });
              },
              timeoutMs: 600_000
            });
            const response = await ApiService.removeServiceFromCache(serviceName);
            waitPromise.captureOperationId(response.operationId, response.status);
            const outcome = await waitPromise;
            const stillRunning = settleBatchItem({
              outcome,
              ctx,
              timedOutMessage: `Service removal timed out for ${serviceName}`,
              neverStartedMessage: `Service removal never started for ${serviceName}`,
              failedMessage: `Service removal failed for ${serviceName}`
            });
            if (!stillRunning) return;
          } else {
            const game = entry.game;
            const entity = classifyGameFromCacheInfo(game);
            let currentOperationId: string | null = null;
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
              GameRemovalProgressEvent
            >({
              signalR: { on, off },
              events,
              completeEvent: 'GameRemovalComplete',
              startedEvent: 'GameRemovalStarted',
              match: (payload) => matchesGameRemovalComplete(payload, entity, currentOperationId),
              waitingOperationId: () => currentOperationId,
              onStartedCapture: (payload) =>
                matchesGameRemovalIdentity(payload, entity) &&
                typeof payload.operationId === 'string'
                  ? { opId: payload.operationId }
                  : null,
              onOperationIdCaptured: (opId, ownsCancellation = true) => {
                currentOperationId = opId;
                ctx.setOperationId(ownsCancellation ? opId : null);
                if (!events.current.waiting.has(opId)) restoreItemMessage();
              },
              progressEvent: 'GameRemovalProgress',
              onProgress: (payload) => {
                if (!currentOperationId || payload.operationId !== currentOperationId) return;
                updateBulkProgress({
                  bulkNotifId,
                  currentIndex,
                  total,
                  inner: payload.percentComplete,
                  updateNotification
                });
              },
              timeoutMs: 600_000
            });

            const response =
              entity.kind === 'epicGame'
                ? await ApiService.removeEpicGameFromCache(game.game_name)
                : entity.kind === 'namedGame'
                  ? await ApiService.removeNamedGameFromCache(entity.service, entity.gameName)
                  : await ApiService.removeGameFromCache(entity.gameAppId);
            waitPromise.captureOperationId(response.operationId, response.status);
            const outcome = await waitPromise;
            const label = game.game_name;
            const stillRunning = settleBatchItem({
              outcome,
              ctx,
              timedOutMessage: `Game removal timed out for ${label}`,
              neverStartedMessage: `Game removal never started for ${label}`,
              failedMessage: `Game removal failed for ${label}`
            });
            if (!stillRunning) return;
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
    [addNotification, updateNotification, runCacheQueue, on, off, t, events]
  );

  const isCacheRemovalRunning = cacheState.status === 'running';

  // --- Evicted-items queue -------------------------------------------------
  // Sequential/cancellable pipeline like the cache queue, but each item hits a
  // per-entity evicted endpoint. Completion carries only operationId, so after
  // a queued DELETE the waiter re-binds from EvictionRemovalStarted context
  // (scope + key). The HTTP body id is the waiting id and would never match.
  const evictedRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);

  const { run: runEvictedQueue, state: evictedState } = useBatchQueue<EvictedQueueEntry>({
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
      // Same reason as the cache run: the wait-queue handler borrows this card's text to name
      // the blocking operation, and this is what the card goes back to once the item starts.
      let currentItemMessage = '';
      const restoreItemMessage = (): void => {
        if (bulkNotifId && currentItemMessage) {
          // Back to running as well as back to the item's own line: the card turned purple while
          // this item sat in the queue, and the promotion is what makes it a live removal again.
          updateNotification(bulkNotifId, { status: 'running', message: currentItemMessage });
        }
      };

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
            details: { itemTypes: ['eviction_removal'] }
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label =
            entry.kind === 'service' ? entry.service.service_name : entry.game.game_name;
          options.onProgress?.({ current: index, total, label });
          currentItemMessage = t('management.sections.data.evictionRemoveSelectedProgress', {
            current: index,
            total,
            label,
            defaultValue: 'Removing {{current}} of {{total}} - {{label}}'
          });
          updateNotification(notifId, {
            message: currentItemMessage,
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          let operationId: string | null = null;

          // Entity identity as the backend encodes it in the EvictionRemovalStarted context
          // ({scope, key}). Needed because a QUEUED item is promoted under a NEW operationId:
          // the id in the DELETE response is the waiting op's id and the completion event
          // would never match it. Re-capturing the promoted id from the Started event keeps
          // the opId-based match (and cancel) correct across promotion.
          const evictedGame = entry.kind === 'game' ? entry.game : null;
          const evictedEntity = evictedGame ? classifyGameFromCacheInfo(evictedGame) : null;
          const expectedScope =
            entry.kind === 'service'
              ? 'service'
              : evictedEntity?.kind === 'epicGame'
                ? 'epic'
                : evictedEntity?.kind === 'namedGame'
                  ? 'named'
                  : 'steam';
          const expectedKey =
            entry.kind === 'service'
              ? entry.service.service_name
              : evictedEntity?.kind === 'epicGame'
                ? (evictedGame?.epic_app_id ?? '')
                : evictedEntity?.kind === 'namedGame'
                  ? `${evictedEntity.service}:${evictedEntity.gameName}`
                  : String(evictedGame?.game_app_id ?? '');
          const matchesEntryIdentity = (
            contextBag?: Record<string, string | number | boolean | null>
          ): boolean => {
            const scope = contextBag?.scope;
            const key = contextBag?.key;
            return (
              typeof scope === 'string' &&
              typeof key === 'string' &&
              scope.toLowerCase() === expectedScope &&
              key.toLowerCase() === expectedKey.toLowerCase()
            );
          };

          const waitPromise = waitForSignalRCompletion<
            EvictionRemovalStartedEvent,
            EvictionRemovalCompleteEvent,
            EvictionRemovalProgressEvent
          >({
            signalR: { on, off },
            events,
            completeEvent: 'EvictionRemovalComplete',
            // The id is the sharper test, but this item can legitimately have none: a request
            // deduplicated onto an eviction removal that is already live hands back that
            // removal's id, which the batch refuses. The completion carries the same
            // {scope, key} context the Started event does, so identity still settles the item
            // instead of leaving it to time out ten minutes later.
            match: (payload) =>
              operationId !== null
                ? payload.operationId === operationId
                : matchesEntryIdentity(payload.context),
            // Until promotion rebinds it below, operationId still holds the waiting op's id,
            // which is exactly what a waiting-complete for this item carries.
            waitingOperationId: () => operationId,
            startedEvent: 'EvictionRemovalStarted',
            onStartedCapture: (payload) =>
              matchesEntryIdentity(payload.context) ? { opId: payload.operationId } : null,
            onOperationIdCaptured: (opId, ownsCancellation = true) => {
              operationId = opId;
              ctx.setOperationId(ownsCancellation ? opId : null);
              if (!events.current.waiting.has(opId)) restoreItemMessage();
            },
            progressEvent: 'EvictionRemovalProgress',
            onProgress: (payload) => {
              if (!operationId || payload.operationId !== operationId) return;
              updateBulkProgress({
                bulkNotifId,
                currentIndex,
                total,
                inner: payload.percentComplete ?? 0,
                updateNotification
              });
            },
            timeoutMs: 600_000
          });

          // Dispatch to the per-entity evicted endpoint. Identity logic mirrors
          // StorageSection.confirmPartialEvictedRemoval exactly: Epic games are
          // keyed by epic_app_id, named (Blizzard/Riot/Xbox) games by
          // (service, gameName), Steam games by game_app_id.
          let response: { operationId: string; status?: OperationStatus };
          if (entry.kind === 'service') {
            response = await ApiService.removeEvictedForService(entry.service.service_name);
          } else {
            const game = entry.game;
            const entity = classifyGameFromCacheInfo(game);
            if (entity.kind === 'epicGame') {
              if (!game.epic_app_id) {
                throw new Error(t(FAILED_TO_REMOVE_GAME_I18N_KEY));
              }
              response = await ApiService.removeEvictedForEpicGame(game.epic_app_id);
            } else if (entity.kind === 'namedGame') {
              response = await ApiService.removeEvictedForNamedGame(game.service!, game.game_name);
            } else {
              response = await ApiService.removeEvictedForGame(game.game_app_id);
            }
          }
          waitPromise.captureOperationId(response.operationId, response.status);
          const outcome = await waitPromise;
          const stillRunning = settleBatchItem({
            outcome,
            ctx,
            timedOutMessage: 'Evicted removal timed out',
            neverStartedMessage: 'Evicted removal never started',
            failedMessage: 'Evicted removal failed'
          });
          if (!stillRunning) return;
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
    [addNotification, updateNotification, runEvictedQueue, on, off, t, events]
  );

  const isEvictedRemovalRunning = evictedState.status === 'running';

  // --- Log-removal queue ---------------------------------------------------
  // Same sequential/cancellable pipeline, but each item rewrites one datasource's
  // log entries for one service. Log removal is single-flight server-side and this
  // queue runs one item at a time, so matching on the captured operationId (else
  // the service name on the terminal event) is unambiguous within the batch.
  const logRunOptionsRef = useRef<BulkRemovalRunOptions | null>(null);

  const { run: runLogQueue, state: logState } = useBatchQueue<LogBatchEntry>({
    onSettled: () => {
      const opts = logRunOptionsRef.current;
      opts?.onRunningChange?.(false);
      opts?.onSettled?.();
    }
  });

  const runLogRemoval = useCallback(
    async (items: LogBatchEntry[], options: BulkRemovalRunOptions): Promise<void> => {
      const total = items.length;
      if (total === 0) return;

      logRunOptionsRef.current = options;
      options.onRunningChange?.(true);

      let bulkNotifId: string | null = null;
      let currentIndex = 0;
      // Same reason as the cache run: the wait-queue handler borrows this card's text to name
      // the blocking operation, and this is what the card goes back to once the item starts.
      let currentItemMessage = '';
      const restoreItemMessage = (): void => {
        if (bulkNotifId && currentItemMessage) {
          // Back to running as well as back to the item's own line: the card turned purple while
          // this item sat in the queue, and the promotion is what makes it a live removal again.
          updateNotification(bulkNotifId, { status: 'running', message: currentItemMessage });
        }
      };

      await runLogQueue({
        items,
        openNotification: () => {
          const id = addNotification({
            type: 'bulk_removal',
            status: 'running',
            message: t('management.batchSelect.removeSelected', { count: total }),
            progress: 0,
            // No operationId → handleCancel special-cases bulk_removal
            details: { itemTypes: ['log_removal'] }
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label = getServiceDisplayName(entry.service);
          options.onProgress?.({ current: index, total, label });
          currentItemMessage = t('signalr.logRemoval.removing', { service: label });
          updateNotification(notifId, {
            message: currentItemMessage,
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          const { datasource, service } = entry;
          let operationId: string | null = null;
          // Register the SignalR listeners BEFORE the DELETE so the Started/Complete
          // events are never missed in a race.
          const waitPromise = waitForSignalRCompletion<
            LogRemovalStartedEvent,
            LogRemovalCompleteEvent,
            LogRemovalProgressEvent
          >({
            signalR: { on, off },
            events,
            completeEvent: 'LogRemovalComplete',
            startedEvent: 'LogRemovalStarted',
            match: (payload) =>
              operationId ? payload.operationId === operationId : payload.service === service,
            // Until promotion rebinds it below, operationId still holds the waiting op's id,
            // which is exactly what a waiting-complete for this item carries.
            waitingOperationId: () => operationId,
            onStartedCapture: (payload) => {
              const startedService = payload.context?.service;
              return startedService === undefined || startedService === service
                ? { opId: payload.operationId ?? undefined }
                : null;
            },
            onOperationIdCaptured: (opId, ownsCancellation = true) => {
              operationId = opId;
              ctx.setOperationId(ownsCancellation ? opId : null);
              if (!events.current.waiting.has(opId)) restoreItemMessage();
            },
            progressEvent: 'LogRemovalProgress',
            onProgress: (payload) => {
              if (!operationId || payload.operationId !== operationId) return;
              updateBulkProgress({
                bulkNotifId,
                currentIndex,
                total,
                inner: payload.percentComplete,
                updateNotification
              });
            },
            // Large log files can take several minutes to rewrite; give each item a
            // generous window so a legitimately-slow removal is not misreported.
            timeoutMs: 600_000
          });

          const result = await ApiService.removeServiceFromDatasourceLogs(datasource, service);
          waitPromise.captureOperationId(result?.operationId, result?.status);
          const outcome = await waitPromise;
          const stillRunning = settleBatchItem({
            outcome,
            ctx,
            timedOutMessage: `Log removal timed out for ${service}`,
            neverStartedMessage: `Log removal never started for ${service}`,
            failedMessage: `Log removal failed for ${service}`
          });
          if (!stillRunning) return;
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
              completeKey: 'management.batchSelect.batchComplete',
              completeDefaultValue: 'Removed {{count}} of {{total}} service logs',
              partialFailureKey: 'management.batchSelect.batchCompleteWithFailures',
              partialFailureDefaultValue: 'Removed {{count}} service logs, but {{failed}} failed',
              cancelledKey: 'management.batchSelect.batchCancelled',
              cancelledDefaultValue: 'Log removal cancelled after {{count}} service logs',
              cancelledWithFailuresKey: 'management.batchSelect.batchCancelledWithFailures',
              cancelledWithFailuresDefaultValue:
                'Log removal cancelled after {{count}} service logs, with {{failed}} failures'
            }
          });
        }
      });
    },
    [addNotification, updateNotification, runLogQueue, on, off, t, events]
  );

  const isLogRemovalRunning = logState.status === 'running' || logState.status === 'cancelling';

  // Memoized so a parent re-render (NotificationsProvider updates on every
  // notification tick) does not hand consumers a fresh context object when
  // nothing they read has changed.
  const contextValue = useMemo(
    () => ({
      runCacheRemoval,
      isCacheRemovalRunning,
      runEvictedRemoval,
      isEvictedRemovalRunning,
      runLogRemoval,
      isLogRemovalRunning
    }),
    [
      runCacheRemoval,
      isCacheRemovalRunning,
      runEvictedRemoval,
      isEvictedRemovalRunning,
      runLogRemoval,
      isLogRemovalRunning
    ]
  );

  return <BulkRemovalContext.Provider value={contextValue}>{children}</BulkRemovalContext.Provider>;
};
