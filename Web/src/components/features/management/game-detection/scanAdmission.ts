import type { OperationStatus } from '@/types/operations';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import type { OperationStatusResponse } from '@contexts/notifications/recoveryStatusResponses';

/** How long a scan may run before the waiter asks the recovery endpoints. */
export const SCAN_WAIT_MS = 30 * 60 * 1000;

export type ScanAdmission = 'started' | 'queued' | 'alreadyRunning';

export type ScanHoldDecision = 'hold' | 'release' | 'unknown';

const LIVE_STATUSES: readonly OperationStatus[] = ['waiting', 'running', 'cancelling', 'pending'];

const isLive = (status: OperationStatus | null | undefined): boolean =>
  !!status && LIVE_STATUSES.includes(status);

const isTerminal = (status: OperationStatus | null | undefined): boolean =>
  !!status && isTerminalNotificationStatus(status);

/**
 * Whether the server answered at all. A 400 is the download or capability refusal these
 * routes return before admitting anything; a timeout or dropped connection may mean the
 * scan was already accepted. Distinct from `isRefusal`, which may only be used where a
 * route's single 400 meaning is the decline: here both 400 bodies are refusals, and this
 * decides guard release rather than how the message reads.
 */
export function isConfirmedScanRefusalStatus(status: number | undefined): boolean {
  return status === 400;
}

export function readScanAdmission(response: {
  queued?: boolean;
  alreadyRunning?: boolean;
}): ScanAdmission {
  if (response.queued) return 'queued';
  if (response.alreadyRunning) return 'alreadyRunning';
  return 'started';
}

/**
 * `Active: false` is not release. Waiting operations are excluded from the active
 * set, so a parked scan still reports a waiting status.
 */
export function decideScanHold(input: {
  operation: Pick<OperationStatusResponse, 'status' | 'nextOperationId' | 'nextStatus'> | null;
  waitingListed: boolean;
  activeScanMatches: boolean;
  endpointFailed: boolean;
}): ScanHoldDecision {
  if (input.endpointFailed) return 'unknown';

  const status = input.operation?.status ?? null;
  if (isLive(status)) return 'hold';
  if (input.operation?.nextOperationId && isLive(input.operation.nextStatus)) return 'hold';
  if (isTerminal(status)) return 'release';
  if (input.waitingListed || input.activeScanMatches) return 'hold';
  if (status) return 'unknown';
  return 'release';
}

export function successorOperationId(
  operation: Pick<OperationStatusResponse, 'nextOperationId' | 'nextStatus'> | null
): string | null {
  const next = operation?.nextOperationId;
  if (!next || !isLive(operation?.nextStatus)) return null;
  return next;
}
