/**
 * Generic helper for awaiting a SignalR completion event that matches a per-item
 * predicate. Supports two API shapes used by the per-item cache/eviction endpoints:
 *
 * 1. "opId-in-body" - the POST response includes the operationId directly.
 *    The caller passes `match` as a payload-to-boolean predicate that checks the
 *    returned operationId against its already-known opId.
 *
 * 2. "202-Accepted + Started event" - the POST returns only a lightweight
 *    acknowledgement (e.g. `{ message, gameAppId }`) without an opId. The
 *    operationId is published later on a `*Started` SignalR event. In this
 *    case the caller provides `startedEvent` + `onStartedCapture` so the
 *    helper can correlate the opId to the current item.
 *
 * The helper registers its SignalR listeners synchronously (before the caller
 * performs the HTTP POST) so the Started event is never missed in a race.
 * Listeners are always removed on resolution - success or timeout. Cancelling a RUNNING
 * operation does not settle the wait early: the item's own terminal event (which a cancelled
 * operation still emits) is what resolves it, so the caller sees exactly one settle per item.
 *
 * An item parked in the operation wait-queue is the exception. It has no worker, so cancelling
 * it (or the queue dropping it) ends the wait-queue entry and nothing else - the item's own
 * complete event is never emitted because the work never started. `waitingOperationId` is what
 * settles those, otherwise a cancelled queued item holds its caller until the timeout elapses.
 */
import type { EventHandler, OperationWaitingCompleteEvent } from '../SignalRContext/types';

const WAITING_COMPLETE_EVENT = 'OperationWaitingComplete';

interface WaitForSignalRCompletionOptions<TStarted, TCompleted, TProgress = unknown> {
  /** SignalR facade. Typically `{ on, off }` from `useSignalR()`. */
  signalR: {
    on: (eventName: string, handler: EventHandler) => void;
    off: (eventName: string, handler: EventHandler) => void;
  };
  /** The SignalR event name that signals completion (e.g. "GameRemovalComplete"). */
  completeEvent: string;
  /**
   * Predicate that returns true when a completion event payload matches this
   * particular item. The caller is responsible for identity-key comparison.
   */
  match: (payload: TCompleted) => boolean;
  /**
   * Reads the id of the wait-queue entry this item is currently parked in, or null when it is
   * not parked in one. The caller keeps that id in a closure variable and rebinds it as the
   * queue promotes the item, so this is a getter rather than a value. Omit it and no
   * OperationWaitingComplete can ever settle the wait, which is right for a caller that cannot
   * be queued. Promotion is filtered out by the helper (a promoted operation goes on to emit
   * the item's real completion event).
   */
  waitingOperationId?: () => string | null;
  /**
   * Optional Started event name. When present, the helper also subscribes to
   * this event and calls `onStartedCapture` on each payload. Useful for the
   * 202-Accepted + Started flow where the operationId is not known up-front.
   */
  startedEvent?: string;
  /**
   * Called for every Started event that arrives while the helper is waiting.
   * Return `{ opId }` to hand the operationId to the caller (via the
   * `onOperationIdCaptured` callback on the outer run context). Return null
   * if this Started event does not correspond to the current item.
   */
  onStartedCapture?: (payload: TStarted) => { opId?: string } | null;
  /**
   * Called with the captured operationId whenever `onStartedCapture` returns a
   * non-null `opId`. This is how the caller plumbs the opId into its own
   * cancellation bookkeeping (e.g. `currentItemOperationIdRef.current = opId`).
   */
  onOperationIdCaptured?: (opId: string) => void;
  /**
   * Optional progress event name (e.g. "EvictionRemovalProgress"). When present
   * the helper subscribes to it for the lifetime of the wait and forwards
   * payloads to `onProgress`. Listener cleanup runs through the same `detach`
   * path as the complete/started subscriptions.
   */
  progressEvent?: string;
  /**
   * Called for every progress event that arrives while the helper is waiting.
   * The caller is responsible for filtering by operationId if multiple
   * operations can share the same progress event name.
   */
  onProgress?: (payload: TProgress) => void;
  /** Safety timeout in milliseconds. Defaults to 120_000 (2 minutes). */
  timeoutMs?: number;
  /**
   * Opaque correlation id for the current wait. Defaults to a fresh
   * `crypto.randomUUID()`. Exposed so the caller can tie listener pairs
   * to a single iteration in its own logging. Not used by the helper.
   */
  requestId?: string;
}

interface WaitForSignalRCompletionResult<TCompleted> {
  /** The matching completion payload, if the wait succeeded. */
  event?: TCompleted;
  /** True when the wait ended because `timeoutMs` elapsed. */
  timedOut?: boolean;
  /**
   * The wait-queue entry's terminal payload, when the item was cancelled or dropped from the
   * queue before it was ever promoted. The work never ran, so there is no completion event
   * coming and the caller must not count the item as done.
   */
  dequeued?: OperationWaitingCompleteEvent;
}

export function waitForSignalRCompletion<TStarted, TCompleted, TProgress = unknown>(
  opts: WaitForSignalRCompletionOptions<TStarted, TCompleted, TProgress>
): Promise<WaitForSignalRCompletionResult<TCompleted>> {
  const {
    signalR,
    completeEvent,
    match,
    waitingOperationId,
    startedEvent,
    onStartedCapture,
    onOperationIdCaptured,
    progressEvent,
    onProgress,
    timeoutMs = 120_000
  } = opts;

  return new Promise<WaitForSignalRCompletionResult<TCompleted>>((resolve) => {
    let settled = false;
    let timeoutHandle: ReturnType<typeof setTimeout> | null = null;

    const startedHandler: EventHandler = (payload: TStarted) => {
      if (settled || !onStartedCapture) return;
      const captured = onStartedCapture(payload);
      if (captured && typeof captured.opId === 'string') {
        onOperationIdCaptured?.(captured.opId);
      }
    };

    const progressHandler: EventHandler = (payload: TProgress) => {
      if (settled || !onProgress) return;
      onProgress(payload);
    };

    const completeHandler: EventHandler = (payload: TCompleted) => {
      if (settled) return;
      if (!match(payload)) return;
      finish({ event: payload });
    };

    const waitingCompleteHandler: EventHandler = (payload: OperationWaitingCompleteEvent) => {
      if (settled || payload.promoted === true) return;
      const parkedId = waitingOperationId?.() ?? null;
      if (parkedId === null || payload.operationId !== parkedId) return;
      finish({ dequeued: payload });
    };

    const detach = () => {
      signalR.off(completeEvent, completeHandler);
      signalR.off(WAITING_COMPLETE_EVENT, waitingCompleteHandler);
      if (startedEvent) {
        signalR.off(startedEvent, startedHandler);
      }
      if (progressEvent) {
        signalR.off(progressEvent, progressHandler);
      }
      if (timeoutHandle !== null) {
        clearTimeout(timeoutHandle);
        timeoutHandle = null;
      }
    };

    const finish = (result: WaitForSignalRCompletionResult<TCompleted>) => {
      settled = true;
      detach();
      resolve(result);
    };

    // Register BEFORE the caller fires its HTTP POST so the Started event
    // published immediately after the backend accepts the request is never
    // missed. The caller is responsible for not performing the POST until
    // this function has returned its Promise.
    signalR.on(completeEvent, completeHandler);
    signalR.on(WAITING_COMPLETE_EVENT, waitingCompleteHandler);
    if (startedEvent) {
      signalR.on(startedEvent, startedHandler);
    }
    if (progressEvent) {
      signalR.on(progressEvent, progressHandler);
    }

    timeoutHandle = setTimeout(() => {
      if (settled) return;
      finish({ timedOut: true });
    }, timeoutMs);
  });
}

interface SettleBatchItemOptions {
  /** The resolved wait for this item. */
  outcome: WaitForSignalRCompletionResult<unknown>;
  /** The item's batch context. Only `cancelRun` is read. */
  ctx: { cancelRun: () => void };
  /** Failure text when the window elapsed, e.g. `Log removal timed out for steam`. */
  timedOutMessage: string;
  /** Failure text when the queue dropped the item and reported no error of its own. */
  neverStartedMessage: string;
}

/**
 * Turns one item's wait outcome into that item's outcome, for a batch whose items go through the
 * operation wait-queue. Returns true when the item's own completion arrived and the caller should
 * carry on with it; returns false when the item was cancelled out of the queue, which ends the
 * whole run and leaves the caller nothing more to do for this item.
 *
 * A timeout is a failed item rather than a silent success, so the batch tally stays honest. An
 * item dequeued before promotion never ran, so nothing was removed either way: a cancel ends the
 * run as cancelled and only a queue failure counts as a failed item.
 */
export function settleBatchItem({
  outcome,
  ctx,
  timedOutMessage,
  neverStartedMessage
}: SettleBatchItemOptions): boolean {
  if (outcome.timedOut) {
    throw new Error(timedOutMessage);
  }
  if (outcome.dequeued) {
    if (outcome.dequeued.cancelled) {
      ctx.cancelRun();
      return false;
    }
    throw new Error(outcome.dequeued.error ?? neverStartedMessage);
  }
  return true;
}
