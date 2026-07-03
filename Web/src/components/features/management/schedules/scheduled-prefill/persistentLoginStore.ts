import { useSyncExternalStore } from 'react';
import ApiService from '@services/api.service';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from '@hooks/usePrefillSteamAuth';

/**
 * Login-flow state for the persistent-container auth flows (Steam/Epic/Xbox), keyed by service
 * and held OUTSIDE the modal component tree. Reopening the config modal (or a PersistentLoginHost
 * remount from container-list churn) no longer loses an in-progress login: this module-level store
 * is the single source of truth for the UI, and `reconcilePersistentLoginFromServer` is what makes
 * it safe to resume even after a full page reload, by reading the backend's own pending-challenge
 * cache (see PersistentPrefillController / DaemonSession).
 */
interface PersistentLoginStoreState {
  loading: boolean;
  error: string | null;
  authenticated: boolean;
  pendingChallenge: CredentialChallenge | null;
  needsTwoFactor: boolean;
  needsEmailCode: boolean;
  waitingForMobileConfirmation: boolean;
  needsAuthorizationCode: boolean;
  authorizationUrl: string;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
  /**
   * True once the user has hidden the auth modal without cancelling. The daemon login flow (and
   * the cached challenge) stays alive; the modal reopens showing the same challenge next time
   * instead of restarting the login.
   */
  dismissed: boolean;
}

interface ChallengeFlags {
  needsTwoFactor: boolean;
  needsEmailCode: boolean;
  waitingForMobileConfirmation: boolean;
  needsAuthorizationCode: boolean;
  authorizationUrl: string;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
}

const EMPTY_CHALLENGE_FLAGS: ChallengeFlags = {
  needsTwoFactor: false,
  needsEmailCode: false,
  waitingForMobileConfirmation: false,
  needsAuthorizationCode: false,
  authorizationUrl: '',
  needsDeviceCode: false,
  deviceUserCode: '',
  deviceVerificationUri: ''
};

const INITIAL_PERSISTENT_LOGIN_STATE: PersistentLoginStoreState = {
  ...EMPTY_CHALLENGE_FLAGS,
  loading: false,
  error: null,
  authenticated: false,
  pendingChallenge: null,
  dismissed: false
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

export function isPersistentLoginAuthenticatedResponse(response: unknown): boolean {
  return (
    response === 'authenticated' ||
    (isRecord(response) && response.authenticated === true) ||
    (isRecord(response) && (response.status === 'authenticated' || response.status === 'logged-in'))
  );
}

export function isPersistentLoginCredentialChallenge(
  response: unknown
): response is CredentialChallenge {
  return isRecord(response) && typeof response.credentialType === 'string';
}

/** Derives the challenge-type UI flags from a challenge - the same mapping the auth modals key off. */
function challengeToFlags(challenge: CredentialChallenge): ChallengeFlags {
  const flags: ChallengeFlags = { ...EMPTY_CHALLENGE_FLAGS };
  switch (challenge.credentialType) {
    case '2fa':
      flags.needsTwoFactor = true;
      break;
    case 'steamguard':
      flags.needsEmailCode = true;
      break;
    case 'authorization-url':
      flags.needsAuthorizationCode = true;
      flags.authorizationUrl = challenge.authUrl ?? '';
      break;
    case 'device-confirmation':
      flags.waitingForMobileConfirmation = true;
      break;
    case 'device-code':
      flags.needsDeviceCode = true;
      flags.deviceUserCode = challenge.userCode ?? '';
      flags.deviceVerificationUri = challenge.verificationUri ?? challenge.authUrl ?? '';
      break;
  }
  return flags;
}

type Listener = () => void;

const states = new Map<PersistentPrefillServiceId, PersistentLoginStoreState>();
const listeners = new Map<PersistentPrefillServiceId, Set<Listener>>();
// Per-service cancel token, separate from the persisted UI state above: a poll loop started by a
// since-remounted component instance still needs to observe a later cancel() call correctly.
const cancelFlags = new Map<PersistentPrefillServiceId, { current: boolean }>();
const startPromises = new Map<PersistentPrefillServiceId, Promise<CredentialChallenge | null>>();

function notify(service: PersistentPrefillServiceId): void {
  listeners.get(service)?.forEach((listener) => listener());
}

function getPersistentLoginState(service: PersistentPrefillServiceId): PersistentLoginStoreState {
  return states.get(service) ?? INITIAL_PERSISTENT_LOGIN_STATE;
}

export function updatePersistentLoginState(
  service: PersistentPrefillServiceId,
  updater: (current: PersistentLoginStoreState) => PersistentLoginStoreState
): void {
  states.set(service, updater(getPersistentLoginState(service)));
  notify(service);
}

export function resetPersistentLoginState(service: PersistentPrefillServiceId): void {
  const current = states.get(service);
  if (current === undefined || current === INITIAL_PERSISTENT_LOGIN_STATE) {
    // Already at rest - skip the Map write + subscriber notify. Matters because a container-list
    // cleanup pass now calls this for every persistent service on every refresh (see
    // ScheduledPrefillConfigModal's authenticated-or-stopped cleanup effect), and most services are
    // simply idle/never touched most of the time - without this guard every refresh would re-render
    // every subscribed card for no reason.
    return;
  }
  states.set(service, INITIAL_PERSISTENT_LOGIN_STATE);
  notify(service);
}

/**
 * Applies a challenge to the store - from either delivery path (the REST resume/poll response, or
 * the SignalR CredentialChallenge push). The two can race and deliver the SAME challenge twice
 * (e.g. the poll's in-flight response lands right after SignalR already applied it): when the
 * incoming challenge's id matches what's already stored, this is a re-delivery, not a new
 * challenge, so `dismissed` is left untouched instead of being force-reset to false - otherwise a
 * modal the user deliberately dismissed would silently reopen itself.
 */
export function applyPersistentLoginChallenge(
  service: PersistentPrefillServiceId,
  challenge: CredentialChallenge
): void {
  updatePersistentLoginState(service, (current) => {
    const isRedelivery = current.pendingChallenge?.challengeId === challenge.challengeId;
    return {
      ...current,
      ...challengeToFlags(challenge),
      pendingChallenge: challenge,
      dismissed: isRedelivery ? current.dismissed : false
    };
  });
}

function subscribePersistentLoginState(
  service: PersistentPrefillServiceId,
  listener: Listener
): () => void {
  const set = listeners.get(service) ?? new Set<Listener>();
  set.add(listener);
  listeners.set(service, set);
  return () => {
    set.delete(listener);
  };
}

export function usePersistentLoginStoreState(
  service: PersistentPrefillServiceId
): PersistentLoginStoreState {
  return useSyncExternalStore(
    (listener) => subscribePersistentLoginState(service, listener),
    () => getPersistentLoginState(service)
  );
}

/**
 * Plain (non-hook) read of whether a service has a login actively in flight - the same
 * `loading || pendingChallenge !== null` check `PersistentLoginHost` uses to decide whether a
 * reported stop is real. Exposed for callers outside a component's render (e.g. a loop over every
 * service inside an effect body) that cannot call the `usePersistentLoginStoreState` hook there.
 */
export function hasActivePersistentLogin(service: PersistentPrefillServiceId): boolean {
  const state = getPersistentLoginState(service);
  return state.loading || state.pendingChallenge !== null;
}

/**
 * Plain (non-hook) read of whether the user explicitly hid this service's auth modal
 * (`dismissModal`) without cancelling the login. Used by the reconcile effect so it never
 * re-surfaces a challenge the user just closed - reconcile is for restoring state lost to a
 * reload/unmount, not for undoing an explicit dismiss.
 */
export function isPersistentLoginDismissed(service: PersistentPrefillServiceId): boolean {
  return getPersistentLoginState(service).dismissed;
}

export function isPersistentLoginCancelled(service: PersistentPrefillServiceId): boolean {
  return cancelFlags.get(service)?.current ?? false;
}

export function setPersistentLoginCancelled(
  service: PersistentPrefillServiceId,
  cancelled: boolean
): void {
  const flag = cancelFlags.get(service);
  if (flag) {
    flag.current = cancelled;
  } else {
    cancelFlags.set(service, { current: cancelled });
  }
}

/** Service-scoped (not component-instance-scoped) in-flight start() dedup, so a remount can never
 *  fire a second daemon login while a first one from a prior mount is still awaiting its challenge. */
export function getPersistentLoginStartPromise(
  service: PersistentPrefillServiceId
): Promise<CredentialChallenge | null> | null {
  return startPromises.get(service) ?? null;
}

export function setPersistentLoginStartPromise(
  service: PersistentPrefillServiceId,
  promise: Promise<CredentialChallenge | null> | null
): void {
  if (promise) {
    startPromises.set(service, promise);
  } else {
    startPromises.delete(service);
  }
}

const RESUME_PROBE_TIMEOUT_SECONDS = 1;

type PersistentLoginReconcileResult = 'authenticated' | 'challenge' | 'none';

/**
 * Used when the config modal reopens (or on first load): asks the backend for the CACHED pending
 * challenge without ever issuing a fresh daemon login (see the pending-challenge cache in
 * PersistentPrefillController/DaemonSession) and hydrates the store when one is found, so a login
 * survives a full page reload, not just a component remount. Any failure is treated as "no pending
 * login" - this is a best-effort probe, not a user-facing action.
 */
export async function reconcilePersistentLoginFromServer(
  service: PersistentPrefillServiceId
): Promise<PersistentLoginReconcileResult> {
  try {
    const response = await ApiService.getPersistentChallenge(service, RESUME_PROBE_TIMEOUT_SECONDS);
    if (isPersistentLoginAuthenticatedResponse(response)) {
      resetPersistentLoginState(service);
      updatePersistentLoginState(service, (current) => ({ ...current, authenticated: true }));
      return 'authenticated';
    }
    if (isPersistentLoginCredentialChallenge(response)) {
      applyPersistentLoginChallenge(service, response);
      return 'challenge';
    }
    return 'none';
  } catch {
    return 'none';
  }
}
