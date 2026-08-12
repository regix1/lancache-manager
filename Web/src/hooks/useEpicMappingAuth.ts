import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import {
  NOTIFICATION_IDS,
  useNotifications,
  type NotificationStatus
} from '@contexts/notifications';
import { getErrorMessage } from '@utils/error';

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
  loading: boolean;
  needsAuthorizationCode: boolean;
  authorizationUrl: string;
  authorizationCode: string;
}

export interface EpicAuthActions {
  setAuthorizationCode: (code: string) => void;
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

export function useEpicMappingAuth(options: UseEpicMappingAuthOptions = {}) {
  const { onSuccess, onError, loginStatusNotifications = false } = options;

  const { t } = useTranslation();
  const { addNotification, removeNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsAuthorizationCode, setNeedsAuthorizationCode] = useState(false);
  const [authorizationUrl, setAuthorizationUrl] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');
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
    if (abortController) {
      abortController.abort();
    }
    if (loginNotificationActiveRef.current) {
      // The user backed out of a login still waiting on them (closed the modal, or restarted the
      // flow). Status stays 'completed' (there is no 'cancelled' status) so the card still
      // auto-dismisses; details.cancelled:true is what renders it red + XCircle, matching Xbox's
      // terminal cancel (specialCaseHandlers.ts:271), which sets the very same flag.
      loginNotificationActiveRef.current = false;
      pushLoginCard('completed', t('signalr.epicMapping.signInCancelled'), undefined, true);
    }
    setLoading(false);
    setNeedsAuthorizationCode(false);
    setAuthorizationUrl('');
    setAuthorizationCode('');
    setAbortController(null);
  }, [abortController, pushLoginCard, t]);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

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
    resetAuthForm();
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      // Backend returns the Epic authorization URL directly (no Docker needed)
      const response = await ApiService.startEpicMappingLogin(controller.signal);
      setAuthorizationUrl(response.authorizationUrl);
      setNeedsAuthorizationCode(true);
      setLoading(false);
      loginNotificationActiveRef.current = true;
      pushLoginCard('running', t('signalr.epicMapping.waitingSignIn'));
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        setLoading(false);
        return;
      }
      const message = getErrorMessage(error);
      onError?.(message);
      setLoading(false);
    } finally {
      setAbortController(null);
    }
  }, [resetAuthForm, onError, pushLoginCard, t]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    // The prompt's Continue button and the code box's Submit button share this handler, so the step
    // the modal is on decides what it means. Until the authorization URL comes back there is no
    // code to send, and asking Epic for that URL is the only thing left to do - which is also the
    // retry after a first request that failed. Returning false keeps the modal open for the code.
    if (!needsAuthorizationCode) {
      await startLogin();
      return false;
    }

    if (!authorizationCode.trim()) return false;

    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);
    pushLoginCard('running', t('signalr.epicMapping.signingIn'));

    try {
      // Send the authorization code directly to the backend
      // Backend exchanges it for tokens, fetches games, saves credentials
      await ApiService.completeEpicMappingAuth(authorizationCode.trim(), controller.signal);
      // The backend's own Epic mapping lifecycle events own the card from here. Clear this
      // BEFORE onSuccess so modal-close triggers can never read the login as still needing
      // a "cancelled" card.
      loginNotificationActiveRef.current = false;
      onSuccess?.();
      return true;
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        return false;
      }
      const message = getErrorMessage(error);
      loginNotificationActiveRef.current = false;
      pushLoginCard(
        'failed',
        t('signalr.epicMapping.signInFailed', { errorDetail: message }),
        message
      );
      onError?.(message);
      return false;
    } finally {
      setLoading(false);
      setAbortController(null);
    }
  }, [authorizationCode, needsAuthorizationCode, startLogin, onSuccess, onError, pushLoginCard, t]);

  const state: EpicAuthState = {
    loading,
    needsAuthorizationCode,
    authorizationUrl,
    authorizationCode
  };

  const actions: EpicAuthActions = {
    setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin };
}
