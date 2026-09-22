import type { RefObject } from 'react';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { waitForSignalRCompletion } from '@contexts/notifications/waitForSignalRCompletion';
import type { NotificationEvents } from '@contexts/notifications/types';
import type { OperationStatusResponse } from '@contexts/notifications/recoveryStatusResponses';
import type { EventHandler } from '@contexts/SignalRContext/types';
import {
  decideScanHold,
  successorOperationId,
  type ScanAdmission,
  type ScanHoldDecision
} from './scanAdmission';

type ScanKind = 'gameDetection' | 'evictionScan';

interface ScanHold {
  decision: ScanHoldDecision;
  operationId: string | null;
}

type ScanWatch = ScanHold | { decision: 'aborted'; operationId: string | null };

const RECOVERY_POLL_MS = 2000;

const permission = (error: unknown): boolean =>
  error instanceof ApiError && (error.status === 401 || error.status === 403);

const readOperation = async (
  operationId: string
): Promise<{ operation: OperationStatusResponse | null; failed: boolean; missing: boolean }> => {
  try {
    const operation = await ApiService.getTrackedOperation(operationId);
    return { operation, failed: false, missing: !operation.status };
  } catch (error: unknown) {
    if (error instanceof ApiError && error.status === 404) {
      return { operation: null, failed: false, missing: true };
    }
    return { operation: null, failed: true, missing: false };
  }
};

/**
 * Follows an admitted scan through the operation, waiting-list, and active-scan
 * reads. A 401 or 403 from the eviction status endpoint is a permission answer
 * and is ignored when the operation read already succeeded.
 */
export async function recoverScanHold(
  operationId: string | null,
  kind: ScanKind
): Promise<ScanHold> {
  let endpointFailed = false;
  let operation: OperationStatusResponse | null = null;
  let missing = operationId === null;

  if (operationId) {
    const read = await readOperation(operationId);
    operation = read.operation;
    endpointFailed = read.failed;
    missing = read.missing;
  }

  const followed = successorOperationId(operation);
  if (followed && followed !== operationId) {
    return recoverScanHold(followed, kind);
  }

  let waitingListed = false;
  let recoveredOperationId = operationId;
  try {
    const waiting = await ApiService.getWaitingOperations();
    const waitingOperation = operationId
      ? waiting.find((row) => row.operationId === operationId)
      : waiting.find((row) => row.operationType === kind);
    waitingListed = Boolean(waitingOperation);
    recoveredOperationId ??= waitingOperation?.operationId ?? null;
  } catch (error: unknown) {
    if (!permission(error)) endpointFailed = true;
  }

  let activeScanMatches = false;
  let permissionDenied = false;
  try {
    if (kind === 'gameDetection') {
      const active = await ApiService.getActiveGameDetection();
      const activeId = active.operation?.operationId ?? null;
      const parentId = active.operation?.parentOperationId ?? null;
      activeScanMatches = Boolean(
        active.isProcessing &&
        activeId &&
        (operationId === null || activeId === operationId || parentId === operationId)
      );
      if (activeScanMatches) recoveredOperationId ??= activeId;
    } else {
      const active = await ApiService.getEvictionScanStatus();
      const activeId = active.operationId ?? null;
      const previousId = active.previousOperationId ?? null;
      activeScanMatches = Boolean(
        active.isProcessing &&
        activeId &&
        (operationId === null || activeId === operationId || previousId === operationId)
      );
      if (activeScanMatches) recoveredOperationId = activeId;
    }
  } catch (error: unknown) {
    if (permission(error)) permissionDenied = kind === 'evictionScan';
    else endpointFailed = true;
  }

  if (permissionDenied && !endpointFailed && missing && !waitingListed) {
    return { decision: 'release', operationId: recoveredOperationId };
  }

  return {
    decision: decideScanHold({
      operation: missing ? null : operation,
      waitingListed,
      activeScanMatches,
      endpointFailed
    }),
    operationId: recoveredOperationId
  };
}

const waitForRecoveryPoll = (timeoutMs: number, abortSignal: AbortSignal): Promise<boolean> =>
  new Promise((resolve) => {
    if (abortSignal.aborted) {
      resolve(false);
      return;
    }
    const finish = (continued: boolean) => {
      clearTimeout(timeout);
      abortSignal.removeEventListener('abort', onAbort);
      resolve(continued);
    };
    const onAbort = () => finish(false);
    const timeout = setTimeout(() => finish(true), timeoutMs);
    abortSignal.addEventListener('abort', onAbort, { once: true });
  });

/** Keep a confirmed hold alive until the operation ends or recovery becomes uncertain. */
export async function watchScanHold(input: {
  operationId: string | null;
  kind: ScanKind;
  abortSignal: AbortSignal;
  pollMs?: number;
}): Promise<ScanWatch> {
  let operationId = input.operationId;
  while (!input.abortSignal.aborted) {
    const hold = await recoverScanHold(operationId, input.kind);
    if (input.abortSignal.aborted) break;
    operationId = hold.operationId;
    if (hold.decision !== 'hold') return hold;
    if (!(await waitForRecoveryPoll(input.pollMs ?? RECOVERY_POLL_MS, input.abortSignal))) break;
  }
  return { decision: 'aborted', operationId };
}

/**
 * Follow SignalR and server recovery together. A failed read is not an answer: it keeps
 * polling and leaves the SignalR listener in place. Only a terminal operation releases the hold.
 */
export async function followAdmittedScan(input: {
  signalR: {
    on: (eventName: string, handler: EventHandler) => void;
    off: (eventName: string, handler: EventHandler) => void;
  };
  events: RefObject<NotificationEvents>;
  abortSignal: AbortSignal;
  operationId: string;
  admission: ScanAdmission;
  completeEvent: string;
  kind: ScanKind;
}): Promise<ScanHoldDecision | 'aborted'> {
  const signalAbort = new AbortController();
  const recoveryAbort = new AbortController();
  const abort = () => {
    signalAbort.abort();
    recoveryAbort.abort();
  };
  if (input.abortSignal.aborted) abort();
  else input.abortSignal.addEventListener('abort', abort, { once: true });

  const pollUntilRelease = async (): Promise<ScanWatch> => {
    let operationId: string | null = input.operationId;
    while (!recoveryAbort.signal.aborted) {
      const hold = await recoverScanHold(operationId, input.kind);
      if (recoveryAbort.signal.aborted) break;
      operationId = hold.operationId;
      if (hold.decision === 'release') return hold;
      if (!(await waitForRecoveryPoll(RECOVERY_POLL_MS, recoveryAbort.signal))) break;
    }
    return { decision: 'aborted', operationId };
  };

  try {
    const wait = waitForSignalRCompletion<
      { operationId?: string; previousOperationId?: string | null },
      { operationId?: string }
    >({
      signalR: input.signalR,
      events: input.events,
      completeEvent: input.completeEvent,
      match: () => true,
      startedEvent: input.kind === 'evictionScan' ? 'EvictionScanStarted' : 'GameDetectionStarted',
      onStartedCapture: (event) =>
        typeof event.operationId === 'string' ? { opId: event.operationId } : null,
      progressEvent: input.kind === 'evictionScan' ? 'EvictionScanProgress' : undefined,
      abortSignal: signalAbort.signal,
      timeoutMs: null
    });
    const status =
      input.admission === 'queued'
        ? 'waiting'
        : input.admission === 'alreadyRunning'
          ? 'alreadyRunning'
          : 'started';
    wait.captureOperationId(input.operationId, status);

    const first = await Promise.race([
      wait.then((outcome) => ({ source: 'signal' as const, outcome })),
      pollUntilRelease().then((outcome) => ({ source: 'recovery' as const, outcome }))
    ]);

    if (first.source === 'recovery') {
      signalAbort.abort();
      return first.outcome.decision === 'release' ? 'release' : 'aborted';
    }

    if (first.outcome.aborted || input.abortSignal.aborted) {
      recoveryAbort.abort();
      return 'aborted';
    }
    if (first.outcome.terminal) {
      recoveryAbort.abort();
      return 'release';
    }
    if (first.outcome.dequeued && !first.outcome.dequeued.promoted) {
      recoveryAbort.abort();
      return 'release';
    }

    const rest = await pollUntilRelease();
    return rest.decision === 'release' ? 'release' : 'aborted';
  } finally {
    input.abortSignal.removeEventListener('abort', abort);
    abort();
  }
}
