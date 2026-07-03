import { useCallback, useState } from 'react';
import ApiService from '@services/api.service';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import type { SteamAuthActions, SteamLoginFlowState } from './useSteamAuthentication';
import {
  applyPersistentLoginChallenge,
  getPersistentLoginStartPromise,
  isPersistentLoginAuthenticatedResponse,
  isPersistentLoginCancelled,
  isPersistentLoginCredentialChallenge,
  resetPersistentLoginState,
  setPersistentLoginCancelled,
  setPersistentLoginStartPromise,
  updatePersistentLoginState,
  usePersistentLoginStoreState
} from '@components/features/management/schedules/scheduled-prefill/persistentLoginStore';

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
// usePersistentLoginChallengeSignalR); this GET long-poll is the fallback for when SignalR is
// disconnected, so it can afford a slower interval than before (matches
// waitForPersistentContainerAuth's 60s default for the same "SignalR is primary, this is the
// safety net" reasoning).
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

  const stored = usePersistentLoginStoreState(service);

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

  const setNeedsTwoFactor = useCallback(
    (value: boolean) => {
      updatePersistentLoginState(service, (current) => ({ ...current, needsTwoFactor: value }));
    },
    [service]
  );

  const setWaitingForMobileConfirmation = useCallback(
    (value: boolean) => {
      updatePersistentLoginState(service, (current) => ({
        ...current,
        waitingForMobileConfirmation: value
      }));
    },
    [service]
  );

  const applyChallenge = useCallback(
    (challenge: CredentialChallenge) => {
      applyPersistentLoginChallenge(service, challenge);
    },
    [service]
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
    const response = await ApiService.getPersistentChallenge(service, timeoutSeconds);
    if (isPersistentLoginAuthenticatedResponse(response)) {
      return { status: 'authenticated' };
    }
    if (isPersistentLoginCredentialChallenge(response)) {
      return { status: 'challenge', challenge: response };
    }
    // Empty/204: the long-poll timed out with no new challenge yet (e.g. waiting
    // for the user to confirm a device code). Keep polling instead of erroring.
    return { status: 'pending' };
  }, [service, timeoutSeconds]);

  const submitChallenge = useCallback(
    async (challenge: CredentialChallenge, credential: string): Promise<PollResult> => {
      setLoading(true);
      setError(null);
      await ApiService.providePersistentCredential(service, challenge, credential);

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

      if (result.status === 'challenge') {
        applyChallenge(result.challenge);
      }
      setLoading(false);
      return result;
    },
    [applyChallenge, finishAuthenticated, pollForResult, service, setError, setLoading]
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
        applyChallenge(result.challenge);
      }
      setLoading(false);
      return result;
    } catch (err) {
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
      setPersistentLoginCancelled(service, false);
      setLoading(true);
      setError(null);

      try {
        const challenge = await ApiService.startPersistentLogin(service);
        if (isPersistentLoginCancelled(service)) {
          setLoading(false);
          return null;
        }
        if (isPersistentLoginAuthenticatedResponse(challenge)) {
          // Container already authenticated (daemon self-authed from its own volume).
          finishAuthenticated();
          return null;
        }
        if (isPersistentLoginCredentialChallenge(challenge)) {
          applyChallenge(challenge);
          setLoading(false);
          return challenge;
        }
        // No actionable challenge (empty/pending) - stop the spinner.
        setLoading(false);
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
  }, [applyChallenge, fail, finishAuthenticated, service, setError, setLoading]);

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
      await ApiService.cancelPersistentLogin(service);
    } finally {
      resetAuthForm();
    }
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
    if (stored.needsTwoFactor) {
      if (!twoFactorCode.trim()) {
        fail('Please enter your 2FA code');
        return false;
      }
      return submit(twoFactorCode);
    }

    if (stored.needsEmailCode) {
      if (!emailCode.trim()) {
        fail('Please enter your email verification code');
        return false;
      }
      return submit(emailCode);
    }

    if (stored.needsAuthorizationCode) {
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
    stored.needsAuthorizationCode,
    stored.needsEmailCode,
    stored.needsTwoFactor,
    stored.pendingChallenge,
    submit,
    submitChallenge,
    twoFactorCode,
    username
  ]);

  const state: PersistentPrefillAuthState = {
    loading: stored.loading,
    needsTwoFactor: stored.needsTwoFactor,
    needsEmailCode: stored.needsEmailCode,
    waitingForMobileConfirmation: stored.waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode,
    needsAuthorizationCode: stored.needsAuthorizationCode,
    authorizationUrl: stored.authorizationUrl,
    authorizationCode,
    needsDeviceCode: stored.needsDeviceCode,
    deviceUserCode: stored.deviceUserCode,
    deviceVerificationUri: stored.deviceVerificationUri,
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
