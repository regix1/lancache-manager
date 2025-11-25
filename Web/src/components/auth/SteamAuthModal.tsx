import React from 'react';
import { Key, Lock, Loader2, Shield, Mail, Smartphone } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { type SteamLoginFlowState, type SteamAuthActions } from '@hooks/useSteamAuthentication';

interface SteamAuthModalProps {
  opened: boolean;
  onClose: () => void;
  state: SteamLoginFlowState;
  actions: SteamAuthActions;
}

export const SteamAuthModal: React.FC<SteamAuthModalProps> = ({
  opened,
  onClose,
  state,
  actions
}) => {
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

  const handleCloseModal = () => {
    if (!loading) {
      onClose();
    }
  };

  const handleSubmit = async () => {
    const success = await handleAuthenticate();
    if (success) {
      onClose();
    }
  };

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

  return (
    <Modal
      opened={opened}
      onClose={handleCloseModal}
      title={
        <div className="flex items-center gap-3">
          <Key className="w-5 h-5" style={{ color: 'var(--theme-steam)' }} />
          <span>Steam Account Login</span>
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
                <div
                  className="w-16 h-16 rounded-full flex items-center justify-center mb-4"
                  style={{ backgroundColor: 'var(--theme-info-bg)' }}
                >
                  <Smartphone className="w-8 h-8" style={{ color: 'var(--theme-info)' }} />
                </div>
                <h3 className="text-lg font-semibold text-themed-primary mb-2">
                  Waiting for Confirmation
                </h3>
                <p className="text-sm text-themed-secondary max-w-xs">
                  Open your Steam Mobile App and tap <strong>Approve</strong> to complete login.
                </p>
                <div className="flex items-center gap-2 mt-4 text-themed-muted">
                  <Loader2 className="w-4 h-4 animate-spin" />
                  <span className="text-sm">Waiting for response...</span>
                </div>
                <p className="text-xs text-themed-muted mt-2 max-w-xs">
                  Steam sessions expire after ~2 minutes. If it times out, you'll be prompted to enter a 2FA code instead.
                </p>
              </div>

              <div className="border-t border-themed-secondary pt-4">
                <button
                  onClick={handleSwitchToManualCode}
                  className="w-full text-center text-sm text-themed-accent hover:underline"
                >
                  Enter 2FA code manually instead
                </button>
              </div>
            </div>
          )}

          {/* Credentials Form */}
          {!needsTwoFactor && !needsEmailCode && !waitingForMobileConfirmation && (
            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                  Username
                </label>
                <input
                  type="text"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  placeholder="Steam username"
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={loading}
                  autoComplete="username"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-themed-secondary mb-1.5">
                  Password
                </label>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                  placeholder="Password"
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={loading}
                  autoComplete="current-password"
                />
              </div>

              {/* Security Info */}
              <div
                className="flex items-start gap-3 p-3 rounded-lg mt-4"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <Shield className="w-4 h-4 mt-0.5 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
                <p className="text-xs text-themed-muted leading-relaxed">
                  Your password is used once for authentication and never stored.
                  Only secure refresh tokens are saved locally.
                </p>
              </div>
            </div>
          )}

          {/* Email Verification */}
          {needsEmailCode && (
            <div className="space-y-4">
              <div className="flex flex-col items-center text-center py-4">
                <div
                  className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
                  style={{ backgroundColor: 'var(--theme-info-bg)' }}
                >
                  <Mail className="w-7 h-7" style={{ color: 'var(--theme-info)' }} />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  Email Verification
                </h3>
                <p className="text-sm text-themed-secondary">
                  Enter the code sent to your email
                </p>
              </div>

              <div>
                <input
                  type="text"
                  value={emailCode}
                  onChange={(e) => setEmailCode(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                  placeholder="XXXXX"
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
                <div
                  className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
                  style={{ backgroundColor: 'var(--theme-warning-bg)' }}
                >
                  <Lock className="w-7 h-7" style={{ color: 'var(--theme-warning)' }} />
                </div>
                <h3 className="text-base font-semibold text-themed-primary mb-1">
                  Two-Factor Authentication
                </h3>
                <p className="text-sm text-themed-secondary">
                  {useManualCode
                    ? 'Enter your authenticator code'
                    : 'Enter code or approve on mobile app'}
                </p>
              </div>

              <div>
                <input
                  type="text"
                  value={twoFactorCode}
                  onChange={(e) => setTwoFactorCode(e.target.value.toUpperCase())}
                  onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                  placeholder="XXXXX"
                  className="w-full px-3 py-3 themed-input text-center text-xl tracking-[0.5em] font-mono uppercase"
                  disabled={loading}
                  autoFocus
                  maxLength={5}
                />
              </div>

              {!useManualCode && (
                <p className="text-xs text-themed-muted text-center">
                  Leave empty and click confirm to use mobile app approval
                </p>
              )}
            </div>
          )}
        </div>

        {/* Action Buttons */}
        <div className="flex gap-3 pt-2 border-t border-themed-secondary">
          <Button
            variant="default"
            onClick={handleCloseModal}
            disabled={loading}
            className="flex-1"
          >
            Cancel
          </Button>
          {!waitingForMobileConfirmation && (
            <Button
              variant="filled"
              color="green"
              onClick={handleSubmit}
              disabled={
                loading ||
                (!needsTwoFactor && !needsEmailCode && (!username.trim() || !password.trim())) ||
                (useManualCode && !twoFactorCode.trim())
              }
              className="flex-1"
            >
              {loading && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
              {loading
                ? 'Authenticating...'
                : needsEmailCode
                  ? 'Verify'
                  : needsTwoFactor
                    ? 'Confirm'
                    : 'Login'}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};

// Step indicator dot component
const StepDot: React.FC<{ active?: boolean; completed?: boolean }> = ({ active, completed }) => (
  <div
    className="w-2.5 h-2.5 rounded-full transition-all duration-200"
    style={{
      backgroundColor: active
        ? 'var(--theme-primary)'
        : completed
          ? 'var(--theme-success)'
          : 'var(--theme-bg-hover)'
    }}
  />
);
