import { useState, useCallback } from 'react';
import { HubConnection } from '@microsoft/signalr';
import { useNotifications } from '@contexts/NotificationsContext';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';

export interface UsePrefillSteamAuthOptions {
  sessionId: string | null;
  hubConnection: HubConnection | null;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

/**
 * Hook for Steam authentication within a prefill Docker container.
 * Uses SignalR hub methods to send credentials directly to the container
 * instead of the API-based authentication used elsewhere.
 */
export function usePrefillSteamAuth(options: UsePrefillSteamAuthOptions) {
  const { sessionId, hubConnection, onSuccess, onError } = options;
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  const cancelPendingRequest = useCallback(() => {
    // For container-based auth, we can't really cancel - just reset state
    setLoading(false);
    setWaitingForMobileConfirmation(false);
  }, []);

  const resetAuthForm = useCallback(() => {
    setUsername('');
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setLoading(false);
  }, []);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (!sessionId || !hubConnection) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'No active session',
        details: { notificationType: 'error' }
      });
      return false;
    }

    // Validate credentials
    if (!needsTwoFactor && !needsEmailCode) {
      if (!username.trim() || !password.trim()) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Please enter both username and password',
          details: { notificationType: 'error' }
        });
        return false;
      }
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

    if (needsTwoFactor && useManualCode && !twoFactorCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter your 2FA code',
        details: { notificationType: 'error' }
      });
      return false;
    }

    setLoading(true);

    try {
      if (needsEmailCode) {
        // Send email verification code
        await hubConnection.invoke('SendEmailCode', sessionId, emailCode);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'Email code sent',
          details: { notificationType: 'success' }
        });
        resetAuthForm();
        onSuccess?.();
        return true;
      }

      if (needsTwoFactor) {
        // Send 2FA code
        await hubConnection.invoke('SendTwoFactorCode', sessionId, twoFactorCode);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: '2FA code sent',
          details: { notificationType: 'success' }
        });
        resetAuthForm();
        onSuccess?.();
        return true;
      }

      // Send initial credentials
      await hubConnection.invoke('SendCredentials', sessionId, username, password);
      addNotification({
        type: 'generic',
        status: 'completed',
        message: 'Credentials sent to container',
        details: { notificationType: 'success' }
      });
      resetAuthForm();
      onSuccess?.();
      return true;

    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to send credentials';
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMessage,
        details: { notificationType: 'error' }
      });
      onError?.(errorMessage);
      setLoading(false);
      return false;
    }
  }, [
    sessionId,
    hubConnection,
    username,
    password,
    twoFactorCode,
    emailCode,
    needsTwoFactor,
    needsEmailCode,
    useManualCode,
    addNotification,
    resetAuthForm,
    onSuccess,
    onError
  ]);

  /**
   * Call this when terminal output indicates a login prompt is needed
   */
  const triggerLoginPrompt = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  /**
   * Call this when terminal output indicates 2FA is needed
   */
  const trigger2FAPrompt = useCallback(() => {
    setNeedsTwoFactor(true);
    setNeedsEmailCode(false);
  }, []);

  /**
   * Call this when terminal output indicates email code is needed
   */
  const triggerEmailPrompt = useCallback(() => {
    setNeedsEmailCode(true);
    setNeedsTwoFactor(false);
  }, []);

  const state: SteamLoginFlowState = {
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode
  };

  const actions: SteamAuthActions = {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    setUseManualCode,
    setNeedsTwoFactor,
    setWaitingForMobileConfirmation,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return {
    state,
    actions,
    triggerLoginPrompt,
    trigger2FAPrompt,
    triggerEmailPrompt
  };
}
