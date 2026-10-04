import { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { getErrorMessage } from '@utils/error';
import { ApiError } from '@services/apiError';
import { createUuid } from '@utils/uuid';
import {
  getIntegrationReasonKey,
  type IntegrationAccess,
  type IntegrationLoginEnding
} from '../types';
import { STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS } from './loginAttemptTimeout';
import { useSignInEndingWait } from './useSignInEndingWait';
import type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

interface SteamLoginFlowOptions {
  integration?: {
    identity: string;
    access: IntegrationAccess | null;
    refresh: () => Promise<void>;
  };
  loginUrl: string;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
  getExtraRequestBody?: () => Record<string, unknown>;
}

interface SteamLoginApiResult {
  attemptId?: string;
  expiresAtUtc?: string;
  sessionExpired?: boolean;
  requiresTwoFactor?: boolean;
  requiresEmailCode?: boolean;
  success?: boolean;
  message?: string;
  error?: string;
}

function buildSteamOnlyState(
  loading: boolean,
  needsTwoFactor: boolean,
  needsEmailCode: boolean,
  waitingForMobileConfirmation: boolean,
  useManualCode: boolean,
  username: string,
  password: string,
  twoFactorCode: string,
  emailCode: string,
  error: string | null
): SteamLoginFlowState {
  return {
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
    needsAuthorizationCode: false,
    authorizationUrl: '',
    authorizationCode: '',
    needsDeviceCode: false,
    deviceUserCode: '',
    deviceVerificationUri: ''
  };
}

export function useSteamLoginFlow(options: SteamLoginFlowOptions) {
  const { loginUrl, onSuccess, onError, getExtraRequestBody, integration } = options;
  const { t } = useTranslation();
  const identityRef = useRef(integration?.identity);
  identityRef.current = integration?.identity;
  const formIdentityRef = useRef(integration?.identity);
  const formCurrent = formIdentityRef.current === integration?.identity;
  const requestRef = useRef(0);
  const attemptRef = useRef<string | null>(null);
  const cancelledAttemptRef = useRef<string | null>(null);
  const busyRef = useRef(false);
  const [attemptId, setAttemptId] = useState<string | null>(null);
  // The attempt whose answer this page lost and whose ending it is waiting to be told about.
  const [endingWait, setEndingWait] = useState<{
    attemptId: string;
    username: string;
    deadline: number;
  } | null>(null);
  const accessUnavailable = Boolean(integration && (!formCurrent || integration.access === null));
  const canAuthenticate =
    formCurrent &&
    (!integration ||
      integration.access?.canSignIn === true ||
      integration.access?.canRecover === true ||
      (integration.access?.canCancel === true && integration.access.attemptId === attemptId));
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [abortController, setAbortController] = useState<AbortController | null>(null);
  // When the phone-approval wait gives up, so the modal counts down the same window that will end
  // it instead of running a clock of its own. Null the rest of the time, which is the truth: the
  // Steam Guard step waits on the person, and checking a code it hands over is done in seconds.
  const [loginDeadline, setLoginDeadline] = useState<number | null>(null);

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  useEffect(() => {
    return () => {
      if (abortController) {
        abortController.abort();
      }
    };
  }, [abortController]);

  const cancelPendingRequest = () => {
    requestRef.current += 1;
    busyRef.current = false;
    setLoading(false);
    if (abortController) {
      abortController.abort();
      setAbortController(null);
    }
  };

  const resetAuthForm = () => {
    if (attemptRef.current) cancelledAttemptRef.current = attemptRef.current;
    attemptRef.current = null;
    setAttemptId(null);
    cancelPendingRequest();
    setError(null);
    // The account name survives, because it was almost never the thing that was wrong. Every path
    // that lands here - a refused password, a timeout, a cancel, a close - leaves the person
    // wanting to try the same account again, and retyping it is pure friction. The password does
    // not survive: it is the field that has to be entered again anyway.
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setLoading(false);
    // The attempt is over, so its countdown is too; a clock left running beside the reason would
    // say the same thing twice.
    setLoginDeadline(null);
    setEndingWait(null);
  };

  // Reads once how the sign-in with this attempt id ended and acts on it: true when it saved, false when it ended
  // another way (or the form moved on to another attempt), null while the server still runs it with nothing to show.
  const readLoginEnding = async (
    askedAttempt: string,
    askedUsername: string,
    final: boolean
  ): Promise<boolean | null> => {
    const identity = integration?.identity;
    const status = await fetch(
      `/api/steam-auth/status?attemptId=${encodeURIComponent(askedAttempt)}`,
      ApiService.getFetchOptions()
    )
      .then((response) =>
        ApiService.handleResponse<
          IntegrationAccess & {
            loginEnding?: IntegrationLoginEnding;
            pendingPrompt?: 'twoFactor' | 'email';
            isAuthenticated?: boolean;
          }
        >(response)
      )
      .catch(() => null);
    if (identityRef.current !== identity || attemptRef.current !== askedAttempt) return false;
    const endWithError = (message: string): false => {
      resetAuthForm();
      setError(message);
      onError?.(message);
      return false;
    };
    if (status === null) return final ? endWithError(t('errors.integration.attemptExpired')) : null;
    const ending = status.loginEnding?.attemptId === askedAttempt ? status.loginEnding : null;
    if (ending?.status === 'completed') {
      if (
        status.isAuthenticated &&
        (status.canManage === true || status.ownershipReason === 'login-in-progress')
      ) {
        attemptRef.current = null;
        cancelledAttemptRef.current = null;
        onSuccess?.(t('modals.steamAuth.success.authenticatedAs', { username: askedUsername }));
        resetAuthForm();
        return true;
      }
      // Saved, then signed out (a logout from another tab) before this read.
      return endWithError(t('modals.steamAuth.errors.authenticationFailed'));
    }
    if (ending) return endWithError(t(ending.stageKey, ending.context ?? {}));
    // With no ending and another attempt (or none) running, the server no longer knows this one.
    if (status.attemptId !== askedAttempt)
      return endWithError(t('errors.integration.attemptExpired'));
    if (status.pendingPrompt) {
      setWaitingForMobileConfirmation(false);
      if (status.pendingPrompt === 'email') setNeedsEmailCode(true);
      else setNeedsTwoFactor(true);
      if (status.loginExpiresAtUtc) setLoginDeadline(Date.parse(status.loginExpiresAtUtc));
      setEndingWait(null);
      setLoading(false);
      return false;
    }
    // A final read (at the attempt's deadline) decides even while the server still names the attempt: a browser clock
    // that runs ahead must not leave the dialog waiting with nothing left to come.
    if (final) return endWithError(t('errors.integration.attemptExpired'));
    // Still this caller's pending sign-in: wait until the server's own window for it ends, which the server announces.
    if (status.loginExpiresAtUtc) {
      const deadline = Date.parse(status.loginExpiresAtUtc);
      setEndingWait((wait) =>
        wait?.attemptId === askedAttempt && wait.deadline !== deadline
          ? { ...wait, deadline }
          : wait
      );
    }
    return null;
  };

  useSignInEndingWait(endingWait, (final) => {
    if (!endingWait) return;
    const waited = endingWait.attemptId;
    void readLoginEnding(waited, endingWait.username, final).then((decided) => {
      if (decided === null) return;
      // A slow read for an attempt the form already left must not end the wait of a newer one.
      setEndingWait((wait) => (wait?.attemptId === waited ? null : wait));
      // The access read while the answer was lost still names this attempt as running, so read it again now it ended.
      void integration?.refresh();
    });
  });

  const cancelLogin = () => {
    if (!formCurrent || identityRef.current !== integration?.identity) return;
    const cancelled = attemptId;
    if (cancelledAttemptRef.current === cancelled) cancelledAttemptRef.current = null;
    if (!integration || !cancelled) return;
    void ApiService.cancelSteamLogin(cancelled)
      .catch((error: unknown) => {
        console.warn('Steam sign-in cancellation was not acknowledged:', getErrorMessage(error));
      })
      .finally(() => {
        void integration.refresh();
      });
  };

  useEffect(() => {
    formIdentityRef.current = integration?.identity;
    requestRef.current += 1;
    busyRef.current = false;
    attemptRef.current = null;
    cancelledAttemptRef.current = null;
    setAttemptId(null);
    setUsername('');
    resetAuthForm();
    return () => {
      requestRef.current += 1;
    };
    // The identity, not an authority refresh during this attempt, owns the form lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [integration?.identity]);

  const handleAuthenticate = async (): Promise<boolean> => {
    if (
      !canAuthenticate ||
      identityRef.current !== integration?.identity ||
      busyRef.current ||
      endingWait !== null
    )
      return false;
    const continuation = needsTwoFactor || needsEmailCode || useManualCode;
    if (
      integration &&
      (continuation
        ? !attemptRef.current
        : integration.access?.canSignIn !== true && integration.access?.canRecover !== true)
    ) {
      setError(
        accessUnavailable || (continuation && !attemptRef.current)
          ? t('errors.integration.statusUnavailable')
          : t(getIntegrationReasonKey(integration.access?.ownershipReason))
      );
      return false;
    }
    if (!username.trim() || !password.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('modals.steamAuth.errors.credentialsRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (needsEmailCode && !emailCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('modals.steamAuth.errors.emailCodeRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (useManualCode && !twoFactorCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('modals.steamAuth.errors.twoFactorRequired'),
        details: { notificationType: 'error' }
      });
      return false;
    }

    // A fresh attempt starts here, so the last one's failure stops being the current answer.
    setError(null);
    setLoading(true);
    busyRef.current = true;
    const request = ++requestRef.current;
    const identity = integration?.identity;
    const current = () => requestRef.current === request && identityRef.current === identity;
    const submittedAttempt = integration ? (attemptRef.current ?? createUuid()) : null;
    cancelledAttemptRef.current = null;
    attemptRef.current = submittedAttempt;
    setAttemptId(submittedAttempt);

    // The answer was lost (this page's two-minute wait ran out, the connection dropped, or something in between
    // answered in its own words) while the server can still finish this sign-in. Read how it ended once; when nothing
    // is decided, wait for the server's ending push, a reconnect, or the attempt's own deadline.
    const readLostAnswer = async (): Promise<boolean> => {
      setLoginDeadline(null);
      if (!submittedAttempt) return false;
      const decided = await readLoginEnding(submittedAttempt, username, false);
      if (decided === null && current()) {
        // The server's attempt window, from the moment the answer was lost.
        setEndingWait({
          attemptId: submittedAttempt,
          username,
          deadline: Date.now() + 15 * 60 * 1000
        });
      }
      return decided === true;
    };

    const controller = new AbortController();
    setAbortController(controller);

    const willWaitForMobileConfirmation = !needsTwoFactor && !needsEmailCode && !useManualCode;
    if (willWaitForMobileConfirmation) {
      setWaitingForMobileConfirmation(true);
    }
    let requestTimeout: ReturnType<typeof setTimeout> | null = null;
    let timedOut = false;
    try {
      requestTimeout = setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS);
      // Only the phone-approval wait gets a countdown drawn over it. Every request runs under the
      // same abort timer, but sending a Steam Guard code takes about two seconds, and putting a
      // two-minute clock over those two seconds made the line appear and vanish on every submit,
      // sliding the centred panel each way. A wait that ends in seconds does not need a countdown.
      // The timer arms here, after the click, never while a field is being filled, so a short
      // window cannot cut anyone off mid-typing.
      if (willWaitForMobileConfirmation) {
        setLoginDeadline(Date.now() + STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS);
      }

      const response = await fetch(
        loginUrl,
        ApiService.getJsonFetchOptions(
          {
            username,
            password,
            twoFactorCode: needsTwoFactor || useManualCode ? twoFactorCode : undefined,
            emailCode: needsEmailCode ? emailCode : undefined,
            allowMobileConfirmation: !useManualCode,
            ...getExtraRequestBody?.(),
            ...(integration
              ? {
                  attemptId: submittedAttempt,
                  recover: !continuation && integration.access?.canRecover === true
                }
              : {})
          },
          { method: 'POST', signal: controller.signal }
        )
      );
      if (!current()) return false;

      let refusal: ApiError | null = null;
      if (!response.ok) {
        try {
          await ApiService.handleResponse(response.clone());
        } catch (error: unknown) {
          if (!(error instanceof ApiError)) throw error;
          refusal = error;
        }
        if (!current()) return false;
      }

      let result: SteamLoginApiResult;
      try {
        result = await response.json();
        if (!current()) return false;
      } catch (_jsonError) {
        return await readLostAnswer();
      }

      if (response.ok) {
        if (result.attemptId) {
          attemptRef.current = result.attemptId;
          setAttemptId(result.attemptId);
        }
        if (result.expiresAtUtc) setLoginDeadline(Date.parse(result.expiresAtUtc));
        if (result.sessionExpired) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          setUseManualCode(true);
          addNotification({
            type: 'generic',
            status: 'failed',
            message: t('modals.steamAuth.errors.mobileConfirmationTimedOut')
          });
          return false;
        }

        if (result.requiresTwoFactor) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          return false;
        }

        if (result.requiresEmailCode) {
          setWaitingForMobileConfirmation(false);
          setNeedsEmailCode(true);
          return false;
        }

        if (result.success) {
          attemptRef.current = null;
          cancelledAttemptRef.current = null;
          onSuccess?.(t('modals.steamAuth.success.authenticatedAs', { username }));
          resetAuthForm();
          return true;
        }

        setWaitingForMobileConfirmation(false);
        setLoginDeadline(null);
        const refused = t('modals.steamAuth.errors.authenticationFailed');
        setError(refused);
        return false;
      }

      setWaitingForMobileConfirmation(false);
      setLoading(false);
      if (result.attemptId) {
        attemptRef.current = result.attemptId;
        setAttemptId(result.attemptId);
        setNeedsTwoFactor(true);
        setUseManualCode(true);
        if (result.expiresAtUtc) setLoginDeadline(Date.parse(result.expiresAtUtc));
        if (refusal?.body?.stageKey) setError(t(refusal.body.stageKey, refusal.body.context ?? {}));
        return false;
      }
      const errorMsg = refusal?.body?.stageKey
        ? t(refusal.body.stageKey, refusal.body.context ?? {})
        : t('modals.steamAuth.errors.authenticationFailed');
      resetAuthForm();
      // After the reset, which clears the previous attempt's error along with the typed
      // credentials. A wrong password lands here, and this is the line the modal shows.
      setError(errorMsg);
      onError?.(errorMsg);
      return false;
    } catch (err: unknown) {
      if (!current()) return false;
      // Closing the dialog or switching to a code moves the request on before it aborts, so an abort that is still
      // current is this page's own two-minute wait; a dropped connection is the other way here. The server keeps the
      // sign-in going in both.
      if (!(err instanceof Error && err.name === 'AbortError') || timedOut)
        return await readLostAnswer();
      return false;
    } finally {
      if (requestTimeout) {
        clearTimeout(requestTimeout);
      }
      if (current()) {
        busyRef.current = false;
        setLoading(false);
        setAbortController(null);
      }
      if (identityRef.current === integration?.identity) void integration?.refresh();
    }
  };

  const state = buildSteamOnlyState(
    formCurrent && (loading || endingWait !== null),
    formCurrent && needsTwoFactor,
    formCurrent && needsEmailCode,
    formCurrent && waitingForMobileConfirmation,
    formCurrent && useManualCode,
    formCurrent ? username : '',
    formCurrent ? password : '',
    formCurrent ? twoFactorCode : '',
    formCurrent ? emailCode : '',
    formCurrent ? error : null
  );

  const actions: SteamAuthActions = {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    setUseManualCode,
    setNeedsTwoFactor,
    setWaitingForMobileConfirmation,
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setAuthorizationCode: () => {},
    handleAuthenticate,
    resetAuthForm: () => {
      if (identityRef.current === integration?.identity && attemptRef.current === attemptId)
        resetAuthForm();
    },
    cancelPendingRequest: () => {
      if (identityRef.current === integration?.identity && attemptRef.current === attemptId)
        cancelPendingRequest();
    },
    ...(integration ? { cancelLogin } : {})
  };

  return {
    state: {
      ...state,
      ...(integration
        ? {
            attemptId: formCurrent ? attemptId : null,
            canAuthenticate,
            accessUnavailable,
            ownershipReason: integration.access?.ownershipReason,
            recovering: integration.access?.canRecover === true
          }
        : {})
    },
    actions,
    loginDeadline: formCurrent ? loginDeadline : null
  };
}
