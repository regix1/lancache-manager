import { useCallback, useEffect, useRef, useState } from 'react';
import ApiService from '@services/api.service';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import type { SteamAuthActions, SteamLoginFlowState } from './useSteamAuthentication';

interface UsePersistentPrefillAuthOptions {
  service?: PersistentPrefillServiceId;
  timeoutSeconds?: number;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

interface PersistentPrefillAuthState extends SteamLoginFlowState {
  error: string | null;
  authenticated: boolean;
}

interface PersistentPrefillAuthActions extends SteamAuthActions {
  start: () => Promise<CredentialChallenge | null>;
  submit: (credential: string) => Promise<boolean>;
  cancel: () => Promise<void>;
}

interface PersistentPrefillAuthResult {
  state: PersistentPrefillAuthState;
  actions: PersistentPrefillAuthActions;
}

type PollResult =
  | { status: 'authenticated' }
  | { status: 'challenge'; challenge: CredentialChallenge };

const DEFAULT_TIMEOUT_SECONDS = 30;
const DEVICE_CONFIRMATION_CREDENTIAL = 'confirm';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isAuthenticatedResponse(response: unknown): boolean {
  return (
    response === 'authenticated' ||
    (isRecord(response) && response.authenticated === true) ||
    (isRecord(response) && response.status === 'authenticated')
  );
}

function isCredentialChallenge(response: unknown): response is CredentialChallenge {
  return isRecord(response) && typeof response.credentialType === 'string';
}

export function usePersistentPrefillAuth(
  options: UsePersistentPrefillAuthOptions = {}
): PersistentPrefillAuthResult {
  const {
    service = 'Steam',
    timeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    onSuccess,
    onError
  } = options;

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [authenticated, setAuthenticated] = useState(false);
  const [pendingChallenge, setPendingChallenge] = useState<CredentialChallenge | null>(null);
  const cancelledRef = useRef(false);

  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');
  const [needsAuthorizationCode, setNeedsAuthorizationCode] = useState(false);
  const [authorizationUrl, setAuthorizationUrl] = useState('');
  const [needsDeviceCode, setNeedsDeviceCode] = useState(false);
  const [deviceUserCode, setDeviceUserCode] = useState('');
  const [deviceVerificationUri, setDeviceVerificationUri] = useState('');

  useEffect(() => {
    return () => {
      cancelledRef.current = true;
    };
  }, []);

  const clearChallengeFlags = useCallback(() => {
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setNeedsAuthorizationCode(false);
    setAuthorizationUrl('');
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
  }, []);

  const applyChallenge = useCallback(
    (challenge: CredentialChallenge) => {
      setPendingChallenge(challenge);
      clearChallengeFlags();

      switch (challenge.credentialType) {
        case '2fa':
          setNeedsTwoFactor(true);
          break;
        case 'steamguard':
          setNeedsEmailCode(true);
          break;
        case 'authorization-url':
          setNeedsAuthorizationCode(true);
          setAuthorizationUrl(challenge.authUrl ?? '');
          break;
        case 'device-confirmation':
          setWaitingForMobileConfirmation(true);
          break;
        case 'device-code':
          setNeedsDeviceCode(true);
          setDeviceUserCode(challenge.userCode ?? '');
          setDeviceVerificationUri(challenge.verificationUri ?? challenge.authUrl ?? '');
          break;
      }
    },
    [clearChallengeFlags]
  );

  const finishAuthenticated = useCallback(() => {
    setAuthenticated(true);
    setPendingChallenge(null);
    clearChallengeFlags();
    setLoading(false);
    onSuccess?.();
  }, [clearChallengeFlags, onSuccess]);

  const fail = useCallback(
    (message: string) => {
      setError(message);
      setLoading(false);
      onError?.(message);
    },
    [onError]
  );

  const pollForResult = useCallback(async (): Promise<PollResult> => {
    const response = await ApiService.getPersistentChallenge(service, timeoutSeconds);
    if (isAuthenticatedResponse(response)) {
      return { status: 'authenticated' };
    }
    if (isCredentialChallenge(response)) {
      return { status: 'challenge', challenge: response };
    }
    throw new Error('Unexpected persistent login challenge response');
  }, [service, timeoutSeconds]);

  const submitChallenge = useCallback(
    async (challenge: CredentialChallenge, credential: string): Promise<PollResult> => {
      setLoading(true);
      setError(null);
      await ApiService.providePersistentCredential(service, challenge, credential);

      const result = await pollForResult();
      if (cancelledRef.current) {
        setLoading(false);
        return result;
      }

      if (result.status === 'authenticated') {
        finishAuthenticated();
        return result;
      }

      applyChallenge(result.challenge);
      setLoading(false);
      return result;
    },
    [applyChallenge, finishAuthenticated, pollForResult, service]
  );

  const submit = useCallback(
    async (credential: string): Promise<boolean> => {
      if (!pendingChallenge) {
        fail('No pending credential challenge');
        return false;
      }

      try {
        const result = await submitChallenge(pendingChallenge, credential);
        return result.status === 'authenticated';
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to submit credential';
        fail(message);
        return false;
      }
    },
    [fail, pendingChallenge, submitChallenge]
  );

  const start = useCallback(async (): Promise<CredentialChallenge | null> => {
    cancelledRef.current = false;
    setLoading(true);
    setError(null);
    setAuthenticated(false);

    try {
      const challenge = await ApiService.startPersistentLogin(service);
      if (cancelledRef.current) {
        setLoading(false);
        return null;
      }
      applyChallenge(challenge);
      setLoading(false);
      return challenge;
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to start persistent login';
      fail(message);
      return null;
    }
  }, [applyChallenge, fail, service]);

  const resetAuthForm = useCallback(() => {
    setUsername('');
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setAuthorizationCode('');
    setUseManualCode(false);
    setPendingChallenge(null);
    setError(null);
    setAuthenticated(false);
    clearChallengeFlags();
    setLoading(false);
  }, [clearChallengeFlags]);

  const cancel = useCallback(async (): Promise<void> => {
    cancelledRef.current = true;
    setLoading(false);
    try {
      await ApiService.cancelPersistentLogin(service);
    } finally {
      resetAuthForm();
    }
  }, [resetAuthForm, service]);

  const cancelPendingRequest = useCallback(() => {
    void cancel();
  }, [cancel]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (needsTwoFactor) {
      if (!twoFactorCode.trim()) {
        fail('Please enter your 2FA code');
        return false;
      }
      return submit(twoFactorCode);
    }

    if (needsEmailCode) {
      if (!emailCode.trim()) {
        fail('Please enter your email verification code');
        return false;
      }
      return submit(emailCode);
    }

    if (needsAuthorizationCode) {
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
      let challenge = pendingChallenge ?? (await start());
      if (!challenge) {
        return false;
      }

      if (challenge.credentialType === 'username') {
        const usernameResult = await submitChallenge(challenge, username);
        if (usernameResult.status === 'authenticated') {
          return true;
        }
        challenge = usernameResult.challenge;
      }

      if (challenge?.credentialType === 'password') {
        const passwordResult = await submitChallenge(challenge, password);
        if (passwordResult.status === 'authenticated') {
          return true;
        }
        challenge = passwordResult.challenge;
      }

      if (challenge?.credentialType === 'device-confirmation') {
        const confirmationResult = await submitChallenge(challenge, DEVICE_CONFIRMATION_CREDENTIAL);
        return confirmationResult.status === 'authenticated';
      }

      return authenticated;
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to authenticate';
      fail(message);
      return false;
    }
  }, [
    authenticated,
    authorizationCode,
    emailCode,
    fail,
    needsAuthorizationCode,
    needsEmailCode,
    needsTwoFactor,
    password,
    pendingChallenge,
    start,
    submit,
    submitChallenge,
    twoFactorCode,
    username
  ]);

  const state: PersistentPrefillAuthState = {
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode,
    needsAuthorizationCode,
    authorizationUrl,
    authorizationCode,
    needsDeviceCode,
    deviceUserCode,
    deviceVerificationUri,
    error,
    authenticated
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
    cancel
  };

  return { state, actions };
}
