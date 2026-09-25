import { noAutofill } from '@utils/autofill';
import React, { useEffect } from 'react';
import { Modal } from '@components/ui/Modal';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Button } from '@components/ui/Button';
import { SteamIcon } from '@components/ui/SteamIcon';
import FormField from '@components/ui/FormField';
import { cancelAuthModalLogin } from './authModalCancel';
import { LoginSteps } from './LoginSteps';
import { LoginAttemptStatus } from './LoginAttemptStatus';
import { type SteamLoginFlowState, type SteamAuthActions } from '@hooks/useSteamAuthentication';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTranslation } from 'react-i18next';
import { getIntegrationReasonKey } from '../../../types';

interface SteamAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: SteamLoginFlowState;
  actions: SteamAuthActions;
  /** If true, uses daemon mode behavior (cancel ends session instead of switching to manual code) */
  isPrefillMode?: boolean;
  /** Called when user cancels during device confirmation in prefill mode - should end session */
  onCancelLogin?: () => void;
  /**
   * 'cancel' (default, the manager's own mapping-login flow): any close - X, backdrop, Escape, or
   * the footer button - cancels the in-flight login. 'keep-pending' (the persistent-container
   * flow): X, Escape and the footer button cancel the daemon login; a backdrop click does nothing.
   */
  dismissBehavior?: 'cancel' | 'keep-pending';
  /** Persistent-container flow only: the manager's own SteamAutoLogout event must not force-close
   *  a container login (that event is about the manager's mapping-flow session, not this one). */
  disableAutoLogoutClose?: boolean;
  /** Persistent-container flow only: show a "contacting daemon" state before any challenge has
   *  arrived, instead of the (empty) credentials form. */
  awaitingChallenge?: boolean;
  /** Epoch ms this login attempt expires at, from whichever timer governs THIS mount: the
   *  persistent-container store, `useSteamLoginFlow`'s request timeout, or `usePrefillSteamAuth`'s
   *  phone-approval wait. `null`/unset renders no countdown, which is the honest answer for a step
   *  that waits on the person instead of on a clock. */
  loginDeadline?: number | null;
}

export const SteamAuthModal: React.FC<SteamAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  isPrefillMode = false,
  onCancelLogin,
  dismissBehavior = 'cancel',
  disableAutoLogoutClose = false,
  awaitingChallenge = false,
  loginDeadline = null
}) => {
  const { t } = useTranslation();
  const { on, off } = useSignalR();
  const isKeepPending = dismissBehavior === 'keep-pending';
  const {
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode,
    error
  } = state;

  const {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    handleAuthenticate,
    cancelPendingRequest
  } = actions;

  // Track if a submit is in progress to prevent spam clicks
  const [isSubmitting, setIsSubmitting] = React.useState(false);

  // Listen for SteamAutoLogout event - if session is replaced, close the modal. This is about the
  // MANAGER's own mapping-flow session; a persistent-container login is unrelated and must not be
  // force-closed by it (diagnostic §6 item 5).
  useEffect(() => {
    if (!opened || disableAutoLogoutClose || state.canAuthenticate !== undefined) return;

    const handleAutoLogout = () => {
      cancelPendingRequest();
      actions.resetAuthForm();
      onClose();
    };

    on('SteamAutoLogout', handleAutoLogout);
    return () => {
      off('SteamAutoLogout', handleAutoLogout);
    };
  }, [
    opened,
    disableAutoLogoutClose,
    on,
    off,
    cancelPendingRequest,
    actions,
    onClose,
    state.canAuthenticate
  ]);

  const handleCloseModal = () => {
    if (state.canAuthenticate !== undefined) {
      handleExplicitCancel();
      return;
    }
    // Allow closing when waiting for mobile confirmation (user should be able to cancel).
    // onCancelLogin ends the daemon session in prefill mode and stops the credentials poll in the
    // manager's own flow: both are this dismiss ending the sign-in, so neither is gated on the mode.
    if (waitingForMobileConfirmation) {
      cancelPendingRequest();
      actions.resetAuthForm();
      onCancelLogin?.();
      onClose();
      return;
    }

    // In prefill mode, closing during any auth state should cancel the login
    if (isPrefillMode && (loading || needsTwoFactor || needsEmailCode)) {
      cancelPendingRequest();
      actions.resetAuthForm();
      onCancelLogin?.();
      onClose();
      return;
    }

    if (!loading && !isSubmitting) {
      onClose();
    }
  };

  const handleExplicitCancel = () => {
    cancelAuthModalLogin({
      cancelPendingRequest,
      resetAuthForm: actions.resetAuthForm,
      onCancelLogin: actions.cancelLogin ?? onCancelLogin,
      onClose
    });
  };

  const handleSubmit = async () => {
    // Prevent multiple clicks - check immediately before any async work
    if (isSubmitting || loading || state.canAuthenticate === false) return;
    setIsSubmitting(true);

    try {
      const success = await handleAuthenticate();
      if (success) {
        onClose();
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  // For regular mode: switch to manual 2FA code entry.
  //
  // Abandoning the phone wait needs the same server-side cancel a dismiss does. Dropping the
  // request only ends this side of it: the wait it started keeps running for its own window, the
  // account stays marked as signing in, and the code typed here is then refused with "a Steam
  // sign-in is already in progress" until that window expires. The typed code is still good by
  // then, but the person has been told it is not.
  const handleSwitchToManualCode = () => {
    if (state.canAuthenticate === false) return;
    cancelPendingRequest();
    if (state.canAuthenticate === undefined) onCancelLogin?.();
    actions.setWaitingForMobileConfirmation(false);
    actions.setNeedsTwoFactor(true);
    actions.setUseManualCode(true);
    actions.setTwoFactorCode('');
  };

  // Determine current step for visual indicator
  const getCurrentStep = () => {
    if (waitingForMobileConfirmation) return 'mobile';
    if (needsEmailCode) return 'email';
    if (needsTwoFactor) return '2fa';
    return 'credentials';
  };

  const currentStep = getCurrentStep();

  const secondStepName = {
    credentials: t('modals.steamAuth.steps.verify'),
    email: t('modals.steamAuth.steps.email'),
    '2fa': t('modals.steamAuth.steps.guard'),
    mobile: t('modals.steamAuth.steps.phone')
  }[currentStep];

  // The manual-code escape hatch sits in the phone-approval step's own body rather than the
  // footer, so the footer keeps Cancel as its only button in that step and a mistaken press on
  // one never lands on the other.
  const showManualCodeButton = waitingForMobileConfirmation && !isPrefillMode;
  const isPhoneStage = !awaitingChallenge && waitingForMobileConfirmation;

  return (
    <Modal
      opened={opened}
      onClose={isKeepPending ? handleExplicitCancel : handleCloseModal}
      dismissOnBackdrop={!isKeepPending}
      bodyFlexLayout
      // The persistent-container login (keep-pending) is a prompt that must always stay clickable
      // above the Configure modal, even if Configure is reopened after it - open it in the elevated
      // stacking band. The mapping/guest flow (dismissBehavior 'cancel') stacks normally.
      stackPriority={isKeepPending ? 'elevated' : 'normal'}
      title={
        <div className="login-modal-header">
          <span className="icon-box login-modal-icon login-modal-icon--steam">
            <SteamIcon size={20} />
          </span>
          <div className="min-w-0">
            <div>{t('modals.steamAuth.title')}</div>
            {isKeepPending && (
              <p className="login-modal-notice">{t('modals.steamAuth.containerAccountNotice')}</p>
            )}
          </div>
        </div>
      }
      size="md"
    >
      <div className="modal-body-layout">
        <CustomScrollbar
          maxHeight="none"
          className="modal-body-scroll"
          contentClassName="space-y-4 sm:space-y-6"
          railPlacement="outer"
          radius="none"
        >
          {state.recovering && (
            <p className="text-sm text-themed-secondary">{t('errors.integration.recovery')}</p>
          )}
          {state.canAuthenticate === false && (
            <p className="text-sm text-themed-secondary" role="status">
              {state.accessUnavailable
                ? t('errors.integration.statusUnavailable')
                : t(getIntegrationReasonKey(state.ownershipReason))}
            </p>
          )}
          <LoginSteps
            steps={[t('modals.steamAuth.steps.signIn'), secondStepName]}
            pastFirstStep={currentStep !== 'credentials'}
          />

          <div className="login-states">
            {/* Rendered in every state, so the live region is already in the page when the login
              moves on and its label changes. The error rides under it: it is the same title and
              reason the notification bar gets, drawn where the person is actually looking,
              because the modal sits over the bar and a wrong password used to change nothing on
              screen at all. */}
            <LoginAttemptStatus
              label={
                isPhoneStage
                  ? t('modals.steamAuth.mobileConfirmation.waiting')
                  : awaitingChallenge || loading || isSubmitting
                    ? t('modals.steamAuth.connectingSubtitle')
                    : needsTwoFactor || needsEmailCode
                      ? t('common.waitingForCode')
                      : t('modals.steamAuth.status.credentials')
              }
              busy={isPhoneStage || awaitingChallenge || loading || isSubmitting}
              deadline={loginDeadline}
              error={error}
              errorTitle={t('common.errors.signInFailed', {
                platform: t('prefill.persistent.services.steam')
              })}
            />

            <div>
              <h3 className="text-base font-semibold text-themed-primary">
                {needsEmailCode
                  ? t('modals.steamAuth.emailVerification.title')
                  : needsTwoFactor
                    ? t('modals.steamAuth.twoFactor.title')
                    : isPhoneStage
                      ? t('modals.steamAuth.mobileConfirmation.title')
                      : t('modals.steamAuth.signInTitle')}
              </h3>
              {needsEmailCode ? (
                <p className="mt-1 text-sm text-themed-muted">
                  {t('modals.steamAuth.emailVerification.help')}
                </p>
              ) : needsTwoFactor ? (
                <p className="mt-1 text-sm text-themed-muted">
                  {t('modals.steamAuth.twoFactor.help')}
                  {!useManualCode && <> {t('modals.steamAuth.twoFactor.leaveEmptyHint')}</>}
                </p>
              ) : isPhoneStage ? (
                <p className="mt-1 text-sm text-themed-muted">
                  {showManualCodeButton
                    ? t('modals.steamAuth.mobileConfirmation.phoneOrCode')
                    : t('modals.steamAuth.mobileConfirmation.phoneOnly')}
                </p>
              ) : awaitingChallenge ? (
                <p className="mt-1 text-sm text-themed-muted">
                  {t('modals.steamAuth.connectingHelp')}
                </p>
              ) : null}
            </div>

            <div className="login-task">
              {!needsTwoFactor &&
                !needsEmailCode &&
                !waitingForMobileConfirmation &&
                !awaitingChallenge && (
                  <>
                    <div>
                      <FormField label={t('modals.steamAuth.labels.username')}>
                        {(field) => (
                          <input
                            {...field}
                            type="text"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                            placeholder={t('modals.steamAuth.placeholders.username')}
                            className="w-full px-3 py-2.5 themed-input"
                            disabled={
                              state.canAuthenticate === false ||
                              loading ||
                              awaitingChallenge ||
                              waitingForMobileConfirmation
                            }
                            autoComplete="username"
                          />
                        )}
                      </FormField>
                    </div>

                    <div>
                      <FormField label={t('modals.steamAuth.labels.password')}>
                        {(field) => (
                          <input
                            {...field}
                            type="password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                            placeholder={t('modals.steamAuth.placeholders.password')}
                            className="w-full px-3 py-2.5 themed-input"
                            disabled={
                              state.canAuthenticate === false ||
                              loading ||
                              awaitingChallenge ||
                              waitingForMobileConfirmation
                            }
                            autoComplete="current-password"
                          />
                        )}
                      </FormField>
                    </div>

                    <p className="text-sm text-themed-muted">
                      {t('modals.steamAuth.security.description')}
                    </p>
                  </>
                )}

              {needsEmailCode && (
                <div>
                  <FormField label={t('modals.steamAuth.labels.emailCode')}>
                    {(field) => (
                      <input
                        {...noAutofill}
                        {...field}
                        type="text"
                        value={emailCode}
                        onChange={(e) => setEmailCode(e.target.value)}
                        onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                        placeholder={t('modals.steamAuth.placeholders.guardCode')}
                        className="w-full px-3 py-3 themed-input text-center text-xl tracking-[0.5em] font-mono uppercase"
                        disabled={state.canAuthenticate === false || loading}
                        autoFocus
                        maxLength={5}
                      />
                    )}
                  </FormField>
                </div>
              )}

              {needsTwoFactor && (
                <div>
                  <FormField label={t('modals.steamAuth.labels.guardCode')}>
                    {(field) => (
                      <input
                        {...noAutofill}
                        {...field}
                        type="text"
                        value={twoFactorCode}
                        onChange={(e) => setTwoFactorCode(e.target.value.toUpperCase())}
                        onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                        placeholder={t('modals.steamAuth.placeholders.guardCode')}
                        className="w-full px-3 py-3 themed-input text-center text-xl tracking-[0.5em] font-mono uppercase"
                        disabled={state.canAuthenticate === false || loading}
                        autoFocus
                        maxLength={5}
                      />
                    )}
                  </FormField>
                </div>
              )}

              {/* A container login answers the challenge the daemon raised and cannot volunteer a
                different one, so once Steam has picked phone approval the wait is the whole step.
                The in-process login re-sends the whole credential set with
                allowMobileConfirmation off instead, which is why it can offer a manual code here
                and a container login cannot. */}
              {showManualCodeButton && (
                <Button
                  variant="default"
                  onClick={handleSwitchToManualCode}
                  disabled={state.canAuthenticate === false}
                  className="w-full sm:w-56 min-h-[44px] sm:min-h-10"
                >
                  {t('modals.steamAuth.actions.enterCodeManually')}
                </Button>
              )}
            </div>
          </div>
        </CustomScrollbar>

        <div className="confirmation-modal__actions">
          <Button
            variant="default"
            onClick={isKeepPending ? handleExplicitCancel : handleCloseModal}
            disabled={!isKeepPending && (loading || isSubmitting) && !waitingForMobileConfirmation}
            className="min-h-[44px] sm:min-h-10"
          >
            {t('common.cancel')}
          </Button>
          {!waitingForMobileConfirmation && (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={
                awaitingChallenge ||
                state.canAuthenticate === false ||
                loading ||
                isSubmitting ||
                (!needsTwoFactor && !needsEmailCode && (!username.trim() || !password.trim())) ||
                (useManualCode && !twoFactorCode.trim())
              }
              className="min-h-[44px] sm:min-h-10"
            >
              {needsEmailCode
                ? t('modals.steamAuth.actions.verify')
                : needsTwoFactor
                  ? t('modals.steamAuth.actions.confirm')
                  : t('modals.steamAuth.actions.login')}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};
