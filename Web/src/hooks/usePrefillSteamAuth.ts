import { useState, useCallback, useEffect, useRef } from 'react';
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

// Timeout for device confirmation (60 seconds - users need time to notice and approve)
const DEVICE_CONFIRMATION_TIMEOUT_MS = 60000;

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
  const deviceConfirmationTimeoutRef = useRef<NodeJS.Timeout | null>(null);

  // Track if we're in device confirmation mode to handle success correctly
  const isWaitingForDeviceConfirmationRef = useRef(false);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  // Listen for AuthStateChanged - this is the reliable way to know when login succeeds
  useEffect(() => {
    if (!hubConnection || !sessionId) return;

    const handleAuthStateChanged = (_sessionId: string, newState: string) => {
      if (_sessionId !== sessionId) return;

      console.log('[usePrefillSteamAuth] AuthStateChanged:', newState);

      if (newState === 'Authenticated') {
        // Login succeeded - clear any pending timeouts and notify success
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }
        isWaitingForDeviceConfirmationRef.current = false;
        setWaitingForMobileConfirmation(false);
        setLoading(false);

        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'Successfully logged in to Steam',
          details: { notificationType: 'success' }
        });

        // The PrefillPanel's handleAuthStateChanged will also close the modal,
        // but we call onSuccess here for consistency
        onSuccess?.();
      }
    };

    hubConnection.on('AuthStateChanged', handleAuthStateChanged);

    return () => {
      hubConnection.off('AuthStateChanged', handleAuthStateChanged);
    };
  }, [hubConnection, sessionId, onSuccess, addNotification]);

  // Listen for credential challenges from the daemon
  useEffect(() => {
    if (!hubConnection || !sessionId) return;

    const handleCredentialChallenge = async (_sessionId: string, challenge: CredentialChallenge) => {
      if (_sessionId !== sessionId) return;

      console.log('[usePrefillSteamAuth] CredentialChallenge:', challenge.credentialType);
      setPendingChallenge(challenge);

      // Set the appropriate state based on credential type
      switch (challenge.credentialType) {
        case 'password':
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case '2fa':
          setNeedsTwoFactor(true);
          setNeedsEmailCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case 'steamguard':
          setNeedsEmailCode(true);
          setNeedsTwoFactor(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case 'device-confirmation':
          setWaitingForMobileConfirmation(true);
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          isWaitingForDeviceConfirmationRef.current = true;

          // Send acknowledgement for device confirmation
          // This unblocks the daemon to continue polling Steam for approval
          // Note: We delay slightly to help WaitForChallenge see the file first
          await new Promise(resolve => setTimeout(resolve, 300));
          try {
            await hubConnection.invoke('ProvideCredential', sessionId, challenge, 'confirm');
            console.log('[usePrefillSteamAuth] Device confirmation acknowledged');
          } catch (err) {
            console.error('Failed to send device confirmation acknowledgement:', err);
          }
          break;
      }

      setLoading(false);
    };

    hubConnection.on('CredentialChallenge', handleCredentialChallenge);

    return () => {
      hubConnection.off('CredentialChallenge', handleCredentialChallenge);
    };
  }, [hubConnection, sessionId]);

  // Timeout for device confirmation - switch to manual 2FA code entry after timeout
  useEffect(() => {
    if (waitingForMobileConfirmation) {
      deviceConfirmationTimeoutRef.current = setTimeout(() => {
        console.log('[usePrefillSteamAuth] Device confirmation timed out, switching to manual code');
        setWaitingForMobileConfirmation(false);
        isWaitingForDeviceConfirmationRef.current = false;
        setNeedsTwoFactor(true);
        setUseManualCode(true);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Device confirmation timed out. Please enter your Steam Guard code manually.',
          details: { notificationType: 'warning' }
        });
      }, DEVICE_CONFIRMATION_TIMEOUT_MS);

      return () => {
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }
      };
    }
  }, [waitingForMobileConfirmation, addNotification]);

  const cancelPendingRequest = useCallback(() => {
    setLoading(false);
    setWaitingForMobileConfirmation(false);
    isWaitingForDeviceConfirmationRef.current = false;
    setPendingChallenge(null);
    if (deviceConfirmationTimeoutRef.current) {
      clearTimeout(deviceConfirmationTimeoutRef.current);
      deviceConfirmationTimeoutRef.current = null;
    }
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
    isWaitingForDeviceConfirmationRef.current = false;
    if (deviceConfirmationTimeoutRef.current) {
      clearTimeout(deviceConfirmationTimeoutRef.current);
      deviceConfirmationTimeoutRef.current = null;
    }
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

        // Wait for next challenge or success
        // AuthStateChanged will trigger onSuccess if login succeeds
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>('WaitForChallenge', sessionId, 30);
        if (nextChallenge) {
          setPendingChallenge(nextChallenge);
          handleChallengeType(nextChallenge);
        } else {
          // No more challenges - login likely successful
          // AuthStateChanged should have fired, but call onSuccess as fallback
          resetAuthForm();
          onSuccess?.();
        }
        setLoading(false);
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

        // Wait for next challenge or success
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>('WaitForChallenge', sessionId, 30);
        if (nextChallenge) {
          setPendingChallenge(nextChallenge);
          handleChallengeType(nextChallenge);
        } else {
          resetAuthForm();
          onSuccess?.();
        }
        setLoading(false);
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
      // Start login to get initial challenge (username)
      const challenge = await hubConnection.invoke<CredentialChallenge | null>('StartLogin', sessionId);

      if (!challenge) {
        throw new Error('No challenge received from daemon');
      }

      // Daemon flow: username -> password -> (optional 2FA/steamguard/device-confirmation)
      if (challenge.credentialType === 'username') {
        // Send username
        await hubConnection.invoke('ProvideCredential', sessionId, challenge, username);

        // Wait for password challenge
        const passChallenge = await hubConnection.invoke<CredentialChallenge | null>('WaitForChallenge', sessionId, 30);
        if (!passChallenge) {
          throw new Error('No password challenge received');
        }

        if (passChallenge.credentialType === 'password') {
          // Send password
          await hubConnection.invoke('ProvideCredential', sessionId, passChallenge, password);

          addNotification({
            type: 'generic',
            status: 'completed',
            message: 'Credentials sent, authenticating...',
            details: { notificationType: 'success' }
          });

          // Wait for next challenge (2FA, steamguard, device-confirmation) or success
          const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>('WaitForChallenge', sessionId, 60);
          if (nextChallenge) {
            setPendingChallenge(nextChallenge);
            handleChallengeType(nextChallenge);

            // For device-confirmation, DON'T treat this as success yet
            // We wait for AuthStateChanged to trigger onSuccess
            if (nextChallenge.credentialType === 'device-confirmation') {
              console.log('[usePrefillSteamAuth] Device confirmation required, waiting for approval...');
              // Don't call onSuccess here - wait for AuthStateChanged
            }
          } else {
            // No more challenges - check if we were waiting for device confirmation
            if (isWaitingForDeviceConfirmationRef.current) {
              // WaitForChallenge timed out but we're in device confirmation mode
              // Wait for AuthStateChanged instead of assuming success
              console.log('[usePrefillSteamAuth] WaitForChallenge returned null during device confirmation, waiting for AuthStateChanged...');
            } else {
              // Normal login success
              resetAuthForm();
              onSuccess?.();
            }
          }
        } else {
          // Unexpected challenge type
          setPendingChallenge(passChallenge);
          handleChallengeType(passChallenge);
        }
      } else {
        // Handle other initial challenge types
        setPendingChallenge(challenge);
        handleChallengeType(challenge);
      }

      setLoading(false);
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

  // Helper to set state based on challenge type
  const handleChallengeType = useCallback((challenge: CredentialChallenge) => {
    switch (challenge.credentialType) {
      case '2fa':
        setNeedsTwoFactor(true);
        setNeedsEmailCode(false);
        setWaitingForMobileConfirmation(false);
        isWaitingForDeviceConfirmationRef.current = false;
        break;
      case 'steamguard':
        setNeedsEmailCode(true);
        setNeedsTwoFactor(false);
        setWaitingForMobileConfirmation(false);
        isWaitingForDeviceConfirmationRef.current = false;
        break;
      case 'device-confirmation':
        setWaitingForMobileConfirmation(true);
        setNeedsTwoFactor(false);
        setNeedsEmailCode(false);
        isWaitingForDeviceConfirmationRef.current = true;
        break;
    }
  }, []);

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
