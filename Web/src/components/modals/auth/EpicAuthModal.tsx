import React from 'react';
import { ExternalLink } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import FormField from '@components/ui/FormField';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { LoginSteps } from './LoginSteps';
import { LoginAttemptStatus } from './LoginAttemptStatus';
import { cancelAuthModalLogin } from './authModalCancel';
import { type EpicAuthState, type EpicAuthActions } from '@hooks/useEpicMappingAuth';
import { useTranslation } from 'react-i18next';

interface EpicAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: EpicAuthState;
  actions: EpicAuthActions;
  onCancelLogin?: () => void;
  /**
   * 'cancel' (default, the manager's own mapping-login flow): any close - X, backdrop, Escape, or
   * the footer button - cancels the in-flight login. 'keep-pending' (the persistent-container
   * flow): a plain close only hides the modal and leaves the daemon login resumable; only the
   * footer button actually cancels.
   */
  dismissBehavior?: 'cancel' | 'keep-pending';
  /** Epoch ms this login attempt expires at, from whichever timer governs THIS mount - only the
   *  persistent-container store has one. `null`/unset renders no countdown, which is the honest
   *  answer for the other Epic mounts: nothing client-side is counting there. */
  loginDeadline?: number | null;
}

export const EpicAuthModal: React.FC<EpicAuthModalProps> = ({
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
  const { loading, needsAuthorizationCode, authorizationUrl, authorizationCode, error } = state;

  const { setAuthorizationCode, handleAuthenticate, cancelPendingRequest } = actions;

  const [isSubmitting, setIsSubmitting] = React.useState(false);

  // The authorization URL has not come back from the daemon yet.
  const isConnecting = (loading || isSubmitting) && !needsAuthorizationCode;

  const handleCloseModal = () => {
    if (loading || isSubmitting) {
      cancelPendingRequest();
      actions.resetAuthForm();
      onCancelLogin?.();
      onClose();
      return;
    }

    if (needsAuthorizationCode) {
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
  // as the explicit Cancel button - a soft, cancel-nothing close used to leave the daemon login
  // (and the Configure card's "Authenticating..." badge) stuck forever.
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

  const handleOpenAuthUrl = () => {
    if (authorizationUrl) {
      window.open(authorizationUrl, '_blank', 'noopener,noreferrer');
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
          <EpicIcon size={20} className="text-[var(--theme-epic)]" />
          <span>{t('modals.epicAuth.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-6">
        <LoginSteps
          notice={isKeepPending ? t('modals.epicAuth.containerAccountNotice') : null}
          deadline={loginDeadline}
          pastFirstStep={needsAuthorizationCode}
        />

        <div className="login-states">
          {/* Sign-in prompt, and the connect that follows it: the same box either way, so pressing
              Continue changes the line at the bottom and moves the panel no pixels. */}
          {!needsAuthorizationCode && (
            <>
              <h3 className="text-base font-semibold text-themed-primary text-center">
                {t('modals.epicAuth.signInTitle')}
              </h3>
              {/* This step's only control is the footer Continue button. */}
              <div className="login-task" />
            </>
          )}

          {/* Authorization Code Input - shown after user gets the URL */}
          {needsAuthorizationCode && (
            <>
              <h3 className="text-base font-semibold text-themed-primary text-center">
                {t('modals.epicAuth.enterCodeTitle')}
              </h3>
              <div className="login-task">
                {/* Open Epic Login Button */}
                {authorizationUrl && (
                  <Button
                    variant="filled"
                    color="secondary"
                    onClick={handleOpenAuthUrl}
                    className="w-full"
                  >
                    <ExternalLink className="w-4 h-4" />
                    {t('modals.epicAuth.openEpicLogin')}
                  </Button>
                )}

                {/* Code Input */}
                <div>
                  <FormField label={t('modals.epicAuth.authorizationCodeLabel')}>
                    {(field) => (
                      <input
                        {...field}
                        type="password"
                        value={authorizationCode}
                        onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                          setAuthorizationCode(e.target.value)
                        }
                        onKeyPress={(e: React.KeyboardEvent<HTMLInputElement>) =>
                          e.key === 'Enter' && handleSubmit()
                        }
                        placeholder={t('modals.epicAuth.authorizationCodePlaceholder')}
                        className="w-full px-3 py-2.5 themed-input font-mono text-sm"
                        disabled={loading}
                        autoFocus
                      />
                    )}
                  </FormField>
                </div>
              </div>
            </>
          )}

          {/* Rendered in every state, including the ones with nothing to say, so the live region
              is already in the page when the login moves on and its label changes. The error rides
              in the same reserved row: it is the same sentence the notification bar gets, drawn
              where the person is actually looking, because the modal sits over the bar and a
              rejected code used to change nothing on screen at all. */}
          <LoginAttemptStatus
            label={
              isConnecting
                ? t('modals.epicAuth.connectingSubtitle')
                : needsAuthorizationCode && (loading || isSubmitting)
                  ? t('modals.epicAuth.authenticatingMessage')
                  : ''
            }
            note={needsAuthorizationCode ? undefined : t('modals.epicAuth.signInDescription')}
            error={error}
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
          {needsAuthorizationCode ? (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={loading || isSubmitting || !authorizationCode.trim()}
              className="flex-1"
            >
              {loading || isSubmitting
                ? t('modals.epicAuth.actions.authenticating')
                : t('modals.epicAuth.actions.submitCode')}
            </Button>
          ) : (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={loading || isSubmitting}
              className="flex-1"
            >
              {(loading || isSubmitting) && <LoadingSpinner inline size="sm" className="mr-2" />}
              {loading || isSubmitting
                ? t('modals.epicAuth.actions.connecting')
                : t('modals.epicAuth.actions.continue')}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};
