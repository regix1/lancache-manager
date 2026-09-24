import React, { useCallback, useMemo, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { FAILED_TO_REMOVE_GAME_I18N_KEY } from '@contexts/notifications/constants';
import type { NotificationTerminal } from '@contexts/notifications/types';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useBatchQueue } from '@/hooks/useBatchQueue';
import { finalizeBulkRemovalNotification } from '@components/features/management/game-detection/cacheRemovalHelpers';
import { classifyGameFromCacheInfo } from '@components/features/management/game-detection/gameRemovalEntity';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import type {
  EvictionRemovalProgressEvent,
  GameRemovalProgressEvent,
  LogRemovalProgressEvent,
  ServiceRemovalProgressEvent,
  SignalREventName
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

/** One batch item's request, followed to its run's end by `followBatchItem`. */
interface BatchItemRequest {
  /** The item type's progress event; its payloads feed the bulk card's bar. */
  progressEvent: SignalREventName;
  /** Sends the item's removal request. */
  request: () => Promise<{ operationId: string; status?: OperationStatus }>;
  ctx: { setOperationId: (opId: string | null) => void };
  /** Receives the item's own percent (0-100). */
  onPercent: (inner: number) => void;
}

/** Inputs for {@link settleBatchItem}: the run's end and the item's own failure text. */
interface SettleBatchItemOptions {
  end: NotificationTerminal;
  ctx: { cancelRun: () => void };
  failedMessage: string;
  neverStartedMessage: string;
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
 * Turns how an item's run ended into the queue item's outcome. A canceled run ends the batch as
 * canceled, never as a failed item; `gone` is a run the server stopped tracking without saying
 * how it ended.
 */
function settleBatchItem({
  end,
  ctx,
  failedMessage,
  neverStartedMessage
}: SettleBatchItemOptions): void {
  switch (end.status) {
    case 'completed':
      return;
    case 'cancelled':
      ctx.cancelRun();
      return;
    case 'failed':
      throw new Error(end.error ?? failedMessage);
    case 'skipped':
      throw new Error(end.error ?? neverStartedMessage);
    case 'gone':
      throw new Error(failedMessage);
  }
}

/**
 * App-root provider that owns the sequential full-cache bulk-removal queue.
 * Because it is mounted near the top of the provider tree and never unmounts,
 * the queue run loop survives in-app tab switches by construction — there is
 * no unmount-abort path to misfire on a Management-tab navigation.
 *
 * The queue is PRE-BAKED here: the i18n strings, the per-item ApiService
 * selection, and following each item's run to its end all live in this file.
 * Callers only supply the item list and the per-run options (`onSettled`
 * refresh, inline `onProgress`, and `onRunningChange`).
 *
 * The evicted-items "Remove All" no longer queues per-entity removals here:
 * it calls the batched DELETE /api/cache/evicted endpoint (one log rewrite
 * pass + one DB transaction server-side) and its progress/cancel/recovery flow
 * through the standard eviction_removal notification.
 */
export const BulkRemovalProvider: React.FC<BulkRemovalProviderProps> = ({ children }) => {
  const { t } = useTranslation();
  const { addNotification, updateNotification, runs, waitForRunEnd } = useNotifications();
  const { on, off } = useSignalR();
  const runsRef = useRef(runs);
  runsRef.current = runs;

  // Registers the progress listener before the request so no progress for this item is missed,
  // then waits for the run's end. A promotion keeps every merged id in `details.operationIds`
  // and the id doing the work in `details.operationId`, so progress is matched against that.
  const followBatchItem = useCallback(
    async ({
      progressEvent,
      request,
      ctx,
      onPercent
    }: BatchItemRequest): Promise<NotificationTerminal> => {
      let itemOperationId: string | null = null;
      const handleProgress = (
        progress:
          | ServiceRemovalProgressEvent
          | GameRemovalProgressEvent
          | EvictionRemovalProgressEvent
          | LogRemovalProgressEvent
      ) => {
        const requested = itemOperationId;
        if (!requested) return;
        const liveOperationId =
          runsRef.current.find((run) => run.details?.operationIds?.includes(requested))?.details
            ?.operationId ?? requested;
        if (progress.operationId !== liveOperationId) return;
        onPercent(progress.percentComplete ?? 0);
      };

      on(progressEvent, handleProgress);
      try {
        const response = await request();
        itemOperationId = response.operationId;
        // An identical removal that was already live is not this batch's to cancel.
        ctx.setOperationId(response.status === 'alreadyRunning' ? null : response.operationId);
        return await waitForRunEnd(response.operationId);
      } finally {
        off(progressEvent, handleProgress);
      }
    },
    [on, off, waitForRunEnd]
  );

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
      const onPercent = (inner: number): void =>
        updateBulkProgress({ bulkNotifId, currentIndex, total, inner, updateNotification });

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
            details: { itemTypes: ['service_removal', 'game_removal'], itemOperationIds: [] }
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label =
            entry.kind === 'service' ? entry.service.service_name : entry.game.game_name;
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
            const end = await followBatchItem({
              progressEvent: 'ServiceRemovalProgress',
              request: () => ApiService.removeServiceFromCache(serviceName),
              ctx,
              onPercent
            });
            settleBatchItem({
              end,
              ctx,
              failedMessage: `Service removal failed for ${serviceName}`,
              neverStartedMessage: `Service removal never started for ${serviceName}`
            });
          } else {
            const game = entry.game;
            const entity = classifyGameFromCacheInfo(game);
            const end = await followBatchItem({
              progressEvent: 'GameRemovalProgress',
              request: () =>
                entity.kind === 'epicGame'
                  ? ApiService.removeEpicGameFromCache(game.game_name)
                  : entity.kind === 'namedGame'
                    ? ApiService.removeNamedGameFromCache(entity.service, entity.gameName)
                    : ApiService.removeGameFromCache(entity.gameAppId),
              ctx,
              onPercent
            });
            settleBatchItem({
              end,
              ctx,
              failedMessage: `Game removal failed for ${game.game_name}`,
              neverStartedMessage: `Game removal never started for ${game.game_name}`
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
    [addNotification, updateNotification, runCacheQueue, followBatchItem, t]
  );

  const isCacheRemovalRunning = cacheState.status === 'running';

  // --- Evicted-items queue -------------------------------------------------
  // Sequential/cancellable pipeline like the cache queue, but each item hits a
  // per-entity evicted endpoint.
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
      const onPercent = (inner: number): void =>
        updateBulkProgress({ bulkNotifId, currentIndex, total, inner, updateNotification });

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
            details: { itemTypes: ['eviction_removal'], itemOperationIds: [] }
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label =
            entry.kind === 'service' ? entry.service.service_name : entry.game.game_name;
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
          const end = await followBatchItem({
            progressEvent: 'EvictionRemovalProgress',
            // Dispatch to the per-entity evicted endpoint. Identity logic mirrors
            // StorageSection.confirmPartialEvictedRemoval exactly: Epic games are
            // keyed by epic_app_id, named (Blizzard/Riot/Xbox) games by
            // (service, gameName), Steam games by game_app_id.
            request: async () => {
              if (entry.kind === 'service') {
                return ApiService.removeEvictedForService(entry.service.service_name);
              }
              const game = entry.game;
              const entity = classifyGameFromCacheInfo(game);
              if (entity.kind === 'epicGame') {
                if (!game.epic_app_id) {
                  throw new Error(t(FAILED_TO_REMOVE_GAME_I18N_KEY));
                }
                return ApiService.removeEvictedForEpicGame(game.epic_app_id);
              }
              if (entity.kind === 'namedGame') {
                return ApiService.removeEvictedForNamedGame(game.service!, game.game_name);
              }
              return ApiService.removeEvictedForGame(game.game_app_id);
            },
            ctx,
            onPercent
          });
          settleBatchItem({
            end,
            ctx,
            failedMessage: 'Evicted removal failed',
            neverStartedMessage: 'Evicted removal never started'
          });
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
    [addNotification, updateNotification, runEvictedQueue, followBatchItem, t]
  );

  const isEvictedRemovalRunning = evictedState.status === 'running';

  // --- Log-removal queue ---------------------------------------------------
  // Same sequential/cancellable pipeline, but each item rewrites one datasource's
  // log entries for one service.
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
      const onPercent = (inner: number): void =>
        updateBulkProgress({ bulkNotifId, currentIndex, total, inner, updateNotification });

      await runLogQueue({
        items,
        openNotification: () => {
          const id = addNotification({
            type: 'bulk_removal',
            status: 'running',
            message: t('management.batchSelect.removeSelected', { count: total }),
            progress: 0,
            // No operationId → handleCancel special-cases bulk_removal
            details: { itemTypes: ['log_removal'], itemOperationIds: [] }
          });
          bulkNotifId = id;
          return id;
        },
        onItemStart: (entry, index, _total, notifId) => {
          currentIndex = index;
          const label = getServiceDisplayName(entry.service);
          options.onProgress?.({ current: index, total, label });
          updateNotification(notifId, {
            message: t('signalr.logRemoval.removing', { service: label }),
            progress: Math.floor(((index - 1) / total) * 100)
          });
        },
        processItem: async (entry, ctx) => {
          const { datasource, service } = entry;
          const failedMessage = `Log removal failed for ${service}`;
          const end = await followBatchItem({
            progressEvent: 'LogRemovalProgress',
            request: async () => {
              const result = await ApiService.removeServiceFromDatasourceLogs(datasource, service);
              // The response type leaves the id optional; the route answers every accepted
              // request with one (LogsController's 202 bodies).
              if (!result.operationId) throw new Error(failedMessage);
              return { operationId: result.operationId, status: result.status };
            },
            ctx,
            onPercent
          });
          settleBatchItem({
            end,
            ctx,
            failedMessage,
            neverStartedMessage: `Log removal never started for ${service}`
          });
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
    [addNotification, updateNotification, runLogQueue, followBatchItem, t]
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
