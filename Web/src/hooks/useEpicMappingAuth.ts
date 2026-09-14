import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import {
  NOTIFICATION_IDS,
  useNotifications,
  type NotificationStatus
} from '@contexts/notifications';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/useAuth';
import { ApiError } from '@services/apiError';
import { createUuid } from '@utils/uuid';
import { getIntegrationReasonKey, type EpicMappingAuthStatus } from '../types';

interface UseEpicMappingAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
  /**
   * Surfaces the login lifecycle (waiting for sign-in / signing in / cancelled / failed) on the
   * universal notification bar, in the SAME epic_game_mapping card the backend catalog refresh
   * drives once the authorization code is submitted - mirroring how the Xbox mapping login and its
   * catalog resolve share one card. Opt-in because this hook is also used by the setup wizard,
   * where the notification bar is not part of the flow.
   */
  loginStatusNotifications?: boolean;
}

export interface EpicAuthState {
  attemptId?: string | null;
  canAuthenticate?: boolean;
  ownershipReason?: string | null;
  recovering?: boolean;
  loading: boolean;
  needsAuthorizationCode: boolean;
  authorizationUrl: string;
  authorizationCode: string;
  /** Why the last attempt failed, or `null` while nothing has failed. The modal draws it inside
   *  itself, because it covers the notification bar this message also goes to. */
  error: string | null;
}

export interface EpicAuthActions {
  setAuthorizationCode: (code: string) => void;
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
  cancelLogin?: () => void;
}

export function useEpicMappingAuth(options: UseEpicMappingAuthOptions = {}) {
  const { onSuccess, onError, loginStatusNotifications = false } = options;

  const { t } = useTranslation();
  const { authenticationEnabled, authMode, accountId, sessionId, isLoading } = useAuth();
  const identity = JSON.stringify([authenticationEnabled, authMode, accountId, sessionId]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const requestRef = useRef(0);
  const statusRequestRef = useRef(0);
  const attemptRef = useRef<string | null>(null);
  const cancelledAttemptRef = useRef<string | null>(null);
  const busyRef = useRef(false);
  const [attemptId, setAttemptId] = useState<string | null>(null);
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);
  const [status, setStatus] = useState<EpicMappingAuthStatus | null>(null);
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
  const canAuthenticate =
    formCurrent &&
    (authStatus?.canSignIn === true ||
      authStatus?.canRecover === true ||
      (authStatus?.canCancel === true && authStatus.attemptId === attemptId));
  const refreshStatus = useCallback(async () => {
    if (!hasAccess || identityRef.current !== identity) return;
    const request = ++statusRequestRef.current;
    try {
      const next = await ApiService.getEpicMappingAuthStatus();
      if (identityRef.current !== identity || statusRequestRef.current !== request) return;
      setStatus(next);
      setStatusIdentity(identity);
      setStatusError(false);
    } catch (error: unknown) {
      if (identityRef.current !== identity || statusRequestRef.current !== request) return;
      setStatus(null);
      setStatusError(true);
      console.warn('Epic integration status unavailable:', getErrorMessage(error));
    } finally {
      if (identityRef.current === identity && statusRequestRef.current === request)
        setStatusLoading(false);
    }
  }, [identity, hasAccess]);
  const { addNotification, removeNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsAuthorizationCode, setNeedsAuthorizationCode] = useState(false);
  const [authorizationUrl, setAuthorizationUrl] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  // True while a login this hook started still owns the epic_game_mapping card - i.e. from the
  // moment the authorization URL is obtained until the flow terminates (success hands the card to
  // the backend refresh events, failure/cancel write their own terminal state). Guards
  // resetAuthForm from emitting a "cancelled" card when there is no login to cancel.
  const loginNotificationActiveRef = useRef(false);

  const pushLoginCard = useCallback(
    (status: NotificationStatus, message: string, error?: string, cancelled = false) => {
      if (!loginStatusNotifications) {
        return;
      }
      if (status === 'running') {
        // A back-out leaves a terminal card on this singleton id, and addNotification refuses to
        // replace one that landed seconds ago. Signing in again straight after cancelling is
        // ordinary, so drop the old card first - otherwise the new sign-in, which waits on the
        // user pasting an authorization code, shows nothing at all.
        removeNotification(NOTIFICATION_IDS.EPIC_GAME_MAPPING);
      }
      addNotification({
        type: 'epic_game_mapping',
        status,
        message,
        details: { cancelled },
        ...(error !== undefined ? { error } : {})
      });
    },
    [loginStatusNotifications, addNotification, removeNotification]
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
      // The user backed out of a login still waiting on them (closed the modal, or restarted the
      // flow). The status stays 'completed' because pushLoginCard takes the completed/failed
      // pair; details.cancelled:true is what makes it read as a stop rather than a finish, giving
      // it the grey neutral tone and the XCircle. Matches the Xbox mapping cancel, which sets the
      // very same flag.
      loginNotificationActiveRef.current = false;
      pushLoginCard('completed', t('signalr.epicMapping.signInCancelled'), undefined, true);
    }
    setLoading(false);
    setError(null);
    setNeedsAuthorizationCode(false);
    setAuthorizationUrl('');
    setAuthorizationCode('');
    setAbortController(null);
  }, [abortController, pushLoginCard, t]);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const cancelLogin = useCallback(() => {
    if (identityRef.current !== identity || !formCurrent) return;
    const cancelled = attemptId;
    if (cancelledAttemptRef.current === cancelled) cancelledAttemptRef.current = null;
    if (!cancelled) return;
    void ApiService.cancelEpicMappingLogin(cancelled)
      .catch((error: unknown) => {
        console.warn('Epic sign-in cancellation was not acknowledged:', getErrorMessage(error));
      })
      .finally(() => {
        void refreshStatus();
      });
  }, [refreshStatus, formCurrent, identity, attemptId]);

  useEffect(() => {
    formIdentityRef.current = identity;
    resetAuthForm();
    cancelledAttemptRef.current = null;
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

  // Unmount with a login still waiting on the user (tab switched away mid-flow): nothing else
  // would ever settle the card, so it would spin forever - settle it as cancelled. A login whose
  // code was already submitted is fine either way: the backend's own terminal event still lands
  // over SignalR and overwrites this card with the real outcome.
  useEffect(() => {
    return () => {
      if (loginNotificationActiveRef.current) {
        loginNotificationActiveRef.current = false;
        pushLoginCard('completed', t('signalr.epicMapping.signInCancelled'), undefined, true);
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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
    busyRef.current = true;
    const request = ++requestRef.current;
    const current = () => identityRef.current === identity && requestRef.current === request;
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      // Backend returns the Epic authorization URL directly (no Docker needed)
      const response = await ApiService.startEpicMappingLogin(controller.signal, {
        attemptId: submittedAttempt,
        recover: authStatus.canRecover === true
      });
      if (!current()) return;
      attemptRef.current = response.attemptId;
      setAttemptId(response.attemptId);
      setLoginDeadline(Date.parse(response.expiresAtUtc));
      setAuthorizationUrl(response.authorizationUrl);
      setNeedsAuthorizationCode(true);
      setLoading(false);
      loginNotificationActiveRef.current = true;
      pushLoginCard('running', t('signalr.epicMapping.waitingSignIn'));
    } catch (error) {
      if (!current()) return;
      attemptRef.current = null;
      setAttemptId(null);
      if (error instanceof Error && error.name === 'AbortError') {
        setLoading(false);
        return;
      }
      const message =
        error instanceof ApiError && error.body?.stageKey
          ? t(error.body.stageKey, error.body.context ?? {})
          : t('modals.epicAuth.errors.authenticationFailed');
      setError(message);
      onError?.(message);
      setLoading(false);
    } finally {
      if (current()) {
        busyRef.current = false;
        setAbortController(null);
        void refreshStatus();
      }
    }
  }, [resetAuthForm, onError, pushLoginCard, t, authStatus, identity, refreshStatus, formCurrent]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (identityRef.current !== identity || !canAuthenticate || busyRef.current) return false;
    // The prompt's Continue button and the code box's Submit button share this handler, so the step
    // the modal is on decides what it means. Until the authorization URL comes back there is no
    // code to send, and asking Epic for that URL is the only thing left to do - which is also the
    // retry after a first request that failed. Returning false keeps the modal open for the code.
    if (!needsAuthorizationCode) {
      await startLogin();
      return false;
    }

    if (!authorizationCode.trim() || !attemptRef.current) return false;
    const submittedAttempt = attemptRef.current;
    const request = ++requestRef.current;
    const current = () => identityRef.current === identity && requestRef.current === request;
    busyRef.current = true;

    // A fresh attempt starts here, so the last one's failure stops being the current answer. The
    // other branch above reaches resetAuthForm through startLogin, which clears it there.
    setError(null);
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);
    pushLoginCard('running', t('signalr.epicMapping.signingIn'));

    try {
      // Send the authorization code directly to the backend
      // Backend exchanges it for tokens, fetches games, saves credentials
      await ApiService.completeEpicMappingAuth(
        authorizationCode.trim(),
        controller.signal,
        submittedAttempt
      );
      if (!current()) return false;
      attemptRef.current = null;
      setAttemptId(null);
      // The backend's own Epic mapping lifecycle events own the card from here. Clear this
      // BEFORE onSuccess so modal-close triggers can never read the login as still needing
      // a "cancelled" card.
      loginNotificationActiveRef.current = false;
      onSuccess?.();
      return true;
    } catch (error) {
      if (!current()) return false;
      if (error instanceof Error && error.name === 'AbortError') {
        return false;
      }
      attemptRef.current = null;
      setAttemptId(null);
      setNeedsAuthorizationCode(false);
      setAuthorizationCode('');
      setAuthorizationUrl('');
      const message =
        error instanceof ApiError && error.body?.stageKey
          ? t(error.body.stageKey, error.body.context ?? {})
          : t('modals.epicAuth.errors.authenticationFailed');
      setError(message);
      loginNotificationActiveRef.current = false;
      pushLoginCard(
        'failed',
        t('signalr.epicMapping.signInFailed', { errorDetail: message }),
        message
      );
      onError?.(message);
      return false;
    } finally {
      if (current()) {
        busyRef.current = false;
        setLoading(false);
        setAbortController(null);
        void refreshStatus();
      }
    }
  }, [
    authorizationCode,
    needsAuthorizationCode,
    startLogin,
    onSuccess,
    onError,
    pushLoginCard,
    t,
    identity,
    refreshStatus,
    canAuthenticate
  ]);

  const state: EpicAuthState = {
    attemptId: formCurrent ? attemptId : null,
    canAuthenticate,
    ownershipReason: authStatus?.ownershipReason,
    recovering: authStatus?.canRecover === true,
    loading: formCurrent && loading,
    needsAuthorizationCode: formCurrent && needsAuthorizationCode,
    authorizationUrl: formCurrent ? authorizationUrl : '',
    authorizationCode: formCurrent ? authorizationCode : '',
    error: formCurrent ? error : null
  };

  const actions: EpicAuthActions = {
    setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm: () => {
      if (identityRef.current === identity && attemptRef.current === attemptId) resetAuthForm();
    },
    cancelPendingRequest: () => {
      if (identityRef.current === identity && attemptRef.current === attemptId)
        cancelPendingRequest();
    },
    cancelLogin
  };

  return {
    state,
    actions,
    startLogin,
    authStatus,
    refreshStatus,
    loginDeadline: formCurrent ? loginDeadline : null,
    statusLoading,
    statusError,
    identity
  };
}
