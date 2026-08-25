import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import { useNotifications, type NotificationStatus } from '@contexts/notifications';
import { useErrorHandler } from './useErrorHandler';
import { useReconnectRefetch } from './useReconnectRefetch';
import { getErrorMessage } from '@utils/error';
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

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'cancelled']);

export function useXboxMappingAuth(options: UseXboxMappingAuthOptions = {}) {
  const { onSuccess, onError, loginStatusNotifications = false } = options;
  const { on, off, isConnected } = useSignalR();
  const { notifyError } = useErrorHandler();
  const { t } = useTranslation();
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

  // Listen for the dedicated, non-notification auth event that signals login success or failure.
  useEffect(() => {
    const handleAuthStateChanged = (event: XboxMappingAuthStateChangedEvent) => {
      if (!loginInProgressRef.current) return;
      if (event.status === 'waiting') {
        // No card is written here. The backend builds its reporter before the device code is even
        // requested, so its started event has already put the card up and that card carries the
        // operationId the cancel X needs. Seeding one here would insert a fresh object over it and
        // throw the id away. All this branch does is record that a login is live, so the terminal
        // paths below know whether a card is still theirs to settle.
        loginNotificationActiveRef.current = true;
        return;
      }
      if (!TERMINAL_STATUSES.has(event.status)) return;
      if (event.status === 'completed') {
        finishLogin();
        return;
      }
      if (event.status === 'failed') {
        failLogin(event.error ?? event.message ?? t('modals.xboxAuth.errors.loginFailed'));
        return;
      }
      // Cancelled. Same ownership snapshot failLogin takes, for the same reason.
      const loginCardActive = loginNotificationActiveRef.current;
      loginInProgressRef.current = false;
      loginNotificationActiveRef.current = false;
      setNeedsDeviceCode(false);
      setLoading(false);
      if (loginCardActive) {
        // Reached when the poll is cancelled or expires server-side while the modal is still
        // open. Before approval the catalog reporter does not exist yet, so this is the only
        // event that can settle the card.
        pushLoginCard('completed', t('signalr.xbox.mapping.cancelled'), undefined, true);
      }
    };
    // A mapping run owns the xbox_game_mapping card from its started event until its own terminal
    // one, so the hook must not settle it: an unmount (or resetAuthForm) during the collect /
    // resolve / backfill stretch would stamp a red "cancelled" over a run that is still going.
    // The login's own started event arrives just before the 'waiting' event that arms the settles,
    // so this leaves the pre-approval back-out paths below intact.
    const handleMappingStarted = () => {
      loginNotificationActiveRef.current = false;
    };
    on('XboxMappingAuthStateChanged', handleAuthStateChanged);
    on('XboxMappingStarted', handleMappingStarted);
    return () => {
      off('XboxMappingAuthStateChanged', handleAuthStateChanged);
      off('XboxMappingStarted', handleMappingStarted);
    };
  }, [on, off, finishLogin, failLogin, pushLoginCard, t]);

  // The backend polls Microsoft for the device code and pushes the outcome, so a socket that drops
  // during that wait loses the only message that can end this login: the modal would sit on a code
  // the user has already approved, or on one the poll has since given up on. The status route reads
  // cached flags with no I/O, so asking it again on recovery is cheap. A failed ask leaves the login
  // in flight for the next recovery.
  useReconnectRefetch(isConnected, () => {
    if (!loginInProgressRef.current) return;
    void ApiService.getXboxMappingAuthStatus()
      .then((status) => {
        // Closing the modal aborts nothing here, and two reconnects in quick succession both ask.
        // Re-read the flag the caller checked before the request went out, or a login the user
        // backed out of is completed anyway, and onSuccess fires twice for one login.
        if (!loginInProgressRef.current) return;
        if (status.isAuthenticated) {
          finishLogin();
          return;
        }
        // Signed out AND no attempt alive: the poll died while the socket was down, so nothing will
        // ever end this login. loginInProgress stays true through the catalog stretch after
        // approval, so a busy login never reaches here. The device code is the proof the backend
        // registered this attempt at all - without it the login POST is still on the wire, its own
        // rejection handles the failure, and reading the flag now would kill a login that has not
        // started yet.
        if (!status.loginInProgress && needsDeviceCode) {
          failLogin(t('modals.xboxAuth.errors.loginFailed'));
        }
      })
      .catch((error) => {
        notifyError('Failed to resync Xbox mapping auth status', error, {
          silent: true,
          logLabel: 'useXboxMappingAuth reconnect'
        });
      });
  });

  const startLogin = useCallback(async () => {
    resetAuthForm();
    loginInProgressRef.current = true;
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      const response = await ApiService.startXboxMappingLogin(controller.signal);
      setDeviceUserCode(response.userCode);
      setDeviceVerificationUri(response.verificationUri);
      setNeedsDeviceCode(true);
      setLoading(false);
    } catch (error) {
      loginInProgressRef.current = false;
      setLoading(false);
      if (error instanceof Error && error.name === 'AbortError') return;
      const message = getErrorMessage(error);
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
      setAbortController(null);
    }
  }, [resetAuthForm, onError, pushLoginCard, t]);

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
    try {
      await ApiService.cancelXboxMappingLogin();
    } catch (error) {
      // Best-effort: the poll will expire on its own if the cancel request fails.
      notifyError('Failed to cancel Xbox mapping login', error, {
        silent: true,
        logLabel: 'useXboxMappingAuth cancelLogin'
      });
    }
  }, [notifyError]);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const state: XboxAuthState = {
    loading,
    needsDeviceCode,
    deviceUserCode,
    deviceVerificationUri,
    error
  };

  const actions: XboxAuthActions = {
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin, cancelLogin };
}
