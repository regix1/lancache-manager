import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import type { HubConnection } from '@microsoft/signalr';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler, useNotifySuccess } from './useErrorHandler';
import { getErrorMessage } from '@utils/error';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';
import { loginAttemptTimeoutMs, STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS } from './loginAttemptTimeout';
import { getEventName } from '@components/features/prefill/hooks/prefillConstants';
import { prefillServiceConfig } from '@components/features/prefill/hooks/prefillServiceConfig';
import { getAuthStage, type AuthStage, type AuthStep } from './authStage';

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
  /** Tracker id of the daemon sign-in this challenge belongs to. */
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
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { notifySuccess } = useNotifySuccess();
  const { t } = useTranslation();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [pendingChallenge, setPendingChallenge] = useState<CredentialChallenge | null>(null);
  const pendingChallengeRef = useRef<CredentialChallenge | null>(null);
  pendingChallengeRef.current = pendingChallenge;
  const [error, setError] = useState<string | null>(null);
  const deviceConfirmationTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  // Both mutually exclusive waits keep their accepted challenge's deadline across effect reruns.
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);
  const waitRef = useRef<{ sessionId: string; challengeId: string; deadline: number } | null>(null);
  const loginEpochRef = useRef(0);
  const authStepRef = useRef<AuthStep | null>(null);
  const authStepCounterRef = useRef(0);
  const retiredChallengeIdsRef = useRef<Set<string>>(new Set());
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

  useEffect(() => {
    authStepRef.current = null;
    retiredChallengeIdsRef.current.clear();
    confirmedChallengeIdsRef.current.clear();
    waitRef.current = null;
    setLoginDeadline(null);
    hasStartedAuthRef.current = false;
    setLoading(false);
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setNeedsAuthorizationCode(false);
    setAuthorizationUrl('');
    pendingChallengeRef.current = null;
    setPendingChallenge(null);
    setWaitingForMobileConfirmation(false);
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
    return () => {
      loginEpochRef.current += 1;
      waitRef.current = null;
    };
  }, [sessionId]);

  const beginAuthStep = useCallback((stage: AuthStage): AuthStep | null => {
    if (authStepRef.current) return null;
    const challengeIds = new Set<string>();
    const challenge = pendingChallengeRef.current;
    if (challenge && getAuthStage(challenge.credentialType) === stage) {
      challengeIds.add(challenge.challengeId);
    }
    const step: AuthStep = {
      actionId: ++authStepCounterRef.current,
      stage,
      challengeIds
    };
    authStepRef.current = step;
    setError(null);
    setLoading(true);
    return step;
  }, []);

  const finishAuthStep = useCallback((actionId?: number): boolean => {
    const step = authStepRef.current;
    if (!step || (actionId !== undefined && step.actionId !== actionId)) return false;
    for (const challengeId of step.challengeIds) {
      retiredChallengeIdsRef.current.add(challengeId);
    }
    authStepRef.current = null;
    return true;
  }, []);

  const ownsAuthStep = useCallback(
    (step: AuthStep, challengeId?: string): boolean =>
      authStepRef.current?.actionId === step.actionId &&
      (challengeId === undefined || pendingChallengeRef.current?.challengeId === challengeId),
    []
  );

  // Helper to set state based on challenge type
  const handleChallengeType = useCallback(
    (challenge: CredentialChallenge) => {
      if (!sessionId || sessionIdRef.current !== sessionId || !hasStartedAuthRef.current)
        return false;
      const stage = getAuthStage(challenge.credentialType);
      if (retiredChallengeIdsRef.current.has(challenge.challengeId)) return false;
      const expiry = Date.parse(challenge.expiresAt);
      if (!stage || !Number.isFinite(expiry)) {
        finishAuthStep();
        setError(t('prefill.auth.errors.noChallenge'));
        setLoading(false);
        return false;
      }
      const submitted = authStepRef.current;
      const sameStage = submitted?.stage === stage;
      const sameVisibleStage = getAuthStage(pendingChallengeRef.current?.credentialType) === stage;
      if (submitted && sameStage) {
        submitted.challengeIds.add(challenge.challengeId);
      } else if (submitted) {
        finishAuthStep(submitted.actionId);
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
          current?.sessionId === sessionId && (sameStage || sameVisibleStage)
            ? Math.min(current.deadline, expiry)
            : Math.min(Date.now() + duration, expiry);
        waitRef.current = { sessionId, challengeId: challenge.challengeId, deadline };
        setLoginDeadline(deadline);
      } else {
        if (expiry <= Date.now()) {
          finishAuthStep();
          setError(t('prefill.auth.errors.noChallenge'));
          setLoading(false);
          return false;
        }
        const current = waitRef.current;
        waitRef.current = {
          sessionId,
          challengeId: challenge.challengeId,
          deadline:
            current?.sessionId === sessionId && (sameStage || sameVisibleStage)
              ? Math.min(current.deadline, expiry)
              : expiry
        };
        setLoginDeadline(null);
      }
      pendingChallengeRef.current = challenge;
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
      setLoading(Boolean(authStepRef.current));
      return true;
    },
    [finishAuthStep, sessionId, serviceId, t]
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
        if (!hasStartedAuthRef.current) return;
        loginEpochRef.current += 1;
        finishAuthStep();
        retiredChallengeIdsRef.current.clear();
        waitRef.current = null;
        setLoginDeadline(null);
        setPendingChallenge(null);
        // Login succeeded - clear any pending timeouts and notify success
        if (deviceConfirmationTimeoutRef.current) {
          clearTimeout(deviceConfirmationTimeoutRef.current);
          deviceConfirmationTimeoutRef.current = null;
        }
        isWaitingForDeviceConfirmationRef.current = false;
        setWaitingForMobileConfirmation(false);
        setNeedsDeviceCode(false);
        setLoading(false);

        // Note: PrefillPanel's handleAuthStateChanged handles the log entry,
        // so we don't add a notification here to avoid duplicates
        hasStartedAuthRef.current = false;
        onSuccess?.();
      } else if (authState === 'NotAuthenticated') {
        loginEpochRef.current += 1;
        finishAuthStep();
        retiredChallengeIdsRef.current.clear();
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

        if (wasAuthenticating) {
          // This is the sentence a person reads when the daemon or Steam ends the attempt before
          // our own clock does, which is the usual way a phone approval ends, so it is the one
          // that most needs to arrive in their own language. It stays in the dialog: the server's
          // run card is the one notice for the failure. [110]
          const refused = t('prefill.auth.signInRefused');
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
  }, [hubConnection, sessionId, onSuccess, onError, serviceId, finishAuthStep, t]);

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
        challenge.credentialType === 'device-confirmation' &&
        !confirmedChallengeIdsRef.current.has(challenge.challengeId)
      ) {
        confirmedChallengeIdsRef.current.add(challenge.challengeId);
        const confirmationStep = beginAuthStep('phone-approval');
        const epoch = loginEpochRef.current;
        await new Promise((resolve) => setTimeout(resolve, 300));
        if (
          epoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          waitRef.current?.challengeId !== challenge.challengeId ||
          (confirmationStep && !ownsAuthStep(confirmationStep, challenge.challengeId))
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
    };

    const eventName = getEventName('CredentialChallenge', serviceId);
    hubConnection.on(eventName, handleCredentialChallenge);

    return () => {
      hubConnection.off(eventName, handleCredentialChallenge);
    };
  }, [
    beginAuthStep,
    handleChallengeType,
    hubConnection,
    notifyError,
    ownsAuthStep,
    serviceId,
    sessionId
  ]);

  // Timeout for device confirmation - cancel daemon login and reset state
  useEffect(() => {
    if (waitingForMobileConfirmation && sessionId && loginDeadline !== null) {
      const epoch = loginEpochRef.current;
      const challengeId = waitRef.current?.challengeId;
      const handle = setTimeout(
        async () => {
          if (
            epoch !== loginEpochRef.current ||
            sessionIdRef.current !== sessionId ||
            waitRef.current?.challengeId !== challengeId
          )
            return;
          // End the browser attempt before awaiting cancellation so retries cannot be overwritten.
          loginEpochRef.current += 1;
          finishAuthStep();
          retiredChallengeIdsRef.current.clear();
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
          pendingChallengeRef.current = null;
          setPendingChallenge(null);
          isWaitingForDeviceConfirmationRef.current = false;
          hasStartedAuthRef.current = false;

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
      deviceConfirmationTimeoutRef.current = handle;

      return () => {
        clearTimeout(handle);
        if (deviceConfirmationTimeoutRef.current === handle) {
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
    finishAuthStep,
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
    const generation = loginEpochRef.current;
    const challengeId = waitRef.current?.challengeId;
    const handle = setTimeout(
      async () => {
        if (
          generation !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          waitRef.current?.challengeId !== challengeId
        )
          return;
        loginEpochRef.current += 1;
        finishAuthStep();
        retiredChallengeIdsRef.current.clear();
        waitRef.current = null;
        setLoginDeadline(null);
        setNeedsDeviceCode(false);
        setDeviceUserCode('');
        setDeviceVerificationUri('');
        setLoading(false);
        pendingChallengeRef.current = null;
        setPendingChallenge(null);
        isWaitingForDeviceConfirmationRef.current = false;
        hasStartedAuthRef.current = false;

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
    deviceConfirmationTimeoutRef.current = handle;

    return () => {
      clearTimeout(handle);
      if (deviceConfirmationTimeoutRef.current === handle) {
        deviceConfirmationTimeoutRef.current = null;
      }
    };
  }, [
    needsDeviceCode,
    sessionId,
    pendingChallenge?.challengeId,
    loginDeadline,
    notifyError,
    finishAuthStep,
    t
  ]);

  useEffect(() => {
    const step = authStepRef.current;
    const challenge = pendingChallenge;
    const deadline = waitRef.current?.deadline;
    if (
      !step ||
      !challenge ||
      deadline === undefined ||
      step.stage === 'phone-approval' ||
      step.stage === 'device-code' ||
      step.stage === 'start'
    )
      return;

    const actionId = step.actionId;
    const epoch = loginEpochRef.current;
    const handle = setTimeout(
      async () => {
        if (
          epoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          authStepRef.current?.actionId !== actionId ||
          waitRef.current?.deadline !== deadline
        )
          return;
        loginEpochRef.current += 1;
        finishAuthStep(actionId);
        retiredChallengeIdsRef.current.clear();
        waitRef.current = null;
        setNeedsTwoFactor(false);
        setNeedsEmailCode(false);
        setNeedsAuthorizationCode(false);
        setLoading(false);
        pendingChallengeRef.current = null;
        setPendingChallenge(null);
        hasStartedAuthRef.current = false;
        setError(t('prefill.auth.errors.noChallenge'));
        try {
          await hubConnectionRef.current?.invoke('CancelLoginAsync', sessionId);
        } catch (err) {
          notifyError('Failed to cancel expired login on daemon', err, {
            silent: true,
            logLabel: 'usePrefillSteamAuth challenge expiry cancel'
          });
        }
      },
      Math.max(0, deadline - Date.now())
    );
    deviceConfirmationTimeoutRef.current = handle;
    return () => {
      clearTimeout(handle);
      if (deviceConfirmationTimeoutRef.current === handle) {
        deviceConfirmationTimeoutRef.current = null;
      }
    };
  }, [finishAuthStep, notifyError, pendingChallenge, sessionId, t]);

  const cancelPendingRequest = useCallback(() => {
    loginEpochRef.current += 1;
    finishAuthStep();
    retiredChallengeIdsRef.current.clear();
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
    pendingChallengeRef.current = null;
    setPendingChallenge(null);
    if (deviceConfirmationTimeoutRef.current) {
      clearTimeout(deviceConfirmationTimeoutRef.current);
      deviceConfirmationTimeoutRef.current = null;
    }
  }, [finishAuthStep]);

  const resetAuthForm = useCallback(() => {
    loginEpochRef.current += 1;
    finishAuthStep();
    retiredChallengeIdsRef.current.clear();
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
    pendingChallengeRef.current = null;
    setPendingChallenge(null);
    isWaitingForDeviceConfirmationRef.current = false;
    confirmedChallengeIdsRef.current.clear();
    if (deviceConfirmationTimeoutRef.current) {
      clearTimeout(deviceConfirmationTimeoutRef.current);
      deviceConfirmationTimeoutRef.current = null;
    }
  }, [finishAuthStep]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    const attemptEpoch = loginEpochRef.current;
    const signInFailed = t('common.errors.signInFailed', {
      platform: t(prefillServiceConfig(serviceId).serviceNameKey)
    });
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
      const step = beginAuthStep('two-factor');
      if (!step) return false;

      try {
        await hubConnection.invoke(
          'ProvideCredentialAsync',
          sessionId,
          pendingChallenge,
          twoFactorCode
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step, pendingChallenge.challengeId)
        )
          return false;
        notifySuccess(t('prefill.auth.status.twoFactorSent'));

        // Wait for next challenge or success
        // AuthStateChanged will trigger onSuccess if login succeeds
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        if (nextChallenge) {
          if (!handleChallengeType(nextChallenge)) return false;
        }
        return false;
      } catch (err) {
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        finishAuthStep(step.actionId);
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        notifyError(signInFailed, err);
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
      const step = beginAuthStep('email-code');
      if (!step) return false;

      try {
        await hubConnection.invoke(
          'ProvideCredentialAsync',
          sessionId,
          pendingChallenge,
          emailCode
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step, pendingChallenge.challengeId)
        )
          return false;
        notifySuccess(t('prefill.auth.status.emailCodeSent'));

        // Wait for next challenge or success
        const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        if (nextChallenge) {
          if (!handleChallengeType(nextChallenge)) return false;
        }
        return false;
      } catch (err) {
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        finishAuthStep(step.actionId);
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        notifyError(signInFailed, err);
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
      const step = beginAuthStep('authorization-code');
      if (!step) return false;

      try {
        await hubConnection.invoke(
          'ProvideCredentialAsync',
          sessionId,
          pendingChallenge,
          authorizationCode
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step, pendingChallenge.challengeId)
        )
          return false;
        notifySuccess(t('prefill.auth.status.authCodeSent'));

        // Don't call WaitForChallenge here - rely on events instead.
        // The daemon will either:
        //   1. Send AuthStateChanged -> "Authenticated" (handled by useEffect above)
        //   2. Send a new CredentialChallenge -> authorization-url if code was rejected
        //      (handled by handleCredentialChallenge event handler which shows error)
        // This mirrors the device-confirmation pattern which already works correctly.
        // Return false so the modal stays open while we wait for the event
        return false;
      } catch (err) {
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        finishAuthStep(step.actionId);
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        notifyError(signInFailed, err);
        onError?.(errorMessage);
        setLoading(false);
        return false;
      }
    }

    // Epic OAuth: start login to get authorization URL challenge
    if (serviceId === 'epic' && !needsAuthorizationCode) {
      const step = beginAuthStep('start');
      if (!step) return false;
      hasStartedAuthRef.current = true;

      try {
        const challenge = await hubConnection.invoke<CredentialChallenge | null>(
          'StartLoginAsync',
          sessionId
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;

        if (challenge && challenge.credentialType === 'authorization-url') {
          handleChallengeType(challenge);
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
          if (
            attemptEpoch !== loginEpochRef.current ||
            sessionIdRef.current !== sessionId ||
            !ownsAuthStep(step)
          )
            return false;
          if (eventChallenge && eventChallenge.credentialType === 'authorization-url') {
            handleChallengeType(eventChallenge);
            return false;
          }
          return false;
        }

        // Handle other challenge types
        if (!handleChallengeType(challenge)) return false;
        return false;
      } catch (err) {
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        finishAuthStep(step.actionId);
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        notifyError(signInFailed, err);
        onError?.(errorMessage);
        setLoading(false);
        return false;
      }
    }

    // Xbox: Microsoft OAuth device flow - start login to get the device-code challenge.
    // No password ever enters the container; the user approves in their own browser.
    if (serviceId === 'xbox') {
      const step = beginAuthStep('start');
      if (!step) return false;
      hasStartedAuthRef.current = true;

      try {
        const challenge = await hubConnection.invoke<CredentialChallenge | null>(
          'StartLoginAsync',
          sessionId
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;

        if (challenge && challenge.credentialType === 'device-code') {
          if (!handleChallengeType(challenge)) return false;
          return false; // Modal stays open while the user approves in their browser
        }

        if (!challenge) {
          // Challenge may arrive via event shortly after StartLogin.
          const eventChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallengeAsync',
            sessionId,
            10
          );
          if (
            attemptEpoch !== loginEpochRef.current ||
            sessionIdRef.current !== sessionId ||
            !ownsAuthStep(step)
          )
            return false;
          if (eventChallenge && eventChallenge.credentialType === 'device-code') {
            if (!handleChallengeType(eventChallenge)) return false;
            return false;
          }
          return false;
        }

        // Unexpected challenge type - surface it via the generic handler.
        if (!handleChallengeType(challenge)) return false;
        return false;
      } catch (err) {
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        finishAuthStep(step.actionId);
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        notifyError(signInFailed, err);
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
        message: t('prefill.auth.errors.credentialsRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    const step = beginAuthStep('credentials');
    if (!step) return false;
    hasStartedAuthRef.current = true;

    try {
      // Start login to get initial challenge (username)
      const challenge = await hubConnection.invoke<CredentialChallenge | null>(
        'StartLoginAsync',
        sessionId
      );
      if (
        attemptEpoch !== loginEpochRef.current ||
        sessionIdRef.current !== sessionId ||
        !ownsAuthStep(step)
      )
        return false;

      if (!challenge) {
        throw new Error(t('prefill.auth.errors.noChallenge'));
      }

      if (!handleChallengeType(challenge)) return false;

      // Daemon flow: username -> password -> (optional 2FA/steamguard/device-confirmation)
      if (challenge.credentialType === 'username') {
        if (!ownsAuthStep(step, challenge.challengeId)) return false;
        // Send username
        await hubConnection.invoke('ProvideCredentialAsync', sessionId, challenge, username);
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;

        // Wait for password challenge
        const passChallenge = await hubConnection.invoke<CredentialChallenge | null>(
          'WaitForChallengeAsync',
          sessionId,
          30
        );
        if (
          attemptEpoch !== loginEpochRef.current ||
          sessionIdRef.current !== sessionId ||
          !ownsAuthStep(step)
        )
          return false;
        if (!passChallenge) {
          throw new Error(t('prefill.auth.errors.noPasswordChallenge'));
        }

        if (!handleChallengeType(passChallenge)) return false;
        if (passChallenge.credentialType === 'password') {
          if (!ownsAuthStep(step, passChallenge.challengeId)) return false;
          // Send password
          await hubConnection.invoke('ProvideCredentialAsync', sessionId, passChallenge, password);
          if (
            attemptEpoch !== loginEpochRef.current ||
            sessionIdRef.current !== sessionId ||
            !ownsAuthStep(step)
          )
            return false;

          notifySuccess(t('prefill.auth.status.credentialsSent'));

          // Wait for next challenge (2FA, steamguard, device-confirmation) or success
          const nextChallenge = await hubConnection.invoke<CredentialChallenge | null>(
            'WaitForChallengeAsync',
            sessionId,
            60
          );
          if (
            attemptEpoch !== loginEpochRef.current ||
            sessionIdRef.current !== sessionId ||
            !ownsAuthStep(step)
          )
            return false;
          if (nextChallenge) {
            if (!handleChallengeType(nextChallenge)) return false;
          }
          return false;
        } else {
          return false; // Need more input
        }
      } else {
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
      return false;
    } catch (err) {
      if (
        attemptEpoch !== loginEpochRef.current ||
        sessionIdRef.current !== sessionId ||
        !ownsAuthStep(step)
      )
        return false;
      finishAuthStep(step.actionId);
      const errorMessage = getErrorMessage(err);
      setError(errorMessage);
      notifyError(signInFailed, err);
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
    notifyError,
    notifySuccess,
    resetAuthForm,
    onSuccess,
    onError,
    serviceId,
    t,
    handleChallengeType,
    beginAuthStep,
    finishAuthStep,
    ownsAuthStep
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
