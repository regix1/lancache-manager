import { useCallback, useEffect, useRef, useState } from 'react';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';

/**
 * Shape of the finalize callback invoked once the queue settles. The caller
 * uses this to transition its bulk notification into a terminal state:
 *
 *  - Cancelled flows should flip `status: 'completed'` with
 *    `details: { cancelled: true, cancelling: false }` so
 *    `UniversalNotificationBar` renders the red X-circle auto-dismiss.
 *  - Successful flows should update progress to 100 and swap the message.
 *  - Failures should flip `status: 'failed'` with a user-facing `error`.
 */
interface CancellableQueueFinalizeArgs {
  id: string;
  succeeded: number;
  failed: number;
  cancelled: boolean;
  total: number;
}

/**
 * Per-item context threaded through `processItem`. Exposes:
 *   - `signal` — AbortSignal that fires on user cancel or unmount.
 *   - `setOperationId` — tells the hook the current in-flight opId so the
 *     cascade effect can cancel it server-side when the user clicks the X
 *     on the bulk notification. Call this as soon as the opId is known
 *     (directly after `await ApiService.removeX(...)` for opId-in-body
 *     APIs; from within the `onStartedCapture` callback for 202+Started).
 *   - `requestId` — fresh id per iteration; pass to `waitForSignalRCompletion`.
 */
interface CancellableQueueItemContext {
  signal: AbortSignal;
  setOperationId: (opId: string | null) => void;
  requestId: string;
}

interface CancellableQueueRunArgs<TItem> {
  /** The items to process sequentially. */
  items: TItem[];
  /**
   * Opens the bulk notification. Must be called synchronously and return the
   * generated id. The hook never reads `notifications.find(...)` to rediscover
   * this id — it uses the returned value directly to avoid stale-closure bugs
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
  processItem: (item: TItem, ctx: CancellableQueueItemContext) => Promise<void>;
  /**
   * Called exactly once after all items settle (success, cancel, or error).
   * The caller is responsible for transitioning the notification to a
   * terminal state here. See `CancellableQueueFinalizeArgs` docs.
   */
  finalize: (args: CancellableQueueFinalizeArgs) => void;
}

interface CancellableQueueState {
  status: 'idle' | 'running' | 'cancelling' | 'done' | 'error';
  progress?: { current: number; total: number };
  error?: Error;
}

interface UseCancellableQueueOptions<TItem> {
  /**
   * Called with the item currently in flight when the user cancels. Use for
   * side-effects like fetching fresh data after cancellation. Fired BEFORE
   * `onSettled`.
   */
  onCancel?: (itemInFlight: TItem | null) => Promise<void> | void;
  /**
   * Called once the queue has fully settled (success, cancel, or error).
   * Use for post-run refreshes, e.g. `void fetchEvictedItems()`.
   */
  onSettled?: () => void;
}

interface UseCancellableQueueResult<TItem> {
  run: (args: CancellableQueueRunArgs<TItem>) => Promise<void>;
  cancel: () => void;
  state: CancellableQueueState;
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
 *   - AbortController plumbing so `processItem` receives a signal
 *   - unmount-cleanup that aborts any in-flight iteration
 *
 * The caller retains responsibility for per-item API selection, i18n
 * strings, confirmation modal gating, and the `finalize` update.
 *
 * Contract: when the user cancels the bulk notification,
 * `UniversalNotificationBar.handleCancel`'s bulk_removal special case flips
 * `details.cancelling = true` (it does NOT call ApiService.cancelOperation
 * directly, because bulk notifications carry no server-side opId). The
 * cascade effect inside this hook picks up that flag and calls
 * `ApiService.cancelOperation(currentItemOpId)` on the in-flight item.
 */
export function useCancellableQueue<TItem>(
  options?: UseCancellableQueueOptions<TItem>
): UseCancellableQueueResult<TItem> {
  const { notifications, updateNotification, scheduleAutoDismiss } = useNotifications();
  const onCancel = options?.onCancel;
  const onSettled = options?.onSettled;

  const bulkNotifIdRef = useRef<string | null>(null);
  const currentItemOperationIdRef = useRef<string | null>(null);
  const currentItemRef = useRef<TItem | null>(null);
  const cancelRequestedRef = useRef<boolean>(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const unmountControllerRef = useRef<AbortController | null>(null);

  const [state, setState] = useState<CancellableQueueState>({ status: 'idle' });

  // Unmount guard — abort any in-flight iteration when the caller unmounts.
  useEffect(() => {
    const controller = new AbortController();
    unmountControllerRef.current = controller;
    return () => {
      controller.abort();
      unmountControllerRef.current = null;
    };
  }, []);

  // Cascade effect: watch the bulk notification for `details.cancelling` (set
  // by UniversalNotificationBar.handleCancel's bulk_removal branch).
  // When cancellation is requested, fire a server-side cancel on the in-flight
  // item so it aborts immediately rather than running to natural completion.
  //
  // Only the `cancelling === true` flag is treated as a cancel signal. We do
  // NOT trip cancel when the notification is missing from the list — that
  // misread caused "Bulk removal cancelled after 0 items" on the very first
  // iteration whenever React rendered an unrelated notifications update before
  // the freshly-added bulk notification was batched into the array (the
  // `bulkNotifIdRef` is set synchronously but the array update is queued).
  // Unmount-driven cancellation is handled by the unmountControllerRef → AbortController
  // path in run(), not by this effect.
  useEffect(() => {
    const activeId = bulkNotifIdRef.current;
    if (!activeId) return;
    const notif = notifications.find((n) => n.id === activeId);
    if (!notif) return;
    if (notif.details?.cancelling !== true) return;
    if (cancelRequestedRef.current) return;
    cancelRequestedRef.current = true;

    // Trip the per-item AbortController so processItem sees signal.aborted.
    abortControllerRef.current?.abort();

    const currentOp = currentItemOperationIdRef.current;
    if (currentOp) {
      currentItemOperationIdRef.current = null;
      ApiService.cancelOperation(currentOp).catch(() => {
        /* best-effort — current item may already be past the point of cancel */
      });
    }
  }, [notifications]);

  const cancel = useCallback(() => {
    const activeId = bulkNotifIdRef.current;
    if (!activeId) return;
    // Flip cancelling=true on the bulk notification so the cascade effect
    // picks it up on the next render tick (same path as the user clicking X).
    updateNotification(activeId, {
      details: { cancelling: true }
    });
  }, [updateNotification]);

  const run = useCallback(
    async (args: CancellableQueueRunArgs<TItem>): Promise<void> => {
      const { items, openNotification, onItemStart, processItem, finalize } = args;
      const total = items.length;
      if (total === 0) return;

      // Reset bookkeeping for this run.
      cancelRequestedRef.current = false;
      currentItemOperationIdRef.current = null;
      currentItemRef.current = null;

      // One AbortController per run, composed of user-cancel + unmount.
      const controller = new AbortController();
      abortControllerRef.current = controller;
      const unmountAbort = () => controller.abort();
      unmountControllerRef.current?.signal.addEventListener('abort', unmountAbort, { once: true });

      const notifId = openNotification();
      bulkNotifIdRef.current = notifId;
      setState({ status: 'running', progress: { current: 0, total } });

      let succeeded = 0;
      let failed = 0;
      let cancelled = false;
      let lastError: Error | null = null;

      const wasCancelled = () => cancelRequestedRef.current || controller.signal.aborted;

      try {
        for (let index = 0; index < items.length; index += 1) {
          const item = items[index];
          if (wasCancelled()) {
            cancelled = true;
            break;
          }

          currentItemRef.current = item;
          onItemStart?.(item, index + 1, total, notifId);
          setState({ status: 'running', progress: { current: index + 1, total } });

          const requestId =
            typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
              ? crypto.randomUUID()
              : `req-${Date.now()}-${index}`;

          const ctx: CancellableQueueItemContext = {
            signal: controller.signal,
            setOperationId: (opId) => {
              currentItemOperationIdRef.current = opId;
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
            // Intentionally continue the queue — a single failure must not
            // abort the rest (mirrors the pre-refactor behaviour).
          } finally {
            currentItemOperationIdRef.current = null;
            currentItemRef.current = null;
          }
        }
      } finally {
        unmountControllerRef.current?.signal.removeEventListener('abort', unmountAbort);
      }

      if (cancelled && onCancel) {
        try {
          await onCancel(currentItemRef.current);
        } catch {
          /* onCancel failures are not user-facing */
        }
      }

      // Finalize hook — callers transition the notification to its terminal
      // state here (see CancellableQueueFinalizeArgs docs).
      finalize({ id: notifId, succeeded, failed, cancelled, total });

      // Registry-driven notifications get auto-dismiss scheduled by their
      // handler factory when they transition to a terminal state. The
      // bulk_removal notification this hook manages is NOT registry-driven,
      // so we must schedule auto-dismiss ourselves — otherwise the "Bulk
      // removal cancelled/completed" toast lingers indefinitely.
      scheduleAutoDismiss(notifId);

      bulkNotifIdRef.current = null;
      abortControllerRef.current = null;
      cancelRequestedRef.current = false;
      setState({
        status: lastError && !cancelled ? 'error' : 'done',
        progress: { current: succeeded + failed, total },
        error: lastError ?? undefined
      });

      onSettled?.();
    },
    [onCancel, onSettled, scheduleAutoDismiss]
  );

  return { run, cancel, state };
}
