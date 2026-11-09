import { noAutofill } from '@utils/autofill';
import React from 'react';
import { Modal } from '@components/ui/Modal';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import FormField from '@components/ui/FormField';
import { ProgressBar } from '@components/ui/ProgressBar';
import { LoginSteps } from './LoginSteps';
import { LoginAttemptStatus } from './LoginAttemptStatus';
import { cancelAuthModalLogin } from './authModalCancel';
import { type EpicAuthState, type EpicAuthActions } from '@hooks/useEpicMappingAuth';
import { describeEpicMappingRun } from '@hooks/useEpicMappingRun';
import { useTranslation } from 'react-i18next';
import { getIntegrationReasonKey } from '../../../types';

interface EpicAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: EpicAuthState;
  actions: EpicAuthActions;
  onCancelLogin?: () => void;
  /**
   * 'cancel' (default, the manager's own mapping-login flow): any close - X, backdrop, Escape, or
   * the footer button - cancels the in-flight login. 'keep-pending' (the persistent-container
   * flow): X, Escape and the footer button cancel the daemon login; a backdrop click does nothing.
   * 'background' (the mapping sign-in): like 'cancel' until the server accepts the code; after that the account is
   * saved and the games are mapped in the background, so every close leaves the mapping running.
   */
  dismissBehavior?: 'cancel' | 'keep-pending' | 'background';
  /** Unix time in ms this login attempt expires at, from whichever timer governs THIS mount - only the
   *  persistent-container store has one. `null`/unset renders no countdown, which is the honest
   *  answer for the other Epic mounts: nothing client-side is counting there. */
  loginDeadline?: number | null;
  /** Persistent-container flow only: set when the attempt ended before Epic asked for anything.
   *  The prompt keeps its error on screen and offers a new attempt in place of Continue. */
  onRetry?: () => void;
}

export const EpicAuthModal: React.FC<EpicAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  onCancelLogin,
  dismissBehavior = 'cancel',
  loginDeadline = null,
  onRetry
}) => {
  const { t } = useTranslation();
  const isKeepPending = dismissBehavior === 'keep-pending';
  const accepted = dismissBehavior === 'background' && state.codeAccepted === true;
  const { loading, needsAuthorizationCode, authorizationUrl, authorizationCode, error } = state;
  const mapping = state.mapping;

  const { setAuthorizationCode, handleAuthenticate, cancelPendingRequest } = actions;

  const [isSubmitting, setIsSubmitting] = React.useState(false);

  // The authorization URL has not come back from the daemon yet.
  const isConnecting = (loading || isSubmitting) && !needsAuthorizationCode;

  const handleCloseModal = () => {
    if (accepted) {
      onClose();
      return;
    }
    if (state.canAuthenticate !== undefined) {
      handleExplicitCancel();
      return;
    }
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
      onCancelLogin: actions.cancelLogin ?? onCancelLogin,
      onClose
    });
  };

  const handleSubmit = async () => {
    if (isSubmitting || loading || state.canAuthenticate === false) return;
    setIsSubmitting(true);

    try {
      // The host closes the dialog when its task ends; a mapping sign-in keeps it open for the mapping's progress.
      await handleAuthenticate();
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleOpenAuthUrl = () => {
    if (state.canAuthenticate === false || loading || isSubmitting) return;
    if (authorizationUrl) {
      window.open(authorizationUrl, '_blank', 'noopener,noreferrer');
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
          <span className="icon-box login-modal-icon login-modal-icon--epic">
            <EpicIcon size={20} />
          </span>
          <div className="min-w-0">
            <div>{t('modals.epicAuth.title')}</div>
            {isKeepPending && (
              <p className="login-modal-notice">{t('modals.epicAuth.containerAccountNotice')}</p>
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
          {!accepted && state.canAuthenticate === false && (
            <p className="text-sm text-themed-secondary">
              {state.accessUnavailable
                ? t('errors.integration.statusUnavailable')
                : t(getIntegrationReasonKey(state.ownershipReason))}
            </p>
          )}
          <LoginSteps
            steps={[t('modals.epicAuth.steps.link'), t('modals.epicAuth.steps.code')]}
            pastFirstStep={needsAuthorizationCode}
          />

          {accepted && mapping && (
            <div className="space-y-3" role="status" aria-live="polite">
              <p className="text-sm text-themed-primary">{describeEpicMappingRun(mapping, t)}</p>
              {(mapping.phase === 'none' || mapping.phase === 'running') && (
                <ProgressBar
                  value={mapping.percent}
                  height="md"
                  label={describeEpicMappingRun(mapping, t)}
                />
              )}
            </div>
          )}

          <div className="login-states">
            {/* Rendered in every state, so the live region is already in the page when the login
              moves on and its label changes. The error rides under it: it is the same title and
              reason the notification bar gets, drawn where the person is actually looking,
              because the modal sits over the bar and a rejected code used to change nothing on
              screen at all. */}
            {!accepted && (
              <LoginAttemptStatus
                label={
                  isConnecting
                    ? t('modals.epicAuth.connectingSubtitle')
                    : needsAuthorizationCode && (loading || isSubmitting)
                      ? t('modals.epicAuth.authenticatingMessage')
                      : needsAuthorizationCode
                        ? t('common.waitingForCode')
                        : onRetry
                          ? t('common.pressRetry')
                          : ''
                }
                busy={loading || isSubmitting}
                deadline={loginDeadline}
                error={error}
                errorTitle={t('common.errors.signInFailed', {
                  platform: t('prefill.persistent.services.epic')
                })}
              />
            )}

            {/* Sign-in prompt, and the connect that follows it: the same box either way, so pressing
              Continue changes the line in the strip and moves the panel no pixels. A failed attempt
              swaps it for the error, so the prompt keeps about the same height. */}
            {!accepted && !needsAuthorizationCode && !onRetry && (
              <div>
                <h3 className="text-base font-semibold text-themed-primary">
                  {t('modals.epicAuth.signInTitle')}
                </h3>
                <p className="mt-1 text-sm text-themed-muted">
                  {t('modals.epicAuth.signInDescription')}
                </p>
              </div>
            )}

            {/* Authorization Code Input - shown after user gets the URL */}
            {!accepted && needsAuthorizationCode && (
              <>
                <h3 className="text-base font-semibold text-themed-primary">
                  {t('modals.epicAuth.enterCodeTitle')}
                </h3>
                <div className="login-task">
                  <ol className="list-decimal pl-5 text-sm text-themed-secondary">
                    <li>{t('modals.epicAuth.instructions.open')}</li>
                    <li>{t('modals.epicAuth.instructions.copy')}</li>
                    <li>{t('modals.epicAuth.instructions.paste')}</li>
                  </ol>

                  {authorizationUrl && (
                    <Button
                      variant="default"
                      onClick={handleOpenAuthUrl}
                      disabled={state.canAuthenticate === false || loading || isSubmitting}
                      className="w-full sm:w-56 min-h-[44px] sm:min-h-10"
                    >
                      {t('modals.epicAuth.openEpicLogin')}
                    </Button>
                  )}

                  {/* Code Input */}
                  <div>
                    <FormField label={t('modals.epicAuth.authorizationCodeLabel')}>
                      {(field) => (
                        <input
                          {...noAutofill}
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
                          disabled={state.canAuthenticate === false}
                          autoFocus
                        />
                      )}
                    </FormField>
                  </div>
                </div>
              </>
            )}
          </div>
        </CustomScrollbar>

        <div className="confirmation-modal__actions">
          {accepted ? (
            <>
              <Button variant="default" onClick={onClose} className="min-h-[44px] sm:min-h-10">
                {mapping?.phase === 'done' ||
                mapping?.phase === 'failed' ||
                mapping?.phase === 'cancelled'
                  ? t('common.close')
                  : t('modals.epicAuth.actions.continueInBackground')}
              </Button>
              {(mapping?.phase === 'failed' || mapping?.phase === 'cancelled') && (
                <Button
                  variant="filled"
                  color="primary"
                  onClick={actions.retryLogin}
                  className="min-h-[44px] sm:min-h-10"
                >
                  {t('common.retry')}
                </Button>
              )}
            </>
          ) : (
            <Button
              variant="default"
              onClick={isKeepPending ? handleExplicitCancel : handleCloseModal}
              className="min-h-[44px] sm:min-h-10"
            >
              {t('common.cancel')}
            </Button>
          )}
          {accepted ? null : needsAuthorizationCode ? (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={
                state.canAuthenticate === false ||
                loading ||
                isSubmitting ||
                !authorizationCode.trim()
              }
              className="min-h-[44px] sm:min-h-10"
            >
              {t('modals.epicAuth.actions.submitCode')}
            </Button>
          ) : onRetry ? (
            <Button
              variant="filled"
              color="primary"
              onClick={onRetry}
              loading={loading}
              stableWidth
              className="min-h-[44px] sm:min-h-10"
            >
              {t('common.retry')}
            </Button>
          ) : (
            <Button
              variant="filled"
              color="primary"
              onClick={handleSubmit}
              disabled={state.canAuthenticate === false || loading || isSubmitting}
              className="min-h-[44px] sm:min-h-10"
            >
              {t('modals.epicAuth.actions.continue')}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};
