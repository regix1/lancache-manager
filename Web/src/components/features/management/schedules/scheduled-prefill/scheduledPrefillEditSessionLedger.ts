import { createUuid } from '@utils/uuid';
import type { ScheduledPrefillServiceId } from './types';

const SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY = 'scheduled-prefill:edit-session:v1';

/**
 * The three methods this file actually calls on the store it is handed.
 *
 * Naming the whole DOM `Storage` here required the caller to pass `sessionStorage` itself, which
 * throws on the property access in a browser with site data blocked for the origin. Nothing here
 * reads `length` or `key()`, so asking for them only ruled out the safe wrapper.
 */
type EditSessionStore = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

const createScheduledPrefillEditSessionId = (): string => createUuid();

type ScheduledPrefillEditSessionServiceId = ScheduledPrefillServiceId;

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

interface ScheduledPrefillEditSessionLedger {
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

const persistLedger = (
  storage: EditSessionStore,
  ledger: ScheduledPrefillEditSessionLedger
): void => {
  storage.setItem(SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY, JSON.stringify(ledger));
};

const hasEditAction = (service: ScheduledPrefillEditSessionServiceState): boolean =>
  Boolean(service.start || service.login || service.download || service.selection);

function loadScheduledPrefillEditSession(
  storage: EditSessionStore
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

function hasScheduledPrefillEditActions(ledger: ScheduledPrefillEditSessionLedger): boolean {
  return SERVICES.some((service) => hasEditAction(ledger.services[service]));
}

function beginEditSessionCleanup(
  storage: EditSessionStore,
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

function buildEditSessionCleanupRequest(
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

function clearConfirmedEditSession(
  storage: EditSessionStore,
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

function discardCommittedEditSession(storage: EditSessionStore, editSessionId: string): boolean {
  const current = loadScheduledPrefillEditSession(storage);
  if (!current || current.editSessionId !== editSessionId) {
    return false;
  }

  storage.removeItem(SCHEDULED_PREFILL_EDIT_SESSION_STORAGE_KEY);
  return true;
}

let recovery: Promise<void> | null = null;

export function recoverScheduledPrefillEditSession(
  storage: EditSessionStore,
  cleanup: (request: PersistentPrefillEditSessionCleanupRequest) => Promise<unknown>
): Promise<void> {
  if (recovery) return recovery;
  const stored = loadScheduledPrefillEditSession(storage);
  if (!stored) return Promise.resolve();

  recovery = Promise.resolve()
    .then(async () => {
      let current: ScheduledPrefillEditSessionLedger | null = stored;
      while (current) {
        if (!hasScheduledPrefillEditActions(current)) {
          discardCommittedEditSession(storage, current.editSessionId);
        } else {
          const pending = beginEditSessionCleanup(
            storage,
            current,
            createScheduledPrefillEditSessionId
          );
          const request = buildEditSessionCleanupRequest(pending);
          await cleanup(request);
          clearConfirmedEditSession(storage, request.editSessionId, request.cleanupId);
        }
        current = loadScheduledPrefillEditSession(storage);
      }
    })
    .finally(() => {
      recovery = null;
    });
  return recovery;
}
