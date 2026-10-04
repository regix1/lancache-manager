import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService, { type PersistentSessionConflictInfo } from '@services/api.service';
import { ApiError } from '@services/apiError';
import { getErrorMessage } from '@utils/error';
import { createUuid } from '@utils/uuid';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import { loginAttemptTimeoutMs } from './loginAttemptTimeout';
import { getAuthStage } from './authStage';
import type { SteamAuthActions, SteamLoginFlowState } from './useSteamAuthentication';
import {
  applyPersistentLoginChallenge,
  armPersistentLoginTimeout,
  beginPersistentLoginStep,
  clearPersistentLoginIntegrationReuse,
  consumePersistentLoginStartRequest,
  derivePersistentChallengeFlags,
  endPersistentLogin,
  ensurePersistentLoginTimeout,
  extractPersistentSessionId,
  getPersistentLoginEpoch,
  getPersistentLoginState,
  getPersistentLoginEditAction,
  getPersistentLoginSessionId,
  getPersistentLoginStartPromise,
  finishPersistentLoginStep,
  isPersistentLoginAuthenticatedResponse,
  isPersistentLoginCancelled,
  isPersistentLoginCredentialChallenge,
  isPersistentLoginSuspended,
  markPersistentLoginAuthenticated,
  resetPersistentLoginSessionReplaced,
  resetPersistentLoginState,
  setPersistentLoginCancelled,
  setPersistentLoginStartPromise,
  updatePersistentLoginState,
  usePersistentLoginStoreState,
  waitForPersistentLoginChange
} from '@components/features/management/schedules/scheduled-prefill/persistentLoginStore';

// RC3 fix: the provide-credential 409
// structurally when the pinned sessionId no longer matches the active session, or
// when the daemon reported it dropped the credential (RC4 manager leg). Detected
// via `.cause` - never by message-sniffing.
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

interface UsePersistentPrefillAuthOptions {
  service?: PersistentPrefillServiceId;
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

type PollResult =
  | { status: 'authenticated' }
  | { status: 'challenge'; challenge: CredentialChallenge }
  | { status: 'pending' };

const DEVICE_CONFIRMATION_CREDENTIAL = 'confirm';

export function usePersistentPrefillAuth(
  options: UsePersistentPrefillAuthOptions = {}
): PersistentPrefillAuthResult {
  const { service = 'Steam', onSuccess, onError } = options;

  const { t } = useTranslation();
  const messages = useMemo(
    () => ({
      noResult: t('prefill.persistent.errors.noResult'),
      timedOut: t('prefill.persistent.loginTimedOut')
    }),
    [t]
  );
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
      return applyPersistentLoginChallenge(service, challenge, messages, sessionId);
    },
    [service, messages]
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
    if (isPersistentLoginSuspended()) return;
    markPersistentLoginAuthenticated(service);
    onSuccess?.();
  }, [service, onSuccess]);

  const fail = useCallback(
    (message: string) => {
      const step = getPersistentLoginState(service).step;
      if (step) finishPersistentLoginStep(service, step.actionId);
      clearPersistentLoginIntegrationReuse(service);
      updatePersistentLoginState(service, (current) => ({
        ...current,
        error: message,
        loading: false
      }));
      onError?.(message);
    },
    [service, onError]
  );

  const submitChallenge = useCallback(
    async (
      challenge: CredentialChallenge,
      credential: string,
      actionId?: number
    ): Promise<PollResult> => {
      if (
        !ensurePersistentLoginTimeout(service, messages) ||
        getPersistentLoginState(service).pendingChallenge?.challengeId !== challenge.challengeId ||
        getPersistentLoginState(service).pendingChallenge?.operationId !== challenge.operationId
      ) {
        return { status: 'pending' };
      }
      const stage = getAuthStage(challenge.credentialType);
      if (!stage) throw new Error(messages.noResult);
      const submitted =
        actionId === undefined
          ? beginPersistentLoginStep(service, stage)
          : getPersistentLoginState(service).step;
      if (
        !submitted ||
        submitted.stage !== stage ||
        submitted.actionId !== (actionId ?? submitted.actionId)
      ) {
        return { status: 'pending' };
      }
      const ownsStep = () => getPersistentLoginState(service).step?.actionId === submitted.actionId;
      // Captured before the first await and re-checked each time the wait below wakes. An ending that
      // lands while the wait is open (the modal's X/Cancel, Logout, the overall timeout) resets
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
        if (!ownsStep()) return { status: 'pending' };
        if (
          isPersistentSessionConflictError(err) &&
          !isPersistentLoginSuspended() &&
          getPersistentLoginEpoch(service) === attemptEpoch
        ) {
          handleSessionConflict(err);
        }
        // Rethrown either way so this submit still ends where it always did.
        throw err;
      }

      if (!ownsStep()) return { status: 'pending' };
      const loginId = getPersistentLoginState(service).loginId;
      // The next challenge, the sign-in's success and its ending are pushed over SignalR into the store (and read once when
      // the connection comes back), so this waits for the store to move instead of asking the server again.
      for (;;) {
        const state = getPersistentLoginState(service);
        if (state.authenticated && state.loginId === loginId) return { status: 'authenticated' };
        // The store no longer belongs to this attempt: write nothing, and report 'pending' so a caller never reads a
        // dead attempt as a success that closes whatever attempt is live now.
        if (isPersistentLoginSuspended() || getPersistentLoginEpoch(service) !== attemptEpoch) {
          return { status: 'pending' };
        }
        if (isPersistentLoginCancelled(service)) {
          finishPersistentLoginStep(service, submitted.actionId);
          return { status: 'pending' };
        }
        if (
          state.pendingChallenge &&
          state.pendingChallenge.challengeId !== challenge.challengeId
        ) {
          return { status: 'challenge', challenge: state.pendingChallenge };
        }
        if (!ownsStep()) return { status: 'pending' };
        await waitForPersistentLoginChange(service);
      }
    },
    [handleSessionConflict, messages, service]
  );

  const submit = useCallback(
    async (credential: string): Promise<boolean> => {
      if (!ensurePersistentLoginTimeout(service, messages)) return false;
      const attemptEpoch = getPersistentLoginEpoch(service);
      if (!stored.pendingChallenge) {
        fail(t('prefill.persistent.errors.noPendingChallenge'));
        return false;
      }

      try {
        const result = await submitChallenge(stored.pendingChallenge, credential);
        return result.status === 'authenticated';
      } catch (err) {
        if (
          getPersistentLoginEpoch(service) !== attemptEpoch ||
          isPersistentLoginSuspended() ||
          getPersistentLoginState(service).loginDeadline === null
        )
          return false;
        if (isPersistentSessionConflictError(err)) {
          // Not a real failure - see the session-conflict handling above; a generic fail() here
          // would stomp whatever the originating catch already did.
          return false;
        }
        const message = getErrorMessage(err);
        fail(message);
        return false;
      }
    },
    [fail, messages, service, stored.pendingChallenge, submitChallenge, t]
  );

  const start = useCallback(async (): Promise<CredentialChallenge | null> => {
    if (isPersistentLoginSuspended()) return null;
    const inFlight = getPersistentLoginStartPromise(service);
    if (inFlight) {
      return inFlight;
    }
    const current = getPersistentLoginState(service);
    if (current.pendingChallenge || current.loginDeadline !== null) {
      return ensurePersistentLoginTimeout(service, messages) ? current.pendingChallenge : null;
    }

    const startPromise = (async (): Promise<CredentialChallenge | null> => {
      if (
        !armPersistentLoginTimeout(service, Date.now() + loginAttemptTimeoutMs(service), messages)
      )
        return null;
      setPersistentLoginCancelled(service, false);
      const submitted = beginPersistentLoginStep(service, 'start');
      if (!submitted) return getPersistentLoginState(service).pendingChallenge;
      const loginId = createUuid();
      updatePersistentLoginState(service, (state) => ({ ...state, loginId }));
      const startRequest = consumePersistentLoginStartRequest(service);
      const startSessionId = startRequest?.sessionId ?? getPersistentLoginState(service).sessionId;
      const cancelStale = async (response?: unknown): Promise<void> => {
        const sessionId = startSessionId ?? extractPersistentSessionId(response);
        if (!sessionId) return;
        const loginAttempt = isPersistentLoginCredentialChallenge(response)
          ? (response.loginAttempt ?? null)
          : null;
        await ApiService.cancelPersistentLogin(service, sessionId, { loginId, loginAttempt }).catch(
          () => undefined
        );
      };
      // Captured AFTER the synchronous writes above: every reset path (container stop/start, the
      // cleanup retire, an explicit cancel, the overall timeout) bumps the store's login epoch, so
      // comparing against this snapshot below tells this attempt that the store no longer belongs
      // to it by the time its REST call settles - the backend can hold this call open for tens of
      // seconds waiting for a daemon challenge, easily spanning a whole stop/start cycle.
      const startEpoch = getPersistentLoginEpoch(service);

      try {
        const challenge = await ApiService.startPersistentLogin(
          service,
          startRequest?.sessionId,
          startRequest?.editSessionId,
          startRequest?.editActionId,
          startRequest?.reuseIntegration,
          loginId
        );
        const settled = getPersistentLoginState(service);
        const epochStale = getPersistentLoginEpoch(service) !== startEpoch;
        if (settled.authenticated && settled.loginId === loginId) {
          return null;
        }
        if (isPersistentLoginSuspended()) {
          if (epochStale) await cancelStale(challenge);
          return null;
        }
        if (!epochStale && settled.step?.actionId !== submitted.actionId) {
          return settled.pendingChallenge;
        }
        if (epochStale || isPersistentLoginCancelled(service)) {
          if (!epochStale) {
            // Same attempt, an explicit cancel racing this response (cancel() sets the flag before
            // its own reset runs): stop the spinner this attempt still owns. A stale epoch writes
            // NOTHING - the store already belongs to whatever came after the reset, and stomping it
            // with loading=false/fail() is exactly how a hung start from a stopped container used
            // to close or wedge the replacement attempt's auth modal.
            finishPersistentLoginStep(service, submitted.actionId);
          }
          // Either way the daemon answered a login attempt that no longer has an owner - tear a
          // late challenge down so its daemon login is not left running orphaned.
          // cancelPersistentLogin is idempotent, session-pinned and names this challenge's attempt,
          // so this can never cancel a newer attempt's login.
          await cancelStale(challenge);
          return null;
        }
        if (isPersistentLoginAuthenticatedResponse(challenge)) {
          // Container already authenticated (daemon self-authed from its own volume).
          finishAuthenticated();
          return null;
        }
        if (isPersistentLoginCredentialChallenge(challenge)) {
          if (!applyChallenge(challenge, extractPersistentSessionId(challenge))) return null;
          return challenge;
        }
        // Empty/no-op response: previously stopped the spinner in silence, which is exactly how a
        // login could go invisible forever - nothing else ever re-fetches this attempt's result
        // (diagnostic §3.2, wedge W1). Surface it as a real error instead so the card's existing
        // error alert shows it.
        fail(t('prefill.persistent.errors.noResult'));
        return null;
      } catch (err) {
        const settled = getPersistentLoginState(service);
        const attemptStale = getPersistentLoginEpoch(service) !== startEpoch;
        if (settled.authenticated && settled.loginId === loginId) {
          return null;
        }
        if (!attemptStale && settled.step?.actionId !== submitted.actionId) {
          return settled.pendingChallenge;
        }
        if (isPersistentLoginSuspended() || getPersistentLoginEpoch(service) !== startEpoch) {
          if (getPersistentLoginEpoch(service) !== startEpoch) await cancelStale();
          // The flow this call belonged to was already reset/superseded - its failure (typically
          // the backend's "Login timeout" 400 for a container that has since been stopped) is not
          // the current attempt's failure. Discard it instead of writing a stale error over live
          // state.
          return null;
        }
        if (getPersistentLoginState(service).step?.actionId !== submitted.actionId) {
          await cancelStale();
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
  }, [applyChallenge, fail, finishAuthenticated, messages, service, t]);

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
    // One shared ending for every way a login stops (see endPersistentLogin): it reads the pinned
    // sessionId live rather than from the `stored` snapshot closed over here - a cancel fired from
    // the same synchronous flow that just started a login must still see the id written moments
    // ago - and it skips the round trip when nothing was ever pinned, which is only the brief
    // window before start()'s very first response lands (RC3). Skipping it there is safe rather
    // than a silent no-op: nothing server-side has been told about this attempt yet either, and
    // the store reset inside bumps the login epoch, so a still-hanging start() recognizes its own
    // settlement as stale and discards it.
    const ending = endPersistentLogin(service);
    const epoch = getPersistentLoginEpoch(service);
    const ended = await ending;
    // A newer attempt moved the epoch before this cancel settled; leave its state alone.
    if (getPersistentLoginEpoch(service) !== epoch) return;
    // The reset clears the typed credentials and the store; the failed-cancel error is written after
    // it with no await between, so the reset cannot erase it.
    resetAuthForm();
    if (!ended)
      updatePersistentLoginState(service, (current) => ({
        ...current,
        error: t('prefill.persistent.loginNotCanceled'),
        endReason: 'cancelFailed'
      }));
  }, [resetAuthForm, service, t]);

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
    if (isPersistentLoginSuspended()) return false;
    if (
      getPersistentLoginState(service).pendingChallenge &&
      !ensurePersistentLoginTimeout(service, messages)
    )
      return false;
    const attemptEpoch = getPersistentLoginEpoch(service);
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

      const stage = getAuthStage(challenge.credentialType);
      const credentialsStep =
        stage === 'credentials' ? beginPersistentLoginStep(service, stage) : null;
      if (stage === 'credentials' && !credentialsStep) return false;

      if (challenge.credentialType === 'username') {
        const usernameResult = await submitChallenge(
          challenge,
          username,
          credentialsStep?.actionId
        );
        if (usernameResult.status === 'authenticated') {
          return true;
        }
        if (usernameResult.status !== 'challenge') {
          return false;
        }
        challenge = usernameResult.challenge;
      }

      if (challenge.credentialType === 'password') {
        const passwordResult = await submitChallenge(
          challenge,
          password,
          credentialsStep?.actionId
        );
        if (passwordResult.status === 'authenticated') {
          return true;
        }
        if (passwordResult.status !== 'challenge') {
          return false;
        }
        challenge = passwordResult.challenge;
      }

      if (challenge.credentialType === 'device-confirmation') {
        const confirmationResult = await submitChallenge(challenge, DEVICE_CONFIRMATION_CREDENTIAL);
        return confirmationResult.status === 'authenticated';
      }

      return stored.authenticated;
    } catch (err) {
      if (isPersistentSessionConflictError(err)) {
        // Not a real failure - see the session-conflict handling above; avoid a second, unrelated
        // fail() write here.
        return false;
      }
      if (
        getPersistentLoginEpoch(service) !== attemptEpoch ||
        isPersistentLoginSuspended() ||
        getPersistentLoginState(service).loginDeadline === null
      )
        return false;
      const message = getErrorMessage(err);
      fail(message);
      return false;
    }
  }, [
    authorizationCode,
    emailCode,
    fail,
    messages,
    password,
    start,
    service,
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
    cancel,
    dismissModal,
    resumeModal
  };

  return { state, actions };
}
