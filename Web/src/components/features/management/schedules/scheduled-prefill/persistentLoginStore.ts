import { useSyncExternalStore } from 'react';
import ApiService from '@services/api.service';
import type {
  PersistentPrefillServiceId,
  PersistentSessionNotFoundState
} from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from '@hooks/usePrefillSteamAuth';
import { isRecord } from './typeGuards';

/**
 * Login-flow state for the persistent-container auth flows (Steam/Epic/Xbox), keyed by service
 * and held OUTSIDE the modal component tree. Reopening the config modal (or a PersistentLoginHost
 * remount from container-list churn) no longer loses an in-progress login: this module-level store
 * is the single source of truth for the UI, and `reconcilePersistentLoginFromServer` is what makes
 * it safe to resume even after a full page reload, by reading the backend's own pending-challenge
 * cache (see PersistentPrefillController / DaemonSession).
 *
 * The store keeps only the raw `pendingChallenge` from the daemon - it does NOT also flatten the
 * challenge into separate `needsTwoFactor`/`needsDeviceCode`/etc. fields. Those are UI rendering
 * detail (which form fields the auth modal shows), not state-machine detail, so they are derived
 * on read by `derivePersistentChallengeFlags` instead of being duplicated storage that every
 * challenge-applying code path had to keep in sync with `pendingChallenge` by hand.
 */
interface PersistentLoginStoreState {
  loading: boolean;
  error: string | null;
  authenticated: boolean;
  pendingChallenge: CredentialChallenge | null;
  /**
   * True once the user has hidden the auth modal without cancelling. The daemon login flow (and
   * the cached challenge) stays alive; the modal reopens showing the same challenge next time
   * instead of restarting the login.
   */
  dismissed: boolean;
  /**
   * Non-null after a challenge poll came back 404 (the daemon session backing this login is gone -
   * socket dropped, container stopped, etc. - see diagnostic ADDENDUM). Carries the backend's
   * errored-vs-never-started discriminator so the card can show distinct copy for each, instead of
   * the raw HTTP error that triggered the reset. Cleared by the next `resetPersistentLoginState`
   * (Start/Stop/Logout all call it).
   */
  sessionUnavailableState: PersistentSessionNotFoundState | null;
  /**
   * RC3 fix: the `DaemonSession.Id` this login flow is
   * currently pinned to, taken from the `sessionId` every persistent-login REST response now
   * carries. Every follow-up call (challenge poll, provide-credential, cancel-login) sends this
   * back so the server can reject a request that has been superseded by a newer session instead of
   * silently substituting it (the pre-fix cross-session leak). `null` when no login is pinned yet.
   */
  sessionId: string | null;
}

interface PersistentChallengeFlags {
  needsTwoFactor: boolean;
  needsEmailCode: boolean;
  waitingForMobileConfirmation: boolean;
  needsAuthorizationCode: boolean;
  authorizationUrl: string;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
}

const EMPTY_CHALLENGE_FLAGS: PersistentChallengeFlags = {
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
  loading: false,
  error: null,
  authenticated: false,
  pendingChallenge: null,
  dismissed: false,
  sessionUnavailableState: null,
  sessionId: null
};

/**
 * Reads the `sessionId` field a persistent-login REST response now carries (RC3 fix),
 * regardless of which response variant it is (challenge object,
 * `{authenticated:true}`, `{status:...}`, or the SignalR `CredentialChallengePayload` wrapper -
 * all of them put `sessionId` at the top level). Returns `null` for the legacy bare `'authenticated'`
 * string variant or any shape missing the field.
 */
export function extractPersistentSessionId(response: unknown): string | null {
  if (isRecord(response) && typeof response.sessionId === 'string') {
    return response.sessionId;
  }
  return null;
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

/**
 * Derives the challenge-type UI flags (which fields the auth modal should show) from the store's
 * raw `pendingChallenge` - the ONLY place this mapping happens, so the flags can never drift out
 * of sync with the challenge that produced them. `null` (no pending challenge) maps to all-false.
 */
export function derivePersistentChallengeFlags(
  challenge: CredentialChallenge | null
): PersistentChallengeFlags {
  if (!challenge) {
    return EMPTY_CHALLENGE_FLAGS;
  }
  const flags: PersistentChallengeFlags = { ...EMPTY_CHALLENGE_FLAGS };
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
// Also kept separate from the persisted state above (and NOT cleared by resetPersistentLoginState):
// an explicit "Log in" click must always be observable by an already-mounted login component, even
// across a store reset, so this counter's value is never reused/collided with a prior click.
const loginAttemptCounters = new Map<PersistentPrefillServiceId, number>();
const loginAttemptListeners = new Map<PersistentPrefillServiceId, Set<Listener>>();
// Monotonic per-service epoch, bumped by every reset/terminal transition. An async login flow
// (start()'s REST call, which the backend can hold open ~40s waiting for a daemon challenge)
// captures the epoch when it begins and compares on settlement: a mismatch means the store was
// reset out from under it (container stop/start, cleanup retire, explicit cancel, overall timeout)
// and its result must be DISCARDED, not written - a stale fail()/setLoading(false) landing in the
// NEXT attempt's store is exactly how a hung start from a stopped container used to close or wedge
// the replacement attempt's auth modal.
const loginEpochs = new Map<PersistentPrefillServiceId, number>();

/** Reads the current login epoch for a service - see `loginEpochs`. Captured by start() when an
 *  attempt begins; a later mismatch marks that attempt's settlement as stale. */
export function getPersistentLoginEpoch(service: PersistentPrefillServiceId): number {
  return loginEpochs.get(service) ?? 0;
}

/**
 * Invalidates everything a still-running async login flow could later write through: bumps the
 * epoch (so an in-flight start()'s eventual settlement is recognized as stale and discarded),
 * drops the registered in-flight start promise (so the NEXT "Log in" click fires a real daemon
 * login instead of silently awaiting a dead session's hung REST call - the wedge where every
 * click after a container stop/start did nothing until that call timed out server-side), and
 * clears the cancel flag (which otherwise leaked `true` into a later challenge that arrived
 * without a fresh start() ever re-running, making that modal's submit poll loop bail instantly).
 * Called by every reset/terminal transition below.
 */
function invalidateInFlightLogin(service: PersistentPrefillServiceId): void {
  loginEpochs.set(service, (loginEpochs.get(service) ?? 0) + 1);
  startPromises.delete(service);
  const cancelFlag = cancelFlags.get(service);
  if (cancelFlag) {
    cancelFlag.current = false;
  }
}

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

// Overall wall-clock ceiling on a single login ATTEMPT (from the explicit "Log in" click that
// starts it, not from a resumed/re-shown challenge - see armPersistentLoginTimeout below). None of
// the three services had one before this: Steam/Epic could poll forever on a stuck 2FA/email/auth-
// code step, and Xbox's only ceiling was the daemon's own ~15min device-code expiry (Microsoft's
// `expires_in`), which only fires AFTER the daemon gives up - this manager-side timer is a service-
// agnostic backstop that fires first for a hung UI step regardless of what the daemon does.
const OVERALL_LOGIN_TIMEOUT_MS = 10 * 60 * 1000;
const loginTimeoutHandles = new Map<PersistentPrefillServiceId, ReturnType<typeof setTimeout>>();

function clearPersistentLoginTimeout(service: PersistentPrefillServiceId): void {
  const handle = loginTimeoutHandles.get(service);
  if (handle !== undefined) {
    clearTimeout(handle);
    loginTimeoutHandles.delete(service);
  }
}

/**
 * Arms the overall login-attempt timeout. Lives at module level (not a component ref) for the same
 * reason every other piece of this flow's lifecycle does - the attempt must keep timing out even
 * across a PersistentLoginHost remount (Configure modal closed/reopened, container-list churn).
 * Call once per fresh attempt (from `start()`); resuming an already-pending challenge via
 * resumeModal() must NOT re-arm it - the original attempt's clock keeps ticking correctly on its
 * own, tracked here independent of any component's mount state.
 */
export function armPersistentLoginTimeout(service: PersistentPrefillServiceId): void {
  clearPersistentLoginTimeout(service);
  const handle = setTimeout(() => {
    loginTimeoutHandles.delete(service);
    // Epoch bump (not the cancel flag, which would leak `true` into a later resumed challenge):
    // the still-hanging start() recognizes its settlement as stale, discards it, and best-effort
    // cancels a late daemon challenge itself - the same teardown the flag used to buy here.
    invalidateInFlightLogin(service);
    updatePersistentLoginState(service, () => ({
      ...INITIAL_PERSISTENT_LOGIN_STATE,
      error: 'Timed out waiting for a response. Please try again.'
    }));
  }, OVERALL_LOGIN_TIMEOUT_MS);
  loginTimeoutHandles.set(service, handle);
}

export function resetPersistentLoginState(service: PersistentPrefillServiceId): void {
  clearPersistentLoginTimeout(service);
  const current = states.get(service);
  if (current === undefined || current === INITIAL_PERSISTENT_LOGIN_STATE) {
    // Already at rest - skip the Map write + subscriber notify. Matters because a container-list
    // cleanup pass now calls this for every persistent service on every refresh (see
    // ScheduledPrefillConfigModal's authenticated-or-stopped cleanup effect), and most services are
    // simply idle/never touched most of the time - without this guard every refresh would re-render
    // every subscribed card for no reason. Skipping invalidateInFlightLogin here is safe for the
    // same reason: a start() attempt writes loading=true synchronously before anything can await,
    // so the state can only be at rest again after a reset that already invalidated it.
    return;
  }
  invalidateInFlightLogin(service);
  states.set(service, INITIAL_PERSISTENT_LOGIN_STATE);
  notify(service);
}

/**
 * Terminal reset for a challenge poll that came back 404 (see diagnostic ADDENDUM): the daemon
 * session backing this login is gone entirely (socket dropped, container stopped, etc.), so
 * continuing to poll it is pointless. Resets the flow to idle like `resetPersistentLoginState`,
 * but sets `sessionUnavailableState` so the card can show a friendly "not running - press Start"
 * (or, for an errored session, "session errored - press Start to restart") message instead of
 * leaving the raw HTTP 404 that triggered this in `error`.
 */
export function terminatePersistentLoginSessionUnavailable(
  service: PersistentPrefillServiceId,
  state: PersistentSessionNotFoundState = 'notStarted'
): void {
  clearPersistentLoginTimeout(service);
  invalidateInFlightLogin(service);
  states.set(service, { ...INITIAL_PERSISTENT_LOGIN_STATE, sessionUnavailableState: state });
  notify(service);
}

/**
 * Terminal reset for a REST call (challenge poll or credential submit) that came back 409 because
 * the pinned session was superseded by a newer one (RC3 fix):
 * the daemon session this login flow was pinned to is no longer the active one for this service
 * (the user stopped it and a fresh container started, or a scheduled run replaced it). Continuing
 * to poll/submit against the dead session is exactly the cross-session leak RC3 closed on the
 * backend, so the flow resets to idle - like `resetPersistentLoginState` - but carries a translated
 * `message` (set as `error`, the same field the card's existing login-error alert already renders)
 * so the reset explains itself instead of just silently going blank.
 */
export function resetPersistentLoginSessionReplaced(
  service: PersistentPrefillServiceId,
  message: string
): void {
  clearPersistentLoginTimeout(service);
  invalidateInFlightLogin(service);
  states.set(service, { ...INITIAL_PERSISTENT_LOGIN_STATE, error: message });
  notify(service);
}

/**
 * Applies a challenge to the store - from either delivery path (the REST resume/poll response, or
 * the SignalR CredentialChallenge push). The two can race and deliver the SAME challenge twice
 * (e.g. the poll's in-flight response lands right after SignalR already applied it): when the
 * incoming challenge's id matches what's already stored, this is a re-delivery, not a new
 * challenge, so `dismissed` is left untouched instead of being force-reset to false - otherwise a
 * modal the user deliberately dismissed would silently reopen itself.
 *
 * `sessionId` (RC3 fix) pins the store to the session this
 * challenge came from, when the caller has one to supply - omit it (`undefined`) to leave an
 * already-pinned session untouched (e.g. a caller that hasn't resolved a session id itself yet).
 * Pass `null` explicitly only to deliberately clear the pin.
 */
/**
 * Marks a service's persistent login authenticated (resets the flow to a clean state, then sets
 * `authenticated: true`) - the same store transition `finishAuthenticated`/the reconcile probe make.
 * Called from the SignalR `AuthStateChanged: Authenticated` push (see usePersistentLoginChallengeSignalR),
 * which is the RELIABLE, event-driven signal that the daemon logged in - e.g. the moment the user
 * approves a Steam mobile device-confirmation on their phone. Without this the persistent flow had no
 * event path to completion and relied solely on the REST challenge poll, which can miss the login
 * transition (the mapping/guest flow has always completed via AuthStateChanged; this gives the
 * persistent flow parity). Setting `authenticated: true` drives the login component's own
 * `onAuthenticated` effect, closing the modal.
 */
export function markPersistentLoginAuthenticated(service: PersistentPrefillServiceId): void {
  resetPersistentLoginState(service);
  updatePersistentLoginState(service, (current) => ({ ...current, authenticated: true }));
}

export function applyPersistentLoginChallenge(
  service: PersistentPrefillServiceId,
  challenge: CredentialChallenge,
  sessionId?: string | null
): void {
  updatePersistentLoginState(service, (current) => {
    const isRedelivery = current.pendingChallenge?.challengeId === challenge.challengeId;
    return {
      ...current,
      pendingChallenge: challenge,
      // A genuinely NEW challenge means the network phase that produced it is over - the flow is
      // now waiting on the USER - so the spinner state must end even when the challenge arrived
      // via the SignalR push while the REST leg that started the login is still hanging (or was
      // reset away entirely). Leaving `loading` stuck true here rendered the credentials form with
      // every input disabled and the submit button spinning "Authenticating..." with no way out.
      // A redelivery keeps whatever loading state an in-flight submit currently owns.
      loading: isRedelivery ? current.loading : false,
      dismissed: isRedelivery ? current.dismissed : false,
      sessionId: sessionId !== undefined ? sessionId : current.sessionId
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

/**
 * Plain (non-hook) read of the sessionId a service's in-flight login is currently pinned to (RC3
 * fix). Used by the SignalR credential-challenge handler
 * (`usePersistentLoginChallengeSignalR`), which runs outside render and cannot subscribe via
 * `usePersistentLoginStoreState`: it fences an incoming push against this pin so a challenge for
 * an already-superseded session can never be applied, even if the caller's own `containersByService`
 * (React state, refreshed on its own cadence) is momentarily stale and would otherwise let the same
 * stale push through.
 */
export function getPersistentLoginSessionId(service: PersistentPrefillServiceId): string | null {
  return getPersistentLoginState(service).sessionId;
}

function notifyLoginAttempt(service: PersistentPrefillServiceId): void {
  loginAttemptListeners.get(service)?.forEach((listener) => listener());
}

function subscribeLoginAttempt(
  service: PersistentPrefillServiceId,
  listener: Listener
): () => void {
  const set = loginAttemptListeners.get(service) ?? new Set<Listener>();
  set.add(listener);
  loginAttemptListeners.set(service, set);
  return () => {
    set.delete(listener);
  };
}

/**
 * Bumped once per explicit "Log in" click (ScheduledPrefillConfigModal's `handlePersistentLogin`),
 * independent of whether `persistentLoginTarget`'s value actually changes. Setting the target to a
 * service it already points at (a dismissed-but-still-pending challenge, or a wedge where `start()`
 * settled with nothing) is a same-value no-op for React, so an already-mounted login component would
 * otherwise never see the click. Its autostart effect watches this nonce instead of firing only once
 * per mount, so an explicit click always reaches `beginLogin` again.
 */
export function requestPersistentLoginAttempt(service: PersistentPrefillServiceId): void {
  loginAttemptCounters.set(service, (loginAttemptCounters.get(service) ?? 0) + 1);
  notifyLoginAttempt(service);
}

/** Reads the nonce `requestPersistentLoginAttempt` bumps - see its doc comment for why this exists
 *  as a hook a login component subscribes to, rather than a one-shot mount ref alone. */
export function usePersistentLoginRequestNonce(service: PersistentPrefillServiceId): number {
  return useSyncExternalStore(
    (listener) => subscribeLoginAttempt(service, listener),
    () => loginAttemptCounters.get(service) ?? 0
  );
}

// Highest login-attempt nonce (see loginAttemptCounters above) that has already triggered exactly
// one beginLogin() attempt for a service. -1 means "nothing consumed yet", so nonce 0 (the value
// before any explicit click ever happens this session) still attempts once - matching the old
// per-component ref's null-sentinel behavior, which is what lets a reconciled/resumed challenge
// reveal itself on mount without a click. Deliberately NOT cleared by resetPersistentLoginState,
// for the same reason loginAttemptCounters isn't: it tracks nonce VALUES already acted on, not
// login-flow state, and a stale service that later starts fresh still must not re-attempt a nonce
// it already consumed.
const consumedLoginAttemptNonces = new Map<PersistentPrefillServiceId, number>();

/**
 * Attempts to consume `nonce` for `service`. Returns true exactly once per nonce value - the first
 * caller (any component instance, across any number of remounts) to observe a given nonce gets
 * true and should proceed with `beginLogin()`; every subsequent observation of that same (or an
 * older) nonce - including from a component that just remounted, losing its own local state -
 * gets false. This is what closes the diagnostic §2 wedge: a `PersistentLoginHost` remount used to
 * reset a per-component ref and let an already-settled nonce re-fire `start()` with no user click;
 * consuming the nonce here instead, in the module-level store, survives the remount.
 */
export function consumeLoginAttemptNonce(
  service: PersistentPrefillServiceId,
  nonce: number
): boolean {
  const consumed = consumedLoginAttemptNonces.get(service) ?? -1;
  if (nonce <= consumed) {
    return false;
  }
  consumedLoginAttemptNonces.set(service, nonce);
  return true;
}

/**
 * Peek-only companion to `consumeLoginAttemptNonce`: true when a login-attempt nonce has been
 * bumped (an explicit "Log in" click happened, or this is a fresh service that has never consumed
 * its initial nonce 0) but not yet consumed by any login component this session. Does not mutate
 * `consumedLoginAttemptNonces` - only `consumeLoginAttemptNonce` itself does that, exactly once per
 * nonce. Used by `PersistentLoginHost` to derive `autoStart` structurally instead of hardcoding it,
 * alongside `pendingChallenge !== null` (an already reconcile-confirmed cached challenge) - see its
 * doc comment for why either signal alone is what makes auto-firing `beginLogin()` legitimate.
 *
 * `nonce` defaults to a live Map read, but callers that already hold a value subscribed via
 * `usePersistentLoginRequestNonce` should pass it explicitly - this function itself is not
 * reactive, so a caller relying only on the default would need its own re-render trigger to ever
 * observe a bump.
 */
export function hasUnconsumedLoginAttempt(
  service: PersistentPrefillServiceId,
  nonce: number = loginAttemptCounters.get(service) ?? 0
): boolean {
  const consumed = consumedLoginAttemptNonces.get(service) ?? -1;
  return nonce > consumed;
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
 *
 * `sessionId` (RC3 fix) is now REQUIRED by the challenge GET -
 * the caller passes the currently-known container's session id (`PersistentPrefillContainerDto.sessionId`,
 * already loaded for the reconcile effect's own running/authenticated checks). A 409
 * `session_replaced` here (the container list was momentarily stale) is just another "no pending
 * login" outcome for this best-effort probe, not a special case - the catch-all below covers it.
 */
export async function reconcilePersistentLoginFromServer(
  service: PersistentPrefillServiceId,
  sessionId: string
): Promise<PersistentLoginReconcileResult> {
  try {
    const response = await ApiService.getPersistentChallenge(
      service,
      RESUME_PROBE_TIMEOUT_SECONDS,
      sessionId
    );
    if (isPersistentLoginAuthenticatedResponse(response)) {
      resetPersistentLoginState(service);
      updatePersistentLoginState(service, (current) => ({ ...current, authenticated: true }));
      return 'authenticated';
    }
    if (isPersistentLoginCredentialChallenge(response)) {
      applyPersistentLoginChallenge(
        service,
        response,
        extractPersistentSessionId(response) ?? sessionId
      );
      return 'challenge';
    }
    return 'none';
  } catch {
    return 'none';
  }
}
