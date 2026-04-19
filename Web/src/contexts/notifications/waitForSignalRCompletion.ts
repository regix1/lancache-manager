/**
 * Generic helper for awaiting a SignalR completion event that matches a per-item
 * predicate. Supports two API shapes used by the per-item cache/eviction endpoints:
 *
 * 1. "opId-in-body" — the POST response includes the operationId directly.
 *    The caller passes `match` as a payload-to-boolean predicate that checks the
 *    returned operationId against its already-known opId.
 *
 * 2. "202-Accepted + Started event" — the POST returns only a lightweight
 *    acknowledgement (e.g. `{ message, gameAppId }`) without an opId. The
 *    operationId is published later on a `*Started` SignalR event. In this
 *    case the caller provides `startedEvent` + `onStartedCapture` so the
 *    helper can correlate the opId to the current item.
 *
 * The helper registers its SignalR listeners synchronously (before the caller
 * performs the HTTP POST) so the Started event is never missed in a race.
 * Listeners are always removed on resolution — success, cancel, or timeout.
 */
import type { EventHandler } from '../SignalRContext/types';

interface WaitForSignalRCompletionOptions<TStarted, TCompleted> {
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
   * Abort signal. When aborted the helper resolves with `{ cancelled: true }`
   * within a single event-loop tick and detaches all listeners.
   */
  signal?: AbortSignal;
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
  /** True when the wait ended because `signal.aborted` fired. */
  cancelled?: boolean;
  /** True when the wait ended because `timeoutMs` elapsed. */
  timedOut?: boolean;
}

export function waitForSignalRCompletion<TStarted, TCompleted>(
  opts: WaitForSignalRCompletionOptions<TStarted, TCompleted>
): Promise<WaitForSignalRCompletionResult<TCompleted>> {
  const {
    signalR,
    completeEvent,
    match,
    startedEvent,
    onStartedCapture,
    onOperationIdCaptured,
    signal,
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

    const completeHandler: EventHandler = (payload: TCompleted) => {
      if (settled) return;
      if (!match(payload)) return;
      finish({ event: payload });
    };

    const abortListener = () => {
      if (settled) return;
      finish({ cancelled: true });
    };

    const detach = () => {
      signalR.off(completeEvent, completeHandler);
      if (startedEvent) {
        signalR.off(startedEvent, startedHandler);
      }
      if (signal) {
        signal.removeEventListener('abort', abortListener);
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
    if (startedEvent) {
      signalR.on(startedEvent, startedHandler);
    }

    if (signal) {
      if (signal.aborted) {
        finish({ cancelled: true });
        return;
      }
      signal.addEventListener('abort', abortListener, { once: true });
    }

    timeoutHandle = setTimeout(() => {
      if (settled) return;
      finish({ timedOut: true });
    }, timeoutMs);
  });
}
