import { useCallback, useEffect, useRef, useState } from 'react';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import i18n from '@/i18n';
import { useErrorHandler } from './useErrorHandler';

/**
 * Shape of the finalize callback invoked once the queue settles. The caller
 * uses this to transition its bulk notification into a terminal state:
 *
 *  - Cancelled flows should flip `status: 'completed'` with
 *    `details: { cancelled: true, cancelling: false }` so
 *    `UniversalNotificationBar` renders the neutral gray X-circle auto-dismiss.
 *  - Successful flows should update progress to 100 and swap the message.
 *  - Failures should flip `status: 'failed'` with a user-facing `error`.
 */
interface BatchQueueFinalizeArgs {
  id: string;
  succeeded: number;
  failed: number;
  cancelled: boolean;
  total: number;
}

/**
 * Per-item context threaded through `processItem`. Exposes:
 *   - `setOperationId` - tells the hook the current in-flight opId so the
 *     cascade effect can cancel it server-side when the user clicks the X
 *     on the bulk notification. Call this as soon as the opId is known
 *     (directly after `await ApiService.removeX(...)` for opId-in-body
 *     APIs; from within the `onStartedCapture` callback for 202+Started).
 *   - `requestId` - fresh id per iteration; pass to `waitForSignalRCompletion`.
 */
interface BatchQueueItemContext {
  setOperationId: (opId: string | null) => void;
  /**
   * Ends the run as cancelled from inside an item. An item parked in the operation
   * wait-queue can be cancelled from outside this batch (its purple waiting card is
   * cancellable in any other open tab), and that must read as a cancelled run, never as a
   * failed item - cancellation is a terminal state of its own, not a failure.
   */
  cancelRun: () => void;
  requestId: string;
}

interface BatchQueueRunArgs<TItem> {
  /** The items to process sequentially. */
  items: TItem[];
  /**
   * Opens the bulk notification. Must be called synchronously and return the
   * generated id. The hook never reads `notifications.find(...)` to rediscover
   * this id - it uses the returned value directly to avoid stale-closure bugs
   * during the same synchronous tick as `addNotification`.
   */
  openNotification: () => string;
  /**
   * Called before each item to push a progress update to the bulk
   * notification. Receives the item, the 1-based index, the total count,
   * and the notification id returned by `openNotification` (so the caller
   * can call `updateNotification(notificationId, ...)` without duplicating
   * the ref plumbing the hook already owns).
   */
  onItemStart?: (item: TItem, index: number, total: number, notificationId: string) => void;
  /**
   * Performs the per-item API call. MUST call `ctx.setOperationId(opId)` as
   * soon as the opId is known so the cascade effect can cancel it server-side.
   */
  processItem: (item: TItem, ctx: BatchQueueItemContext) => Promise<void>;
  /**
   * Called exactly once after all items settle (success, cancel, or error).
   * The caller is responsible for transitioning the notification to a
   * terminal state here. See `BatchQueueFinalizeArgs` docs.
   */
  finalize: (args: BatchQueueFinalizeArgs) => void;
}

interface BatchQueueState {
  status: 'idle' | 'running' | 'cancelling' | 'done' | 'error';
  error?: Error;
}

interface UseBatchQueueOptions {
  /**
   * Called once the queue has fully settled (success, cancel, or error).
   * Use for post-run refreshes, e.g. `void fetchEvictedItems()`.
   */
  onSettled?: () => void;
}

interface UseBatchQueueResult<TItem> {
  run: (args: BatchQueueRunArgs<TItem>) => Promise<void>;
  state: BatchQueueState;
}

/**
 * Hook that orchestrates a sequential, user-cancellable queue of per-item
 * operations tied to a single bulk notification. It owns all the plumbing
 * that was previously duplicated in StorageSection + GameCacheDetector:
 *
 *   - refs for the bulk notification id and the current-item operation id
 *   - the cascade useEffect that watches the bulk notification's
 *     `details.cancelling` flag and fires `ApiService.cancelOperation` on
 *     the in-flight item when the user clicks X
 *
 * The queue deliberately SURVIVES unmount of the calling component: an
 * in-app tab switch unmounts the Management tab, and treating that as a
 * user cancel aborted bulk removals mid-run ("Bulk removal cancelled after
 * 0 items") while the server kept working. The run loop keeps executing in
 * its closure; progress/finalize flow through the notifications context
 * (which lives at app root), and local setState calls on the unmounted
 * instance are React no-ops.
 *
 * This hook is instantiated by the app-root `BulkRemovalProvider`, which never
 * unmounts. Because the provider is always mounted, the cascade effect below
 * always observes a `details.cancelling`/`cancelRequested` flag flip and is the
 * sole cancel path - the former module-level `bulkQueueCancelRegistry` bridge
 * (needed only when the owning component could unmount mid-run) is gone.
 *
 * The caller retains responsibility for per-item API selection, i18n
 * strings, confirmation modal gating, and the `finalize` update.
 *
 * Contract: when the user cancels the bulk notification,
 * `notificationCancel.handleCancel`'s clientQueue branch flips
 * `details.cancelling = true` (it does NOT call ApiService.cancelOperation
 * directly, because bulk notifications carry no server-side opId). The cascade
 * effect below picks up that flag and cancels the live run.
 */
export function useBatchQueue<TItem>(options?: UseBatchQueueOptions): UseBatchQueueResult<TItem> {
  const { notifications, scheduleAutoDismiss, updateNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const onSettled = options?.onSettled;

  const bulkNotifIdRef = useRef<string | null>(null);
  const currentItemOperationIdRef = useRef<string | null>(null);
  const currentItemRef = useRef<TItem | null>(null);
  const cancelRequestedRef = useRef<boolean>(false);
  // Synchronous in-progress guard. A same-tick double-invoke of `run` (e.g. the
  // confirm Modal's button is still clickable during its 250ms close animation)
  // must not start the SAME queue instance twice — the second run would clobber
  // cancelRequestedRef and the provider's per-run options ref,
  // and run #1's settle would fire run #2's callbacks. A ref (not React state) is
  // required so the guard is observed on the very next synchronous call.
  const runActiveRef = useRef<boolean>(false);

  const [state, setState] = useState<BatchQueueState>({ status: 'idle' });

  const cancelItemOperation = useCallback(
    (opId: string) => {
      ApiService.cancelOperation(opId).catch((err: unknown) => {
        // Best-effort - current item may already be past the point of cancel.
        notifyError('Failed to cancel in-flight queue item', err, {
          silent: true,
          logLabel: 'useBatchQueue cancelItemOperation'
        });
      });
    },
    [notifyError]
  );

  // The single cancellation entry point: fires a best-effort server-side cancel on the in-flight
  // item and marks the run cancelled so the loop stops after that item settles. It does not cut
  // that settle short, because the two kinds of item end differently. Cancelling a RUNNING
  // operation still produces that operation's own terminal event, and the wait ends on it. A
  // queued item has no worker, so nothing of its own is ever emitted and the wait ends on the
  // wait-queue entry closing instead. Either way the batch card still owns the item's events
  // until then, so no per-item card appears next to it. The message flip below is the immediate
  // feedback for the click while that settle is in flight. Invoked by the cascade effect when the
  // bulk notification's cancel flag flips - dedupes via cancelRequestedRef.
  const triggerCancel = useCallback(() => {
    if (cancelRequestedRef.current) return;
    cancelRequestedRef.current = true;

    const activeId = bulkNotifIdRef.current;
    if (activeId) {
      updateNotification(activeId, { message: i18n.t('common.notifications.cancelling') });
    }

    // No id yet means the item's POST has not come back, so there is nothing to cancel server-side
    // and nothing has gone wrong: the flag set above already stops the loop, setOperationId fires
    // the deferred cancel the moment an id lands, and finalize reports the run as cancelled.
    const currentOp = currentItemOperationIdRef.current;
    if (currentOp) {
      currentItemOperationIdRef.current = null;
      cancelItemOperation(currentOp);
    }
  }, [cancelItemOperation, updateNotification]);

  // Puts the in-flight item's operation id on the batch card, and until that id exists, the fact
  // that a request is out for it. The wait-queue handler has no other way to tell a queued
  // operation this batch owns from one it does not, and the card is the only thing it can see.
  // `updateNotification` merges the top level only, so the previous details have to be spread here
  // or this write drops `itemTypes` and blinds that handler. The function form reads the card
  // React is about to update: the X button's `details.cancelling = true` is committed at the end
  // of the click, and a snapshot taken any earlier would spread a pre-cancel copy back over it.
  const setCardOperationId = useCallback(
    (opId: string | null, requestPending: boolean) => {
      const activeId = bulkNotifIdRef.current;
      if (!activeId) return;
      updateNotification(activeId, (card) => ({
        details: {
          ...card.details,
          currentOperationId: opId ?? undefined,
          itemRequestPending: requestPending
        }
      }));
    },
    [updateNotification]
  );

  // Cascade effect: watch the bulk notification for `details.cancelling` (set
  // by notificationCancel.handleCancel's bulk_removal branch).
  // When cancellation is requested, fire a server-side cancel on the in-flight
  // item so it aborts immediately rather than running to natural completion.
  //
  // Only the `cancelling === true` flag is treated as a cancel signal. We do
  // NOT trip cancel when the notification is missing from the list - that
  // misread caused "Bulk removal cancelled after 0 items" on the very first
  // iteration whenever React rendered an unrelated notifications update before
  // the freshly-added bulk notification was batched into the array (the
  // `bulkNotifIdRef` is set synchronously but the array update is queued).
  // The owning provider (BulkRemovalProvider) is app-root and never unmounts,
  // so this effect is the sole cancel path for the flag.
  useEffect(() => {
    const activeId = bulkNotifIdRef.current;
    if (!activeId) return;
    const notif = notifications.find((n) => n.id === activeId);
    if (!notif) return;
    // ONLY `cancelling === true` is a cancel signal for the bulk queue — it is
    // set exclusively by handleCancel's clientQueue branch (the X button).
    // `cancelRequested` alone is NOT honored: that flag is also used by the
    // serverOp deferred-cancel handshake with different semantics, and honoring
    // it here would let any stray details merge cancel a live bulk run.
    if (notif.details?.cancelling !== true) return;
    triggerCancel();
  }, [notifications, triggerCancel]);

  const run = useCallback(
    async (args: BatchQueueRunArgs<TItem>): Promise<void> => {
      const { items, openNotification, onItemStart, processItem, finalize } = args;
      const total = items.length;
      if (total === 0) return;

      // C1 in-progress guard: a same-tick double-invoke must be a no-op. Set
      // synchronously BEFORE any work so the second call returns immediately;
      // cleared in the finally below so the next legitimate run can start.
      if (runActiveRef.current) return;
      runActiveRef.current = true;

      // Reset bookkeeping for this run.
      cancelRequestedRef.current = false;
      currentItemOperationIdRef.current = null;
      currentItemRef.current = null;

      // C2: everything from openNotification() onward runs inside try/finally so
      // a synchronous throw (e.g. from openNotification) still clears the C1
      // guard and delivers onSettled (→ provider's onRunningChange(false)). The
      // finalize() call lives inside the try and only runs once a notifId
      // exists; if openNotification throws there is nothing to finalize and the
      // error propagates after the finally.
      try {
        const notifId = openNotification();
        bulkNotifIdRef.current = notifId;
        setState({ status: 'running' });

        let succeeded = 0;
        let failed = 0;
        let cancelled = false;
        let lastError: Error | null = null;

        const wasCancelled = () => cancelRequestedRef.current;

        for (let index = 0; index < items.length; index += 1) {
          const item = items[index];
          if (wasCancelled()) {
            cancelled = true;
            break;
          }

          currentItemRef.current = item;
          onItemStart?.(item, index + 1, total, notifId);

          const requestId =
            typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
              ? crypto.randomUUID()
              : `req-${Date.now()}-${index}`;

          // The queue announces a parked operation from inside the item's request, so that push can
          // reach the card before the response does. Saying a request is out for this item is what
          // lets the wait-queue handler tell that race apart from the batch simply having no id -
          // between items, or after an item was deduplicated onto a removal it does not own.
          setCardOperationId(null, true);

          const ctx: BatchQueueItemContext = {
            setOperationId: (opId) => {
              currentItemOperationIdRef.current = opId;
              setCardOperationId(opId, false);
              // The X can land while the item's POST is still in flight, before any id
              // exists to cancel. triggerCancel has already run by then, so fire the
              // deferred cancel here the moment the id arrives - otherwise the item runs
              // to natural completion and the "Cancelling..." card sits for its full length.
              if (cancelRequestedRef.current && opId) {
                currentItemOperationIdRef.current = null;
                cancelItemOperation(opId);
              }
            },
            cancelRun: () => {
              // A caller ends the run this way only after its item has already left the queue, so
              // the id still in the ref is terminal and a cancel POST for it can only be rejected.
              // Drop it first and the cancel below reaches nothing, which is the honest outcome.
              currentItemOperationIdRef.current = null;
              triggerCancel();
            },
            requestId
          };

          try {
            await processItem(item, ctx);
            if (wasCancelled()) {
              cancelled = true;
              break;
            }
            succeeded += 1;
          } catch (err) {
            if (wasCancelled()) {
              cancelled = true;
              break;
            }
            failed += 1;
            lastError = err instanceof Error ? err : new Error(String(err));
            // Intentionally continue the queue - a single failure must not
            // abort the rest (mirrors the pre-refactor behaviour).
          } finally {
            currentItemOperationIdRef.current = null;
            setCardOperationId(null, false);
            currentItemRef.current = null;
          }
        }

        // Finalize hook - callers transition the notification to its terminal
        // state here (see BatchQueueFinalizeArgs docs).
        finalize({ id: notifId, succeeded, failed, cancelled, total });

        // Registry-driven notifications get auto-dismiss scheduled by their
        // handler factory when they transition to a terminal state. The
        // bulk_removal notification this hook manages is NOT registry-driven,
        // so we must schedule auto-dismiss ourselves - otherwise the "Bulk
        // removal cancelled/completed" toast lingers indefinitely.
        scheduleAutoDismiss(notifId);

        setState({
          status: lastError && !cancelled ? 'error' : 'done',
          error: lastError ?? undefined
        });
      } finally {
        bulkNotifIdRef.current = null;
        cancelRequestedRef.current = false;
        runActiveRef.current = false;
        onSettled?.();
      }
    },
    [cancelItemOperation, onSettled, scheduleAutoDismiss, setCardOperationId, triggerCancel]
  );

  return { run, state };
}
