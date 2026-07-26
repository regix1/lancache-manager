import type { ScheduledPrefillServiceId } from './types';

const SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY = 'scheduled-prefill:edit-session:v1';

export const createScheduledPrefillEditSessionId = (): string =>
  globalThis.crypto?.randomUUID?.() ??
  `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;

export type ScheduledPrefillEditSessionServiceId = ScheduledPrefillServiceId;

export type ScheduledPrefillEditActionKind = 'start' | 'login' | 'download' | 'selection';

interface ScheduledPrefillEditSessionBaseline {
  selectedAppIdsByService: Record<ScheduledPrefillEditSessionServiceId, string[]>;
  sessionIdByService: Record<ScheduledPrefillEditSessionServiceId, string | null>;
}

interface ScheduledPrefillEditAction {
  editActionId: string;
  sessionId: string | null;
}

interface ScheduledPrefillStartEditAction extends ScheduledPrefillEditAction {
  returnedSessionId: string | null;
}

interface ScheduledPrefillEditSessionServiceState {
  baselineSessionId: string | null;
  baselineSelectedAppIds: string[];
  start?: ScheduledPrefillStartEditAction;
  login?: ScheduledPrefillEditAction;
  download?: ScheduledPrefillEditAction;
  selection?: ScheduledPrefillEditAction;
}

export interface ScheduledPrefillEditSessionLedger {
  version: 1;
  editSessionId: string;
  phase: 'active' | 'cleanup-pending';
  cleanupId: string | null;
  services: Record<ScheduledPrefillEditSessionServiceId, ScheduledPrefillEditSessionServiceState>;
}

export interface PersistentPrefillEditSessionCleanupServiceRequest {
  service: ScheduledPrefillEditSessionServiceId;
  baselineSessionId: string | null;
  baselineSelectedAppIds: string[];
  startSessionId: string | null;
  loginSessionId: string | null;
  prefillSessionId: string | null;
  selectionSessionId: string | null;
}

export interface PersistentPrefillEditSessionCleanupRequest {
  editSessionId: string;
  cleanupId: string;
  services: PersistentPrefillEditSessionCleanupServiceRequest[];
}

const SERVICES: readonly ScheduledPrefillEditSessionServiceId[] = [
  'Steam',
  'Epic',
  'Xbox',
  'BattleNet',
  'Riot'
];

const cloneLedger = (
  ledger: ScheduledPrefillEditSessionLedger
): ScheduledPrefillEditSessionLedger => ({
  ...ledger,
  services: Object.fromEntries(
    SERVICES.map((service) => [
      service,
      {
        ...ledger.services[service],
        baselineSelectedAppIds: [...ledger.services[service].baselineSelectedAppIds]
      }
    ])
  ) as ScheduledPrefillEditSessionLedger['services']
});

const persistLedger = (storage: Storage, ledger: ScheduledPrefillEditSessionLedger): void => {
  storage.setItem(SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY, JSON.stringify(ledger));
};

const hasEditAction = (service: ScheduledPrefillEditSessionServiceState): boolean =>
  Boolean(service.start || service.login || service.download || service.selection);

export function createScheduledPrefillEditSession(
  baseline: ScheduledPrefillEditSessionBaseline,
  createId: () => string
): ScheduledPrefillEditSessionLedger {
  return {
    version: 1,
    editSessionId: createId(),
    phase: 'active',
    cleanupId: null,
    services: Object.fromEntries(
      SERVICES.map((service) => [
        service,
        {
          baselineSessionId: baseline.sessionIdByService[service],
          baselineSelectedAppIds: [...baseline.selectedAppIdsByService[service]]
        }
      ])
    ) as ScheduledPrefillEditSessionLedger['services']
  };
}

export function loadScheduledPrefillEditSession(
  storage: Storage
): ScheduledPrefillEditSessionLedger | null {
  const raw = storage.getItem(SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY);
  if (!raw) {
    return null;
  }

  try {
    const parsed = JSON.parse(raw) as Partial<ScheduledPrefillEditSessionLedger>;
    if (
      parsed.version !== 1 ||
      typeof parsed.editSessionId !== 'string' ||
      (parsed.phase !== 'active' && parsed.phase !== 'cleanup-pending') ||
      !parsed.services
    ) {
      return null;
    }
    return parsed as ScheduledPrefillEditSessionLedger;
  } catch {
    return null;
  }
}

export function hasScheduledPrefillEditActions(ledger: ScheduledPrefillEditSessionLedger): boolean {
  return SERVICES.some((service) => hasEditAction(ledger.services[service]));
}

export function recordEditActionIntent(
  storage: Storage,
  ledger: ScheduledPrefillEditSessionLedger,
  service: ScheduledPrefillEditSessionServiceId,
  kind: ScheduledPrefillEditActionKind,
  sessionId: string | null,
  createId: () => string
): { editSession: ScheduledPrefillEditSessionLedger; editActionId: string } {
  if (ledger.phase !== 'active') {
    throw new Error('Cannot change a scheduled-prefill edit session after cleanup has begun.');
  }

  const editActionId = createId();
  const next = cloneLedger(ledger);
  const editAction = { editActionId, sessionId };
  next.services[service] = {
    ...next.services[service],
    [kind]: kind === 'start' ? { ...editAction, returnedSessionId: null } : editAction
  };
  persistLedger(storage, next);
  return { editSession: next, editActionId };
}

export function recordEditSessionStartResult(
  storage: Storage,
  ledger: ScheduledPrefillEditSessionLedger,
  service: ScheduledPrefillEditSessionServiceId,
  editActionId: string,
  sessionId: string
): ScheduledPrefillEditSessionLedger {
  const current = loadScheduledPrefillEditSession(storage);
  const start = current?.services[service].start;
  if (
    !current ||
    current.phase !== 'active' ||
    current.editSessionId !== ledger.editSessionId ||
    start?.editActionId !== editActionId
  ) {
    return ledger;
  }

  const next = cloneLedger(current);
  next.services[service] = {
    ...next.services[service],
    start: { ...start, returnedSessionId: sessionId }
  };
  persistLedger(storage, next);
  return next;
}

export function beginEditSessionCleanup(
  storage: Storage,
  ledger: ScheduledPrefillEditSessionLedger | null,
  createId: () => string
): ScheduledPrefillEditSessionLedger {
  if (!ledger) {
    throw new Error('No scheduled-prefill edit session is available for cleanup.');
  }

  if (ledger.phase === 'cleanup-pending' && ledger.cleanupId) {
    persistLedger(storage, ledger);
    return ledger;
  }

  const next = cloneLedger(ledger);
  next.phase = 'cleanup-pending';
  next.cleanupId = createId();
  persistLedger(storage, next);
  return next;
}

export function buildEditSessionCleanupRequest(
  ledger: ScheduledPrefillEditSessionLedger
): PersistentPrefillEditSessionCleanupRequest {
  if (ledger.phase !== 'cleanup-pending' || !ledger.cleanupId) {
    throw new Error('Scheduled-prefill cleanup must be marked pending before it is sent.');
  }

  return {
    editSessionId: ledger.editSessionId,
    cleanupId: ledger.cleanupId,
    services: SERVICES.filter((service) => hasEditAction(ledger.services[service])).map(
      (service) => {
        const state = ledger.services[service];
        return {
          service,
          baselineSessionId: state.baselineSessionId,
          baselineSelectedAppIds: [...state.baselineSelectedAppIds],
          startSessionId: state.start?.returnedSessionId ?? null,
          loginSessionId: state.login?.sessionId ?? null,
          prefillSessionId: state.download?.sessionId ?? null,
          // Starting a download also applies its appIds to the daemon before prefill begins. Treat
          // that as selection ownership even when the user never opened the separate game picker.
          selectionSessionId: state.selection?.sessionId ?? state.download?.sessionId ?? null
        };
      }
    )
  };
}

export function clearConfirmedEditSession(
  storage: Storage,
  editSessionId: string,
  cleanupId: string
): boolean {
  const current = loadScheduledPrefillEditSession(storage);
  if (
    !current ||
    current.editSessionId !== editSessionId ||
    current.phase !== 'cleanup-pending' ||
    current.cleanupId !== cleanupId
  ) {
    return false;
  }

  storage.removeItem(SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY);
  return true;
}

export function discardCommittedEditSession(storage: Storage, editSessionId: string): boolean {
  const current = loadScheduledPrefillEditSession(storage);
  if (!current || current.editSessionId !== editSessionId) {
    return false;
  }

  storage.removeItem(SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY);
  return true;
}
