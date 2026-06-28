import { useState, useEffect } from 'react';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

const REQUEST_TIMEOUT_MS = 5 * 60 * 1000;

interface SteamLoginFlowOptions {
  loginUrl: string;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
  getExtraRequestBody?: () => Record<string, unknown>;
}

interface SteamLoginApiResult {
  sessionExpired?: boolean;
  requiresTwoFactor?: boolean;
  requiresEmailCode?: boolean;
  success?: boolean;
  message?: string;
  error?: string;
}

function buildSteamOnlyState(
  loading: boolean,
  needsTwoFactor: boolean,
  needsEmailCode: boolean,
  waitingForMobileConfirmation: boolean,
  useManualCode: boolean,
  username: string,
  password: string,
  twoFactorCode: string,
  emailCode: string
): SteamLoginFlowState {
  return {
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode,
    needsAuthorizationCode: false,
    authorizationUrl: '',
    authorizationCode: '',
    needsDeviceCode: false,
    deviceUserCode: '',
    deviceVerificationUri: ''
  };
}

export function useSteamLoginFlow(options: SteamLoginFlowOptions) {
  const { loginUrl, onSuccess, onError, getExtraRequestBody } = options;
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  useEffect(() => {
    return () => {
      if (abortController) {
        abortController.abort();
      }
    };
  }, [abortController]);

  const cancelPendingRequest = () => {
    if (abortController) {
      abortController.abort();
      setAbortController(null);
    }
  };

  const resetAuthForm = () => {
    cancelPendingRequest();
    setUsername('');
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setLoading(false);
  };

  const handleAuthenticate = async (): Promise<boolean> => {
    if (!username.trim() || !password.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter both username and password',
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (needsEmailCode && !emailCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter your email verification code',
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (useManualCode && !twoFactorCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter your 2FA code',
        details: { notificationType: 'error' }
      });
      return false;
    }

    setLoading(true);

    const controller = new AbortController();
    setAbortController(controller);

    if (!needsTwoFactor && !needsEmailCode && !useManualCode) {
      setWaitingForMobileConfirmation(true);
    }

    let requestTimeout: ReturnType<typeof setTimeout> | null = null;
    try {
      requestTimeout = setTimeout(() => {
        controller.abort();
      }, REQUEST_TIMEOUT_MS);

      const response = await fetch(
        loginUrl,
        ApiService.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            username,
            password,
            twoFactorCode: needsTwoFactor || useManualCode ? twoFactorCode : undefined,
            emailCode: needsEmailCode ? emailCode : undefined,
            allowMobileConfirmation: !useManualCode,
            ...getExtraRequestBody?.()
          }),
          signal: controller.signal
        })
      );

      let result: SteamLoginApiResult;
      try {
        result = await response.json();
      } catch (_jsonError) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Invalid response from server',
          details: { notificationType: 'error' }
        });
        setLoading(false);
        setWaitingForMobileConfirmation(false);
        return false;
      }

      if (response.ok) {
        if (result.sessionExpired) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          setUseManualCode(true);
          addNotification({
            type: 'generic',
            status: 'failed',
            message: 'Mobile confirmation timed out. Please enter your 2FA code instead.',
            details: { notificationType: 'warning' }
          });
          return false;
        }

        if (result.requiresTwoFactor) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          return false;
        }

        if (result.requiresEmailCode) {
          setWaitingForMobileConfirmation(false);
          setNeedsEmailCode(true);
          return false;
        }

        if (result.success) {
          onSuccess?.(result.message || `Successfully authenticated as ${username}`);
          resetAuthForm();
          return true;
        }

        setWaitingForMobileConfirmation(false);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: result.message || 'Authentication failed',
          details: { notificationType: 'error' }
        });
        return false;
      }

      setWaitingForMobileConfirmation(false);
      setLoading(false);
      const errorMsg = result.message || result.error || 'Authentication failed';
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMsg,
        details: { notificationType: 'error' }
      });
      resetAuthForm();
      onError?.(errorMsg);
      return false;
    } catch (err: unknown) {
      if (!(err instanceof Error && err.name === 'AbortError')) {
        setWaitingForMobileConfirmation(false);
        setLoading(false);
        const errorMessage = err instanceof Error ? err.message : 'Authentication failed';
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMessage,
          details: { notificationType: 'error' }
        });
        resetAuthForm();
        onError?.(errorMessage);
      }
      return false;
    } finally {
      if (requestTimeout) {
        clearTimeout(requestTimeout);
      }
      setLoading(false);
      setAbortController(null);
    }
  };

  const state = buildSteamOnlyState(
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode
  );

  const actions: SteamAuthActions = {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    setUseManualCode,
    setNeedsTwoFactor,
    setWaitingForMobileConfirmation,
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setAuthorizationCode: () => {},
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions };
}
