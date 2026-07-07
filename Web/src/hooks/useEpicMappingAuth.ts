import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications, type NotificationStatus } from '@contexts/notifications';

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
  const { addNotification } = useNotifications();

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
    (status: NotificationStatus, message: string, error?: string) => {
      if (!loginStatusNotifications) {
        return;
      }
      addNotification({
        type: 'epic_game_mapping',
        status,
        message,
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
      // The user backed out of a login still waiting on them (closed the modal, or restarted the
      // flow). 'completed' rather than a 'cancelled' status so the card auto-dismisses - the same
      // convention the Xbox mapping terminal events use.
      loginNotificationActiveRef.current = false;
      pushLoginCard('completed', t('signalr.epicMapping.signInCancelled'));
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
        pushLoginCard('completed', t('signalr.epicMapping.signInCancelled'));
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (!authorizationCode.trim()) return false;

    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);
    pushLoginCard('running', t('signalr.epicMapping.signingIn'));

    try {
      // Send the authorization code directly to the backend
      // Backend exchanges it for tokens, fetches games, saves credentials
      await ApiService.completeEpicMappingAuth(authorizationCode.trim(), controller.signal);
      // The backend's own EpicMappingProgress events (catalog refresh phases + terminal
      // completed) own the card from here - cleared BEFORE onSuccess so the modal close it
      // triggers can never read this as a login still needing a "cancelled" card.
      loginNotificationActiveRef.current = false;
      onSuccess?.();
      return true;
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        return false;
      }
      const message = error instanceof Error ? error.message : 'Authentication failed';
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
  }, [authorizationCode, onSuccess, onError, pushLoginCard, t]);

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
      const message = error instanceof Error ? error.message : 'Login failed';
      onError?.(message);
      setLoading(false);
    } finally {
      setAbortController(null);
    }
  }, [resetAuthForm, onError, pushLoginCard, t]);

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
