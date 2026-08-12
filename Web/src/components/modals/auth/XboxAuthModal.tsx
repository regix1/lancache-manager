import React from 'react';
import { ExternalLink } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { XboxIcon } from '@components/ui/XboxIcon';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { LoginSteps } from './LoginSteps';
import { LoginAttemptStatus } from './LoginAttemptStatus';
import { cancelAuthModalLogin } from './authModalCancel';
import { copyText } from '@utils/clipboard';
import { useTranslation } from 'react-i18next';

// The Xbox modal only consumes the device-code slice of an auth flow. Both the prefill-daemon
// flow (SteamLoginFlowState/SteamAuthActions, a superset) and the manager-side useXboxMappingAuth
// hook satisfy these narrow shapes structurally, so the modal stays decoupled from either stack.
interface XboxAuthModalState {
  loading: boolean;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
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
   * flow): a plain close only hides the modal (including mid device-code) and leaves the daemon
   * login resumable; only the footer button actually cancels.
   */
  dismissBehavior?: 'cancel' | 'keep-pending';
  /** Persistent-container flow only: epoch ms this login attempt expires at
   *  (`PersistentLoginStoreState.loginDeadline`). `null`/unset renders no countdown. */
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
  const { loading, needsDeviceCode, deviceUserCode, deviceVerificationUri } = state;

  const { handleAuthenticate, cancelPendingRequest } = actions;

  const [isSubmitting, setIsSubmitting] = React.useState(false);
  const [copied, setCopied] = React.useState(false);
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

  // keep-pending (persistent-container flow): X/backdrop/Escape now do the same login-ending work
  // as the explicit Cancel button, even mid device-code - a soft, cancel-nothing close used to
  // leave the daemon login (and the Configure card's "Authenticating..." badge) stuck forever.
  const handleSoftClose = handleExplicitCancel;

  const handleSubmit = async () => {
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

  const handleOpenVerificationUrl = () => {
    if (deviceVerificationUri) {
      window.open(deviceVerificationUri, '_blank', 'noopener,noreferrer');
    }
  };

  const handleCopyCode = async () => {
    // Says which of the two happened. navigator.clipboard is absent over plain http, which is how
    // most people reach this app, and a button that reports nothing there reads as dead next to a
    // nine-character code the user would otherwise retype by hand.
    const ok = await copyText(deviceUserCode);
    setCopied(ok);
    setCopyFailed(!ok);
    if (ok) {
      setTimeout(() => setCopied(false), 2000);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={isKeepPending ? handleSoftClose : handleCloseModal}
      // Keep-pending persistent-container login must stay clickable above a reopened Configure modal:
      // open it in the elevated stacking band. The guest/mapping flow ('cancel') stacks normally.
      stackPriority={isKeepPending ? 'elevated' : 'normal'}
      title={
        <div className="flex items-center gap-3">
          <XboxIcon size={20} className="text-[var(--theme-xbox)]" />
          <span>{t('modals.xboxAuth.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-6">
        <LoginSteps
          notice={isKeepPending ? t('modals.xboxAuth.containerAccountNotice') : null}
          deadline={loginDeadline}
          pastFirstStep={needsDeviceCode}
        />

        <div className="login-states">
          {/* Sign-in prompt, and the connect that follows it: the same box either way, so pressing
              Continue changes the line at the bottom and moves the panel no pixels. */}
          {!needsDeviceCode && (
            <>
              <h3 className="text-base font-semibold text-themed-primary text-center">
                {t('modals.xboxAuth.signInTitle')}
              </h3>
              {/* This step's only control is the footer Continue button. */}
              <div className="login-task" />
            </>
          )}

          {/* Device-code state - show the user code + verification URL */}
          {needsDeviceCode && (
            <>
              <h3 className="text-base font-semibold text-themed-primary text-center">
                {t('modals.xboxAuth.enterCodeTitle')}
              </h3>
              <div className="login-task">
                {/* The device user code the user types at the verification URL, with the copy
                    control on the same row so reaching for it never changes the box's height. */}
                {deviceUserCode && (
                  <div className="flex gap-3">
                    <div className="well-surface flex-1 px-3 py-2.5 font-mono text-xl font-bold tracking-widest text-center text-themed-primary select-all">
                      {deviceUserCode}
                    </div>
                    <Button variant="default" onClick={handleCopyCode}>
                      {copied
                        ? t('modals.xboxAuth.codeCopied')
                        : copyFailed
                          ? t('modals.xboxAuth.copyFailed')
                          : t('common.copy')}
                    </Button>
                  </div>
                )}

                {/* Open Microsoft verification page */}
                {deviceVerificationUri && (
                  <Button variant="filled" onClick={handleOpenVerificationUrl} className="w-full">
                    <ExternalLink className="w-4 h-4" />
                    {t('modals.xboxAuth.openVerification')}
                  </Button>
                )}
              </div>
            </>
          )}

          {/* Rendered in every state, including the ones with nothing to say, so the live region
              is already in the page when the login moves on and its label changes. */}
          <LoginAttemptStatus
            label={
              isConnecting
                ? t('modals.xboxAuth.connectingSubtitle')
                : needsDeviceCode
                  ? t('modals.xboxAuth.waitingMessage')
                  : ''
            }
            note={needsDeviceCode ? undefined : t('modals.xboxAuth.signInDescription')}
          />
        </div>

        {/* Action Buttons */}
        <div className="flex gap-3 pt-2 border-t border-themed-secondary">
          <Button
            variant="default"
            onClick={isKeepPending ? handleExplicitCancel : handleCloseModal}
            className="flex-1"
          >
            {t('common.cancel')}
          </Button>
          {!needsDeviceCode && (
            <Button
              variant="filled"
              onClick={handleSubmit}
              disabled={loading || isSubmitting}
              className="flex-1"
            >
              {(loading || isSubmitting) && <LoadingSpinner inline size="sm" className="mr-2" />}
              {loading || isSubmitting
                ? t('modals.xboxAuth.actions.connecting')
                : t('modals.xboxAuth.actions.continue')}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};
