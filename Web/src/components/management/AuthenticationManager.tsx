import React, { useState, useEffect } from 'react';
import { Key, Lock, Unlock, AlertCircle, AlertTriangle, Clock, Eye, LogOut } from 'lucide-react';
import authService, { AuthMode } from '../../services/auth.service';
import { Button } from '../ui/Button';
import { Alert } from '../ui/Alert';
import { Modal } from '../ui/Modal';
import { API_BASE } from '../../utils/constants';

interface AuthenticationManagerProps {
  onAuthChange?: (isAuthenticated: boolean) => void;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onApiKeyRegenerated?: () => void;
  onAuthModeChange?: (authMode: AuthMode) => void;
}

const AuthenticationManager: React.FC<AuthenticationManagerProps> = ({
  onAuthChange,
  onError,
  onSuccess,
  onApiKeyRegenerated,
  onAuthModeChange
}) => {
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [guestTimeRemaining, setGuestTimeRemaining] = useState<number>(0);
  const [authChecking, setAuthChecking] = useState(true);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [showRegenerateModal, setShowRegenerateModal] = useState(false);
  const [apiKey, setApiKey] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);
  const [hasData, setHasData] = useState(false);
  const [hasBeenInitialized, setHasBeenInitialized] = useState(false);
  const [hasPrimaryKey, setHasPrimaryKey] = useState(false);

  useEffect(() => {
    checkAuth();

    // Set up callback for guest mode expiry
    authService.onGuestExpired(() => {
      setAuthMode('expired');
      setGuestTimeRemaining(0);
      onAuthChange?.(false);
      onAuthModeChange?.('expired');
      onError?.('Guest session has expired. Please authenticate or start a new guest session.');
    });

    return () => {
      // Cleanup callback on unmount
      authService.onGuestExpired(null);
    };
  }, [onAuthChange, onAuthModeChange, onError]);

  // Update guest time remaining every minute
  useEffect(() => {
    if (authMode !== 'guest') return;

    const interval = setInterval(() => {
      const timeRemaining = authService.getGuestTimeRemaining();
      if (timeRemaining <= 0) {
        // Guest mode has expired, check auth to update state
        checkAuth();
      } else {
        setGuestTimeRemaining(timeRemaining);
      }
    }, 60000); // Check every minute

    return () => clearInterval(interval);
  }, [authMode]);

  const checkAuth = async () => {
    setAuthChecking(true);
    try {
      const result = await authService.checkAuth();
      console.log('[AuthenticationManager] Auth check result:', result);
      setAuthMode(result.authMode);
      setGuestTimeRemaining(result.guestTimeRemaining || 0);
      setHasData(result.hasData || false);
      setHasBeenInitialized(result.hasBeenInitialized || false);

      onAuthChange?.(result.isAuthenticated);
      onAuthModeChange?.(result.authMode);

      // Check API key type (only for authenticated users)
      if (result.isAuthenticated && result.authMode === 'authenticated') {
        checkApiKeyType();
      } else {
        setHasPrimaryKey(false);
      }

      if (!result.isAuthenticated && authService.isRegistered() && result.authMode !== 'guest') {
        authService.clearAuthAndDevice();
      }
    } catch (error) {
      console.error('Auth check failed:', error);
      setAuthMode('unauthenticated');
      setHasData(false);
      setHasBeenInitialized(false);
      setHasPrimaryKey(false);
      onAuthChange?.(false);
      onAuthModeChange?.('unauthenticated');
    } finally {
      setAuthChecking(false);
    }
  };

  const checkApiKeyType = async () => {
    try {
      const response = await fetch(`${API_BASE}/auth/api-key-type`, {
        headers: authService.getAuthHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        setHasPrimaryKey(data.hasPrimaryKey || false);
        console.log('[AuthenticationManager] API key type:', data.keyType, 'has primary:', data.hasPrimaryKey);
      } else {
        setHasPrimaryKey(false);
      }
    } catch (error) {
      console.error('[AuthenticationManager] Failed to check API key type:', error);
      setHasPrimaryKey(false);
    }
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      setAuthError('Please enter an API key');
      return;
    }

    setAuthLoading(true);
    setAuthError('');

    try {
      const result = await authService.register(apiKey);

      if (result.success) {
        // Update state immediately to prevent modal flash
        setAuthMode('authenticated');

        // Check API key type to determine if user has admin privileges
        await checkApiKeyType();

        // Then notify parent
        onAuthChange?.(true);
        onAuthModeChange?.('authenticated');

        // Close modal and clear
        setShowAuthModal(false);
        setApiKey('');
        onSuccess?.('Authentication successful! You can now use management features.');
      } else {
        setAuthError(result.message || 'Authentication failed');
      }
    } catch (error: any) {
      console.error('Authentication error:', error);
      setAuthError(error.message || 'Authentication failed');
    } finally {
      setAuthLoading(false);
    }
  };

  const handleStartGuestMode = () => {
    authService.startGuestMode();
    setAuthMode('guest');
    setGuestTimeRemaining(6 * 60); // 6 hours in minutes
    onAuthChange?.(false);
    onAuthModeChange?.('guest');
    setShowAuthModal(false);
    setApiKey('');
    setAuthError('');
    onSuccess?.('Guest mode activated! You have 6 hours to view data before re-authentication is required.');
  };

  const formatTimeRemaining = (minutes: number): string => {
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;

    if (hours > 0) {
      return mins > 0 ? `${hours}h ${mins}m` : `${hours}h`;
    }
    return `${mins}m`;
  };

  const handleRegenerateKey = () => {
    console.log('[AuthenticationManager] Regenerate Key button clicked');
    setShowRegenerateModal(true);
  };

  const confirmRegenerateKey = async () => {
    console.log('[AuthenticationManager] Confirm regenerate clicked');
    setAuthLoading(true);

    try {
      const result = await authService.regenerateApiKey();
      console.log('[AuthenticationManager] Regenerate API result:', result);

      if (result.success) {
        // Store if user was in guest mode before regeneration
        const wasGuestMode = authMode === 'guest';

        setAuthMode('unauthenticated');
        onAuthChange?.(false);
        onAuthModeChange?.('unauthenticated');
        setShowAuthModal(false);
        onSuccess?.(result.message);

        // If user was in guest mode, trigger the API key regenerated callback
        // which should redirect them to depot initialization
        if (wasGuestMode) {
          // Force a page reload to reset the application state properly
          // This ensures the user goes through the depot initialization flow
          window.location.reload();
        } else {
          onApiKeyRegenerated?.();
        }
      } else {
        onError?.(result.message || 'Failed to regenerate API key');
      }
    } catch (error: any) {
      console.error('Error regenerating key:', error);
      onError?.('Failed to regenerate API key: ' + error.message);
    } finally {
      setAuthLoading(false);
      setShowRegenerateModal(false);
    }
  };

  const handleLogout = async () => {
    setAuthLoading(true);

    try {
      const result = await authService.logout();

      if (result.success) {
        setAuthMode('unauthenticated');
        setHasPrimaryKey(false);
        onAuthChange?.(false);
        onAuthModeChange?.('unauthenticated');
        onSuccess?.('Logged out successfully. This device slot is now available for another user.');
      } else {
        onError?.(result.message || 'Failed to logout');
      }
    } catch (error: any) {
      console.error('Error logging out:', error);
      onError?.('Failed to logout: ' + error.message);
    } finally {
      setAuthLoading(false);
    }
  };

  if (authChecking) {
    return null;
  }

  const getAlertColor = () => {
    switch (authMode) {
      case 'authenticated': return 'green';
      case 'guest': return 'blue';
      case 'expired': return 'orange';
      default: return 'yellow';
    }
  };

  const getAlertIcon = () => {
    switch (authMode) {
      case 'authenticated': return <Unlock className="w-5 h-5" />;
      case 'guest': return <Eye className="w-5 h-5" />;
      case 'expired': return undefined; // Let Alert component use default AlertTriangle for orange
      default: return <Lock className="w-5 h-5" />;
    }
  };

  const getStatusText = () => {
    switch (authMode) {
      case 'authenticated': return 'Authenticated';
      case 'guest': return `Guest Mode (${formatTimeRemaining(guestTimeRemaining)} remaining)`;
      case 'expired': return 'Guest Session Expired';
      default: return 'Not Authenticated';
    }
  };

  const getDescriptionText = () => {
    switch (authMode) {
      case 'authenticated': return 'Management features enabled';
      case 'guest': return 'View-only access active';
      case 'expired': return 'Authentication required to continue';
      default: {
        // Show hint about guest mode if eligible
        if (hasData && hasBeenInitialized) {
          return 'Management features require API key or guest access';
        }
        return 'Management features require API key';
      }
    }
  };

  // Check if guest mode should be available
  // Requires: 1) Database has data, 2) Setup has been completed (persistent initialization flag)
  const isGuestModeAvailable = hasData && hasBeenInitialized;

  return (
    <>
      <Alert
        color={getAlertColor()}
        icon={getAlertIcon()}
      >
        <div className="flex items-center justify-between">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2">
              <span className="font-medium">
                {getStatusText()}
              </span>
              {authMode === 'authenticated' && hasPrimaryKey && (
                <span
                  className="px-2 py-0.5 text-xs font-medium rounded"
                  style={{
                    backgroundColor: 'var(--theme-warning-bg)',
                    color: 'var(--theme-warning-text)',
                    border: '1px solid var(--theme-warning)'
                  }}
                >
                  ADMIN
                </span>
              )}
            </div>
            <p className="text-xs mt-1 opacity-75">
              {getDescriptionText()}
            </p>
            {authMode === 'guest' && guestTimeRemaining > 0 && (
              <div className="flex items-center mt-2 text-xs opacity-75">
                <Clock className="w-3 h-3 mr-1" />
                <span>Session expires in {formatTimeRemaining(guestTimeRemaining)}</span>
              </div>
            )}
          </div>

          <div className="flex items-center gap-2 ml-4">
            {authMode === 'authenticated' && (
              <>
                <Button
                  variant="filled"
                  color="gray"
                  size="sm"
                  leftSection={<LogOut className="w-3 h-3" />}
                  onClick={handleLogout}
                  loading={authLoading}
                >
                  Logout
                </Button>
                {hasPrimaryKey && (
                  <>
                    {console.log('[AuthenticationManager] Rendering Regenerate Keys button, authMode:', authMode, 'hasPrimaryKey:', hasPrimaryKey, 'authLoading:', authLoading)}
                    <Button
                      variant="filled"
                      color="red"
                      size="sm"
                      leftSection={<AlertCircle className="w-3 h-3" />}
                      onClick={handleRegenerateKey}
                      loading={authLoading}
                    >
                      Regenerate Keys
                    </Button>
                  </>
                )}
              </>
            )}

            {(authMode === 'unauthenticated' || authMode === 'expired') && (
              <>
                {isGuestModeAvailable && (
                  <Button
                    variant="filled"
                    color="blue"
                    leftSection={<Eye className="w-4 h-4" />}
                    onClick={handleStartGuestMode}
                    disabled={authLoading}
                    size="sm"
                  >
                    Guest Mode
                  </Button>
                )}
                <Button
                  variant="filled"
                  color="yellow"
                  leftSection={<Key className="w-4 h-4" />}
                  onClick={() => setShowAuthModal(true)}
                  size="sm"
                >
                  Authenticate
                </Button>
              </>
            )}

            {authMode === 'guest' && (
              <Button
                variant="filled"
                color="yellow"
                size="sm"
                leftSection={<Key className="w-3 h-3" />}
                onClick={() => setShowAuthModal(true)}
              >
                Full Access
              </Button>
            )}
          </div>
        </div>
      </Alert>

      <Modal
        opened={showAuthModal}
        onClose={() => {
          setShowAuthModal(false);
          setApiKey('');
          setAuthError('');
        }}
        title={
          <div className="flex items-center space-x-3">
            <Key className="w-6 h-6 text-themed-warning" />
            <span>
              {authMode === 'expired' ? 'Session Expired' :
               authMode === 'guest' ? 'Full Access Required' :
               'Authentication Required'}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {authMode === 'expired'
              ? 'Your guest session has expired. Please authenticate with an API key or start a new guest session.'
              : authMode === 'guest'
              ? 'For full management features, please authenticate with your API key.'
              : 'Management operations require authentication. Please enter your API key or continue as guest.'}
          </p>

          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-2">API Key</label>
            <input
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
              placeholder="lm_xxxxxxxxxxxxxxxxxxxxx"
              className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none"
              disabled={authLoading}
            />
          </div>

          {authError && <Alert color="red">{authError}</Alert>}

          <Alert color="blue">
            <div>
              <p className="font-medium mb-2">To find your API key:</p>
              <ol className="list-decimal list-inside text-sm space-y-1 ml-2">
                <li>SSH into your LANCache Manager server</li>
                <li>Check <code className="bg-themed-tertiary px-1 rounded">/data/api_key.txt</code></li>
                <li>Or check container logs: <code className="bg-themed-tertiary px-1 rounded">docker logs lancache-manager-api</code></li>
              </ol>
            </div>
          </Alert>

          <div className="flex flex-col sm:flex-row justify-between gap-3 pt-4 border-t border-themed-secondary">
            <div className="flex gap-3">
              <Button
                variant="default"
                onClick={() => {
                  setShowAuthModal(false);
                  setApiKey('');
                  setAuthError('');
                }}
                disabled={authLoading}
              >
                Cancel
              </Button>
              {/* Show guest mode option only when not already in guest mode and guest mode is available */}
              {(authMode === 'unauthenticated' || authMode === 'expired') && isGuestModeAvailable && (
                <Button
                  variant="filled"
                  color="blue"
                  leftSection={<Eye className="w-4 h-4" />}
                  onClick={handleStartGuestMode}
                  disabled={authLoading}
                >
                  Continue as Guest
                </Button>
              )}
            </div>

            <Button
              variant="filled"
              color="green"
              leftSection={<Lock className="w-4 h-4" />}
              onClick={handleAuthenticate}
              loading={authLoading}
              disabled={!apiKey.trim()}
            >
              {authMode === 'guest' ? 'Upgrade to Full Access' : 'Authenticate'}
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={showRegenerateModal}
        onClose={() => {
          if (!authLoading) {
            setShowRegenerateModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Regenerate API Keys</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Regenerating the API keys will immediately log out all connected devices and guests, requiring everyone to re-authenticate with the new keys.
          </p>

          <Alert color="yellow">
            <div>
              <p className="font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>Both ADMIN and MODERATOR API keys will be regenerated</li>
                <li>All users and guests will be logged out immediately</li>
                <li>Steam integration will be logged out</li>
                <li>Check <code className="bg-themed-tertiary px-1 rounded">/data/api_key.txt</code> for the new ADMIN key</li>
                <li>Check <code className="bg-themed-tertiary px-1 rounded">/data/moderator_api_key.txt</code> for the new MODERATOR key</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowRegenerateModal(false)}
              disabled={authLoading}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<AlertCircle className="w-4 h-4" />}
              onClick={confirmRegenerateKey}
              loading={authLoading}
            >
              Regenerate Keys
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default AuthenticationManager;
