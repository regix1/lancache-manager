import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService, {
  type PersistentSessionConflictInfo,
  type PersistentSessionNotFoundInfo
} from '@services/api.service';
import { ApiError } from '@services/apiError';
import { getErrorMessage } from '@utils/error';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import type { SteamAuthActions, SteamLoginFlowState } from './useSteamAuthentication';
import {
  applyPersistentLoginChallenge,
  armPersistentLoginTimeout,
  consumePersistentLoginStartRequest,
  derivePersistentChallengeFlags,
  endPersistentLogin,
  extractPersistentSessionId,
  getPersistentLoginEpoch,
  getPersistentLoginEditAction,
  getPersistentLoginSessionId,
  getPersistentLoginStartPromise,
  isPersistentLoginAuthenticatedResponse,
  isPersistentLoginCancelled,
  isPersistentLoginCredentialChallenge,
  resetPersistentLoginSessionReplaced,
  resetPersistentLoginState,
  setPersistentLoginCancelled,
  setPersistentLoginStartPromise,
  terminatePersistentLoginSessionUnavailable,
  updatePersistentLoginState,
  usePersistentLoginStoreState
} from '@components/features/management/schedules/scheduled-prefill/persistentLoginStore';

// The challenge GET 404s when its daemon session is gone entirely (socket dropped, container
// stopped, etc. - diagnostic ADDENDUM). getPersistentChallenge (api.service.ts) parses
// ResolveRunningPersistentSession's typed 404 body itself and attaches { status, state } as
// `.cause` on the thrown error - that's the structural signal checked below. The `ApiError` branch
// is kept only as a defensive fallback (e.g. a proxy/CDN 404 that bypasses getPersistentChallenge's
// own 404 handling and instead falls through to `handleResponse`, which throws a typed `ApiError`
// with `status`) - never by sniffing the message text.
function isPersistentChallengeNotFoundError(
  error: unknown
): error is Error & { cause?: PersistentSessionNotFoundInfo } {
  if (!(error instanceof Error)) {
    return false;
  }
  const cause = (error as Error & { cause?: unknown }).cause;
  if (
    typeof cause === 'object' &&
    cause !== null &&
    (cause as { status?: unknown }).status === 404
  ) {
    return true;
  }
  return error instanceof ApiError && error.status === 404;
}

// RC3 fix: the challenge GET and provide-credential 409
// structurally when the pinned sessionId no longer matches the active session, or (provide-
// credential only) when the daemon reported it dropped the credential (RC4 manager leg). Detected
// via `.cause`, mirroring isPersistentChallengeNotFoundError above - never by message-sniffing.
function isPersistentSessionConflictError(
  error: unknown
): error is Error & { cause: PersistentSessionConflictInfo } {
  if (!(error instanceof Error)) {
    return false;
  }
  const cause = (error as Error & { cause?: unknown }).cause;
  return (
    typeof cause === 'object' &&
    cause !== null &&
    ((cause as { error?: unknown }).error === 'session_replaced' ||
      (cause as { error?: unknown }).error === 'credential_rejected')
  );
}

// A login POST refused because something else already holds this session's login. The daemon throws
// ConflictException, which the global exception middleware renders as a 409 whose body is a plain
// { error, statusCode, traceId } - none of the structured conflict fields buildApiError looks for
// (apiError.ts), so `.cause` stays unset and isPersistentSessionConflictError above can never see
// it. Hence a second guard rather than widening that one: the two are different endings, and only
// that one resets the store as a replaced session. The two session-pinning reasons are excluded by
// their code so a replaced session keeps its own message. Matched on status and code, never on the
// message text (the rule is written at the top of apiError.ts).
function isPersistentLoginBusyError(error: unknown): error is ApiError {
  if (!(error instanceof ApiError) || error.status !== 409) {
    return false;
  }
  const reason = error.body?.error;
  return reason !== 'session_replaced' && reason !== 'credential_rejected';
}

/**
 * Thrown by pollForResult instead of ever calling the challenge-GET REST endpoint when the store
 * has no pinned sessionId for this service anymore. This is a normal, already-ended flow, not a
 * failure: something else (a successful SignalR auth, a container-list retire on a stop, the
 * overall login timeout) already reset the store, but a free-standing poll loop that isn't wired
 * to observe that reset (see usePersistentXboxAuth's own guard, which is the primary fix) can still
 * fire one more iteration. Sending an empty sessionId would always 400 with a scary-looking
 * "sessionId is required" - recognized and swallowed everywhere it can surface (poll/submit/
 * handleAuthenticate below) instead of being treated like a real failure.
 */
class PersistentLoginNoPinnedSessionError extends Error {
  constructor(service: string) {
    super(`No pinned session for ${service} - the login flow already ended`);
    this.name = 'PersistentLoginNoPinnedSessionError';
  }
}

function isNoPinnedSessionError(error: unknown): error is PersistentLoginNoPinnedSessionError {
  return error instanceof PersistentLoginNoPinnedSessionError;
}

interface UsePersistentPrefillAuthOptions {
  service?: PersistentPrefillServiceId;
  timeoutSeconds?: number;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

interface PersistentPrefillAuthState extends SteamLoginFlowState {
  error: string | null;
  authenticated: boolean;
  /** True once a challenge has been obtained for the current attempt (drives modal visibility). */
  hasChallenge: boolean;
  /** True if the user hid the auth modal without cancelling; the flow stays alive/resumable. */
  dismissed: boolean;
}

export interface PersistentPrefillAuthActions extends SteamAuthActions {
  start: () => Promise<CredentialChallenge | null>;
  submit: (credential: string) => Promise<boolean>;
  poll: () => Promise<PollResult>;
  cancel: () => Promise<void>;
  /** Hides the auth modal without cancelling the daemon login (default close behavior). */
  dismissModal: () => void;
  /** Reveals the auth modal for an already-pending login again, without starting a new one. */
  resumeModal: () => void;
}

interface PersistentPrefillAuthResult {
  state: PersistentPrefillAuthState;
  actions: PersistentPrefillAuthActions;
}

export type PollResult =
  | { status: 'authenticated' }
  | { status: 'challenge'; challenge: CredentialChallenge }
  | { status: 'pending' };

// SignalR now pushes challenges the instant the daemon emits them (see
// usePersistentLoginChallengeSignalR); this GET long-poll is only the fallback for when SignalR is
// disconnected, so it can afford a slow interval ("SignalR is primary, this is the safety net").
const DEFAULT_TIMEOUT_SECONDS = 60;
const DEVICE_CONFIRMATION_CREDENTIAL = 'confirm';

export function usePersistentPrefillAuth(
  options: UsePersistentPrefillAuthOptions = {}
): PersistentPrefillAuthResult {
  const {
    service = 'Steam',
    timeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    onSuccess,
    onError
  } = options;

  const { t } = useTranslation();
  const stored = usePersistentLoginStoreState(service);
  // Single derivation point for the challenge-type UI flags - see derivePersistentChallengeFlags's
  // doc comment. Recomputed each render from `stored.pendingChallenge`, which is cheap (a switch
  // over one string) and only actually changes reference when a new challenge is applied or the
  // store resets.
  const challengeFlags = derivePersistentChallengeFlags(stored.pendingChallenge);

  const [useManualCode, setUseManualCode] = useState(false);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');

  const setLoading = useCallback(
    (loading: boolean) => {
      updatePersistentLoginState(service, (current) => ({ ...current, loading }));
    },
    [service]
  );

  const setError = useCallback(
    (error: string | null) => {
      updatePersistentLoginState(service, (current) => ({ ...current, error }));
    },
    [service]
  );

  // The challenge-type flags (needsTwoFactor/waitingForMobileConfirmation/etc.) are now derived
  // read-only from `stored.pendingChallenge` (see derivePersistentChallengeFlags) instead of being
  // independently-settable store fields. SteamAuthModal only calls these two setters from its
  // "switch to manual code entry" escape hatch, which it renders exclusively when `isPrefillMode`
  // is false - every persistent-login caller of this hook always passes `isPrefillMode={true}`, so
  // these are structurally unreachable here. Kept as no-ops only to satisfy the shared
  // SteamAuthActions interface.
  const setNeedsTwoFactor = useCallback((_value: boolean) => undefined, []);
  const setWaitingForMobileConfirmation = useCallback((_value: boolean) => undefined, []);

  const applyChallenge = useCallback(
    (challenge: CredentialChallenge, sessionId?: string | null) => {
      applyPersistentLoginChallenge(service, challenge, sessionId);
    },
    [service]
  );

  // Single choke point for the RC3 409 conflict: both
  // `pollForResult` (challenge GET) and `submitChallenge` (provide-credential) funnel their 409
  // here so the reset + translated message are applied exactly once, from exactly one place,
  // regardless of which REST call surfaced the conflict.
  const handleSessionConflict = useCallback(
    (err: Error & { cause: PersistentSessionConflictInfo }) => {
      const message =
        err.cause.error === 'session_replaced'
          ? t('prefill.persistent.sessionReplaced')
          : t('prefill.persistent.credentialRejected');
      resetPersistentLoginSessionReplaced(service, message);
    },
    [service, t]
  );

  const finishAuthenticated = useCallback(() => {
    resetPersistentLoginState(service);
    updatePersistentLoginState(service, (current) => ({ ...current, authenticated: true }));
    onSuccess?.();
  }, [service, onSuccess]);

  const fail = useCallback(
    (message: string) => {
      updatePersistentLoginState(service, (current) => ({
        ...current,
        error: message,
        loading: false
      }));
      onError?.(message);
    },
    [service, onError]
  );

  const pollForResult = useCallback(async (): Promise<PollResult> => {
    // Captured before the long-poll opens. A 409 that arrives after the store has moved on belongs
    // to an attempt that already ended, and handleSessionConflict resets unconditionally - so
    // without this snapshot a late conflict wipes whatever attempt is live now.
    const attemptEpoch = getPersistentLoginEpoch(service);
    // Read the session id LIVE (not the `stored` snapshot closed over when this callback was
    // created) - Xbox's device-code flow calls this from a poll loop that starts synchronously
    // right after start() resolves, in the same render pass that just wrote the real sessionId
    // into the store. That write only schedules a re-render; reading `stored.sessionId` here
    // would still see the pre-login `null` until the next render. getPersistentLoginSessionId
    // reads the module store directly, so it always sees the value as of THIS call.
    const sessionId = getPersistentLoginSessionId(service);
    if (!sessionId) {
      // Nothing pinned anymore - the flow already ended elsewhere (successful auth, a container
      // retire, the overall timeout). Never send an empty sessionId; the backend always 400s on
      // one ("sessionId is required"), which looks like a real failure but isn't. See
      // PersistentLoginNoPinnedSessionError's doc comment.
      throw new PersistentLoginNoPinnedSessionError(service);
    }

    try {
      const response = await ApiService.getPersistentChallenge(service, timeoutSeconds, sessionId);
      if (isPersistentLoginAuthenticatedResponse(response)) {
        return { status: 'authenticated' };
      }
      if (isPersistentLoginCredentialChallenge(response)) {
        return { status: 'challenge', challenge: response };
      }
      // Empty/204: the long-poll timed out with no new challenge yet (e.g. waiting
      // for the user to confirm a device code). Keep polling instead of erroring.
      return { status: 'pending' };
    } catch (err) {
      if (
        isPersistentSessionConflictError(err) &&
        getPersistentLoginEpoch(service) === attemptEpoch
      ) {
        handleSessionConflict(err);
      }
      // The rethrow is unconditional: the caller's loop still has to end, whether or not this
      // attempt was the one that owned the store.
      throw err;
    }
  }, [handleSessionConflict, service, timeoutSeconds]);

  const submitChallenge = useCallback(
    async (challenge: CredentialChallenge, credential: string): Promise<PollResult> => {
      setLoading(true);
      setError(null);
      // Captured before the first await and re-checked after the long-poll settles. An ending that
      // lands while that poll is open (the modal's X/Cancel, Logout, the overall timeout) resets
      // the store, and that reset CLEARS the cancel flag - so the flag alone stops being a usable
      // "this attempt is over" signal by the time the response arrives. The epoch survives it, the
      // same way start() fences its own settlement.
      const attemptEpoch = getPersistentLoginEpoch(service);
      try {
        const editAction = getPersistentLoginEditAction(service);
        await ApiService.providePersistentCredential(
          service,
          challenge,
          credential,
          getPersistentLoginSessionId(service) ?? '',
          editAction?.editSessionId,
          editAction?.editActionId
        );
      } catch (err) {
        if (
          isPersistentSessionConflictError(err) &&
          getPersistentLoginEpoch(service) === attemptEpoch
        ) {
          handleSessionConflict(err);
        }
        // Rethrown either way so this submit still ends where it always did.
        throw err;
      }

      let result = await pollForResult();
      while (
        result.status === 'pending' &&
        !isPersistentLoginCancelled(service) &&
        getPersistentLoginEpoch(service) === attemptEpoch
      ) {
        result = await pollForResult();
      }
      if (getPersistentLoginEpoch(service) !== attemptEpoch) {
        // The store no longer belongs to this attempt. Write NOTHING - not the result, not even
        // the spinner - or a challenge that arrives after the user ended the login reopens the
        // modal and puts the card back into "Authenticating..." for a daemon session that has
        // already been cancelled. Same reasoning as start()'s stale-epoch branch.
        //
        // The neutral 'pending' matters as much as the missing write: reporting 'authenticated'
        // here makes submit() return true, and the caller answers a true by closing its modal -
        // which dismisses whatever attempt is live now, not this dead one.
        return { status: 'pending' };
      }
      if (isPersistentLoginCancelled(service)) {
        setLoading(false);
        return result;
      }

      if (result.status === 'authenticated') {
        finishAuthenticated();
        return result;
      }

      if (result.status === 'challenge') {
        applyChallenge(result.challenge, extractPersistentSessionId(result.challenge));
      }
      setLoading(false);
      return result;
    },
    [
      applyChallenge,
      finishAuthenticated,
      handleSessionConflict,
      pollForResult,
      service,
      setError,
      setLoading
    ]
  );

  const submit = useCallback(
    async (credential: string): Promise<boolean> => {
      if (!stored.pendingChallenge) {
        fail(t('prefill.persistent.errors.noPendingChallenge'));
        return false;
      }

      try {
        const result = await submitChallenge(stored.pendingChallenge, credential);
        return result.status === 'authenticated';
      } catch (err) {
        if (isNoPinnedSessionError(err) || isPersistentSessionConflictError(err)) {
          // Not real failures - see PersistentLoginNoPinnedSessionError's doc comment and the
          // session-conflict handling above; a generic fail() here would stomp whatever the
          // originating catch already did (or, for the no-pinned-session case, write a scary error
          // over a flow that already ended cleanly elsewhere).
          return false;
        }
        const message = getErrorMessage(err);
        fail(message);
        return false;
      }
    },
    [fail, stored.pendingChallenge, submitChallenge, t]
  );

  const poll = useCallback(async (): Promise<PollResult> => {
    // See submitChallenge's matching capture: an ending that lands while this long-poll is open
    // resets the store and clears the cancel flag with it, so the epoch is the only signal left
    // that still says "this attempt is over" once the response arrives.
    const attemptEpoch = getPersistentLoginEpoch(service);
    try {
      setLoading(true);
      setError(null);
      const result = await pollForResult();
      if (getPersistentLoginEpoch(service) !== attemptEpoch) {
        // Store belongs to whatever came after the reset - discard this result rather than
        // resurrecting a login the user already ended. See start()'s stale-epoch branch.
        return result;
      }
      if (isPersistentLoginCancelled(service)) {
        setLoading(false);
        return result;
      }

      if (result.status === 'authenticated') {
        finishAuthenticated();
        return result;
      }

      if (result.status === 'challenge') {
        applyChallenge(result.challenge, extractPersistentSessionId(result.challenge));
      }
      setLoading(false);
      return result;
    } catch (err) {
      if (isNoPinnedSessionError(err)) {
        // Not a real failure - see PersistentLoginNoPinnedSessionError's doc comment. Just let the
        // exception propagate so the caller's poll loop (usePersistentXboxAuth's
        // pollUntilAuthenticated) stops, without ever writing an error into the store.
        throw err;
      }
      if (getPersistentLoginEpoch(service) !== attemptEpoch) {
        // Same rule as the success path above: this attempt's failure is not the current store's
        // failure. Still rethrow so the caller's loop ends, but write nothing.
        throw err;
      }
      if (isPersistentChallengeNotFoundError(err)) {
        // Terminal, not a transient failure: the daemon session behind this poll is gone, so
        // continuing to poll it is pointless. Reset to a friendly idle state instead of leaving
        // the raw HTTP 404 in state.error, then let the exception propagate - poll()'s only
        // caller (usePersistentXboxAuth's pollUntilAuthenticated) relies on the throw to end its
        // while loop.
        terminatePersistentLoginSessionUnavailable(service, err.cause?.state ?? 'notStarted');
        throw err;
      }
      if (isPersistentSessionConflictError(err)) {
        // Already reset (with its own translated message) inside pollForResult's own catch - avoid
        // a second, unrelated fail() write here.
        throw err;
      }
      const message = getErrorMessage(err);
      fail(message);
      throw err;
    }
  }, [applyChallenge, fail, finishAuthenticated, pollForResult, service, setError, setLoading]);

  const start = useCallback(async (): Promise<CredentialChallenge | null> => {
    const inFlight = getPersistentLoginStartPromise(service);
    if (inFlight) {
      return inFlight;
    }

    const startPromise = (async (): Promise<CredentialChallenge | null> => {
      setPersistentLoginCancelled(service, false);
      armPersistentLoginTimeout(service, t('prefill.persistent.loginTimedOut'));
      setLoading(true);
      setError(null);
      // Captured AFTER the synchronous writes above: every reset path (container stop/start, the
      // cleanup retire, an explicit cancel, the overall timeout) bumps the store's login epoch, so
      // comparing against this snapshot below tells this attempt that the store no longer belongs
      // to it by the time its REST call settles - the backend can hold this call open for tens of
      // seconds waiting for a daemon challenge, easily spanning a whole stop/start cycle.
      const startEpoch = getPersistentLoginEpoch(service);

      try {
        const startRequest = consumePersistentLoginStartRequest(service);
        const challenge = await ApiService.startPersistentLogin(
          service,
          startRequest?.sessionId,
          startRequest?.editSessionId,
          startRequest?.editActionId
        );
        const epochStale = getPersistentLoginEpoch(service) !== startEpoch;
        if (epochStale || isPersistentLoginCancelled(service)) {
          if (!epochStale) {
            // Same attempt, an explicit cancel racing this response (cancel() sets the flag before
            // its own reset runs): stop the spinner this attempt still owns. A stale epoch writes
            // NOTHING - the store already belongs to whatever came after the reset, and stomping it
            // with loading=false/fail() is exactly how a hung start from a stopped container used
            // to close or wedge the replacement attempt's auth modal.
            setLoading(false);
          }
          // Either way the daemon answered a login attempt that no longer has an owner - tear a
          // late challenge down so its daemon login is not left running orphaned.
          // cancelPersistentLogin is idempotent and session-pinned, so this can never cancel a
          // newer attempt's login.
          if (isPersistentLoginCredentialChallenge(challenge)) {
            const cancelSessionId = extractPersistentSessionId(challenge);
            if (cancelSessionId) {
              await ApiService.cancelPersistentLogin(service, cancelSessionId).catch(
                () => undefined
              );
            }
          }
          return null;
        }
        if (isPersistentLoginAuthenticatedResponse(challenge)) {
          // Container already authenticated (daemon self-authed from its own volume).
          finishAuthenticated();
          return null;
        }
        if (isPersistentLoginCredentialChallenge(challenge)) {
          applyChallenge(challenge, extractPersistentSessionId(challenge));
          setLoading(false);
          return challenge;
        }
        // Empty/no-op response: previously stopped the spinner in silence, which is exactly how a
        // login could go invisible forever - nothing else ever re-fetches this attempt's result
        // (diagnostic §3.2, wedge W1). Surface it as a real error instead so the card's existing
        // error alert shows it.
        fail(t('prefill.persistent.errors.noResult'));
        return null;
      } catch (err) {
        if (getPersistentLoginEpoch(service) !== startEpoch) {
          // The flow this call belonged to was already reset/superseded - its failure (typically
          // the backend's "Login timeout" 400 for a container that has since been stopped) is not
          // the current attempt's failure. Discard it instead of writing a stale error over live
          // state.
          return null;
        }
        if (isPersistentLoginBusyError(err)) {
          // A retryable ending, not a broken login: another attempt already holds this session's
          // login. The persistent session is shared per service rather than per user, so the other
          // attempt is either the automatic one made at startup or a different admin who is
          // mid-login and may be sitting on a device code for some time. The server sentence names
          // the internal session id, so show a translated one instead. Deliberately no automatic
          // retry - the server already waits before refusing, so a click that gets here has
          // genuinely lost the race and the wait could be a long one.
          fail(t('prefill.persistent.loginBusy'));
          return null;
        }
        const message = getErrorMessage(err);
        fail(message);
        return null;
      }
    })();

    setPersistentLoginStartPromise(service, startPromise);
    try {
      return await startPromise;
    } finally {
      if (getPersistentLoginStartPromise(service) === startPromise) {
        setPersistentLoginStartPromise(service, null);
      }
    }
  }, [applyChallenge, fail, finishAuthenticated, service, setError, setLoading, t]);

  // The username is cleared here and kept by the two sibling hooks, which is deliberate. Those two
  // sign in the person using the app, so their account name is worth keeping across a retry. This one
  // signs in the account belonging to a container, and the modal is mounted per service, so a name left
  // behind would pre-fill the wrong account the next time a different container asks for a login.
  const resetAuthForm = useCallback(() => {
    setUsername('');
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setAuthorizationCode('');
    setUseManualCode(false);
    resetPersistentLoginState(service);
  }, [service]);

  const cancel = useCallback(async (): Promise<void> => {
    setPersistentLoginCancelled(service, true);
    setLoading(false);
    // One shared ending for every way a login stops (see endPersistentLogin): it reads the pinned
    // sessionId live rather than from the `stored` snapshot closed over here - a cancel fired from
    // the same synchronous flow that just started a login must still see the id written moments
    // ago - and it skips the round trip when nothing was ever pinned, which is only the brief
    // window before start()'s very first response lands (RC3). Skipping it there is safe rather
    // than a silent no-op: nothing server-side has been told about this attempt yet either, and
    // the store reset inside bumps the login epoch, so a still-hanging start() recognizes its own
    // settlement as stale and discards it.
    await endPersistentLogin(service);
    // The store half is already at rest; this clears the credentials typed into the form, which is
    // this hook's own state and not the store's.
    resetAuthForm();
  }, [resetAuthForm, service, setLoading]);

  const cancelPendingRequest = useCallback(() => {
    void cancel();
  }, [cancel]);

  const dismissModal = useCallback(() => {
    updatePersistentLoginState(service, (current) => ({ ...current, dismissed: true }));
  }, [service]);

  const resumeModal = useCallback(() => {
    updatePersistentLoginState(service, (current) => ({ ...current, dismissed: false }));
  }, [service]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    const flags = derivePersistentChallengeFlags(stored.pendingChallenge);

    if (flags.needsTwoFactor) {
      if (!twoFactorCode.trim()) {
        fail(t('prefill.auth.errors.twoFactorRequired'));
        return false;
      }
      return submit(twoFactorCode);
    }

    if (flags.needsEmailCode) {
      if (!emailCode.trim()) {
        fail(t('prefill.auth.errors.emailCodeRequired'));
        return false;
      }
      return submit(emailCode);
    }

    if (flags.needsAuthorizationCode) {
      if (!authorizationCode.trim()) {
        fail(t('prefill.auth.errors.authCodeRequired'));
        return false;
      }
      return submit(authorizationCode);
    }

    if (!username.trim() || !password.trim()) {
      fail(t('prefill.auth.errors.credentialsRequired'));
      return false;
    }

    try {
      let challenge = stored.pendingChallenge ?? (await start());
      if (!challenge) {
        return false;
      }

      if (challenge.credentialType === 'username') {
        const usernameResult = await submitChallenge(challenge, username);
        if (usernameResult.status === 'authenticated') {
          return true;
        }
        if (usernameResult.status !== 'challenge') {
          return false;
        }
        challenge = usernameResult.challenge;
      }

      if (challenge?.credentialType === 'password') {
        const passwordResult = await submitChallenge(challenge, password);
        if (passwordResult.status === 'authenticated') {
          return true;
        }
        if (passwordResult.status !== 'challenge') {
          return false;
        }
        challenge = passwordResult.challenge;
      }

      if (challenge?.credentialType === 'device-confirmation') {
        const confirmationResult = await submitChallenge(challenge, DEVICE_CONFIRMATION_CREDENTIAL);
        return confirmationResult.status === 'authenticated';
      }

      return stored.authenticated;
    } catch (err) {
      if (isNoPinnedSessionError(err) || isPersistentSessionConflictError(err)) {
        // Not real failures - see PersistentLoginNoPinnedSessionError's doc comment and the
        // session-conflict handling above; avoid a second, unrelated fail() write here.
        return false;
      }
      const message = getErrorMessage(err);
      fail(message);
      return false;
    }
  }, [
    authorizationCode,
    emailCode,
    fail,
    password,
    start,
    stored.authenticated,
    stored.pendingChallenge,
    submit,
    submitChallenge,
    t,
    twoFactorCode,
    username
  ]);

  const state: PersistentPrefillAuthState = {
    loading: stored.loading,
    ...challengeFlags,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode,
    authorizationCode,
    error: stored.error,
    authenticated: stored.authenticated,
    hasChallenge: stored.pendingChallenge !== null,
    dismissed: stored.dismissed
  };

  const actions: PersistentPrefillAuthActions = {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    setUseManualCode,
    setNeedsTwoFactor,
    setWaitingForMobileConfirmation,
    setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest,
    start,
    submit,
    poll,
    cancel,
    dismissModal,
    resumeModal
  };

  return { state, actions };
}
