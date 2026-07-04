import React from 'react';
import { Shield, ExternalLink, KeyRound } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import LoadingSpinner from '@components/common/LoadingSpinner';
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
}

export const EpicAuthModal: React.FC<EpicAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  onCancelLogin,
  dismissBehavior = 'cancel'
}) => {
  const { t } = useTranslation();
  const isKeepPending = dismissBehavior === 'keep-pending';
  const { loading, needsAuthorizationCode, authorizationUrl, authorizationCode } = state;

  const { setAuthorizationCode, handleAuthenticate, cancelPendingRequest } = actions;

  const [isSubmitting, setIsSubmitting] = React.useState(false);

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

  // keep-pending (persistent-container flow): X/backdrop/Escape only hide the modal - the daemon
  // login keeps running and stays resumable. Only the explicit Cancel button below actually cancels.
  const handleSoftClose = () => {
    onClose();
  };

  const handleExplicitCancel = () => {
    cancelPendingRequest();
    actions.resetAuthForm();
    onCancelLogin?.();
    onClose();
  };

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
      <div className="space-y-5">
        {isKeepPending && (
          <p className="text-xs text-themed-muted text-center">
            {t('modals.epicAuth.containerAccountNotice')}
          </p>
        )}

        {/* Step Indicator */}
        <div className="flex items-center justify-center gap-2">
          <StepDot active={!needsAuthorizationCode} completed={needsAuthorizationCode} />
          <div className="w-8 h-px bg-themed-tertiary" />
          <StepDot active={needsAuthorizationCode} />
        </div>

        {/* Content Area */}
        <div className="min-h-[280px]">
          {/* Initial state - before StartLogin is called */}
          {!needsAuthorizationCode && !loading && !isSubmitting && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-6">
                <div className="w-16 h-16 rounded-2xl flex items-center justify-center mb-4 bg-[var(--theme-epic)]">
                  <EpicIcon size={32} className="text-white" />
                </div>
                <h3 className="text-lg font-semibold text-themed-primary mb-2">
                  {t('modals.epicAuth.signInTitle')}
                </h3>
                <p className="text-sm text-themed-secondary max-w-xs">
                  {t('modals.epicAuth.signInDescription')}
                </p>
              </div>

              <div className="flex items-start gap-3 p-3 rounded-lg bg-themed-tertiary">
                <Shield className="w-4 h-4 mt-0.5 flex-shrink-0 text-success" />
                <p className="text-xs text-themed-muted leading-relaxed">
                  {t('modals.epicAuth.securityNote')}
                </p>
              </div>
            </div>
          )}

          {/* Loading state - waiting for challenge from daemon */}
          {(loading || isSubmitting) && !needsAuthorizationCode && (
            <div className="flex flex-col items-center text-center py-12">
              <LoadingSpinner
                inline
                size="sm"
                className="w-10 h-10 text-[var(--theme-epic)] mb-4"
              />
              <h3 className="text-lg font-semibold text-themed-primary mb-2">
                {t('modals.epicAuth.connectingTitle')}
              </h3>
              <p className="text-sm text-themed-muted">{t('modals.epicAuth.connectingSubtitle')}</p>
            </div>
          )}

          {/* Authorization Code Input - shown after user gets the URL */}
          {needsAuthorizationCode && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-2">
                <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-[var(--theme-epic-subtle)]">
                  <KeyRound className="w-7 h-7 text-[var(--theme-epic)]" />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  {t('modals.epicAuth.enterCodeTitle')}
                </h3>
                <p className="text-sm text-themed-secondary max-w-sm">
                  {t('modals.epicAuth.enterCodeDescription')}
                </p>
              </div>

              {/* Open Epic Login Button */}
              {authorizationUrl && (
                <Button variant="filled" onClick={handleOpenAuthUrl} className="w-full">
                  <ExternalLink className="w-4 h-4" />
                  {t('modals.epicAuth.openEpicLogin')}
                </Button>
              )}

              {/* Code Input */}
              <div>
                <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                  {t('modals.epicAuth.authorizationCodeLabel')}
                </label>
                <input
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
              </div>

              {/* Submitting state */}
              {(loading || isSubmitting) && (
                <div className="flex items-center justify-center gap-2 text-themed-muted">
                  <LoadingSpinner inline size="sm" />
                  <span className="text-sm">{t('modals.epicAuth.authenticatingMessage')}</span>
                </div>
              )}
            </div>
          )}
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

const StepDot: React.FC<{ active?: boolean; completed?: boolean }> = ({ active, completed }) => (
  <div
    className={`w-2.5 h-2.5 rounded-full transition duration-200 ${
      active ? 'bg-primary' : completed ? 'bg-success' : 'bg-themed-hover'
    }`}
  />
);
