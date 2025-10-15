import React, { useState } from 'react';
import { Shield, Lock, User, Key, Loader, CheckCircle, ChevronRight, Users } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import ApiService from '@services/api.service';

interface SteamPicsAuthStepProps {
  onComplete: (usingSteamAuth: boolean) => void;
}

type AuthMode = 'anonymous' | 'account';

export const SteamPicsAuthStep: React.FC<SteamPicsAuthStepProps> = ({ onComplete }) => {
  const [selectedMode, setSelectedMode] = useState<AuthMode>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');
  const [authError, setAuthError] = useState('');

  // Cleanup: abort any pending requests when component unmounts
  React.useEffect(() => {
    return () => {
      if (abortController) {
        abortController.abort();
      }
    };
  }, [abortController]);

  const handleModeSelect = (mode: AuthMode) => {
    setSelectedMode(mode);
    if (mode === 'account') {
      setShowAuthModal(true);
    }
  };

  const handleContinueAnonymous = () => {
    onComplete(false); // false = not using Steam auth
  };

  const handleAuthenticate = async () => {
    if (!username.trim() || !password.trim()) {
      setAuthError('Please enter both username and password');
      return;
    }

    if (needsEmailCode && !emailCode.trim()) {
      setAuthError('Please enter your email verification code');
      return;
    }

    // If user chose manual code entry, require the code
    if (useManualCode && !twoFactorCode.trim()) {
      setAuthError('Please enter your 2FA code');
      return;
    }

    setLoading(true);
    setAuthError('');

    // Create abort controller for this request
    const controller = new AbortController();
    setAbortController(controller);

    // Show mobile confirmation waiting state for initial login (not when entering manual code)
    if (!needsTwoFactor && !needsEmailCode && !useManualCode) {
      setWaitingForMobileConfirmation(true);
    }

    try {
      const response = await fetch('/api/management/steam-auth/login', {
        method: 'POST',
        headers: ApiService.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({
          username,
          password,
          twoFactorCode: (needsTwoFactor || useManualCode) ? twoFactorCode : undefined,
          emailCode: needsEmailCode ? emailCode : undefined,
          // Allow mobile confirmation unless user explicitly chose manual code entry
          allowMobileConfirmation: !useManualCode,
          // Never auto-start PICS rebuild - user will start it in step 3
          autoStartPicsRebuild: false
        }),
        signal: controller.signal
      });

      const result = await response.json();

      if (response.ok) {
        if (result.requiresTwoFactor) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          setAuthError('');
          return; // Stay in modal, show 2FA input for manual code OR mobile confirmation
        }

        if (result.requiresEmailCode) {
          setWaitingForMobileConfirmation(false);
          setNeedsEmailCode(true);
          setAuthError('');
          return; // Stay in modal, wait for email code
        }

        if (result.success) {
          setShowAuthModal(false);
          resetAuthForm();
          // Continue to next step - true = using Steam auth
          onComplete(true);
        } else {
          setWaitingForMobileConfirmation(false);
          setAuthError(result.message || 'Authentication failed');
        }
      } else {
        setWaitingForMobileConfirmation(false);
        setAuthError(result.message || 'Authentication failed');
      }
    } catch (err: any) {
      // Don't show error if request was aborted intentionally
      if (err.name !== 'AbortError') {
        setWaitingForMobileConfirmation(false);
        setAuthError(err.message || 'Authentication failed');
      }
    } finally {
      setLoading(false);
      setAbortController(null);
    }
  };

  const resetAuthForm = () => {
    // Abort any pending request
    if (abortController) {
      abortController.abort();
      setAbortController(null);
    }
    setUsername('');
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setAuthError('');
    setLoading(false);
  };

  const handleCloseModal = () => {
    if (!loading) {
      setShowAuthModal(false);
      resetAuthForm();
      // Reset to anonymous if they cancel
      setSelectedMode('anonymous');
    }
  };

  return (
    <>
      <div className="space-y-6">
        <div className="text-center">
          <div
            className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
            style={{ backgroundColor: 'var(--theme-primary)/10' }}
          >
            <Shield size={32} style={{ color: 'var(--theme-primary)' }} />
          </div>
          <h2 className="text-2xl font-bold text-themed-primary mb-2">
            Steam PICS Authentication
          </h2>
          <p className="text-themed-secondary">
            Choose how to authenticate with Steam for depot mapping data
          </p>
        </div>

        <div
          className="p-4 rounded-lg"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderColor: 'var(--theme-info)',
            color: 'var(--theme-info-text)'
          }}
        >
          <p className="text-sm">
            <strong>What is depot mapping?</strong><br/>
            Depot mapping links cache files to games. Anonymous mode provides access to public games only.
            Logging in with your Steam account enables access to playtest and restricted games.
          </p>
        </div>

        {/* Mode Selection Cards */}
        <div className="space-y-3">
          {/* Anonymous Mode */}
          <button
            onClick={() => setSelectedMode('anonymous')}
            className="w-full p-4 rounded-lg border-2 text-left transition-all"
            style={{
              borderColor: selectedMode === 'anonymous' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
              backgroundColor: selectedMode === 'anonymous' ? 'var(--theme-primary)/10' : 'transparent'
            }}
          >
            <div className="flex items-start gap-3">
              <div
                className="p-2 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <Users className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
              </div>
              <div className="flex-1">
                <h3 className="font-semibold text-themed-primary mb-1">
                  Anonymous (Public Data Only)
                </h3>
                <p className="text-sm text-themed-secondary">
                  Access depot mappings for public games only. No Steam login required.
                </p>
              </div>
              {selectedMode === 'anonymous' && (
                <CheckCircle className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>

          {/* Account Login Mode */}
          <button
            onClick={() => handleModeSelect('account')}
            className="w-full p-4 rounded-lg border-2 text-left transition-all"
            style={{
              borderColor: selectedMode === 'account' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
              backgroundColor: selectedMode === 'account' ? 'var(--theme-primary)/10' : 'transparent'
            }}
          >
            <div className="flex items-start gap-3">
              <div
                className="p-2 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <User className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
              </div>
              <div className="flex-1">
                <h3 className="font-semibold text-themed-primary mb-1">
                  Account Login (All Games)
                </h3>
                <p className="text-sm text-themed-secondary">
                  Login with Steam account to access playtest and restricted games. Depot mapping will be configured in the next step.
                </p>
              </div>
              {selectedMode === 'account' && (
                <CheckCircle className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>
        </div>

        {/* Continue Button */}
        {selectedMode === 'anonymous' && (
          <Button
            variant="filled"
            color="blue"
            leftSection={<ChevronRight className="w-4 h-4" />}
            onClick={handleContinueAnonymous}
            fullWidth
          >
            Continue with Anonymous Mode
          </Button>
        )}
      </div>

      {/* Authentication Modal */}
      <Modal
        opened={showAuthModal}
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
            <Alert color="blue">
              <div className="space-y-3">
                <div className="flex items-center gap-2">
                  <Loader className="w-5 h-5 animate-spin" />
                  <p className="font-medium">Check your Steam Mobile App</p>
                </div>
                <p className="text-sm">
                  A confirmation request has been sent to your Steam Mobile App. Please open the app and tap "Yes, it's me" to complete the login.
                </p>
                <div className="pt-2">
                  <Button
                    size="sm"
                    variant="default"
                    onClick={() => {
                      // Abort the pending authentication request
                      if (abortController) {
                        abortController.abort();
                      }
                      // Switch to manual code entry
                      setWaitingForMobileConfirmation(false);
                      setUseManualCode(true);
                      setNeedsTwoFactor(true);
                      setLoading(false);
                      setAuthError('');
                    }}
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
                  onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
                  placeholder="••••••••"
                  className="w-full px-3 py-2 themed-input"
                  disabled={loading}
                  autoComplete="current-password"
                />
              </div>
            </>
          )}

          {needsEmailCode && (
            <div>
              <label className="block text-sm font-medium text-themed-secondary mb-2">
                Email Verification Code
              </label>
              <input
                type="text"
                value={emailCode}
                onChange={(e) => setEmailCode(e.target.value)}
                onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
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

          {needsTwoFactor && (
            <div>
              <label className="block text-sm font-medium text-themed-secondary mb-2">
                {useManualCode ? 'Two-Factor Authentication Code' : 'Two-Factor Authentication'}
              </label>
              <input
                type="text"
                value={twoFactorCode}
                onChange={(e) => setTwoFactorCode(e.target.value)}
                onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
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
                    <strong>Option 1:</strong> Check your Steam Mobile App and tap "Yes, it's me" to confirm this login<br />
                    <strong>Option 2:</strong> Enter the 2FA code from your authenticator app above
                  </>
                )}
              </p>
            </div>
          )}

          {authError && <Alert color="red">{authError}</Alert>}

          <Alert color="blue">
            <div>
              <p className="font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>Your password is never saved - only refresh tokens</li>
                <li>2FA or email verification may be required</li>
                <li>Depot mapping will be configured in the next step</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2 border-t border-themed-secondary">
            <Button variant="default" onClick={handleCloseModal} disabled={loading}>
              Cancel
            </Button>
            {!waitingForMobileConfirmation && (
              <Button
                variant="filled"
                color="green"
                leftSection={<Lock className="w-4 h-4" />}
                onClick={handleAuthenticate}
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
    </>
  );
};
