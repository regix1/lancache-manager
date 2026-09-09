import type { RefObject } from 'react';
import type { EventHandler, OperationWaitingCompleteEvent } from '../SignalRContext/types';
import type { NotificationEvents, NotificationTerminal } from './types';

const WAITING_COMPLETE_EVENT = 'OperationWaitingComplete';

interface WaitForSignalRCompletionOptions<TStarted, TCompleted, TProgress = unknown> {
  signalR: {
    on: (eventName: string, handler: EventHandler) => void;
    off: (eventName: string, handler: EventHandler) => void;
  };
  events: RefObject<NotificationEvents>;
  completeEvent: string;
  match: (event: TCompleted) => boolean;
  waitingOperationId?: () => string | null;
  startedEvent?: string;
  onStartedCapture?: (event: TStarted) => { opId?: string } | null;
  onOperationIdCaptured?: (opId: string, ownsCancellation?: boolean) => void;
  progressEvent?: string;
  onProgress?: (event: TProgress) => void;
  timeoutMs?: number;
  requestId?: string;
}

interface WaitForSignalRCompletionResult<TCompleted> {
  event?: TCompleted;
  terminal?: NotificationTerminal;
  timedOut?: boolean;
  dequeued?: OperationWaitingCompleteEvent;
}

/** The HTTP response establishes ownership; only a confirmed handoff can change its ID. */
export function waitForSignalRCompletion<TStarted, TCompleted, TProgress = unknown>(
  opts: WaitForSignalRCompletionOptions<TStarted, TCompleted, TProgress>
): Promise<WaitForSignalRCompletionResult<TCompleted>> & {
  captureOperationId: (operationId: string | null | undefined, status?: string) => void;
} {
  const {
    signalR,
    events,
    completeEvent,
    match,
    startedEvent,
    onStartedCapture,
    onOperationIdCaptured,
    progressEvent,
    onProgress,
    timeoutMs = 120_000
  } = opts;
  const startedRevision = events.current.revision;
  let captureOperationId: (operationId: string | null | undefined, status?: string) => void = () =>
    undefined;
  const promise = new Promise<WaitForSignalRCompletionResult<TCompleted>>((resolve) => {
    let settled = false;
    let captured = false;
    let operationId: string | null = null;
    let parkedId: string | null = null;
    let ownsCancellation = false;
    let followed = false;
    let heldId: string | null = null;
    let timeoutHandle: ReturnType<typeof setTimeout> | null = null;

    const release = () => {
      if (!heldId) return;
      const count = events.current.held.get(heldId) ?? 0;
      if (count <= 1) events.current.held.delete(heldId);
      else events.current.held.set(heldId, count - 1);
      heldId = null;
    };
    const bind = (id: string) => {
      if (settled || operationId === id) return;
      release();
      operationId = id;
      heldId = id;
      events.current.held.set(id, (events.current.held.get(id) ?? 0) + 1);
      onOperationIdCaptured?.(id, ownsCancellation);
    };
    const detach = () => {
      signalR.off(completeEvent, completeHandler);
      signalR.off(WAITING_COMPLETE_EVENT, waitingCompleteHandler);
      if (startedEvent) signalR.off(startedEvent, startedHandler);
      if (progressEvent) signalR.off(progressEvent, progressHandler);
      if (timeoutHandle !== null) {
        clearTimeout(timeoutHandle);
        timeoutHandle = null;
      }
      release();
    };
    const finish = (result: WaitForSignalRCompletionResult<TCompleted>) => {
      if (settled) return;
      settled = true;
      detach();
      resolve(result);
    };
    const replay = () => {
      if (settled || !operationId || parkedId) return;
      const terminal = events.current.terminals.get(operationId);
      if (
        !terminal ||
        (terminal.eventName &&
          terminal.eventName !== completeEvent &&
          terminal.eventName !== progressEvent)
      )
        return;
      const retained = events.current.records.get(operationId)?.complete;
      const event =
        retained?.eventName === completeEvent ? (retained.body as TCompleted) : undefined;
      if (event && !match(event)) return;
      finish({ ...(event ? { event } : {}), terminal: { ...terminal } });
    };
    const follow = (event: OperationWaitingCompleteEvent) => {
      if (settled || !captured || !parkedId || event.operationId !== parkedId) return;
      const confirmed = events.current.handoffs.get(parkedId);
      if (
        !confirmed ||
        confirmed.promoted !== event.promoted ||
        confirmed.nextOperationId !== event.nextOperationId
      )
        return;
      if (!event.promoted) {
        finish({ dequeued: confirmed });
        return;
      }
      if (followed || !event.nextOperationId || event.nextOperationId === parkedId) return;
      followed = true;
      parkedId = null;
      if (event.nextStatus !== 'waiting') events.current.waiting.delete(event.nextOperationId);
      bind(event.nextOperationId);
      replay();
      if (
        !settled &&
        (event.nextStatus === 'completed' ||
          event.nextStatus === 'failed' ||
          event.nextStatus === 'cancelled' ||
          event.nextStatus === 'skipped')
      ) {
        finish({
          terminal: {
            operationId: event.nextOperationId,
            status: event.nextStatus,
            error: event.error
          }
        });
      }
    };
    const startedHandler: EventHandler = (event: TStarted) => {
      if (settled || !captured || operationId || parkedId || !onStartedCapture) return;
      const candidate = onStartedCapture(event);
      if (candidate?.opId) {
        bind(candidate.opId);
        replay();
      }
    };
    const progressHandler: EventHandler = (event: TProgress) => {
      if (settled || !captured || !operationId || parkedId) return;
      if ((event as { operationId?: string }).operationId !== operationId) return;
      onProgress?.(event);
    };
    const completeHandler: EventHandler = (event: TCompleted) => {
      if (settled || !captured || !operationId || parkedId) return;
      const fields = event as {
        operationId?: string;
        status?: string;
        success?: boolean;
        cancelled?: boolean;
        skipped?: boolean;
        error?: string;
      };
      if (fields.operationId !== operationId || !match(event)) return;
      replay();
      if (settled) return;
      const error =
        typeof fields.error === 'string' && fields.error.trim() ? fields.error : undefined;
      const status =
        fields.cancelled || fields.status === 'cancelled'
          ? 'cancelled'
          : fields.skipped || fields.status === 'skipped'
            ? 'skipped'
            : fields.status === 'failed' || fields.success === false || error
              ? 'failed'
              : 'completed';
      finish({ event, terminal: { operationId, status, error } });
    };
    const waitingCompleteHandler: EventHandler = (event: OperationWaitingCompleteEvent) => {
      if (settled || !captured || event.operationId !== parkedId) return;
      follow(event);
      // Subscription order is not an ordering guarantee for the shared ingress.
      if (!settled && parkedId) queueMicrotask(() => follow(event));
    };

    captureOperationId = (id, status) => {
      if (settled || captured) return;
      captured = true;
      ownsCancellation = status !== 'alreadyRunning';
      if (id) {
        if (status === 'waiting') {
          parkedId = id;
          const confirmed = events.current.handoffs.get(id);
          if (confirmed) {
            follow(confirmed);
            if (settled || followed) return;
          }
          events.current.waiting.add(id);
        }
        bind(id);
        replay();
        return;
      }
      // Legacy accepted responses may omit the ID; replay only a matching retained Started.
      if (startedEvent && onStartedCapture) {
        for (const record of events.current.records.values()) {
          if (
            record.started?.eventName !== startedEvent ||
            record.started.revision <= startedRevision
          )
            continue;
          const candidate = onStartedCapture(record.started.body as TStarted);
          if (candidate?.opId) {
            bind(candidate.opId);
            replay();
            break;
          }
        }
      }
    };

    signalR.on(completeEvent, completeHandler);
    signalR.on(WAITING_COMPLETE_EVENT, waitingCompleteHandler);
    if (startedEvent) signalR.on(startedEvent, startedHandler);
    if (progressEvent) signalR.on(progressEvent, progressHandler);
    timeoutHandle = setTimeout(() => finish({ timedOut: true }), timeoutMs);
  });
  return Object.assign(promise, {
    captureOperationId: (operationId: string | null | undefined, status?: string) =>
      captureOperationId(operationId, status)
  });
}

interface SettleBatchItemOptions {
  outcome: WaitForSignalRCompletionResult<unknown>;
  ctx: { cancelRun: () => void };
  timedOutMessage: string;
  neverStartedMessage: string;
  failedMessage?: string;
}

/** Convert the authoritative terminal outcome into the existing queue item's outcome. */
export function settleBatchItem({
  outcome,
  ctx,
  timedOutMessage,
  neverStartedMessage,
  failedMessage
}: SettleBatchItemOptions): boolean {
  if (outcome.timedOut) throw new Error(timedOutMessage);
  if (outcome.dequeued) {
    if (outcome.dequeued.cancelled) {
      ctx.cancelRun();
      return false;
    }
    throw new Error(outcome.dequeued.error ?? neverStartedMessage);
  }
  if (outcome.terminal?.status === 'cancelled') {
    ctx.cancelRun();
    return false;
  }
  if (outcome.terminal?.status === 'failed')
    throw new Error(outcome.terminal.error ?? failedMessage ?? neverStartedMessage);
  if (outcome.terminal?.status === 'skipped')
    throw new Error(outcome.terminal.error ?? neverStartedMessage);
  return true;
}
