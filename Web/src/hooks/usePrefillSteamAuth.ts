import { useState, useCallback, useEffect, useRef } from 'react';
import type { HubConnection } from '@microsoft/signalr';
import { useNotifications } from '@contexts/notifications';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';
import { getEventName } from '@components/features/prefill/hooks/prefillConstants';

export interface CredentialChallenge {
  type: string;
  challengeId: string;
  credentialType: string;
  serverPublicKey: string;
  email?: string;
  authUrl?: string;
  /** Microsoft device-code (Xbox): short code the user types at the verification URL. */
  userCode?: string;
  /** Microsoft device-code (Xbox): URL the user opens to enter the userCode. */
  verificationUri?: string;
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

// Fallback cap for the Xbox device-code (Microsoft OAuth device flow) timeout when the challenge
// carries no usable expiry. Microsoft device codes typically last ~15 minutes; the user has to
// open a browser, sign in, and approve, so this is far longer than the mobile-confirmation window.
const DEVICE_CODE_TIMEOUT_MS = 15 * 60 * 1000;

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

  // Device-confirmation challenges are delivered to a subscribed connection twice (once via the
  // session-subscriber broadcast, once via the Clients.All hub mirror the persistent-login modal
  // relies on), so handleCredentialChallenge fires twice for the same challenge. Auto-sending the
  // 'confirm' ack on both drives a second, racy ProvideCredential that clears the pending-challenge
  // the sequential login flow is still waiting on, collapsing the modal. Track which challenge ids
  // we have already acked so the ack is sent at most once per unique challenge.
  const confirmedChallengeIdsRef = useRef<Set<string>>(new Set());

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

  // Xbox device-code state (Microsoft OAuth device flow):
  // the user opens deviceVerificationUri and enters deviceUserCode in their own browser.
  const [needsDeviceCode, setNeedsDeviceCode] = useState(false);
  const [deviceUserCode, setDeviceUserCode] = useState('');
  const [deviceVerificationUri, setDeviceVerificationUri] = useState('');

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
        setNeedsDeviceCode(false);
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
        setNeedsDeviceCode(false);
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

          // Send acknowledgement for device confirmation exactly ONCE per challenge. The manager
          // delivers this challenge twice (subscriber broadcast + Clients.All hub mirror), so without
          // this guard the ack is sent twice; the second, racy ProvideCredential clears the pending
          // challenge the sequential login flow is awaiting and collapses the "waiting for approval"
          // modal. This unblocks the daemon to continue polling Steam for approval.
          // Note: We delay slightly to help WaitForChallenge see the file first.
          if (!confirmedChallengeIdsRef.current.has(challenge.challengeId)) {
            confirmedChallengeIdsRef.current.add(challenge.challengeId);
            await new Promise((resolve) => setTimeout(resolve, 300));
            try {
              await hubConnection.invoke('ProvideCredentialAsync', sessionId, challenge, 'confirm');
            } catch (err) {
              console.error('Failed to send device confirmation acknowledgement:', err);
            }
          }
          break;
        case 'device-code':
          // Microsoft OAuth device flow (Xbox): surface the user code + verification URL and
          // wait for AuthStateChanged once the user approves in their own browser.
          setNeedsDeviceCode(true);
          setDeviceUserCode(challenge.userCode ?? '');
          setDeviceVerificationUri(challenge.verificationUri ?? challenge.authUrl ?? '');
          setNeedsTwoFactor(false);
          setNeedsEmailCode(false);
          setNeedsAuthorizationCode(false);
          setWaitingForMobileConfirmation(false);
          isWaitingForDeviceConfirmationRef.current = true;
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
          await hubConnection.invoke('CancelLoginAsync', sessionId);
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

  // Timeout for the Xbox device-code flow. Unlike Steam's device-confirmation, Xbox sets
  // `needsDeviceCode` (and leaves `waitingForMobileConfirmation` false), so the effect above never
  // fires for it. Without this, an unapproved device code would poll the daemon forever. We honour
  // the challenge's real `expiresAt` when present, capped by DEVICE_CODE_TIMEOUT_MS. Reuses the
  // shared timeout ref (device-code and device-confirmation are mutually exclusive), so the
  // AuthStateChanged / cancel / reset paths already clear it on success or teardown.
  useEffect(() => {
    if (!needsDeviceCode || !hubConnection || !sessionId) return;

    const expiresAtMs = pendingChallenge?.expiresAt
      ? new Date(pendingChallenge.expiresAt).getTime()
      : Number.NaN;
    const remainingMs = Number.isFinite(expiresAtMs)
      ? expiresAtMs - Date.now()
      : DEVICE_CODE_TIMEOUT_MS;
    const delayMs = Math.min(Math.max(remainingMs, 0), DEVICE_CODE_TIMEOUT_MS);

    deviceConfirmationTimeoutRef.current = setTimeout(async () => {
      try {
        await hubConnection.invoke('CancelLoginAsync', sessionId);
      } catch (err) {
        console.error(
          '[usePrefillSteamAuth] Failed to cancel Xbox device-code login on daemon:',
          err
        );
      }

      setNeedsDeviceCode(false);
      setDeviceUserCode('');
      setDeviceVerificationUri('');
      setLoading(false);
      setPendingChallenge(null);
      isWaitingForDeviceConfirmationRef.current = false;
      hasStartedAuthRef.current = false;

      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Xbox sign-in code expired. Please try logging in again.',
        details: { notificationType: 'warning' }
      });
      onDeviceConfirmationTimeout?.();
    }, delayMs);

    return () => {
      if (deviceConfirmationTimeoutRef.current) {
        clearTimeout(deviceConfirmationTimeoutRef.current);
        deviceConfirmationTimeoutRef.current = null;
      }
    };
  }, [
    needsDeviceCode,
    hubConnection,
    sessionId,
    pendingChallenge,
    addNotification,
    onDeviceConfirmationTimeout
  ]);

  const cancelPendingRequest = useCallback(() => {
    setLoading(false);
    setWaitingForMobileConfirmation(false);
    setNeedsAuthorizationCode(false);
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
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
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
    setUseManualCode(false);
    setLoading(false);
    setPendingChallenge(null);
    isWaitingForDeviceConfirmationRef.current = false;
    isWaitingForAuthCodeProcessingRef.current = false;
    confirmedChallengeIdsRef.current.clear();
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
        await hubConnection.invoke(
          'ProvideCredentialAsync',
          sessionId,
          pendingChallenge,
          twoFactorCode
        );
        addNotification({
          type: 'generic',
          status: 'completed',
          message: '2FA code sent',
          details: { notificationType: 'success' }
        });

        // Wait for next challenge or success
        // AuthStateChanged will trigger onSuccess if login succeeds
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
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
        await hubConnection.invoke(
          'ProvideCredentialAsync',
          sessionId,
          pendingChallenge,
          emailCode
        );
        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'Email code sent',
          details: { notificationType: 'success' }
        });

        // Wait for next challenge or success
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
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
          'ProvideCredentialAsync',
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
          'StartLoginAsync',
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
            'WaitForChallengeAsync',
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

    // Xbox: Microsoft OAuth device flow - start login to get the device-code challenge.
    // No password ever enters the container; the user approves in their own browser.
    if (serviceId === 'xbox') {
      setLoading(true);
      hasStartedAuthRef.current = true;

      try {
        const challenge = await hubConnection.invoke<CredentialChallenge | null>(
          'StartLoginAsync',
          sessionId
        );

        if (challenge && challenge.credentialType === 'device-code') {
          setPendingChallenge(challenge);
          handleChallengeType(challenge);
          setLoading(false);
          return false; // Modal stays open while the user approves in their browser
        }

        if (!challenge) {
          // Challenge may arrive via event shortly after StartLogin.
          const eventChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallengeAsync',
            sessionId,
            10
          );
          if (eventChallenge && eventChallenge.credentialType === 'device-code') {
            setPendingChallenge(eventChallenge);
            handleChallengeType(eventChallenge);
            setLoading(false);
            return false;
          }

          // No challenge at all - might already be authenticated.
          resetAuthForm();
          onSuccess?.();
          setLoading(false);
          return true;
        }

        // Unexpected challenge type - surface it via the generic handler.
        setPendingChallenge(challenge);
        handleChallengeType(challenge);
        setLoading(false);
        return false;
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to start Xbox login';
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
        'StartLoginAsync',
        sessionId
      );

      if (!challenge) {
        throw new Error('No challenge received from daemon');
      }

      // Daemon flow: username -> password -> (optional 2FA/steamguard/device-confirmation)
      if (challenge.credentialType === 'username') {
        // Send username
        await hubConnection.invoke('ProvideCredentialAsync', sessionId, challenge, username);

        // Wait for password challenge
        const passChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (!passChallenge) {
          throw new Error('No password challenge received');
        }

        if (passChallenge.credentialType === 'password') {
          // Send password
          await hubConnection.invoke('ProvideCredentialAsync', sessionId, passChallenge, password);

          addNotification({
            type: 'generic',
            status: 'completed',
            message: 'Credentials sent, authenticating...',
            details: { notificationType: 'success' }
          });

          // Wait for next challenge (2FA, steamguard, device-confirmation) or success
          const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallengeAsync',
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
            // WaitForChallenge returned nothing. Do NOT assume success here. A device-confirmation
            // (Steam mobile approval) login legitimately has no further challenge while the daemon
            // polls for the phone tap, and the manager caches then CLEARS the pending challenge as
            // the 'confirm' ack is sent, so a null here does not mean "logged in" - it routinely
            // happens mid device-confirmation and used to fire a false success that closed the modal
            // before the user could approve. The authoritative signal is the AuthStateChanged event
            // (Authenticated -> onSuccess, NotAuthenticated -> error surfaced by its handler), so keep
            // the modal open and let that decide instead of racing a false success. A genuinely
            // successful password-only login still closes the modal via AuthStateChanged: Authenticated.
            setLoading(false);
            return false; // Modal stays open; AuthStateChanged drives the real outcome
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
      case 'device-code':
        // Microsoft OAuth device flow (Xbox): show the user code + verification URL and
        // wait for AuthStateChanged once the user approves in their own browser.
        setNeedsDeviceCode(true);
        setDeviceUserCode(challenge.userCode ?? '');
        setDeviceVerificationUri(challenge.verificationUri ?? challenge.authUrl ?? '');
        setNeedsTwoFactor(false);
        setNeedsEmailCode(false);
        setNeedsAuthorizationCode(false);
        setWaitingForMobileConfirmation(false);
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
    authorizationCode,
    needsDeviceCode,
    deviceUserCode,
    deviceVerificationUri
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
