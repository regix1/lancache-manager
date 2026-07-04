import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService, {
  type PersistentSessionConflictInfo,
  type PersistentSessionNotFoundInfo
} from '@services/api.service';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import type { SteamAuthActions, SteamLoginFlowState } from './useSteamAuthentication';
import {
  applyPersistentLoginChallenge,
  derivePersistentChallengeFlags,
  extractPersistentSessionId,
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
// `.cause` on the thrown error - that's the structural signal checked below. The message-prefix
// check is kept only as a defensive fallback (e.g. a proxy/CDN 404 with no JSON body would still
// carry api.service's default `HTTP 404: ` format), without relying on handleResponse's global
// error semantics either way.
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
  return /^HTTP 404\b/.test(error.message);
}

// RC3 fix (session 20260703-221336-2070027597): the challenge GET and provide-credential 409
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

  // TEMP DIAGNOSTIC: trace the persistent-container login flow. Filter the browser console by
  // "[SteamAuthDebug]" during a login to see why the modal is bypassed. Remove once resolved.
  const dbg = useCallback(
    (event: string, extra?: Record<string, unknown>) => {
      // eslint-disable-next-line no-console
      console.log('[SteamAuthDebug] persistent', service, event, {
        storedSessionId: stored.sessionId,
        authenticated: stored.authenticated,
        hasPendingChallenge: stored.pendingChallenge !== null,
        pendingType: stored.pendingChallenge?.credentialType ?? null,
        ...extra
      });
    },
    [service, stored.sessionId, stored.authenticated, stored.pendingChallenge]
  );

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
      dbg('applyChallenge()', { credentialType: challenge.credentialType, sessionId });
      applyPersistentLoginChallenge(service, challenge, sessionId);
    },
    [service, dbg]
  );

  // Single choke point for the RC3 409 conflict (session 20260703-221336-2070027597): both
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
    dbg(
      'finishAuthenticated() -> marking authenticated + closing modal (login treated as already done)'
    );
    resetPersistentLoginState(service);
    updatePersistentLoginState(service, (current) => ({ ...current, authenticated: true }));
    onSuccess?.();
  }, [service, onSuccess, dbg]);

  const fail = useCallback(
    (message: string) => {
      dbg('fail()', { message });
      updatePersistentLoginState(service, (current) => ({
        ...current,
        error: message,
        loading: false
      }));
      onError?.(message);
    },
    [service, onError, dbg]
  );

  const pollForResult = useCallback(async (): Promise<PollResult> => {
    try {
      dbg('pollForResult() -> GET challenge', { pinnedSessionId: stored.sessionId ?? '' });
      const response = await ApiService.getPersistentChallenge(
        service,
        timeoutSeconds,
        stored.sessionId ?? ''
      );
      if (isPersistentLoginAuthenticatedResponse(response)) {
        dbg('pollForResult() <- AUTHENTICATED');
        return { status: 'authenticated' };
      }
      if (isPersistentLoginCredentialChallenge(response)) {
        dbg('pollForResult() <- challenge', { credentialType: response.credentialType });
        return { status: 'challenge', challenge: response };
      }
      // Empty/204: the long-poll timed out with no new challenge yet (e.g. waiting
      // for the user to confirm a device code). Keep polling instead of erroring.
      dbg('pollForResult() <- pending (204/empty)');
      return { status: 'pending' };
    } catch (err) {
      if (isPersistentSessionConflictError(err)) {
        handleSessionConflict(err);
      }
      throw err;
    }
  }, [handleSessionConflict, service, stored.sessionId, timeoutSeconds, dbg]);

  const submitChallenge = useCallback(
    async (challenge: CredentialChallenge, credential: string): Promise<PollResult> => {
      dbg('submitChallenge() -> provide credential', {
        credentialType: challenge.credentialType,
        pinnedSessionId: stored.sessionId ?? ''
      });
      setLoading(true);
      setError(null);
      try {
        await ApiService.providePersistentCredential(
          service,
          challenge,
          credential,
          stored.sessionId ?? ''
        );
      } catch (err) {
        if (isPersistentSessionConflictError(err)) {
          handleSessionConflict(err);
        }
        throw err;
      }

      let result = await pollForResult();
      while (result.status === 'pending' && !isPersistentLoginCancelled(service)) {
        result = await pollForResult();
      }
      if (isPersistentLoginCancelled(service)) {
        setLoading(false);
        return result;
      }

      if (result.status === 'authenticated') {
        finishAuthenticated();
        return result;
      }

      dbg('submitChallenge() <- result', { status: result.status });
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
      setLoading,
      stored.sessionId,
      dbg
    ]
  );

  const submit = useCallback(
    async (credential: string): Promise<boolean> => {
      if (!stored.pendingChallenge) {
        fail('No pending credential challenge');
        return false;
      }

      try {
        const result = await submitChallenge(stored.pendingChallenge, credential);
        return result.status === 'authenticated';
      } catch (err) {
        if (isPersistentSessionConflictError(err)) {
          // Already reset (with its own translated message) inside submitChallenge/pollForResult's
          // own catch - a generic fail() here would stomp that reset with a second, unrelated write.
          return false;
        }
        const message = err instanceof Error ? err.message : 'Failed to submit credential';
        fail(message);
        return false;
      }
    },
    [fail, stored.pendingChallenge, submitChallenge]
  );

  const poll = useCallback(async (): Promise<PollResult> => {
    try {
      setLoading(true);
      setError(null);
      const result = await pollForResult();
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
      const message = err instanceof Error ? err.message : 'Failed to poll persistent login';
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
      dbg('start() -> POST startPersistentLogin');
      setPersistentLoginCancelled(service, false);
      setLoading(true);
      setError(null);

      try {
        const challenge = await ApiService.startPersistentLogin(service);
        dbg('start() <- startPersistentLogin response', {
          isAuthenticated: isPersistentLoginAuthenticatedResponse(challenge),
          isChallenge: isPersistentLoginCredentialChallenge(challenge),
          responseCredentialType:
            (challenge as { credentialType?: string })?.credentialType ?? null,
          rawResponse: challenge
        });
        if (isPersistentLoginCancelled(service)) {
          dbg('start(): cancelled during round-trip -> bail');
          setLoading(false);
          return null;
        }
        if (isPersistentLoginAuthenticatedResponse(challenge)) {
          // Container already authenticated (daemon self-authed from its own volume).
          dbg(
            'start(): backend says ALREADY AUTHENTICATED -> finishAuthenticated (THIS bypasses the modal)'
          );
          finishAuthenticated();
          return null;
        }
        if (isPersistentLoginCredentialChallenge(challenge)) {
          dbg('start(): got a real challenge -> applyChallenge + return it', {
            credentialType: challenge.credentialType
          });
          applyChallenge(challenge, extractPersistentSessionId(challenge));
          setLoading(false);
          return challenge;
        }
        // Empty/no-op response: previously stopped the spinner in silence, which is exactly how a
        // login could go invisible forever - nothing else ever re-fetches this attempt's result
        // (diagnostic §3.2, wedge W1). Surface it as a real error instead so the card's existing
        // error alert shows it.
        fail(
          'Persistent login did not return a challenge or authentication result. Please try again.'
        );
        return null;
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to start persistent login';
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
  }, [applyChallenge, fail, finishAuthenticated, service, setError, setLoading, dbg]);

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
    try {
      // No sessionId is pinned yet only when cancel is clicked in the brief window before start()'s
      // very first response ever lands (RC3, session 20260703-221336-2070027597) - there is no
      // known daemon session id to send, and the cancel flag set above already makes start() bail
      // out locally the moment it resolves, so skipping the round-trip here is safe rather than a
      // silent no-op: nothing server-side has been told about this login attempt yet either.
      if (stored.sessionId) {
        await ApiService.cancelPersistentLogin(service, stored.sessionId);
      }
    } finally {
      resetAuthForm();
    }
  }, [resetAuthForm, service, setLoading, stored.sessionId]);

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
    dbg('handleAuthenticate() called', {
      flags,
      hasUsername: username.trim().length > 0,
      hasPassword: password.trim().length > 0
    });

    if (flags.needsTwoFactor) {
      if (!twoFactorCode.trim()) {
        fail('Please enter your 2FA code');
        return false;
      }
      return submit(twoFactorCode);
    }

    if (flags.needsEmailCode) {
      if (!emailCode.trim()) {
        fail('Please enter your email verification code');
        return false;
      }
      return submit(emailCode);
    }

    if (flags.needsAuthorizationCode) {
      if (!authorizationCode.trim()) {
        fail('Please enter the authorization code');
        return false;
      }
      return submit(authorizationCode);
    }

    if (!username.trim() || !password.trim()) {
      fail('Please enter both username and password');
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
      if (isPersistentSessionConflictError(err)) {
        // Already reset (with its own translated message) inside submitChallenge/pollForResult's
        // own catch - avoid a second, unrelated fail() write here.
        return false;
      }
      const message = err instanceof Error ? err.message : 'Failed to authenticate';
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
    twoFactorCode,
    username,
    dbg
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
