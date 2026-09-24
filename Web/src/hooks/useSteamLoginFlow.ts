import { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from './useErrorHandler';
import { getErrorMessage } from '@utils/error';
import { ApiError } from '@services/apiError';
import { createUuid } from '@utils/uuid';
import { getIntegrationReasonKey, type IntegrationAccess } from '../types';
import { STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS } from './loginAttemptTimeout';
import type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

interface SteamLoginFlowOptions {
  integration?: {
    identity: string;
    access: IntegrationAccess | null;
    refresh: () => Promise<void>;
  };
  loginUrl: string;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
  getExtraRequestBody?: () => Record<string, unknown>;
}

interface SteamLoginApiResult {
  attemptId?: string;
  expiresAtUtc?: string;
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
  emailCode: string,
  error: string | null
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
    error,
    needsAuthorizationCode: false,
    authorizationUrl: '',
    authorizationCode: '',
    needsDeviceCode: false,
    deviceUserCode: '',
    deviceVerificationUri: ''
  };
}

export function useSteamLoginFlow(options: SteamLoginFlowOptions) {
  const { loginUrl, onSuccess, onError, getExtraRequestBody, integration } = options;
  const { t } = useTranslation();
  const identityRef = useRef(integration?.identity);
  identityRef.current = integration?.identity;
  const formIdentityRef = useRef(integration?.identity);
  const formCurrent = formIdentityRef.current === integration?.identity;
  const requestRef = useRef(0);
  const attemptRef = useRef<string | null>(null);
  const cancelledAttemptRef = useRef<string | null>(null);
  const busyRef = useRef(false);
  const [attemptId, setAttemptId] = useState<string | null>(null);
  const accessUnavailable = Boolean(integration && (!formCurrent || integration.access === null));
  const canAuthenticate =
    formCurrent &&
    (!integration ||
      integration.access?.canSignIn === true ||
      integration.access?.canRecover === true ||
      (integration.access?.canCancel === true && integration.access.attemptId === attemptId));
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();

  const notifyLoginFailure = (message: string): void => {
    notifyError(
      t('common.errors.signInFailed', { platform: t('prefill.persistent.services.steam') }),
      message
    );
  };

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [abortController, setAbortController] = useState<AbortController | null>(null);
  // When the phone-approval wait gives up, so the modal counts down the same window that will end
  // it instead of running a clock of its own. Null the rest of the time, which is the truth: the
  // Steam Guard step waits on the person, and checking a code it hands over is done in seconds.
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);

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
    requestRef.current += 1;
    busyRef.current = false;
    setLoading(false);
    if (abortController) {
      abortController.abort();
      setAbortController(null);
    }
  };

  const resetAuthForm = () => {
    if (attemptRef.current) cancelledAttemptRef.current = attemptRef.current;
    attemptRef.current = null;
    setAttemptId(null);
    cancelPendingRequest();
    setError(null);
    // The account name survives, because it was almost never the thing that was wrong. Every path
    // that lands here - a refused password, a timeout, a cancel, a close - leaves the person
    // wanting to try the same account again, and retyping it is pure friction. The password does
    // not survive: it is the field that has to be entered again anyway.
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setLoading(false);
  };

  const cancelLogin = () => {
    if (!formCurrent || identityRef.current !== integration?.identity) return;
    const cancelled = attemptId;
    if (cancelledAttemptRef.current === cancelled) cancelledAttemptRef.current = null;
    if (!integration || !cancelled) return;
    void ApiService.cancelSteamLogin(cancelled)
      .catch((error: unknown) => {
        console.warn('Steam sign-in cancellation was not acknowledged:', getErrorMessage(error));
      })
      .finally(() => {
        void integration.refresh();
      });
  };

  useEffect(() => {
    formIdentityRef.current = integration?.identity;
    requestRef.current += 1;
    busyRef.current = false;
    attemptRef.current = null;
    cancelledAttemptRef.current = null;
    setAttemptId(null);
    setUsername('');
    resetAuthForm();
    return () => {
      requestRef.current += 1;
    };
    // The identity, not an authority refresh during this attempt, owns the form lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [integration?.identity]);

  const handleAuthenticate = async (): Promise<boolean> => {
    if (!canAuthenticate || identityRef.current !== integration?.identity || busyRef.current)
      return false;
    const continuation = needsTwoFactor || needsEmailCode || useManualCode;
    if (
      integration &&
      (continuation
        ? !attemptRef.current
        : integration.access?.canSignIn !== true && integration.access?.canRecover !== true)
    ) {
      setError(
        accessUnavailable || (continuation && !attemptRef.current)
          ? t('errors.integration.statusUnavailable')
          : t(getIntegrationReasonKey(integration.access?.ownershipReason))
      );
      return false;
    }
    if (!username.trim() || !password.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('modals.steamAuth.errors.credentialsRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (needsEmailCode && !emailCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('modals.steamAuth.errors.emailCodeRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (useManualCode && !twoFactorCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('modals.steamAuth.errors.twoFactorRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    // A fresh attempt starts here, so the last one's failure stops being the current answer.
    setError(null);
    setLoading(true);
    busyRef.current = true;
    const request = ++requestRef.current;
    const identity = integration?.identity;
    const current = () => requestRef.current === request && identityRef.current === identity;
    const submittedAttempt = integration ? (attemptRef.current ?? createUuid()) : null;
    cancelledAttemptRef.current = null;
    attemptRef.current = submittedAttempt;
    setAttemptId(submittedAttempt);

    const controller = new AbortController();
    setAbortController(controller);

    const willWaitForMobileConfirmation = !needsTwoFactor && !needsEmailCode && !useManualCode;
    if (willWaitForMobileConfirmation) {
      setWaitingForMobileConfirmation(true);
    }
    let requestTimeout: ReturnType<typeof setTimeout> | null = null;
    let timedOut = false;
    try {
      requestTimeout = setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS);
      // Only the phone-approval wait gets a countdown drawn over it. Every request runs under the
      // same abort timer, but sending a Steam Guard code takes about two seconds, and putting a
      // two-minute clock over those two seconds made the line appear and vanish on every submit,
      // sliding the centred panel each way. A wait that ends in seconds does not need a countdown.
      // The timer arms here, after the click, never while a field is being filled, so a short
      // window cannot cut anyone off mid-typing.
      if (willWaitForMobileConfirmation) {
        setLoginDeadline(Date.now() + STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS);
      }

      const response = await fetch(
        loginUrl,
        ApiService.getJsonFetchOptions(
          {
            username,
            password,
            twoFactorCode: needsTwoFactor || useManualCode ? twoFactorCode : undefined,
            emailCode: needsEmailCode ? emailCode : undefined,
            allowMobileConfirmation: !useManualCode,
            ...getExtraRequestBody?.(),
            ...(integration
              ? {
                  attemptId: submittedAttempt,
                  recover: !continuation && integration.access?.canRecover === true
                }
              : {})
          },
          { method: 'POST', signal: controller.signal }
        )
      );
      if (!current()) return false;

      let refusal: ApiError | null = null;
      if (!response.ok) {
        try {
          await ApiService.handleResponse(response.clone());
        } catch (error: unknown) {
          if (!(error instanceof ApiError)) throw error;
          refusal = error;
        }
        if (!current()) return false;
      }

      let result: SteamLoginApiResult;
      try {
        result = await response.json();
        if (!current()) return false;
      } catch (_jsonError) {
        const invalidResponse = t('modals.steamAuth.errors.invalidServerResponse');
        notifyLoginFailure(invalidResponse);
        setError(invalidResponse);
        setLoading(false);
        setWaitingForMobileConfirmation(false);
        return false;
      }

      if (response.ok) {
        if (result.attemptId) {
          attemptRef.current = result.attemptId;
          setAttemptId(result.attemptId);
        }
        if (result.expiresAtUtc) setLoginDeadline(Date.parse(result.expiresAtUtc));
        if (result.sessionExpired) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          setUseManualCode(true);
          addNotification({
            type: 'generic',
            status: 'failed',
            message: t('modals.steamAuth.errors.mobileConfirmationTimedOut'),
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
          attemptRef.current = null;
          cancelledAttemptRef.current = null;
          onSuccess?.(t('modals.steamAuth.success.authenticatedAs', { username }));
          resetAuthForm();
          return true;
        }

        setWaitingForMobileConfirmation(false);
        const refused = t('modals.steamAuth.errors.authenticationFailed');
        notifyLoginFailure(refused);
        setError(refused);
        return false;
      }

      setWaitingForMobileConfirmation(false);
      setLoading(false);
      if (result.attemptId) {
        attemptRef.current = result.attemptId;
        setAttemptId(result.attemptId);
        setNeedsTwoFactor(true);
        setUseManualCode(true);
        if (result.expiresAtUtc) setLoginDeadline(Date.parse(result.expiresAtUtc));
        if (refusal?.body?.stageKey) setError(t(refusal.body.stageKey, refusal.body.context ?? {}));
        return false;
      }
      const errorMsg = refusal?.body?.stageKey
        ? t(refusal.body.stageKey, refusal.body.context ?? {})
        : t('modals.steamAuth.errors.authenticationFailed');
      notifyLoginFailure(errorMsg);
      resetAuthForm();
      // After the reset, which clears the previous attempt's error along with the typed
      // credentials. A wrong password lands here, and this is the line the modal shows.
      setError(errorMsg);
      onError?.(errorMsg);
      return false;
    } catch (err: unknown) {
      if (!current()) return false;
      if (!(err instanceof Error && err.name === 'AbortError')) {
        setWaitingForMobileConfirmation(false);
        setLoading(false);
        const errorMessage =
          err instanceof ApiError && err.body?.stageKey
            ? t(err.body.stageKey, err.body.context ?? {})
            : t('modals.steamAuth.errors.authenticationFailed');
        notifyLoginFailure(errorMessage);
        resetAuthForm();
        // Set after the reset, same as the refused-credentials path above.
        setError(errorMessage);
        onError?.(errorMessage);
      } else if (timedOut) {
        // Leave the phone-approval screen the same way a refusal does, or the panel keeps saying
        // it is waiting for an approval that can no longer arrive, with the reason underneath it.
        setWaitingForMobileConfirmation(false);
        const timedOutMessage = t('modals.steamAuth.errors.attemptTimedOut');
        setError(timedOutMessage);
        notifyLoginFailure(timedOutMessage);
      }
      return false;
    } finally {
      if (requestTimeout) {
        clearTimeout(requestTimeout);
      }
      if (current()) {
        busyRef.current = false;
        setLoading(false);
        setAbortController(null);
      }
      if (identityRef.current === integration?.identity) void integration?.refresh();
    }
  };

  const state = buildSteamOnlyState(
    formCurrent && loading,
    formCurrent && needsTwoFactor,
    formCurrent && needsEmailCode,
    formCurrent && waitingForMobileConfirmation,
    formCurrent && useManualCode,
    formCurrent ? username : '',
    formCurrent ? password : '',
    formCurrent ? twoFactorCode : '',
    formCurrent ? emailCode : '',
    formCurrent ? error : null
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
    resetAuthForm: () => {
      if (identityRef.current === integration?.identity && attemptRef.current === attemptId)
        resetAuthForm();
    },
    cancelPendingRequest: () => {
      if (identityRef.current === integration?.identity && attemptRef.current === attemptId)
        cancelPendingRequest();
    },
    ...(integration ? { cancelLogin } : {})
  };

  return {
    state: {
      ...state,
      ...(integration
        ? {
            attemptId: formCurrent ? attemptId : null,
            canAuthenticate,
            accessUnavailable,
            ownershipReason: integration.access?.ownershipReason,
            recovering: integration.access?.canRecover === true
          }
        : {})
    },
    actions,
    loginDeadline: formCurrent ? loginDeadline : null
  };
}
