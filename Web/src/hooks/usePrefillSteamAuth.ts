import { useState, useCallback, useEffect } from 'react';
import { HubConnection } from '@microsoft/signalr';
import { useNotifications } from '@contexts/NotificationsContext';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';

interface CredentialChallenge {
  type: string;
  challengeId: string;
  credentialType: string;
  serverPublicKey: string;
  email?: string;
  createdAt: string;
  expiresAt: string;
}

export interface UsePrefillSteamAuthOptions {
  sessionId: string | null;
  hubConnection: HubConnection | null;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

/**
 * Hook for Steam authentication within a prefill Docker container.
 * Uses SignalR hub methods to handle encrypted credential exchange.
 */
export function usePrefillSteamAuth(options: UsePrefillSteamAuthOptions) {
  const { sessionId, hubConnection, onSuccess, onError } = options;
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [pendingChallenge, setPendingChallenge] = useState<CredentialChallenge | null>(null);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  // Listen for credential challenges from the daemon
  useEffect(() => {
    if (!hubConnection) return;

    const handleCredentialChallenge = (_sessionId: string, challenge: CredentialChallenge) => {
      setPendingChallenge(challenge);

      // Set the appropriate state based on credential type
      switch (challenge.credentialType) {
        case 'password':
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          setWaitingForMobileConfirmation(false);
          break;
        case '2fa':
          setNeedsTwoFactor(true);
          setNeedsEmailCode(false);
          setWaitingForMobileConfirmation(false);
          break;
        case 'steamguard':
          setNeedsEmailCode(true);
          setNeedsTwoFactor(false);
          setWaitingForMobileConfirmation(false);
          break;
        case 'device-confirmation':
          setWaitingForMobileConfirmation(true);
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          break;
      }

      setLoading(false);
    };

    hubConnection.on('CredentialChallenge', handleCredentialChallenge);

    return () => {
      hubConnection.off('CredentialChallenge', handleCredentialChallenge);
    };
  }, [hubConnection]);

  const cancelPendingRequest = useCallback(() => {
    setLoading(false);
    setWaitingForMobileConfirmation(false);
    setPendingChallenge(null);
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
    setPendingChallenge(null);
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

    // Validate credentials based on current state
    if (!pendingChallenge) {
      // Initial login - start the login process
      if (!username.trim() || !password.trim()) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Please enter both username and password',
          details: { notificationType: 'error' }
        });
        return false;
      }

      setLoading(true);

      try {
        // Start login to get initial challenge
        const challenge = await hubConnection.invoke<CredentialChallenge | null>('StartLogin', sessionId);

        if (challenge) {
          setPendingChallenge(challenge);

          // Provide credentials based on challenge type
          if (challenge.credentialType === 'password') {
            // Send username first, then password
            await hubConnection.invoke('ProvideCredential', sessionId, challenge, username);

            // Wait for next challenge (password)
            const passChallenge = await hubConnection.invoke<CredentialChallenge | null>('WaitForChallenge', sessionId, 30);
            if (passChallenge?.credentialType === 'password') {
              await hubConnection.invoke('ProvideCredential', sessionId, passChallenge, password);
            }
          }
        }

        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'Credentials sent',
          details: { notificationType: 'success' }
        });

        return true;
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to authenticate';
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
    }

    // Handle 2FA code
    if (needsTwoFactor && pendingChallenge) {
      if (!twoFactorCode.trim()) {
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
        await hubConnection.invoke('ProvideCredential', sessionId, pendingChallenge, twoFactorCode);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: '2FA code sent',
          details: { notificationType: 'success' }
        });
        resetAuthForm();
        onSuccess?.();
        return true;
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to send 2FA code';
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
    }

    // Handle email verification code
    if (needsEmailCode && pendingChallenge) {
      if (!emailCode.trim()) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Please enter your email verification code',
          details: { notificationType: 'error' }
        });
        return false;
      }

      setLoading(true);

      try {
        await hubConnection.invoke('ProvideCredential', sessionId, pendingChallenge, emailCode);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'Email code sent',
          details: { notificationType: 'success' }
        });
        resetAuthForm();
        onSuccess?.();
        return true;
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to send email code';
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
    }

    return false;
  }, [
    sessionId,
    hubConnection,
    username,
    password,
    twoFactorCode,
    emailCode,
    needsTwoFactor,
    needsEmailCode,
    pendingChallenge,
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
