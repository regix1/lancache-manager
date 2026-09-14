import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import { useNotifications, type NotificationStatus } from '@contexts/notifications';
import { useErrorHandler } from './useErrorHandler';
import { useReconnectRefetch } from './useReconnectRefetch';
import { useAuth } from '@contexts/useAuth';
import { ApiError } from '@services/apiError';
import { createUuid } from '@utils/uuid';
import { getIntegrationReasonKey, type XboxMappingAuthStatus } from '../types';
import type { XboxMappingAuthStateChangedEvent } from '../contexts/SignalRContext/types';

interface UseXboxMappingAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
  /**
   * Surfaces the login lifecycle (waiting for the device code to be approved / cancelled /
   * failed) on the universal notification bar, in the SAME xbox_game_mapping card the backend
   * catalog resolve drives once the code is approved. Opt-in because this hook is also used by
   * the setup wizard, where the notification bar is not part of the flow.
   */
  loginStatusNotifications?: boolean;
}

export interface XboxAuthState {
  attemptId?: string | null;
  canAuthenticate?: boolean;
  ownershipReason?: string | null;
  recovering?: boolean;
  loading: boolean;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
  /** Why the last attempt failed, or `null` while nothing has failed. The modal draws it inside
   *  itself, because it covers the notification bar this message also goes to. */
  error: string | null;
}

export interface XboxAuthActions {
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

export function useXboxMappingAuth(options: UseXboxMappingAuthOptions = {}) {
  const { onSuccess, onError, loginStatusNotifications = false } = options;
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
  const operationRef = useRef<string | null>(null);
  const wasAuthenticatedRef = useRef(false);
  const busyRef = useRef(false);
  const [attemptId, setAttemptId] = useState<string | null>(null);
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);
  const [status, setStatus] = useState<XboxMappingAuthStatus | null>(null);
  const [statusIdentity, setStatusIdentity] = useState<string | null>(null);
  const [statusLoading, setStatusLoading] = useState(true);
  const [statusError, setStatusError] = useState(false);
  const hasAccess =
    !isLoading &&
    (authenticationEnabled === false ||
      (authMode === 'authenticated' && Boolean(accountId && sessionId)));
  const formIdentityRef = useRef(identity);
  const formCurrent = formIdentityRef.current === identity && hasAccess;
  const authStatus = statusIdentity === identity && hasAccess ? status : null;
  const refreshStatus = useCallback(async () => {
    if (!hasAccess || identityRef.current !== identity) return null;
    const request = ++statusRequestRef.current;
    try {
      const next = await ApiService.getXboxMappingAuthStatus();
      if (identityRef.current !== identity || statusRequestRef.current !== request) return null;
      setStatus(next);
      setStatusIdentity(identity);
      setStatusError(false);
      return next;
    } catch (error: unknown) {
      if (identityRef.current !== identity || statusRequestRef.current !== request) return null;
      setStatus(null);
      setStatusError(true);
      notifyError('Xbox integration status unavailable', error, { silent: true });
      return null;
    } finally {
      if (identityRef.current === identity && statusRequestRef.current === request)
        setStatusLoading(false);
    }
  }, [identity, hasAccess, notifyError]);
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsDeviceCode, setNeedsDeviceCode] = useState(false);
  const [deviceUserCode, setDeviceUserCode] = useState('');
  const [deviceVerificationUri, setDeviceVerificationUri] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  // True while a manager-side login is in flight; gates the auth-state listener so
  // unrelated terminal events (catalog refresh) do not close the modal prematurely.
  const loginInProgressRef = useRef(false);

  // True while a login this hook started still owns the xbox_game_mapping card - from the
  // backend's "waiting for sign-in" event until the flow terminates. Approval hands the card over
  // to the catalog resolve events; backing out settles it here instead. Guards resetAuthForm from
  // emitting a "cancelled" card when there is no login to cancel.
  const loginNotificationActiveRef = useRef(false);

  // Terminal statuses only, and that is what keeps it safe. addNotification refuses to put a
  // 'running' card over a terminal one less than TERMINAL_SEED_GUARD_MS old
  // (NotificationsContext.tsx:190), so a client-seeded running card pushed straight after a cancel
  // would be swallowed and the operation would show nothing. Xbox never hits that: its running card
  // comes from the backend's XboxMappingStarted event, which inserts through setNotifications and
  // never passes the guard. Epic does seed its own running card here, which is why it clears the
  // old one with removeNotification first (useEpicMappingAuth.ts:70) - anything added here with a
  // 'running' status needs that same line.
  const pushLoginCard = useCallback(
    (status: NotificationStatus, message: string, error?: string, cancelled = false) => {
      if (!loginStatusNotifications) {
        return;
      }
      addNotification({
        type: 'xbox_game_mapping',
        status,
        message,
        details: { cancelled },
        ...(error !== undefined ? { error } : {})
      });
    },
    [loginStatusNotifications, addNotification]
  );

  const resetAuthForm = useCallback(() => {
    requestRef.current += 1;
    busyRef.current = false;
    if (attemptRef.current) cancelledAttemptRef.current = attemptRef.current;
    attemptRef.current = null;
    setAttemptId(null);
    if (abortController) {
      abortController.abort();
    }
    if (loginNotificationActiveRef.current) {
      // The user backed out of a login still waiting on the device code (closed the modal, or
      // restarted the flow). The backend's own cancelled event can no longer settle the card,
      // because the auth-state listener returns early once loginInProgressRef is cleared just
      // below - so settle it here. The status stays 'completed' because this card is pushed
      // through pushLoginCard, which takes the completed/failed pair; details.cancelled:true is
      // what makes it read as a stop rather than a finish, giving it the grey neutral tone and
      // the XCircle instead of the success tick.
      loginNotificationActiveRef.current = false;
      pushLoginCard('completed', t('signalr.xbox.mapping.cancelled'), undefined, true);
    }
    loginInProgressRef.current = false;
    setLoading(false);
    setError(null);
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
    setAbortController(null);
  }, [abortController, pushLoginCard, t]);

  useEffect(() => {
    formIdentityRef.current = identity;
    resetAuthForm();
    cancelledAttemptRef.current = null;
    operationRef.current = null;
    setStatus(null);
    setStatusIdentity(null);
    setStatusLoading(hasAccess);
    setStatusError(false);
    void refreshStatus();
    return () => {
      requestRef.current += 1;
      statusRequestRef.current += 1;
    };
    // Authority refreshes do not reset a live same-caller form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity, refreshStatus]);

  // Unmount with a device code still waiting on the user (tab switched away mid-flow): nothing
  // else would ever settle the card, so it would sweep forever - settle it as cancelled. Only the
  // pre-approval wait reaches this: once the code is approved the card belongs to the catalog
  // resolve and the ref is already cleared.
  useEffect(() => {
    return () => {
      if (loginNotificationActiveRef.current) {
        loginNotificationActiveRef.current = false;
        pushLoginCard('completed', t('signalr.xbox.mapping.cancelled'), undefined, true);
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // The one way a login ends in success, so the reconnect resync below can end it the same way the
  // backend's own event does. No card is written here: the catalog resolve already completed the
  // same card one message earlier, with the resolved games count. Writing one would replace that
  // with a plainer message and re-arm the auto-dismiss from zero.
  const finishLogin = useCallback(() => {
    attemptRef.current = null;
    cancelledAttemptRef.current = null;
    setAttemptId(null);
    loginInProgressRef.current = false;
    loginNotificationActiveRef.current = false;
    setNeedsDeviceCode(false);
    setLoading(false);
    onSuccess?.();
  }, [onSuccess]);

  // The one way a login ends in failure, for the same reason finishLogin exists. Approval hands the
  // card to the catalog resolve, which settles it with the real stage error one message before this
  // runs, so ownership is snapshotted before it is cleared: the card is written only when this login
  // still owns it, which is the pre-approval death where no reporter exists to settle it.
  const failLogin = useCallback(
    (message: string) => {
      attemptRef.current = null;
      setAttemptId(null);
      const loginCardActive = loginNotificationActiveRef.current;
      loginInProgressRef.current = false;
      loginNotificationActiveRef.current = false;
      setNeedsDeviceCode(false);
      setLoading(false);
      setError(message);
      if (loginCardActive) {
        pushLoginCard('failed', t('signalr.xbox.mapping.failed'), message);
      }
      onError?.(message);
    },
    [onError, pushLoginCard, t]
  );

  const resyncLogin = useCallback(
    async (event?: XboxMappingAuthStateChangedEvent) => {
      const submittedAttempt = attemptRef.current;
      const next = await refreshStatus();
      if (
        !next ||
        !submittedAttempt ||
        attemptRef.current !== submittedAttempt ||
        !loginInProgressRef.current
      )
        return;
      if (next.attemptId === submittedAttempt || next.loginInProgress) return;
      const matchingEvent = Boolean(
        operationRef.current && event?.operationId === operationRef.current
      );
      if (
        next.canManage === true &&
        next.isAuthenticated &&
        (!wasAuthenticatedRef.current || (matchingEvent && event?.status === 'completed'))
      ) {
        finishLogin();
      } else if (needsDeviceCode) {
        const stageKey = matchingEvent ? event?.stageKey : null;
        failLogin(
          stageKey ? t(stageKey, event?.context ?? {}) : t('modals.xboxAuth.errors.loginFailed')
        );
      }
    },
    [refreshStatus, finishLogin, failLogin, needsDeviceCode, t]
  );

  useEffect(() => {
    const handleAuthStateChanged = (event: XboxMappingAuthStateChangedEvent) => {
      void resyncLogin(event);
    };
    on('XboxMappingAuthStateChanged', handleAuthStateChanged);
    return () => off('XboxMappingAuthStateChanged', handleAuthStateChanged);
  }, [on, off, resyncLogin]);

  useReconnectRefetch(isConnected, () => {
    void resyncLogin();
  });

  const startLogin = useCallback(async () => {
    if (identityRef.current !== identity || !formCurrent || busyRef.current || attemptRef.current)
      return;
    if (authStatus?.canSignIn !== true && authStatus?.canRecover !== true) {
      setError(
        authStatus
          ? t(getIntegrationReasonKey(authStatus.ownershipReason))
          : t('errors.integration.statusUnavailable')
      );
      return;
    }
    resetAuthForm();
    const submittedAttempt = createUuid();
    cancelledAttemptRef.current = null;
    attemptRef.current = submittedAttempt;
    setAttemptId(submittedAttempt);
    wasAuthenticatedRef.current = authStatus.isAuthenticated;
    operationRef.current = null;
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
      setAttemptId(response.attemptId);
      operationRef.current = response.operationId ?? null;
      setLoginDeadline(Date.parse(response.expiresAtUtc));
      setDeviceUserCode(response.userCode);
      setDeviceVerificationUri(response.verificationUri);
      setNeedsDeviceCode(true);
      setLoading(false);
      loginNotificationActiveRef.current = true;
    } catch (error) {
      if (!current()) return;
      attemptRef.current = null;
      setAttemptId(null);
      loginInProgressRef.current = false;
      setLoading(false);
      if (error instanceof Error && error.name === 'AbortError') return;
      const message =
        error instanceof ApiError && error.body?.stageKey
          ? t(error.body.stageKey, error.body.context ?? {})
          : t('modals.xboxAuth.errors.loginFailed');
      setError(message);
      // The backend fires "waiting" from a fire-and-forget poll task it starts BEFORE returning the
      // device code, so a card can already be up when the response itself fails. The auth-state
      // listener is deaf from here on (loginInProgressRef was just cleared), so the backend's own
      // cancelled/failed event can no longer settle that card - settle it here.
      if (loginNotificationActiveRef.current) {
        loginNotificationActiveRef.current = false;
        pushLoginCard('failed', t('signalr.xbox.mapping.failed'), message);
      }
      onError?.(message);
    } finally {
      if (current()) {
        busyRef.current = false;
        setAbortController(null);
        void refreshStatus();
      }
    }
  }, [resetAuthForm, onError, pushLoginCard, t, authStatus, identity, refreshStatus, formCurrent]);

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
