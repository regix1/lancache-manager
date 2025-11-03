import React from 'react';
import { Key, Lock, Loader2 } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { SteamLoginFlowState, SteamAuthActions } from '@hooks/useSteamAuthentication';

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
    setUseManualCode,
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
    // Abort the pending authentication request
    cancelPendingRequest();
    // Switch to manual code entry
    actions.setUseManualCode(true);
    actions.resetAuthForm();
    // Reset only specific fields for manual code entry
    setUsername(username);
    setPassword(password);
    setUseManualCode(true);
  };

  return (
    <Modal
      opened={opened}
      onClose={handleCloseModal}
      title={
        <div className="flex items-center space-x-3">
          <Key className="w-6 h-6 text-themed-warning" />
          <span>Steam Account Login</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-4">
        <p className="text-themed-secondary text-sm">
          Login with your Steam account to access playtest and restricted games. Your credentials are never stored - only refresh tokens are saved.
        </p>

        {/* Waiting for Mobile Confirmation */}
        {waitingForMobileConfirmation && (
          <Alert color="blue" icon={<Loader2 className="w-5 h-5 animate-spin" />}>
            <div className="space-y-3">
              <p className="font-medium">Check your Steam Mobile App</p>
              <p className="text-sm">
                A confirmation request has been sent to your Steam Mobile App. Please open the app and tap "Approve" to complete the login.
              </p>
              <div className="pt-2">
                <Button
                  size="sm"
                  variant="default"
                  onClick={handleSwitchToManualCode}
                >
                  Use 2FA Code Instead
                </Button>
              </div>
            </div>
          </Alert>
        )}

        {/* Initial Login Form */}
        {!needsTwoFactor && !needsEmailCode && !waitingForMobileConfirmation && (
          <>
            <div>
              <label className="block text-sm font-medium text-themed-secondary mb-2">
                Steam Username
              </label>
              <input
                type="text"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                placeholder="username"
                className="w-full px-3 py-2 themed-input"
                disabled={loading}
                autoComplete="username"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-themed-secondary mb-2">
                Password
              </label>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
                placeholder="••••••••"
                className="w-full px-3 py-2 themed-input"
                disabled={loading}
                autoComplete="current-password"
              />
            </div>
          </>
        )}

        {/* Email Code Input */}
        {needsEmailCode && (
          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-2">
              Email Verification Code
            </label>
            <input
              type="text"
              value={emailCode}
              onChange={(e) => setEmailCode(e.target.value)}
              onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
              placeholder="12345"
              className="w-full px-3 py-2 themed-input"
              disabled={loading}
              autoFocus
            />
            <p className="text-xs text-themed-muted mt-2">
              Check your email for a verification code from Steam Guard
            </p>
          </div>
        )}

        {/* Two-Factor Code Input */}
        {needsTwoFactor && (
          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-2">
              {useManualCode ? 'Two-Factor Authentication Code' : 'Two-Factor Authentication'}
            </label>
            <input
              type="text"
              value={twoFactorCode}
              onChange={(e) => setTwoFactorCode(e.target.value)}
              onKeyPress={(e) => e.key === 'Enter' && handleSubmit()}
              placeholder={useManualCode ? '12345' : '12345 (optional if confirming on mobile)'}
              className="w-full px-3 py-2 themed-input"
              disabled={loading}
              autoFocus
            />
            <p className="text-xs text-themed-muted mt-2">
              {useManualCode ? (
                <>
                  Enter the 2FA code from your authenticator app. You can switch back to mobile confirmation by closing and reopening the login dialog.
                </>
              ) : (
                <>
                  <strong>Option 1:</strong> Check your Steam Mobile App and tap "Approve" to confirm this login<br />
                  <strong>Option 2:</strong> Enter the 2FA code from your authenticator app above
                </>
              )}
            </p>
          </div>
        )}

        {/* Information Alert */}
        <Alert color="blue">
          <div>
            <p className="font-medium mb-2">Important:</p>
            <ul className="list-disc list-inside text-sm space-y-1 ml-2">
              <li>Your password is never saved - only refresh tokens</li>
              <li>2FA or email verification may be required</li>
              <li>Depot mapping will be configured after login</li>
            </ul>
          </div>
        </Alert>

        {/* Action Buttons */}
        <div className="flex justify-end space-x-3 pt-2 border-t border-themed-secondary">
          <Button variant="default" onClick={handleCloseModal} disabled={loading}>
            Cancel
          </Button>
          {!waitingForMobileConfirmation && (
            <Button
              variant="filled"
              color="green"
              leftSection={<Lock className="w-4 h-4" />}
              onClick={handleSubmit}
              loading={loading}
              disabled={
                (!needsTwoFactor && !needsEmailCode && (!username.trim() || !password.trim())) ||
                (useManualCode && !twoFactorCode.trim())
              }
            >
              {needsEmailCode ? 'Verify Email Code' : (needsTwoFactor || useManualCode) ? 'Confirm Login' : 'Login'}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
};
