import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import type { HubConnection } from '@microsoft/signalr';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler, useNotifySuccess } from './useErrorHandler';
import { getErrorMessage } from '@utils/error';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';
import { loginAttemptTimeoutMs, STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS } from './loginAttemptTimeout';
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
  /**
   * Tracker id of the daemon sign-in this challenge belongs to. The notification card
   * carries it so the bar's X cancels through /api/operations/{id}/cancel; a card that
   * never receives one still shows the wait, just without the X.
   */
  operationId?: string;
}

interface UsePrefillSteamAuthOptions {
  sessionId: string | null;
  hubConnection: HubConnection | null;
  onSuccess?: () => void;
  onError?: (message: string) => void;
  serviceId?: string;
}

/**
 * Hook for Steam authentication within a prefill Docker container.
 * Uses SignalR hub methods to handle encrypted credential exchange.
 */
export function usePrefillSteamAuth(options: UsePrefillSteamAuthOptions) {
  const { sessionId, hubConnection, onSuccess, onError, serviceId = 'steam' } = options;
  const { addNotification, removeNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { notifySuccess } = useNotifySuccess();
  const { t } = useTranslation();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [pendingChallenge, setPendingChallenge] = useState<CredentialChallenge | null>(null);
  const [error, setError] = useState<string | null>(null);
  const deviceConfirmationTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  // Both mutually exclusive waits keep their accepted challenge's deadline across effect reruns.
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);
  const waitRef = useRef<{ sessionId: string; challengeId: string; deadline: number } | null>(null);
  const loginEpochRef = useRef(0);
  const sessionIdRef = useRef(sessionId);
  sessionIdRef.current = sessionId;

  // SignalR hands back a brand new connection object every time it rebuilds the socket, and the two
  // waits below must not read that as a fresh attempt. While the connection was a dependency of
  // their effects, a reconnect cleared the timer and armed it again from full, so the countdown
  // jumped back to the top and the window stopped being a ceiling at all. The event handlers still
  // list the connection and still re-register on the new one; only the clocks read it through here.
  const hubConnectionRef = useRef(hubConnection);
  useEffect(() => {
    hubConnectionRef.current = hubConnection;
  }, [hubConnection]);

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

  // Card the notification bar shows while the daemon waits for the person to finish signing in.
  // This hook owns the card's whole life: no server event raises it and none clears it, so every
  // path that ends an attempt calls endLoginCard. The platform rides in the message rather than in
  // a card type per platform, the way the scheduled prefill card names its service.
  const loginCardIdRef = useRef<string | null>(null);
  const serviceLabel = t(
    `management.schedules.services.scheduledPrefill.config.services.${serviceId}`,
    { defaultValue: serviceId }
  );

  const showLoginCard = useCallback(
    (operationId: string | undefined) => {
      if (loginCardIdRef.current) return;

      loginCardIdRef.current = addNotification({
        type: 'prefill_login',
        status: 'running',
        // No progress key at all: a sign-in has no denominator, and any finite value here
        // turns the travelling sweep into a bar that sits still for the whole wait.
        message: t('prefill.auth.waitingForSignIn', { service: serviceLabel }),
        details: { operationId }
      });
    },
    [addNotification, t, serviceLabel]
  );

  const endLoginCard = useCallback(() => {
    if (!loginCardIdRef.current) return;

    removeNotification(loginCardIdRef.current);
    loginCardIdRef.current = null;
  }, [removeNotification]);

  // Closing the panel mid-sign-in unmounts this hook, and the card lives on in the notification
  // context, so drop it here or it stays on screen with nothing left to clear it.
  useEffect(() => endLoginCard, [endLoginCard]);

  useEffect(() => {
    waitRef.current = null;
    setLoginDeadline(null);
    hasStartedAuthRef.current = false;
    setPendingChallenge(null);
    setWaitingForMobileConfirmation(false);
    setNeedsDeviceCode(false);
    return () => {
      loginEpochRef.current += 1;
      waitRef.current = null;
    };
  }, [sessionId]);

  // Helper to set state based on challenge type
  const handleChallengeType = useCallback(
    (challenge: CredentialChallenge) => {
      if (!sessionId || sessionIdRef.current !== sessionId || !hasStartedAuthRef.current)
        return false;
      const expiry = Date.parse(challenge.expiresAt);
      if (!Number.isFinite(expiry)) {
        setError(t('prefill.auth.errors.noChallenge'));
        setLoading(false);
        return false;
      }
      const waiting =
        challenge.credentialType === 'device-confirmation' ||
        challenge.credentialType === 'device-code';
      if (waiting) {
        const current = waitRef.current;
        const duration =
          challenge.credentialType === 'device-confirmation'
            ? STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS
            : loginAttemptTimeoutMs(serviceId);
        const deadline =
          current?.sessionId === sessionId && current.challengeId === challenge.challengeId
            ? Math.min(current.deadline, expiry)
            : Math.min(Date.now() + duration, expiry);
        waitRef.current = { sessionId, challengeId: challenge.challengeId, deadline };
        setLoginDeadline(deadline);
      } else {
        if (expiry <= Date.now()) {
          setError(t('prefill.auth.errors.noChallenge'));
          setLoading(false);
          return false;
        }
        waitRef.current = null;
        setLoginDeadline(null);
      }
      setPendingChallenge(challenge);
      setNeedsDeviceCode(false);
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
      return true;
    },
    [sessionId, serviceId, t]
  );

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
        loginEpochRef.current += 1;
        waitRef.current = null;
        setLoginDeadline(null);
        setPendingChallenge(null);
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
        endLoginCard();

        // Note: PrefillPanel's handleAuthStateChanged handles the log entry,
        // so we don't add a notification here to avoid duplicates
        hasStartedAuthRef.current = false;
        onSuccess?.();
      } else if (authState === 'NotAuthenticated') {
        loginEpochRef.current += 1;
        waitRef.current = null;
        setLoginDeadline(null);
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
        // The password the daemon just refused must not stay in the box. This is the commonest way
        // a sign-in ends badly, and leaving the wrong one sitting there invites a second submit of
        // the same thing.
        setPassword('');
        setLoading(false);
        setPendingChallenge(null);
        endLoginCard();

        if (wasAuthenticating) {
          // This is the sentence a person reads when the daemon or Steam ends the attempt before
          // our own clock does, which is the usual way a phone approval ends, so it is the one
          // that most needs to arrive in their own language.
          const refused = t('prefill.auth.signInRefused');
          addNotification({
            type: 'generic',
            status: 'failed',
            message: refused,
            details: { notificationType: 'error' }
          });
          setError(refused);
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
  }, [hubConnection, sessionId, onSuccess, addNotification, onError, serviceId, endLoginCard, t]);

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

      if (!handleChallengeType(challenge)) return;

      if (
        challenge.credentialType === 'authorization-url' &&
        isWaitingForAuthCodeProcessingRef.current
      ) {
        isWaitingForAuthCodeProcessingRef.current = false;
        const rejectedCode = t('prefill.auth.authorizationCodeRejected');
        addNotification({
          type: 'generic',
          status: 'failed',
          message: rejectedCode,
          details: { notificationType: 'error' }
        });
        setError(rejectedCode);
        setAuthorizationCode('');
      }

      if (
        challenge.credentialType === 'device-confirmation' &&
        !confirmedChallengeIdsRef.current.has(challenge.challengeId)
      ) {
        confirmedChallengeIdsRef.current.add(challenge.challengeId);
        const epoch = loginEpochRef.current;
        await new Promise((resolve) => setTimeout(resolve, 300));
        if (
          epoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          waitRef.current?.challengeId !== challenge.challengeId
        )
          return;
        try {
          await hubConnectionRef.current?.invoke(
            'ProvideCredentialAsync',
            sessionId,
            challenge,
            'confirm'
          );
        } catch (err) {
          notifyError('Failed to send device confirmation acknowledgement', err, {
            silent: true,
            logLabel: 'usePrefillSteamAuth device confirmation ack'
          });
        }
        if (epoch !== loginEpochRef.current || sessionIdRef.current !== sessionId) return;
      }

      setLoading(false);
    };

    const eventName = getEventName('CredentialChallenge', serviceId);
    hubConnection.on(eventName, handleCredentialChallenge);

    return () => {
      hubConnection.off(eventName, handleCredentialChallenge);
    };
  }, [hubConnection, sessionId, serviceId, addNotification, notifyError, handleChallengeType, t]);

  // Timeout for device confirmation - cancel daemon login and reset state
  useEffect(() => {
    if (waitingForMobileConfirmation && sessionId && loginDeadline !== null) {
      const epoch = loginEpochRef.current;
      const challengeId = waitRef.current?.challengeId;
      deviceConfirmationTimeoutRef.current = setTimeout(
        async () => {
          if (
            epoch !== loginEpochRef.current ||
            sessionIdRef.current !== sessionId ||
            waitRef.current?.challengeId !== challengeId
          )
            return;
          // End the browser attempt before awaiting cancellation so retries cannot be overwritten.
          loginEpochRef.current += 1;
          waitRef.current = null;
          setLoginDeadline(null);
          // Land back on the sign-in form with the account name still typed in, so trying again is
          // one password away.
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
          endLoginCard();

          // Said in the modal, not in a card behind it. The card version of this lasted five seconds
          // (AUTO_DISMISS_DELAY_MS) under a modal that was closing in the same tick, so the wait
          // appeared to end for no reason at all. The modal now stays open holding the reason.
          setError(t('prefill.auth.approvalTimedOut'));
          try {
            await hubConnectionRef.current?.invoke('CancelLoginAsync', sessionId);
          } catch (err) {
            // The modal already holds the timeout reason; transport cancellation is best effort.
            notifyError('Failed to cancel login on daemon', err, {
              silent: true,
              logLabel: 'usePrefillSteamAuth device confirmation timeout cancel'
            });
          }
        },
        Math.max(0, loginDeadline - Date.now())
      );

      return () => {
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }
      };
    }
  }, [
    waitingForMobileConfirmation,
    sessionId,
    pendingChallenge?.challengeId,
    loginDeadline,
    notifyError,
    endLoginCard,
    t
  ]);

  // Timeout for the Xbox device-code flow. Unlike Steam's device-confirmation, Xbox sets
  // `needsDeviceCode` (and leaves `waitingForMobileConfirmation` false), so the effect above never
  // fires for it. Without this, an unapproved device code would poll the daemon forever. The window
  // is the manager's own, and it is the only real one here: the expiry Microsoft puts on the device
  // code stays inside the daemon's polling loop and is never sent, while the expiry the challenge
  // does carry is a placeholder a full day out, wide enough that reading it would promise the
  // person far longer than the sign-in actually gets. The Configure card counts the same window
  // down for the same code, so both Xbox surfaces now show one number. Reuses the shared timeout
  // ref (device-code and device-confirmation are mutually exclusive), so the AuthStateChanged /
  // cancel / reset paths already clear it on success or teardown.
  useEffect(() => {
    if (!needsDeviceCode || !sessionId || loginDeadline === null) return;
    const epoch = loginEpochRef.current;
    const challengeId = waitRef.current?.challengeId;
    deviceConfirmationTimeoutRef.current = setTimeout(
      async () => {
        if (
          epoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          waitRef.current?.challengeId !== challengeId
        )
          return;
        loginEpochRef.current += 1;
        waitRef.current = null;
        setLoginDeadline(null);
        setNeedsDeviceCode(false);
        setDeviceUserCode('');
        setDeviceVerificationUri('');
        setLoading(false);
        setPendingChallenge(null);
        isWaitingForDeviceConfirmationRef.current = false;
        hasStartedAuthRef.current = false;
        endLoginCard();

        // Same reason as the mobile-approval wait above: the modal keeps the explanation instead of
        // closing on a card that is gone five seconds later.
        setError(t('prefill.auth.deviceCodeExpired'));
        try {
          await hubConnectionRef.current?.invoke('CancelLoginAsync', sessionId);
        } catch (err) {
          // The modal already holds the timeout reason; transport cancellation is best effort.
          notifyError('Failed to cancel Xbox device-code login on daemon', err, {
            silent: true,
            logLabel: 'usePrefillSteamAuth Xbox device-code timeout cancel'
          });
        }
      },
      Math.max(0, loginDeadline - Date.now())
    );

    return () => {
      if (deviceConfirmationTimeoutRef.current) {
        clearTimeout(deviceConfirmationTimeoutRef.current);
        deviceConfirmationTimeoutRef.current = null;
      }
    };
  }, [
    needsDeviceCode,
    sessionId,
    pendingChallenge?.challengeId,
    loginDeadline,
    notifyError,
    endLoginCard,
    t
  ]);

  const cancelPendingRequest = useCallback(() => {
    loginEpochRef.current += 1;
    waitRef.current = null;
    setLoginDeadline(null);
    hasStartedAuthRef.current = false;
    setLoading(false);
    setWaitingForMobileConfirmation(false);
    setNeedsAuthorizationCode(false);
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
    isWaitingForDeviceConfirmationRef.current = false;
    isWaitingForAuthCodeProcessingRef.current = false;
    setPendingChallenge(null);
    endLoginCard();
    if (deviceConfirmationTimeoutRef.current) {
      clearTimeout(deviceConfirmationTimeoutRef.current);
      deviceConfirmationTimeoutRef.current = null;
    }
  }, [endLoginCard]);

  const resetAuthForm = useCallback(() => {
    loginEpochRef.current += 1;
    waitRef.current = null;
    setLoginDeadline(null);
    hasStartedAuthRef.current = false;
    setError(null);
    // The account name survives, the same as it does on the other two login surfaces and in the
    // approval-timeout handler above. Every path that lands here, a refused password, a timeout, a
    // cancel, a close, leaves the person wanting the same account again, and retyping it is pure
    // friction. The password does not survive: it has to be entered again anyway.
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
    endLoginCard();
    if (deviceConfirmationTimeoutRef.current) {
      clearTimeout(deviceConfirmationTimeoutRef.current);
      deviceConfirmationTimeoutRef.current = null;
    }
  }, [endLoginCard]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    const attemptEpoch = loginEpochRef.current;
    // A fresh attempt starts here, so the last one's failure stops being the current answer.
    setError(null);
    if (!sessionId || !hubConnection) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('prefill.auth.errors.noActiveSession'),
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
          message: t('prefill.auth.errors.twoFactorRequired'),
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
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        notifySuccess(t('prefill.auth.status.twoFactorSent'));

        // Wait for next challenge or success
        // AuthStateChanged will trigger onSuccess if login succeeds
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        if (nextChallenge) {
          if (!handleChallengeType(nextChallenge)) return false;
        } else {
          // No more challenges - login likely successful
          // AuthStateChanged should have fired, but call onSuccess as fallback
          resetAuthForm();
          onSuccess?.();
        }
        setLoading(false);
        return true;
      } catch (err) {
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
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
          message: t('prefill.auth.errors.emailCodeRequired'),
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
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        notifySuccess(t('prefill.auth.status.emailCodeSent'));

        // Wait for next challenge or success
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        if (nextChallenge) {
          if (!handleChallengeType(nextChallenge)) return false;
        } else {
          resetAuthForm();
          onSuccess?.();
        }
        setLoading(false);
        return true;
      } catch (err) {
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
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
          message: t('prefill.auth.errors.epicAuthCodeRequired'),
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
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        notifySuccess(t('prefill.auth.status.authCodeSent'));

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
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
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
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;

        showLoginCard(challenge?.operationId);

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
          if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
            return false;
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
        if (!handleChallengeType(challenge)) return false;
        setLoading(false);
        return false;
      } catch (err) {
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMessage,
          details: { notificationType: 'error' }
        });
        onError?.(errorMessage);
        setLoading(false);
        endLoginCard();
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
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;

        showLoginCard(challenge?.operationId);

        if (challenge && challenge.credentialType === 'device-code') {
          if (!handleChallengeType(challenge)) return false;
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
          if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
            return false;
          if (eventChallenge && eventChallenge.credentialType === 'device-code') {
            if (!handleChallengeType(eventChallenge)) return false;
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
        if (!handleChallengeType(challenge)) return false;
        setLoading(false);
        return false;
      } catch (err) {
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMessage,
          details: { notificationType: 'error' }
        });
        onError?.(errorMessage);
        setLoading(false);
        endLoginCard();
        return false;
      }
    }

    // Steam: Initial login - start the login process
    if (!username.trim() || !password.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('prefill.auth.errors.credentialsRequired'),
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
      if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
        return false;

      if (!challenge) {
        throw new Error(t('prefill.auth.errors.noChallenge'));
      }

      showLoginCard(challenge.operationId);

      // Daemon flow: username -> password -> (optional 2FA/steamguard/device-confirmation)
      if (challenge.credentialType === 'username') {
        // Send username
        await hubConnection.invoke('ProvideCredentialAsync', sessionId, challenge, username);
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;

        // Wait for password challenge
        const passChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
          return false;
        if (!passChallenge) {
          throw new Error(t('prefill.auth.errors.noPasswordChallenge'));
        }

        if (passChallenge.credentialType === 'password') {
          // Send password
          await hubConnection.invoke('ProvideCredentialAsync', sessionId, passChallenge, password);
          if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
            return false;

          notifySuccess(t('prefill.auth.status.credentialsSent'));

          // Wait for next challenge (2FA, steamguard, device-confirmation) or success
          const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallengeAsync',
            sessionId,
            60
          );
          if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
            return false;
          if (nextChallenge) {
            if (!handleChallengeType(nextChallenge)) return false;

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
          if (!handleChallengeType(passChallenge)) return false;
          setLoading(false);
          return false; // Need more input
        }
      } else {
        // Handle other initial challenge types
        if (!handleChallengeType(challenge)) return false;
        setLoading(false);
        return false; // Need more input
      }

      // Reached when the post-password WaitForChallenge returned a challenge that is NOT
      // device-confirmation - most commonly a STALE 'password' challenge re-served from the
      // manager's pending-challenge cache in the moment right before the real device-confirmation
      // challenge arrives (confirmed via diagnostic logging: "WaitForChallenge returned password"
      // then the modal closed). This is NOT a successful login: returning true here made the modal's
      // submit handler call onClose(), so the "Waiting for Confirmation" screen never appeared even
      // though its state (waitingForMobileConfirmation) was set moments later. Keep the modal open
      // and let the real challenge event (device-confirmation / 2FA / Steam Guard) or the
      // authoritative AuthStateChanged: Authenticated event drive what happens next.
      setLoading(false);
      return false;
    } catch (err) {
      if (attemptEpoch !== loginEpochRef.current || sessionIdRef.current !== sessionId)
        return false;
      const errorMessage = getErrorMessage(err);
      setError(errorMessage);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMessage,
        details: { notificationType: 'error' }
      });
      onError?.(errorMessage);
      setLoading(false);
      endLoginCard();
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
    notifySuccess,
    resetAuthForm,
    onSuccess,
    onError,
    serviceId,
    showLoginCard,
    endLoginCard,
    handleChallengeType
  ]);

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
    error,
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
    loginDeadline,
    trigger2FAPrompt,
    triggerEmailPrompt
  };
}
