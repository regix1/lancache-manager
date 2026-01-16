import React, { useEffect } from 'react';
import { Key, Lock, Loader2, Shield, Mail, Smartphone } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { type SteamLoginFlowState, type SteamAuthActions } from '@hooks/useSteamAuthentication';
import { useSignalR } from '@contexts/SignalRContext';
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
}

export const SteamAuthModal: React.FC<SteamAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  isPrefillMode = false,
  onCancelLogin
}) => {
  const { t } = useTranslation();
  const { on, off } = useSignalR();
  const {
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode
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

  // Listen for SteamAutoLogout event - if session is replaced, close the modal
  useEffect(() => {
    if (!opened) return;

    const handleAutoLogout = () => {
      console.log('[SteamAuthModal] Received auto-logout event, closing modal');
      cancelPendingRequest();
      actions.resetAuthForm();
      onClose();
    };

    on('SteamAutoLogout', handleAutoLogout);
    return () => {
      off('SteamAutoLogout', handleAutoLogout);
    };
  }, [opened, on, off, cancelPendingRequest, actions, onClose]);

  const handleCloseModal = () => {
    // Allow closing when waiting for mobile confirmation (user should be able to cancel)
    if (waitingForMobileConfirmation) {
      cancelPendingRequest();
      actions.resetAuthForm();
      if (isPrefillMode) {
        onCancelLogin?.(); // End the session in prefill mode
      }
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

  // For prefill mode: cancel and close (daemon may be stuck, needs session restart)
  const handleCancelDeviceConfirmation = () => {
    cancelPendingRequest();
    actions.resetAuthForm();
    onCancelLogin?.(); // This should end the session
    onClose();
  };

  // Determine current step for visual indicator
  const getCurrentStep = () => {
    if (waitingForMobileConfirmation) return 'mobile';
    if (needsEmailCode) return 'email';
    if (needsTwoFactor) return '2fa';
    return 'credentials';
  };

  const currentStep = getCurrentStep();

  return (
    <Modal
      opened={opened}
      onClose={handleCloseModal}
      title={
        <div className="flex items-center gap-3">
          <Key className="w-5 h-5 text-steam" />
          <span>{t('modals.steamAuth.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-5">
        {/* Step Indicator */}
        <div className="flex items-center justify-center gap-2">
          <StepDot active={currentStep === 'credentials'} completed={currentStep !== 'credentials'} />
          <div className="w-8 h-px bg-themed-tertiary" />
          <StepDot active={currentStep === '2fa' || currentStep === 'email' || currentStep === 'mobile'} />
        </div>

        {/* Content Area */}
        <div className="min-h-[280px]">
          {/* Mobile Confirmation State */}
          {waitingForMobileConfirmation && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-6">
                <div className="w-16 h-16 rounded-full flex items-center justify-center mb-4 bg-info">
                  <Smartphone className="w-8 h-8 text-info" />
                </div>
                <h3 className="text-lg font-semibold text-themed-primary mb-2">
                  {t('modals.steamAuth.mobileConfirmation.title')}
                </h3>
                <p className="text-sm text-themed-secondary max-w-xs">
                  {t('modals.steamAuth.mobileConfirmation.description')}
                </p>
                <div className="flex items-center gap-2 mt-4 text-themed-muted">
                  <Loader2 className="w-4 h-4 animate-spin" />
                  <span className="text-sm">{t('modals.steamAuth.mobileConfirmation.waiting')}</span>
                </div>
                <p className="text-xs text-themed-muted mt-2 max-w-xs">
                  {isPrefillMode
                    ? t('modals.steamAuth.mobileConfirmation.timeoutPrefill')
                    : t('modals.steamAuth.mobileConfirmation.timeoutNormal')}
                </p>
              </div>

              <div className="border-t border-themed-secondary pt-4">
                {isPrefillMode ? (
                  <button
                    onClick={handleCancelDeviceConfirmation}
                    className="w-full text-center text-sm text-themed-accent hover:underline"
                  >
                    {t('modals.steamAuth.actions.cancelAndTryLater')}
                  </button>
                ) : (
                  <button
                    onClick={handleSwitchToManualCode}
                    className="w-full text-center text-sm text-themed-accent hover:underline"
                  >
                    {t('modals.steamAuth.actions.enterCodeManually')}
                  </button>
                )}
              </div>
            </div>
          )}

          {/* Credentials Form */}
          {!needsTwoFactor && !needsEmailCode && !waitingForMobileConfirmation && (
            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                  {t('modals.steamAuth.labels.username')}
                </label>
                <input
                  type="text"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  placeholder={t('modals.steamAuth.placeholders.username')}
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={loading}
                  autoComplete="username"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                  {t('modals.steamAuth.labels.password')}
                </label>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                  placeholder={t('modals.steamAuth.placeholders.password')}
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={loading}
                  autoComplete="current-password"
                />
              </div>

              {/* Security Info */}
              <div className="flex items-start gap-3 p-3 rounded-lg mt-4 bg-themed-tertiary">
                <Shield className="w-4 h-4 mt-0.5 flex-shrink-0 text-success" />
                <p className="text-xs text-themed-muted leading-relaxed">
                  {t('modals.steamAuth.security.description')}
                </p>
              </div>
            </div>
          )}

          {/* Email Verification */}
          {needsEmailCode && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-4">
                <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-info">
                  <Mail className="w-7 h-7 text-info" />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  {t('modals.steamAuth.emailVerification.title')}
                </h3>
                <p className="text-sm text-themed-secondary">
                  {t('modals.steamAuth.emailVerification.description')}
                </p>
              </div>

              <div>
                <input
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
              </div>
            </div>
          )}

          {/* Two-Factor Authentication */}
          {needsTwoFactor && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-4">
                <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-warning">
                  <Lock className="w-7 h-7 text-warning" />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  {t('modals.steamAuth.twoFactor.title')}
                </h3>
                <p className="text-sm text-themed-secondary">
                  {useManualCode
                    ? t('modals.steamAuth.twoFactor.descriptionManual')
                    : t('modals.steamAuth.twoFactor.descriptionAuto')}
                </p>
              </div>

              <div>
                <input
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
              </div>

              {!useManualCode && (
                <p className="text-xs text-themed-muted text-center">
                  {t('modals.steamAuth.twoFactor.leaveEmptyHint')}
                </p>
              )}
            </div>
          )}
        </div>

        {/* Action Buttons - hide when waiting for mobile confirmation in prefill mode (has its own cancel) */}
        {!(isPrefillMode && waitingForMobileConfirmation) && (
        <div className="flex gap-3 pt-2 border-t border-themed-secondary">
          <Button
            variant="default"
            onClick={handleCloseModal}
            disabled={(loading || isSubmitting) && !waitingForMobileConfirmation}
            className="flex-1"
          >
            {t('common.cancel')}
          </Button>
          {!waitingForMobileConfirmation && (
            <Button
              variant="filled"
              color="green"
              onClick={handleSubmit}
              disabled={
                loading ||
                isSubmitting ||
                (!needsTwoFactor && !needsEmailCode && (!username.trim() || !password.trim())) ||
                (useManualCode && !twoFactorCode.trim())
              }
              className="flex-1"
            >
              {(loading || isSubmitting) && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
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
        )}
      </div>
    </Modal>
  );
};

// Step indicator dot component
const StepDot: React.FC<{ active?: boolean; completed?: boolean }> = ({ active, completed }) => (
  <div
    className={`w-2.5 h-2.5 rounded-full transition-all duration-200 ${
      active
        ? 'bg-primary'
        : completed
          ? 'bg-success'
          : 'bg-themed-hover'
    }`}
  />
);
