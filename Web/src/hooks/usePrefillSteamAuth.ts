import { useState, useCallback, useEffect, useRef } from 'react';
import type { HubConnection } from '@microsoft/signalr';
import { useNotifications } from '@contexts/notifications';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';
import { getEventName } from '@components/features/prefill/hooks/prefillConstants';

interface CredentialChallenge {
  type: string;
  challengeId: string;
  credentialType: string;
  serverPublicKey: string;
  email?: string;
  authUrl?: string;
  createdAt: string;
  expiresAt: string;
}

interface UsePrefillSteamAuthOptions {
  sessionId: string | null;
  hubConnection: HubConnection | null;
  onSuccess?: () => void;
  onError?: (message: string) => void;
  onDeviceConfirmationTimeout?: () => void;
  serviceId?: string;
}

// Timeout for device confirmation (60 seconds - users need time to notice and approve)
const DEVICE_CONFIRMATION_TIMEOUT_MS = 60000;

/**
 * Hook for Steam authentication within a prefill Docker container.
 * Uses SignalR hub methods to handle encrypted credential exchange.
 */
export function usePrefillSteamAuth(options: UsePrefillSteamAuthOptions) {
  const {
    sessionId,
    hubConnection,
    onSuccess,
    onError,
    onDeviceConfirmationTimeout,
    serviceId = 'steam'
  } = options;
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

  // Track if we're waiting for the daemon to process an authorization code (Epic OAuth)
  const isWaitingForAuthCodeProcessingRef = useRef(false);

  // Track if we've started authentication (to avoid showing error on initial NotAuthenticated state)
  const hasStartedAuthRef = useRef(false);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  // Epic OAuth state
  const [needsAuthorizationCode, setNeedsAuthorizationCode] = useState(false);
  const [authorizationUrl, setAuthorizationUrl] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');

  // Listen for AuthStateChanged - this is the reliable way to know when login succeeds
  useEffect(() => {
    if (!hubConnection || !sessionId) return;

    const handleAuthStateChanged = ({
      sessionId: payloadSessionId,
      authState
    }: {
      sessionId: string;
      authState: string;
    }) => {
      if (payloadSessionId !== sessionId) return;

      if (authState === 'Authenticated') {
        // Login succeeded - clear any pending timeouts and notify success
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }
        isWaitingForDeviceConfirmationRef.current = false;
        isWaitingForAuthCodeProcessingRef.current = false;
        setWaitingForMobileConfirmation(false);
        setLoading(false);

        // Note: PrefillPanel's handleAuthStateChanged handles the log entry,
        // so we don't add a notification here to avoid duplicates
        hasStartedAuthRef.current = false;
        onSuccess?.();
      } else if (authState === 'NotAuthenticated') {
        // Login failed - clear any pending timeouts and reset state
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }

        // Only show error if we were actively trying to authenticate
        const wasAuthenticating = hasStartedAuthRef.current;

        isWaitingForDeviceConfirmationRef.current = false;
        isWaitingForAuthCodeProcessingRef.current = false;
        setWaitingForMobileConfirmation(false);
        setNeedsTwoFactor(false);
        setNeedsEmailCode(false);
        setLoading(false);
        setPendingChallenge(null);

        if (wasAuthenticating) {
          addNotification({
            type: 'generic',
            status: 'failed',
            message: 'Authentication failed. Please check your credentials and try again.',
            details: { notificationType: 'error' }
          });
          onError?.('Authentication failed');
        }
        hasStartedAuthRef.current = false;
      }
    };

    const eventName = getEventName('AuthStateChanged', serviceId);
    hubConnection.on(eventName, handleAuthStateChanged);

    return () => {
      hubConnection.off(eventName, handleAuthStateChanged);
    };
  }, [hubConnection, sessionId, onSuccess, addNotification, onError, serviceId]);

  // Listen for credential challenges from the daemon
  useEffect(() => {
    if (!hubConnection || !sessionId) return;

    const handleCredentialChallenge = async ({
      sessionId: payloadSessionId,
      challenge
    }: {
      sessionId: string;
      challenge: CredentialChallenge;
    }) => {
      if (payloadSessionId !== sessionId) return;

      setPendingChallenge(challenge);

      // Set the appropriate state based on credential type
      switch (challenge.credentialType) {
        case 'password':
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          setNeedsAuthorizationCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case '2fa':
          setNeedsTwoFactor(true);
          setNeedsEmailCode(false);
          setNeedsAuthorizationCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case 'steamguard':
          setNeedsEmailCode(true);
          setNeedsTwoFactor(false);
          setNeedsAuthorizationCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case 'authorization-url':
          // If we were waiting for the daemon to process a previously submitted code,
          // a new authorization-url challenge means the code was rejected/expired
          if (isWaitingForAuthCodeProcessingRef.current) {
            isWaitingForAuthCodeProcessingRef.current = false;
            addNotification({
              type: 'generic',
              status: 'failed',
              message:
                'Authorization code was invalid or expired. Please try again with a new code.',
              details: { notificationType: 'error' }
            });
            // Clear the old code so user can paste a new one
            setAuthorizationCode('');
          }
          setNeedsAuthorizationCode(true);
          setAuthorizationUrl(challenge.authUrl ?? '');
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = false;
          break;
        case 'device-confirmation':
          setWaitingForMobileConfirmation(true);
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          setNeedsAuthorizationCode(false);
          isWaitingForDeviceConfirmationRef.current = true;

          // Send acknowledgement for device confirmation
          // This unblocks the daemon to continue polling Steam for approval
          // Note: We delay slightly to help WaitForChallenge see the file first
          await new Promise((resolve) => setTimeout(resolve, 300));
          try {
            await hubConnection.invoke('ProvideCredential', sessionId, challenge, 'confirm');
          } catch (err) {
            console.error('Failed to send device confirmation acknowledgement:', err);
          }
          break;
      }

      setLoading(false);
    };

    const eventName = getEventName('CredentialChallenge', serviceId);
    hubConnection.on(eventName, handleCredentialChallenge);

    return () => {
      hubConnection.off(eventName, handleCredentialChallenge);
    };
  }, [hubConnection, sessionId, serviceId, addNotification]);

  // Timeout for device confirmation - cancel daemon login and reset state
  useEffect(() => {
    if (waitingForMobileConfirmation && hubConnection && sessionId) {
      deviceConfirmationTimeoutRef.current = setTimeout(async () => {
        // Cancel the login on the daemon to reset its state
        try {
          await hubConnection.invoke('CancelLogin', sessionId);
        } catch (err) {
          console.error('[usePrefillSteamAuth] Failed to cancel login on daemon:', err);
        }

        // Reset all auth state
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
        hasStartedAuthRef.current = false;

        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Device confirmation timed out. Please try logging in again.',
          details: { notificationType: 'warning' }
        });
        onDeviceConfirmationTimeout?.();
      }, DEVICE_CONFIRMATION_TIMEOUT_MS);

      return () => {
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }
      };
    }
  }, [
    waitingForMobileConfirmation,
    hubConnection,
    sessionId,
    addNotification,
    onDeviceConfirmationTimeout
  ]);

  const cancelPendingRequest = useCallback(() => {
    setLoading(false);
    setWaitingForMobileConfirmation(false);
    setNeedsAuthorizationCode(false);
    isWaitingForDeviceConfirmationRef.current = false;
    isWaitingForAuthCodeProcessingRef.current = false;
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
    setAuthorizationCode('');
    setAuthorizationUrl('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setNeedsAuthorizationCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setLoading(false);
    setPendingChallenge(null);
    isWaitingForDeviceConfirmationRef.current = false;
    isWaitingForAuthCodeProcessingRef.current = false;
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
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallenge',
          sessionId,
          30
        );
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
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallenge',
          sessionId,
          30
        );
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

    // Handle Epic authorization code submission
    if (needsAuthorizationCode && pendingChallenge) {
      if (!authorizationCode.trim()) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Please enter the authorization code from Epic Games',
          details: { notificationType: 'error' }
        });
        return false;
      }

      setLoading(true);

      try {
        await hubConnection.invoke(
          'ProvideCredential',
          sessionId,
          pendingChallenge,
          authorizationCode
        );
        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'Authorization code sent, authenticating...',
          details: { notificationType: 'success' }
        });

        // Don't call WaitForChallenge here - rely on events instead.
        // The daemon will either:
        //   1. Send AuthStateChanged -> "Authenticated" (handled by useEffect above)
        //   2. Send a new CredentialChallenge -> authorization-url if code was rejected
        //      (handled by handleCredentialChallenge event handler which shows error)
        // This mirrors the device-confirmation pattern which already works correctly.
        isWaitingForAuthCodeProcessingRef.current = true;

        // Return false so the modal stays open while we wait for the event
        return false;
      } catch (err) {
        const errorMessage =
          err instanceof Error ? err.message : 'Failed to send authorization code';
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMessage,
          details: { notificationType: 'error' }
        });
        onError?.(errorMessage);
        setLoading(false);
        isWaitingForAuthCodeProcessingRef.current = false;
        return false;
      }
    }

    // Epic OAuth: start login to get authorization URL challenge
    if (serviceId === 'epic' && !needsAuthorizationCode) {
      setLoading(true);
      hasStartedAuthRef.current = true;

      try {
        const challenge = await hubConnection.invoke<CredentialChallenge | null>(
          'StartLogin',
          sessionId
        );

        if (challenge && challenge.credentialType === 'authorization-url') {
          setPendingChallenge(challenge);
          setNeedsAuthorizationCode(true);
          setAuthorizationUrl(challenge.authUrl ?? '');
          setLoading(false);
          return false; // Modal stays open, waiting for code
        }

        if (!challenge) {
          // No challenge - might already be logged in, or challenge comes via event
          // Wait briefly for a challenge event
          const eventChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallenge',
            sessionId,
            10
          );
          if (eventChallenge && eventChallenge.credentialType === 'authorization-url') {
            setPendingChallenge(eventChallenge);
            setNeedsAuthorizationCode(true);
            setAuthorizationUrl(eventChallenge.authUrl ?? '');
            setLoading(false);
            return false;
          }

          // No challenge at all - might already be authenticated
          resetAuthForm();
          onSuccess?.();
          setLoading(false);
          return true;
        }

        // Handle other challenge types
        setPendingChallenge(challenge);
        handleChallengeType(challenge);
        setLoading(false);
        return false;
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to start Epic login';
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

    // Steam: Initial login - start the login process
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
    hasStartedAuthRef.current = true;

    try {
      // Start login to get initial challenge (username)
      const challenge = await hubConnection.invoke<CredentialChallenge | null>(
        'StartLogin',
        sessionId
      );

      if (!challenge) {
        throw new Error('No challenge received from daemon');
      }

      // Daemon flow: username -> password -> (optional 2FA/steamguard/device-confirmation)
      if (challenge.credentialType === 'username') {
        // Send username
        await hubConnection.invoke('ProvideCredential', sessionId, challenge, username);

        // Wait for password challenge
        const passChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallenge',
          sessionId,
          30
        );
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
          const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallenge',
            sessionId,
            60
          );
          if (nextChallenge) {
            setPendingChallenge(nextChallenge);
            handleChallengeType(nextChallenge);

            // For device-confirmation, DON'T treat this as success yet
            // We wait for AuthStateChanged to trigger onSuccess
            // Return false so modal stays open
            if (nextChallenge.credentialType === 'device-confirmation') {
              setLoading(false);
              return false; // Modal should stay open
            }
          } else {
            // No more challenges - check if we were waiting for device confirmation
            if (isWaitingForDeviceConfirmationRef.current) {
              // WaitForChallenge timed out but we're in device confirmation mode
              // Wait for AuthStateChanged instead of assuming success
              setLoading(false);
              return false; // Modal should stay open
            } else {
              // Normal login success
              resetAuthForm();
              onSuccess?.();
              setLoading(false);
              return true;
            }
          }
        } else {
          // Unexpected challenge type
          setPendingChallenge(passChallenge);
          handleChallengeType(passChallenge);
          setLoading(false);
          return false; // Need more input
        }
      } else {
        // Handle other initial challenge types
        setPendingChallenge(challenge);
        handleChallengeType(challenge);
        setLoading(false);
        return false; // Need more input
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
    authorizationCode,
    needsTwoFactor,
    needsEmailCode,
    needsAuthorizationCode,
    pendingChallenge,
    addNotification,
    resetAuthForm,
    onSuccess,
    onError,
    serviceId
  ]);

  // Helper to set state based on challenge type
  const handleChallengeType = useCallback((challenge: CredentialChallenge) => {
    switch (challenge.credentialType) {
      case '2fa':
        setNeedsTwoFactor(true);
        setNeedsEmailCode(false);
        setNeedsAuthorizationCode(false);
        setWaitingForMobileConfirmation(false);
        isWaitingForDeviceConfirmationRef.current = false;
        break;
      case 'steamguard':
        setNeedsEmailCode(true);
        setNeedsTwoFactor(false);
        setNeedsAuthorizationCode(false);
        setWaitingForMobileConfirmation(false);
        isWaitingForDeviceConfirmationRef.current = false;
        break;
      case 'authorization-url':
        setNeedsAuthorizationCode(true);
        setAuthorizationUrl(challenge.authUrl ?? '');
        setNeedsTwoFactor(false);
        setNeedsEmailCode(false);
        setWaitingForMobileConfirmation(false);
        isWaitingForDeviceConfirmationRef.current = false;
        break;
      case 'device-confirmation':
        setWaitingForMobileConfirmation(true);
        setNeedsTwoFactor(false);
        setNeedsEmailCode(false);
        setNeedsAuthorizationCode(false);
        isWaitingForDeviceConfirmationRef.current = true;
        break;
    }
  }, []);

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
    emailCode,
    needsAuthorizationCode,
    authorizationUrl,
    authorizationCode
  };

  const actions: SteamAuthActions = {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    setUseManualCode,
    setNeedsTwoFactor,
    setWaitingForMobileConfirmation,
    setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return {
    state,
    actions,
    trigger2FAPrompt,
    triggerEmailPrompt
  };
}
