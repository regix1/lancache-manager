import React, { useState, useEffect } from 'react';
import { Key, Lock, User, Shield, Loader } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import ApiService from '@services/api.service';
import { AuthMode } from '@services/auth.service';

interface SteamLoginManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

interface SteamAuthState {
  mode: 'anonymous' | 'authenticated';
  username?: string;
  isAuthenticated: boolean;
}

const SteamLoginManager: React.FC<SteamLoginManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const [steamAuthMode, setSteamAuthMode] = useState<'anonymous' | 'authenticated'>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [abortController, setAbortController] = useState<AbortController | null>(null);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(true);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');
  const [authError, setAuthError] = useState('');

  useEffect(() => {
    loadSteamAuthState();
    // Load auto-start preference from localStorage
    const savedPref = localStorage.getItem('autoStartPics');
    if (savedPref !== null) {
      setAutoStartPics(savedPref === 'true');
    }
  }, []);

  // Cleanup: abort any pending requests when component unmounts
  useEffect(() => {
    return () => {
      if (abortController) {
        abortController.abort();
      }
    };
  }, [abortController]);

  const loadSteamAuthState = async () => {
    try {
      const response = await fetch('/api/management/steam-auth-status', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const state: SteamAuthState = await response.json();
        setSteamAuthMode(state.mode);
        // Also populate the username if authenticated
        if (state.mode === 'authenticated' && state.username) {
          setUsername(state.username);
        }
      }
    } catch (err) {
      console.error('Failed to load Steam auth state:', err);
    }
  };

  const handleAutoStartPicsChange = (enabled: boolean) => {
    setAutoStartPics(enabled);
    localStorage.setItem('autoStartPics', enabled.toString());
  };

  const handleModeChange = (newMode: string) => {
    if (newMode === 'authenticated' && steamAuthMode === 'anonymous') {
      // Show auth modal when switching to authenticated
      setShowAuthModal(true);
    } else if (newMode === 'anonymous' && steamAuthMode === 'authenticated') {
      // Switch back to anonymous
      handleSwitchToAnonymous();
    }
  };

  const handleSwitchToAnonymous = async () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setLoading(true);
    try {
      const response = await fetch('/api/management/steam-auth/logout', {
        method: 'POST',
        headers: ApiService.getHeaders({ 'Content-Type': 'application/json' })
      });

      if (response.ok) {
        setSteamAuthMode('anonymous');
        onSuccess?.('Switched to anonymous Steam mode. Depot mappings preserved.');
      } else {
        const error = await response.json();
        onError?.(error.message || 'Failed to switch to anonymous mode');
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to switch to anonymous mode');
    } finally {
      setLoading(false);
    }
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

    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
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
          // Pass auto-start preference to backend
          autoStartPicsRebuild: autoStartPics
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
          setSteamAuthMode('authenticated');
          setShowAuthModal(false);
          resetAuthForm();
          onSuccess?.(result.message || `Successfully authenticated as ${username}.`);
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
    }
  };

  const dropdownOptions = [
    { value: 'anonymous', label: 'Anonymous (Public Data Only)' },
    { value: 'authenticated', label: 'Account Login (Access All Games)' }
  ];

  return (
    <>
      <Card>
        <div className="flex items-center gap-2 mb-4">
          <Shield className="w-5 h-5 text-themed-accent flex-shrink-0" />
          <h3 className="text-lg font-semibold text-themed-primary">Steam PICS Authentication</h3>
        </div>

        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div className="flex-1">
            <p className="text-themed-secondary">
              {steamAuthMode === 'authenticated'
                ? 'Logged in with Steam account - can access playtest and restricted games'
                : 'Using anonymous mode - only public games available'}
            </p>
            <p className="text-xs text-themed-muted mt-1">
              Change login mode to access different depot mappings
            </p>
          </div>

          <div className="w-full sm:w-64">
            <EnhancedDropdown
              options={dropdownOptions}
              value={steamAuthMode}
              onChange={handleModeChange}
              disabled={loading || mockMode || authMode !== 'authenticated'}
            />
          </div>
        </div>

        {/* Auto-start PICS on login */}
        <div className="mt-4 pt-4 border-t border-themed-secondary">
        <div className="flex flex-col items-end gap-1">
          <div className="flex gap-2">
            <Button
              size="sm"
              variant={autoStartPics ? 'filled' : 'default'}
              color={autoStartPics ? 'blue' : undefined}
              onClick={() => handleAutoStartPicsChange(true)}
              disabled={loading || mockMode}
            >
              Automatic
            </Button>
            <Button
              size="sm"
              variant={!autoStartPics ? 'filled' : 'default'}
              color={!autoStartPics ? 'blue' : undefined}
              onClick={() => handleAutoStartPicsChange(false)}
              disabled={loading || mockMode}
            >
              Manual
            </Button>
          </div>
          <span className="text-xs text-themed-muted text-right">
            {autoStartPics ? 'Auto-start depot mapping after login' : 'Manually trigger depot mapping after login'}
          </span>
        </div>
        </div>

        {steamAuthMode === 'authenticated' && (
          <div className="mt-4">
            <Alert color="green">
              <div className="flex items-center justify-between">
                <span className="text-sm">
                  <User className="w-4 h-4 inline mr-2" />
                  Authenticated as <strong>{username || 'Steam User'}</strong>
                </span>
                <Button
                  size="xs"
                  variant="filled"
                  color="red"
                  onClick={handleSwitchToAnonymous}
                  disabled={loading || mockMode}
                >
                  Logout
                </Button>
              </div>
            </Alert>
          </div>
        )}
      </Card>

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
          <p className="text-themed-secondary">
            Login with your Steam account to access playtest and restricted games. Your credentials are never stored - only refresh tokens are saved.
          </p>

          {/* Waiting for Mobile Confirmation */}
          {waitingForMobileConfirmation && (
            <>
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
            </>
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
                  className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none"
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
                  className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none"
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
                className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none"
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
                className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none"
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
                <li>After login, PICS data will be regenerated from scratch</li>
                <li>This may take 30-60 minutes depending on your library</li>
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

export default SteamLoginManager;
