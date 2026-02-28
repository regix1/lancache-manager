import React from 'react';
import { Loader2, Shield, ExternalLink, KeyRound } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import { type SteamLoginFlowState, type SteamAuthActions } from '@hooks/useSteamAuthentication';
import { useTranslation } from 'react-i18next';

interface EpicAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: SteamLoginFlowState;
  actions: SteamAuthActions;
  onCancelLogin?: () => void;
}

export const EpicAuthModal: React.FC<EpicAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  onCancelLogin
}) => {
  const { t } = useTranslation();
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
      onClose={handleCloseModal}
      title={
        <div className="flex items-center gap-3">
          <EpicIcon size={20} className="text-[var(--theme-epic)]" />
          <span>Epic Games Authentication</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-5">
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
                  Sign in with Epic Games
                </h3>
                <p className="text-sm text-themed-secondary max-w-xs">
                  You&apos;ll be redirected to Epic Games to authorize access to your game library.
                  After signing in, you&apos;ll receive a code to paste back here.
                </p>
              </div>

              <div className="flex items-start gap-3 p-3 rounded-lg bg-themed-tertiary">
                <Shield className="w-4 h-4 mt-0.5 flex-shrink-0 text-success" />
                <p className="text-xs text-themed-muted leading-relaxed">
                  Authentication uses Epic&apos;s official OAuth flow. Your credentials are entered
                  directly on Epic&apos;s website and are never shared with this application.
                </p>
              </div>
            </div>
          )}

          {/* Loading state - waiting for challenge from daemon */}
          {(loading || isSubmitting) && !needsAuthorizationCode && (
            <div className="flex flex-col items-center text-center py-12">
              <Loader2 className="w-10 h-10 animate-spin text-[var(--theme-epic)] mb-4" />
              <h3 className="text-lg font-semibold text-themed-primary mb-2">
                Connecting to Epic Games...
              </h3>
              <p className="text-sm text-themed-muted">Starting the authorization process</p>
            </div>
          )}

          {/* Authorization Code Input - shown after user gets the URL */}
          {needsAuthorizationCode && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-2">
                <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-[color-mix(in_srgb,var(--theme-epic)_15%,transparent)]">
                  <KeyRound className="w-7 h-7 text-[var(--theme-epic)]" />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  Enter Authorization Code
                </h3>
                <p className="text-sm text-themed-secondary max-w-sm">
                  Click the button below to open Epic Games login, then paste the authorization code
                  you receive.
                </p>
              </div>

              {/* Open Epic Login Button */}
              {authorizationUrl && (
                <Button variant="filled" onClick={handleOpenAuthUrl} className="w-full">
                  <ExternalLink className="w-4 h-4" />
                  Open Epic Games Login
                </Button>
              )}

              {/* Code Input */}
              <div>
                <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                  Authorization Code
                </label>
                <input
                  type="text"
                  value={authorizationCode}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setAuthorizationCode(e.target.value)
                  }
                  onKeyPress={(e: React.KeyboardEvent<HTMLInputElement>) =>
                    e.key === 'Enter' && handleSubmit()
                  }
                  placeholder="Paste your authorization code here"
                  className="w-full px-3 py-2.5 themed-input font-mono text-sm"
                  disabled={loading}
                  autoFocus
                />
              </div>

              {/* Submitting state */}
              {(loading || isSubmitting) && (
                <div className="flex items-center justify-center gap-2 text-themed-muted">
                  <Loader2 className="w-4 h-4 animate-spin" />
                  <span className="text-sm">Authenticating with Epic Games...</span>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Action Buttons */}
        <div className="flex gap-3 pt-2 border-t border-themed-secondary">
          <Button variant="default" onClick={handleCloseModal} className="flex-1">
            {t('common.cancel')}
          </Button>
          {needsAuthorizationCode ? (
            <Button
              variant="filled"
              onClick={handleSubmit}
              disabled={loading || isSubmitting || !authorizationCode.trim()}
              className="flex-1"
            >
              {(loading || isSubmitting) && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
              {loading || isSubmitting ? 'Authenticating...' : 'Submit Code'}
            </Button>
          ) : (
            <Button
              variant="filled"
              onClick={handleSubmit}
              disabled={loading || isSubmitting}
              className="flex-1"
            >
              {(loading || isSubmitting) && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
              {loading || isSubmitting ? 'Connecting...' : 'Continue'}
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
