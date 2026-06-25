import React from 'react';
import { Shield, ExternalLink, KeyRound } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { XboxIcon } from '@components/ui/XboxIcon';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { type SteamLoginFlowState, type SteamAuthActions } from '@hooks/useSteamAuthentication';
import { useTranslation } from 'react-i18next';

interface XboxAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: SteamLoginFlowState;
  actions: SteamAuthActions;
  onCancelLogin?: () => void;
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
  onCancelLogin
}) => {
  const { t } = useTranslation();
  const { loading, needsDeviceCode, deviceUserCode, deviceVerificationUri } = state;

  const { handleAuthenticate, cancelPendingRequest } = actions;

  const [isSubmitting, setIsSubmitting] = React.useState(false);

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

  return (
    <Modal
      opened={opened}
      onClose={handleCloseModal}
      title={
        <div className="flex items-center gap-3">
          <XboxIcon size={20} className="text-[var(--theme-xbox)]" />
          <span>{t('modals.xboxAuth.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-5">
        {/* Step Indicator */}
        <div className="flex items-center justify-center gap-2">
          <StepDot active={!needsDeviceCode} completed={needsDeviceCode} />
          <div className="w-8 h-px bg-themed-tertiary" />
          <StepDot active={needsDeviceCode} />
        </div>

        {/* Content Area */}
        <div className="min-h-[280px]">
          {/* Initial state - before StartLogin is called */}
          {!needsDeviceCode && !loading && !isSubmitting && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-6">
                <div className="w-16 h-16 rounded-2xl flex items-center justify-center mb-4 bg-[var(--theme-xbox)]">
                  <XboxIcon size={32} className="text-white" />
                </div>
                <h3 className="text-lg font-semibold text-themed-primary mb-2">
                  {t('modals.xboxAuth.signInTitle')}
                </h3>
                <p className="text-sm text-themed-secondary max-w-xs">
                  {t('modals.xboxAuth.signInDescription')}
                </p>
              </div>

              <div className="flex items-start gap-3 p-3 rounded-lg bg-themed-tertiary">
                <Shield className="w-4 h-4 mt-0.5 flex-shrink-0 text-success" />
                <p className="text-xs text-themed-muted leading-relaxed">
                  {t('modals.xboxAuth.securityNote')}
                </p>
              </div>
            </div>
          )}

          {/* Loading state - waiting for the device-code challenge from the daemon */}
          {(loading || isSubmitting) && !needsDeviceCode && (
            <div className="flex flex-col items-center text-center py-12">
              <LoadingSpinner
                inline
                size="sm"
                className="w-10 h-10 text-[var(--theme-xbox)] mb-4"
              />
              <h3 className="text-lg font-semibold text-themed-primary mb-2">
                {t('modals.xboxAuth.connectingTitle')}
              </h3>
              <p className="text-sm text-themed-muted">{t('modals.xboxAuth.connectingSubtitle')}</p>
            </div>
          )}

          {/* Device-code state - show the user code + verification URL */}
          {needsDeviceCode && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-2">
                <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-[var(--theme-xbox-subtle)]">
                  <KeyRound className="w-7 h-7 text-[var(--theme-xbox)]" />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  {t('modals.xboxAuth.enterCodeTitle')}
                </h3>
                <p className="text-sm text-themed-secondary max-w-sm">
                  {t('modals.xboxAuth.enterCodeDescription')}
                </p>
              </div>

              {/* The device user code the user types at the verification URL */}
              {deviceUserCode && (
                <div className="text-center">
                  <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                    {t('modals.xboxAuth.userCodeLabel')}
                  </label>
                  <div className="px-3 py-2.5 rounded-lg bg-themed-tertiary font-mono text-xl font-bold tracking-widest text-themed-primary select-all">
                    {deviceUserCode}
                  </div>
                </div>
              )}

              {/* Open Microsoft verification page */}
              {deviceVerificationUri && (
                <Button variant="filled" onClick={handleOpenVerificationUrl} className="w-full">
                  <ExternalLink className="w-4 h-4" />
                  {t('modals.xboxAuth.openVerification')}
                </Button>
              )}

              {/* Waiting for approval */}
              <div className="flex items-center justify-center gap-2 text-themed-muted">
                <LoadingSpinner inline size="sm" />
                <span className="text-sm">{t('modals.xboxAuth.waitingMessage')}</span>
              </div>
            </div>
          )}
        </div>

        {/* Action Buttons */}
        <div className="flex gap-3 pt-2 border-t border-themed-secondary">
          <Button variant="default" onClick={handleCloseModal} className="flex-1">
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

const StepDot: React.FC<{ active?: boolean; completed?: boolean }> = ({ active, completed }) => (
  <div
    className={`w-2.5 h-2.5 rounded-full transition-all duration-200 ${
      active ? 'bg-primary' : completed ? 'bg-success' : 'bg-themed-hover'
    }`}
  />
);
