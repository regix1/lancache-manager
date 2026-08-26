import React, { useEffect } from 'react';
import { Key } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { cancelAuthModalLogin } from './authModalCancel';
import { LoginSteps } from './LoginSteps';
import { LoginAttemptStatus } from './LoginAttemptStatus';
import { type SteamLoginFlowState, type SteamAuthActions } from '@hooks/useSteamAuthentication';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTranslation } from 'react-i18next';

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
   * flow): a plain close only hides the modal and leaves the daemon login resumable; only the
   * footer button actually cancels.
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
    if (!opened || disableAutoLogoutClose) return;

    const handleAutoLogout = () => {
      cancelPendingRequest();
      actions.resetAuthForm();
      onClose();
    };

    on('SteamAutoLogout', handleAutoLogout);
    return () => {
      off('SteamAutoLogout', handleAutoLogout);
    };
  }, [opened, disableAutoLogoutClose, on, off, cancelPendingRequest, actions, onClose]);

  const handleCloseModal = () => {
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
      onCancelLogin,
      onClose
    });
  };

  // keep-pending (persistent-container flow): X/backdrop/Escape now do the same login-ending work
  // as the explicit Cancel button - a soft, cancel-nothing close used to leave the daemon login
  // (and the Configure card's "Authenticating..." badge) stuck forever.
  const handleSoftClose = handleExplicitCancel;

  const handleSubmit = async () => {
    // Prevent multiple clicks - check immediately before any async work
    if (isSubmitting || loading) return;
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

  // For regular mode: switch to manual 2FA code entry
  const handleSwitchToManualCode = () => {
    cancelPendingRequest();
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

  // The manual-code escape hatch. It shares the footer with Cancel, and hitting Cancel by mistake
  // throws away the pending session and the typed credentials, so Cancel shrinks to its own text
  // and this one takes the rest of the row.
  const showManualCodeButton = waitingForMobileConfirmation && !isPrefillMode;

  return (
    <Modal
      opened={opened}
      onClose={isKeepPending ? handleSoftClose : handleCloseModal}
      // The persistent-container login (keep-pending) is a prompt that must always stay clickable
      // above the Configure modal, even if Configure is reopened after it - open it in the elevated
      // stacking band. The mapping/guest flow (dismissBehavior 'cancel') stacks normally.
      stackPriority={isKeepPending ? 'elevated' : 'normal'}
      title={
        <div className="flex items-center gap-3">
          <Key className="w-5 h-5 text-steam" />
          <span>{t('modals.steamAuth.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-6">
        <LoginSteps
          notice={isKeepPending ? t('modals.steamAuth.containerAccountNotice') : null}
          deadline={loginDeadline}
          pastFirstStep={currentStep !== 'credentials'}
        />

        <div className="login-states">
          <h3 className="text-base font-semibold text-themed-primary text-center">
            {needsEmailCode
              ? t('modals.steamAuth.emailVerification.title')
              : needsTwoFactor
                ? t('modals.steamAuth.twoFactor.title')
                : !awaitingChallenge && waitingForMobileConfirmation
                  ? t('modals.steamAuth.mobileConfirmation.title')
                  : t('modals.steamAuth.signInTitle')}
          </h3>

          <div className="login-task">
            {/* The credentials form stays on screen through the connect and the phone wait,
                disabled and holding what was typed, so neither move costs the panel a pixel. */}
            {!needsTwoFactor && !needsEmailCode && (
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
                        disabled={loading || awaitingChallenge || waitingForMobileConfirmation}
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
                        disabled={loading || awaitingChallenge || waitingForMobileConfirmation}
                        autoComplete="current-password"
                      />
                    )}
                  </FormField>
                </div>
              </>
            )}

            {needsEmailCode && (
              <div>
                <FormField label={t('modals.steamAuth.labels.emailCode')}>
                  {(field) => (
                    <input
                      {...field}
                      type="text"
                      value={emailCode}
                      onChange={(e) => setEmailCode(e.target.value)}
                      onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                      placeholder={t('modals.steamAuth.placeholders.guardCode')}
                      className="w-full px-3 py-3 themed-input text-center text-xl tracking-[0.5em] font-mono uppercase"
                      disabled={loading}
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
                      {...field}
                      type="text"
                      value={twoFactorCode}
                      onChange={(e) => setTwoFactorCode(e.target.value.toUpperCase())}
                      onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                      placeholder={t('modals.steamAuth.placeholders.guardCode')}
                      className="w-full px-3 py-3 themed-input text-center text-xl tracking-[0.5em] font-mono uppercase"
                      disabled={loading}
                      autoFocus
                      maxLength={5}
                    />
                  )}
                </FormField>
              </div>
            )}

            {/* A container login answers the challenge the daemon raised and cannot volunteer a
                different one, so once Steam has picked phone approval there is no code box to
                offer and the wait is the whole step. The in-process login re-sends the whole
                credential set with allowMobileConfirmation off instead, which is why it can put a
                manual-code button here and this one cannot. Say so, rather than leaving the user
                hunting the panel for an input that is never coming. */}
            {isPrefillMode && waitingForMobileConfirmation && (
              <p className="text-sm text-themed-muted text-center">
                {t('modals.steamAuth.mobileConfirmation.phoneOnly')}
              </p>
            )}
          </div>

          {/* Rendered in every state, including the ones with nothing to say, so the live region
              is already in the page when the login moves on and its label changes. The error rides
              in the same reserved row: it is the same sentence the notification bar gets, drawn
              where the person is actually looking, because the modal sits over the bar and a wrong
              password used to change nothing on screen at all. */}
          <LoginAttemptStatus
            label={
              !awaitingChallenge && waitingForMobileConfirmation
                ? t('modals.steamAuth.mobileConfirmation.waiting')
                : awaitingChallenge || loading || isSubmitting
                  ? t('modals.steamAuth.connectingSubtitle')
                  : ''
            }
            note={
              !needsTwoFactor && !needsEmailCode
                ? t('modals.steamAuth.security.description')
                : needsTwoFactor && !useManualCode
                  ? t('modals.steamAuth.twoFactor.leaveEmptyHint')
                  : undefined
            }
            error={error}
          />
        </div>

        <div className="flex gap-3 pt-2 border-t border-themed-secondary">
          <Button
            variant="default"
            onClick={isKeepPending ? handleExplicitCancel : handleCloseModal}
            disabled={!isKeepPending && (loading || isSubmitting) && !waitingForMobileConfirmation}
            className={showManualCodeButton ? '' : 'flex-1'}
          >
            {t('common.cancel')}
          </Button>
          {showManualCodeButton && (
            <Button variant="default" onClick={handleSwitchToManualCode} className="flex-1">
              {t('modals.steamAuth.actions.enterCodeManually')}
            </Button>
          )}
          {!waitingForMobileConfirmation && !awaitingChallenge && (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={
                loading ||
                isSubmitting ||
                (!needsTwoFactor && !needsEmailCode && (!username.trim() || !password.trim())) ||
                (useManualCode && !twoFactorCode.trim())
              }
              className="flex-1"
            >
              {(loading || isSubmitting) && <LoadingSpinner inline size="sm" className="mr-2" />}
              {loading || isSubmitting
                ? t('modals.steamAuth.actions.authenticating')
                : needsEmailCode
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
