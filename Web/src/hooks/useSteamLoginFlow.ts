import { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { getErrorMessage } from '@utils/error';
import type { NotificationVariant } from '../types/operations';
import type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

const REQUEST_TIMEOUT_MS = 5 * 60 * 1000;

interface SteamLoginFlowOptions {
  loginUrl: string;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
  getExtraRequestBody?: () => Record<string, unknown>;
  /**
   * Surfaces the login lifecycle (waiting for sign-in / Steam Guard step / signed in / cancelled /
   * failed) as one universal-notification card, mirroring the Xbox and Epic mapping logins on the
   * Integrations page. Opt-in because this hook is also used by the setup wizard, where the
   * notification bar is not part of the flow. When enabled, submit failures settle this card
   * instead of raising the separate generic error toast.
   */
  loginStatusNotifications?: boolean;
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
  const {
    loginUrl,
    onSuccess,
    onError,
    getExtraRequestBody,
    loginStatusNotifications = false
  } = options;
  const { t } = useTranslation();
  const { addNotification, updateNotification, scheduleAutoDismiss } = useNotifications();

  // Id of the login-status card while a login this hook started is still live. Steam has no
  // backend-driven mapping card to share (unlike Epic/Xbox), so the lifecycle lives in one
  // 'generic' card updated in place; null once the card settled (signed in/cancelled/failed).
  const loginCardIdRef = useRef<string | null>(null);

  const upsertLoginCard = (message: string): void => {
    if (!loginStatusNotifications) {
      return;
    }
    if (loginCardIdRef.current) {
      updateNotification(loginCardIdRef.current, { status: 'running', message });
    } else {
      loginCardIdRef.current = addNotification({
        type: 'generic',
        status: 'running',
        message,
        details: { notificationType: 'info' }
      });
    }
  };

  const settleLoginCard = (
    status: 'completed' | 'failed',
    message: string,
    variant: NotificationVariant,
    cancelled = false
  ): void => {
    const id = loginCardIdRef.current;
    if (!id) {
      return;
    }
    loginCardIdRef.current = null;
    updateNotification(id, { status, message, details: { notificationType: variant, cancelled } });
    scheduleAutoDismiss(id);
  };

  /** Failure surface: settles the login card when one is live, else the plain error toast. */
  const notifyLoginFailure = (message: string): void => {
    if (loginCardIdRef.current) {
      settleLoginCard(
        'failed',
        t('signalr.steamLogin.signInFailed', { errorDetail: message }),
        'error'
      );
      return;
    }
    addNotification({
      type: 'generic',
      status: 'failed',
      message,
      details: { notificationType: 'error' }
    });
  };

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

  // Unmount with a login still live (tab switched away mid-flow): the abort effect above kills the
  // request silently, which would leave the status card spinning forever - settle it as cancelled.
  // cancelled:true renders the card red + XCircle (same contract as Xbox's terminal cancel) while
  // status stays 'completed' so scheduleAutoDismiss still fires.
  useEffect(() => {
    return () => {
      settleLoginCard('completed', t('signalr.steamLogin.signInCancelled'), 'warning', true);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const cancelPendingRequest = () => {
    if (abortController) {
      abortController.abort();
      setAbortController(null);
    }
  };

  const resetAuthForm = () => {
    // A card still live here means the user backed out mid-flow (closed the modal during the
    // Steam Guard step or the mobile-confirmation wait) - success/failure settle the card
    // themselves BEFORE calling this, so this can only be a cancel.
    settleLoginCard('completed', t('signalr.steamLogin.signInCancelled'), 'warning', true);
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

    const willWaitForMobileConfirmation = !needsTwoFactor && !needsEmailCode && !useManualCode;
    if (willWaitForMobileConfirmation) {
      setWaitingForMobileConfirmation(true);
    }
    upsertLoginCard(
      t(
        willWaitForMobileConfirmation
          ? 'signalr.steamLogin.waitingSignIn'
          : 'signalr.steamLogin.signingIn'
      )
    );

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
        notifyLoginFailure('Invalid response from server');
        setLoading(false);
        setWaitingForMobileConfirmation(false);
        return false;
      }

      if (response.ok) {
        if (result.sessionExpired) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          setUseManualCode(true);
          upsertLoginCard(t('signalr.steamLogin.waitingGuardCode'));
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
          upsertLoginCard(t('signalr.steamLogin.waitingGuardCode'));
          return false;
        }

        if (result.requiresEmailCode) {
          setWaitingForMobileConfirmation(false);
          setNeedsEmailCode(true);
          upsertLoginCard(t('signalr.steamLogin.waitingGuardCode'));
          return false;
        }

        if (result.success) {
          // Settled BEFORE resetAuthForm below, which treats a still-live card as a cancel.
          settleLoginCard('completed', t('signalr.steamLogin.signedIn', { username }), 'success');
          onSuccess?.(result.message || `Successfully authenticated as ${username}`);
          resetAuthForm();
          return true;
        }

        setWaitingForMobileConfirmation(false);
        notifyLoginFailure(result.message || 'Authentication failed');
        return false;
      }

      setWaitingForMobileConfirmation(false);
      setLoading(false);
      const errorMsg = result.message || result.error || 'Authentication failed';
      notifyLoginFailure(errorMsg);
      resetAuthForm();
      onError?.(errorMsg);
      return false;
    } catch (err: unknown) {
      if (!(err instanceof Error && err.name === 'AbortError')) {
        setWaitingForMobileConfirmation(false);
        setLoading(false);
        const errorMessage = getErrorMessage(err);
        notifyLoginFailure(errorMessage);
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
