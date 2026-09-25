import React from 'react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { XboxIcon } from '@components/ui/XboxIcon';
import { LoginSteps } from './LoginSteps';
import { LoginAttemptStatus } from './LoginAttemptStatus';
import { cancelAuthModalLogin } from './authModalCancel';
import { useCopyFeedback } from '@hooks/useCopyFeedback';
import { copyText } from '@utils/clipboard';
import { useTranslation } from 'react-i18next';
import { getIntegrationReasonKey } from '../../../types';

// The Xbox modal only consumes the device-code slice of an auth flow. Both the prefill-daemon
// flow (SteamLoginFlowState/SteamAuthActions, a superset) and the manager-side useXboxMappingAuth
// hook satisfy these narrow shapes structurally, so the modal stays decoupled from either stack.
interface XboxAuthModalState {
  canAuthenticate?: boolean;
  accessUnavailable?: boolean;
  ownershipReason?: string | null;
  recovering?: boolean;
  loading: boolean;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
  error: string | null;
}

interface XboxAuthModalActions {
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

interface XboxAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: XboxAuthModalState;
  actions: XboxAuthModalActions;
  onCancelLogin?: () => void;
  /**
   * 'cancel' (default, the manager's own mapping-login flow): any close - X, backdrop, Escape, or
   * the footer button - cancels the in-flight login. 'keep-pending' (the persistent-container
   * flow): X, Escape and the footer button cancel the daemon login (including mid device-code); a
   * backdrop click does nothing.
   */
  dismissBehavior?: 'cancel' | 'keep-pending';
  /** Epoch ms this login attempt expires at, from whichever timer governs THIS mount: the
   *  persistent-container store, or `usePrefillSteamAuth`'s device-code wait. `null`/unset renders
   *  no countdown, which is the honest answer where nothing client-side is counting - the
   *  manager-side mapping login is polled by the backend and has no timer here. */
  loginDeadline?: number | null;
}

/**
 * Microsoft OAuth device-code modal for Xbox prefill.
 *
 * No password ever enters the daemon container: the user opens the verification URL in
 * their own browser and enters the short user code shown here. The modal stays open while
 * the daemon polls Microsoft; AuthStateChanged drives success (handled in usePrefillSteamAuth).
 * This is the closest sibling to Steam's device-confirmation, NOT Epic's code-paste flow.
 */
export const XboxAuthModal: React.FC<XboxAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  onCancelLogin,
  dismissBehavior = 'cancel',
  loginDeadline = null
}) => {
  const { t } = useTranslation();
  const isKeepPending = dismissBehavior === 'keep-pending';
  const { loading, needsDeviceCode, deviceUserCode, deviceVerificationUri, error } = state;

  const { handleAuthenticate, cancelPendingRequest } = actions;

  const [isSubmitting, setIsSubmitting] = React.useState(false);
  const [copied, markCopied] = useCopyFeedback(false);
  const [copyFailed, setCopyFailed] = React.useState(false);

  // The device-code challenge has not come back from the daemon yet.
  const isConnecting = (loading || isSubmitting) && !needsDeviceCode;

  const handleCloseModal = () => {
    if (loading || isSubmitting || needsDeviceCode) {
      cancelPendingRequest();
      actions.resetAuthForm();
      onCancelLogin?.();
      onClose();
      return;
    }

    onClose();
  };

  const handleExplicitCancel = () => {
    cancelAuthModalLogin({
      cancelPendingRequest,
      resetAuthForm: actions.resetAuthForm,
      onCancelLogin,
      onClose
    });
  };

  const handleSubmit = async () => {
    if (state.canAuthenticate === false || isSubmitting || loading) return;
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

  const handleOpenVerificationUrl = () => {
    if (state.canAuthenticate === false) return;
    if (deviceVerificationUri) {
      window.open(deviceVerificationUri, '_blank', 'noopener,noreferrer');
    }
  };

  const handleCopyCode = async () => {
    if (state.canAuthenticate === false) return;
    // Says which of the two happened. navigator.clipboard is absent over plain http, which is how
    // most people reach this app, and a button that reports nothing there reads as dead next to a
    // nine-character code the user would otherwise retype by hand.
    const ok = await copyText(deviceUserCode);
    setCopyFailed(!ok);
    if (ok) {
      markCopied(true);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={isKeepPending ? handleExplicitCancel : handleCloseModal}
      dismissOnBackdrop={!isKeepPending}
      bodyFlexLayout
      // Keep-pending persistent-container login must stay clickable above a reopened Configure modal:
      // open it in the elevated stacking band. The guest/mapping flow ('cancel') stacks normally.
      stackPriority={isKeepPending ? 'elevated' : 'normal'}
      title={
        <div className="login-modal-header">
          <span className="icon-box login-modal-icon login-modal-icon--xbox">
            <XboxIcon size={20} />
          </span>
          <div className="min-w-0">
            <div>{t('modals.xboxAuth.title')}</div>
            {isKeepPending && (
              <p className="login-modal-notice">{t('modals.xboxAuth.containerAccountNotice')}</p>
            )}
          </div>
        </div>
      }
      size="md"
    >
      <div className="modal-body-layout">
        <div className="modal-body-scroll space-y-4 sm:space-y-6">
          {state.canAuthenticate === false && (
            <p className="text-sm text-themed-muted" role="status">
              {state.accessUnavailable
                ? t('errors.integration.statusUnavailable')
                : t(getIntegrationReasonKey(state.ownershipReason))}
            </p>
          )}
          {state.recovering && (
            <p className="text-sm text-themed-muted">{t('errors.integration.recovery')}</p>
          )}
          <LoginSteps
            steps={[t('modals.xboxAuth.steps.code'), t('modals.xboxAuth.steps.approve')]}
            pastFirstStep={needsDeviceCode}
          />

          <div className="login-states">
            {/* Rendered in every state, so the live region is already in the page when the login
              moves on and its label changes. The error rides under it: it is the same title and
              reason the notification bar gets, drawn where the person is actually looking,
              because the modal sits over the bar and a refused sign-in used to change nothing on
              screen at all. */}
            <LoginAttemptStatus
              label={
                isConnecting
                  ? t('modals.xboxAuth.connectingSubtitle')
                  : needsDeviceCode
                    ? t('modals.xboxAuth.waitingMessage')
                    : ''
              }
              busy={isConnecting || needsDeviceCode}
              deadline={loginDeadline}
              error={error}
              errorTitle={t('common.errors.signInFailed', {
                platform: t('prefill.persistent.services.xbox')
              })}
            />

            {/* Sign-in prompt, and the connect that follows it: the same box either way, so pressing
              Continue changes the line in the strip and moves the panel no pixels. */}
            {!needsDeviceCode && (
              <div>
                <h3 className="text-base font-semibold text-themed-primary">
                  {t('modals.xboxAuth.signInTitle')}
                </h3>
                <p className="mt-1 text-sm text-themed-muted">
                  {t('modals.xboxAuth.signInDescription')}
                </p>
              </div>
            )}

            {/* Device-code state - show the user code + verification URL */}
            {needsDeviceCode && (
              <>
                <h3 className="text-base font-semibold text-themed-primary">
                  {t('modals.xboxAuth.enterCodeTitle')}
                </h3>
                <div className="login-task">
                  {deviceUserCode && (
                    <div className="well-surface flex flex-wrap items-center justify-between gap-3 px-4 py-2.5">
                      <span className="font-mono text-xl font-bold tracking-widest text-themed-primary select-all break-all">
                        {deviceUserCode}
                      </span>
                      <span className="caps-label">{t('modals.xboxAuth.yourCode')}</span>
                    </div>
                  )}

                  {/* Copy and open share one width, so the copied/failed feedback never moves them.
                      They stack below 640px: at 390px half the row is narrower than "Open
                      Microsoft page" on one line. */}
                  <div className="flex flex-col gap-3 sm:flex-row">
                    {deviceUserCode && (
                      <Button
                        variant="default"
                        onClick={handleCopyCode}
                        disabled={state.canAuthenticate === false}
                        className="sm:w-48 min-h-[44px] sm:min-h-10"
                      >
                        {copied
                          ? t('modals.xboxAuth.codeCopied')
                          : copyFailed
                            ? t('modals.xboxAuth.copyFailed')
                            : t('modals.xboxAuth.copyCode')}
                      </Button>
                    )}
                    {deviceVerificationUri && (
                      <Button
                        variant="default"
                        onClick={handleOpenVerificationUrl}
                        disabled={state.canAuthenticate === false}
                        className="sm:w-48 min-h-[44px] sm:min-h-10"
                      >
                        {t('modals.xboxAuth.openVerification')}
                      </Button>
                    )}
                  </div>

                  <ol className="list-decimal pl-5 text-sm text-themed-secondary">
                    <li>{t('modals.xboxAuth.instructions.open')}</li>
                    <li>{t('modals.xboxAuth.instructions.approve')}</li>
                    <li>{t('modals.xboxAuth.instructions.closes')}</li>
                  </ol>
                </div>
              </>
            )}
          </div>
        </div>

        <div className="confirmation-modal__actions">
          <Button
            variant="default"
            onClick={isKeepPending ? handleExplicitCancel : handleCloseModal}
            className="min-h-[44px] sm:min-h-10"
          >
            {t('common.cancel')}
          </Button>
          {!needsDeviceCode && (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={state.canAuthenticate === false || loading || isSubmitting}
              className="min-h-[44px] sm:min-h-10"
            >
              {t('modals.xboxAuth.actions.continue')}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};
