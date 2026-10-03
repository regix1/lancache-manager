import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import { useErrorHandler } from './useErrorHandler';
import { useReconnectRefetch } from './useReconnectRefetch';
import { useAuth } from '@contexts/useAuth';
import { getErrorMessage } from '@utils/error';
import { createUuid } from '@utils/uuid';
import { getIntegrationReasonKey, type XboxMappingAuthStatus } from '../types';

interface UseXboxMappingAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export interface XboxAuthState {
  attemptId?: string | null;
  canAuthenticate?: boolean;
  accessUnavailable?: boolean;
  ownershipReason?: string | null;
  recovering?: boolean;
  loading: boolean;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
  /** Why the last attempt failed, or `null` while nothing has failed. */
  error: string | null;
}

export interface XboxAuthActions {
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

export function useXboxMappingAuth(options: UseXboxMappingAuthOptions = {}) {
  const { onSuccess, onError } = options;
  const { on, off, isConnected } = useSignalR();
  const { notifyError } = useErrorHandler();
  const { t } = useTranslation();
  const { authenticationEnabled, authMode, accountId, sessionId, isLoading } = useAuth();
  const identity = JSON.stringify([authenticationEnabled, authMode, accountId, sessionId]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const requestRef = useRef(0);
  const statusRequestRef = useRef(0);
  const attemptRef = useRef<string | null>(null);
  const cancelledAttemptRef = useRef<string | null>(null);
  // The attempt id the stored status was asked for. Only a status asked for this sign-in after its start answer can
  // say the server no longer knows it.
  const statusAttemptRef = useRef<string | null>(null);
  const busyRef = useRef(false);
  const [attemptId, setAttemptId] = useState<string | null>(null);
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);
  const [status, setStatus] = useState<XboxMappingAuthStatus | null>(null);
  const [statusIdentity, setStatusIdentity] = useState<string | null>(null);
  const [statusLoading, setStatusLoading] = useState(true);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [statusFailures, setStatusFailures] = useState(0);
  const hasAccess =
    !isLoading &&
    (authenticationEnabled === false ||
      (authMode === 'authenticated' && Boolean(accountId && sessionId)));
  const formIdentityRef = useRef(identity);
  const formCurrent = formIdentityRef.current === identity && hasAccess;
  const authStatus = statusIdentity === identity && hasAccess ? status : null;
  const accessUnavailable = !formCurrent || authStatus === null || statusError !== null;
  const refreshStatus = useCallback(async () => {
    if (!hasAccess || identityRef.current !== identity) return null;
    const request = ++statusRequestRef.current;
    const askedAttempt = attemptRef.current;
    try {
      const next = await ApiService.getXboxMappingAuthStatus(askedAttempt);
      if (identityRef.current !== identity || statusRequestRef.current !== request) return null;
      statusAttemptRef.current = askedAttempt;
      setStatus(next);
      setStatusIdentity(identity);
      setStatusError(null);
      return next;
    } catch (error: unknown) {
      if (identityRef.current !== identity || statusRequestRef.current !== request) return null;
      setStatus(null);
      setStatusError(getErrorMessage(error));
      setStatusFailures((count) => count + 1);
      notifyError('Xbox integration status unavailable', error, { silent: true });
      return null;
    } finally {
      if (identityRef.current === identity && statusRequestRef.current === request)
        setStatusLoading(false);
    }
  }, [identity, hasAccess, notifyError]);
  const [loading, setLoading] = useState(false);
  const [needsDeviceCode, setNeedsDeviceCode] = useState(false);
  const [deviceUserCode, setDeviceUserCode] = useState('');
  const [deviceVerificationUri, setDeviceVerificationUri] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  // True while a manager-side login is in flight; gates the auth-state listener so
  // unrelated terminal events (catalog refresh) do not close the modal prematurely.
  const loginInProgressRef = useRef(false);

  const resetAuthForm = useCallback(() => {
    requestRef.current += 1;
    busyRef.current = false;
    if (attemptRef.current) cancelledAttemptRef.current = attemptRef.current;
    attemptRef.current = null;
    setAttemptId(null);
    if (abortController) {
      abortController.abort();
    }
    loginInProgressRef.current = false;
    setLoading(false);
    setError(null);
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
    setAbortController(null);
  }, [abortController]);

  useEffect(() => {
    formIdentityRef.current = identity;
    resetAuthForm();
    cancelledAttemptRef.current = null;
    setStatus(null);
    setStatusIdentity(null);
    setStatusLoading(hasAccess);
    setStatusError(null);
    void refreshStatus();
    return () => {
      requestRef.current += 1;
      statusRequestRef.current += 1;
    };
    // Authority refreshes do not reset a live same-caller form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity, refreshStatus]);

  // The one way a login ends in success, so the reconnect resync below can end it the same way the
  // backend's own event does.
  const finishLogin = useCallback(() => {
    attemptRef.current = null;
    cancelledAttemptRef.current = null;
    setAttemptId(null);
    loginInProgressRef.current = false;
    setNeedsDeviceCode(false);
    setLoading(false);
    onSuccess?.();
  }, [onSuccess]);

  // The one way a login ends in failure, for the same reason finishLogin exists.
  const failLogin = useCallback(
    (message: string) => {
      attemptRef.current = null;
      setAttemptId(null);
      loginInProgressRef.current = false;
      setNeedsDeviceCode(false);
      setLoading(false);
      setError(message);
      onError?.(message);
    },
    [onError]
  );

  // Every status read asks for the waiting sign-in's ending by its attempt id, so whichever read lands (after the
  // ending event, a reconnect, a retry or the card's own reload) ends the dialog on this sign-in's own result.
  useEffect(() => {
    const submittedAttempt = attemptRef.current;
    if (!authStatus || !submittedAttempt || !loginInProgressRef.current || !needsDeviceCode) return;
    const ending = authStatus.loginEnding;
    if (ending?.attemptId === submittedAttempt) {
      if (ending.status !== 'completed') {
        failLogin(t(ending.stageKey, ending.context ?? {}));
      } else if (authStatus.canManage === true && authStatus.isAuthenticated) {
        finishLogin();
      } else {
        // The account was saved, then signed out (a logout from another tab) before this read.
        failLogin(t('modals.xboxAuth.errors.loginFailed'));
      }
    } else if (
      statusAttemptRef.current === submittedAttempt &&
      authStatus.attemptId !== submittedAttempt &&
      !authStatus.loginInProgress
    ) {
      // The server holds no ending for this attempt and runs no sign-in: a restart ended it.
      failLogin(t('errors.integration.attemptExpired'));
    }
  }, [authStatus, needsDeviceCode, finishLogin, failLogin, t]);

  // A failed status read while a sign-in waits is retried every 5 s (the setup wizard watchdog's tick) until a read
  // answers or the device code's deadline passes, so one failed read cannot leave a finished sign-in's code on screen.
  useEffect(() => {
    const waitingAttempt = attemptRef.current;
    if (
      statusError === null ||
      !waitingAttempt ||
      loginDeadline === null ||
      Date.now() >= loginDeadline
    )
      return;
    const retry = setTimeout(() => {
      if (attemptRef.current === waitingAttempt) void refreshStatus();
    }, 5000);
    return () => clearTimeout(retry);
  }, [statusError, statusFailures, loginDeadline, refreshStatus]);

  useEffect(() => {
    const handleAuthStateChanged = () => {
      void refreshStatus();
    };
    on('XboxMappingAuthStateChanged', handleAuthStateChanged);
    return () => off('XboxMappingAuthStateChanged', handleAuthStateChanged);
  }, [on, off, refreshStatus]);

  useReconnectRefetch(isConnected, () => {
    void refreshStatus();
  });

  const startLogin = useCallback(async () => {
    if (identityRef.current !== identity || !formCurrent || busyRef.current || attemptRef.current)
      return;
    if (authStatus?.canSignIn !== true && authStatus?.canRecover !== true) {
      setError(
        accessUnavailable
          ? t('errors.integration.statusUnavailable')
          : t(getIntegrationReasonKey(authStatus?.ownershipReason))
      );
      return;
    }
    resetAuthForm();
    const submittedAttempt = createUuid();
    cancelledAttemptRef.current = null;
    attemptRef.current = submittedAttempt;
    setAttemptId(submittedAttempt);
    busyRef.current = true;
    const request = ++requestRef.current;
    const current = () => identityRef.current === identity && requestRef.current === request;
    loginInProgressRef.current = true;
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      const response = await ApiService.startXboxMappingLogin(controller.signal, {
        attemptId: submittedAttempt,
        recover: authStatus.canRecover === true
      });
      if (!current()) return;
      attemptRef.current = response.attemptId;
      // A status read while the start request was out may predate the server's attempt.
      statusAttemptRef.current = null;
      setAttemptId(response.attemptId);
      setLoginDeadline(Date.parse(response.expiresAtUtc));
      setDeviceUserCode(response.userCode);
      setDeviceVerificationUri(response.verificationUri);
      setNeedsDeviceCode(true);
      setLoading(false);
    } catch (error) {
      if (!current()) return;
      attemptRef.current = null;
      setAttemptId(null);
      loginInProgressRef.current = false;
      setLoading(false);
      if (error instanceof Error && error.name === 'AbortError') return;
      const message = getErrorMessage(error);
      setError(message);
      onError?.(message);
    } finally {
      if (current()) {
        busyRef.current = false;
        setAbortController(null);
        void refreshStatus();
      }
    }
  }, [
    resetAuthForm,
    onError,
    t,
    authStatus,
    identity,
    refreshStatus,
    formCurrent,
    accessUnavailable
  ]);

  // The backend polls the device code automatically, so there is no code-paste "complete" step.
  // The modal's Continue button instead RE-STARTS the login: this gives a working retry if the
  // initial device-code request failed. It returns false so the modal stays open and the
  // loading/device-code state drives the UI; the backend supersedes any stale poll.
  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    await startLogin();
    return false;
  }, [startLogin]);

  // Cancels a pending login poll server-side when the modal is closed, so an abandoned device-code
  // poll stops immediately instead of hammering Microsoft until expiry. Best-effort: the client form
  // is already reset by resetAuthForm; an already-authenticated account is NOT signed out.
  const cancelLogin = useCallback(async () => {
    if (identityRef.current !== identity || !formCurrent) return;
    const cancelled = attemptId;
    if (cancelledAttemptRef.current === cancelled) cancelledAttemptRef.current = null;
    if (!cancelled) return;
    try {
      await ApiService.cancelXboxMappingLogin(cancelled);
    } catch (error) {
      // Best-effort: the poll will expire on its own if the cancel request fails.
      notifyError('Failed to cancel Xbox mapping login', error, {
        silent: true,
        logLabel: 'useXboxMappingAuth cancelLogin'
      });
    } finally {
      void refreshStatus();
    }
  }, [notifyError, refreshStatus, formCurrent, identity, attemptId]);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const state: XboxAuthState = {
    attemptId: formCurrent ? attemptId : null,
    accessUnavailable,
    canAuthenticate:
      formCurrent &&
      (authStatus?.canSignIn === true ||
        authStatus?.canRecover === true ||
        (authStatus?.canCancel === true && authStatus.attemptId === attemptId)),
    ownershipReason: authStatus?.ownershipReason,
    recovering: authStatus?.canRecover === true,
    loading: formCurrent && loading,
    needsDeviceCode: formCurrent && needsDeviceCode,
    deviceUserCode: formCurrent ? deviceUserCode : '',
    deviceVerificationUri: formCurrent ? deviceVerificationUri : '',
    error: formCurrent ? error : null
  };

  const actions: XboxAuthActions = {
    handleAuthenticate,
    resetAuthForm: () => {
      if (identityRef.current === identity && attemptRef.current === attemptId) resetAuthForm();
    },
    cancelPendingRequest: () => {
      if (identityRef.current === identity && attemptRef.current === attemptId)
        cancelPendingRequest();
    }
  };

  return {
    state,
    actions,
    startLogin,
    cancelLogin,
    authStatus,
    refreshStatus,
    loginDeadline: formCurrent ? loginDeadline : null,
    statusLoading,
    statusError,
    identity
  };
}
