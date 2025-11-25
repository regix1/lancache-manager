import React, { useState, useEffect, useRef } from 'react';
import { Key, Lock, Unlock, AlertCircle, AlertTriangle, Clock, Eye, LogOut } from 'lucide-react';
import authService from '@services/auth.service';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { useGuestConfig } from '@contexts/GuestConfigContext';
import { useAuth } from '@contexts/AuthContext';

interface AuthenticationManagerProps {
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onApiKeyRegenerated?: () => void;
}

const AuthenticationManager: React.FC<AuthenticationManagerProps> = ({
  onError,
  onSuccess,
  onApiKeyRegenerated
}) => {
  const { guestDurationHours } = useGuestConfig();
  const { authMode, refreshAuth } = useAuth();
  const [guestTimeRemaining, setGuestTimeRemaining] = useState<number>(0);
  const [authChecking, setAuthChecking] = useState(true);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [showRegenerateModal, setShowRegenerateModal] = useState(false);
  const [apiKey, setApiKey] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);
  const [hasData, setHasData] = useState(false);
  const [hasBeenInitialized, setHasBeenInitialized] = useState(false);

  // Track previous auth mode to detect unexpected logouts
  const prevAuthMode = useRef<typeof authMode>(authMode);
  // Track if we've already shown the revocation modal (to prevent repeated triggers)
  const hasShownRevocationModal = useRef(false);

  useEffect(() => {
    checkAuth();

    // Set up callback for guest mode expiry
    authService.onGuestExpired(() => {
      setGuestTimeRemaining(0);
      refreshAuth();
      onError?.('Guest session has expired. Please authenticate or start a new guest session.');
    });

    return () => {
      // Cleanup callback on unmount
      authService.onGuestExpired(null);
    };
  }, [refreshAuth, onError]);

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

  // Auto-show auth modal when unexpectedly logged out
  useEffect(() => {
    // Skip during initial auth check
    if (authChecking) {
      prevAuthMode.current = authMode;
      return;
    }

    // Don't show modal repeatedly - only once per logout
    if (hasShownRevocationModal.current && authMode === 'unauthenticated') {
      prevAuthMode.current = authMode;
      return;
    }

    // Reset flag when user becomes authenticated again
    if (authMode === 'authenticated' || authMode === 'guest') {
      hasShownRevocationModal.current = false;
      prevAuthMode.current = authMode;
      return;
    }

    // Detect transition from authenticated/guest to unauthenticated (logout/revocation)
    const wasLoggedOut =
      (prevAuthMode.current === 'authenticated' || prevAuthMode.current === 'guest') &&
      authMode === 'unauthenticated';

    if (wasLoggedOut) {
      console.log('[AuthenticationManager] Unexpected logout detected, showing auth modal', {
        authMode,
        prevAuthMode: prevAuthMode.current
      });
      setShowAuthModal(true);
      hasShownRevocationModal.current = true; // Mark as shown
      onError?.('Your session has expired or been revoked. Please authenticate again.');
    }

    // Update ref for next check
    prevAuthMode.current = authMode;
  }, [authMode, authChecking, onError]);

  const checkAuth = async () => {
    setAuthChecking(true);
    try {
      const result = await authService.checkAuth();
      console.log('[AuthenticationManager] Auth check result:', result);
      setGuestTimeRemaining(result.guestTimeRemaining || 0);
      setHasData(result.hasData || false);
      setHasBeenInitialized(result.hasBeenInitialized || false);

      // Refresh the global auth context
      await refreshAuth();

      if (!result.isAuthenticated && authService.isRegistered() && result.authMode !== 'guest') {
        authService.clearAuthAndDevice();
      }
    } catch (error) {
      console.error('Auth check failed:', error);
      setHasData(false);
      setHasBeenInitialized(false);
      await refreshAuth();
    } finally {
      setAuthChecking(false);
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
        // Refresh auth context
        await refreshAuth();

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

  const handleStartGuestMode = async () => {
    await authService.startGuestMode();
    setGuestTimeRemaining(guestDurationHours * 60); // Convert hours to minutes
    await refreshAuth();
    setShowAuthModal(false);
    setApiKey('');
    setAuthError('');
    onSuccess?.(
      `Guest mode activated! You have ${guestDurationHours} hour${guestDurationHours !== 1 ? 's' : ''} to view data before re-authentication is required.`
    );
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

        await refreshAuth();
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
        await refreshAuth();
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
      case 'authenticated':
        return 'green';
      case 'guest':
        return 'blue';
      case 'expired':
        return 'orange';
      default:
        return 'yellow';
    }
  };

  const getAlertIcon = () => {
    switch (authMode) {
      case 'authenticated':
        return <Unlock className="w-5 h-5" />;
      case 'guest':
        return <Eye className="w-5 h-5" />;
      case 'expired':
        return undefined; // Let Alert component use default AlertTriangle for orange
      default:
        return <Lock className="w-5 h-5" />;
    }
  };

  const getStatusText = () => {
    switch (authMode) {
      case 'authenticated':
        return 'Authenticated';
      case 'guest':
        return `Guest Mode (${formatTimeRemaining(guestTimeRemaining)} remaining)`;
      case 'expired':
        return 'Guest Session Expired';
      default:
        return 'Not Authenticated';
    }
  };

  const getDescriptionText = () => {
    switch (authMode) {
      case 'authenticated':
        return 'Management features enabled';
      case 'guest':
        return 'View-only access active';
      case 'expired':
        return 'Authentication required to continue';
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
      <Alert color={getAlertColor()} icon={getAlertIcon()}>
        <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
          <div className="flex-1 min-w-0 w-full sm:w-auto">
            <div className="flex items-center gap-2">
              <span className="font-medium text-sm sm:text-base">{getStatusText()}</span>
            </div>
            <p className="text-xs mt-1 opacity-75">{getDescriptionText()}</p>
            {authMode === 'guest' && guestTimeRemaining > 0 && (
              <div className="flex items-center mt-2 text-xs opacity-75">
                <Clock className="w-3 h-3 mr-1" />
                <span>Session expires in {formatTimeRemaining(guestTimeRemaining)}</span>
              </div>
            )}
          </div>

          <div className="flex items-center gap-2 w-full sm:w-auto sm:ml-4">
            {authMode === 'authenticated' && (
              <>
                <Button
                  variant="filled"
                  color="gray"
                  size="sm"
                  leftSection={<LogOut className="w-3 h-3" />}
                  onClick={handleLogout}
                  loading={authLoading}
                  className="flex-1 sm:flex-none"
                >
                  <span className="hidden sm:inline">Logout</span>
                  <span className="sm:hidden">Logout</span>
                </Button>
                <Button
                  variant="filled"
                  color="red"
                  size="sm"
                  leftSection={<AlertCircle className="w-3 h-3" />}
                  onClick={handleRegenerateKey}
                  loading={authLoading}
                  className="flex-1 sm:flex-none"
                >
                  <span className="hidden sm:inline">Regenerate Key</span>
                  <span className="sm:hidden">Regenerate</span>
                </Button>
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
                    className="flex-1 sm:flex-none"
                  >
                    <span className="hidden sm:inline">Guest Mode</span>
                    <span className="sm:hidden">Guest</span>
                  </Button>
                )}
                <Button
                  variant="filled"
                  color="yellow"
                  leftSection={<Key className="w-4 h-4" />}
                  onClick={() => setShowAuthModal(true)}
                  size="sm"
                  className="flex-1 sm:flex-none"
                >
                  <span className="hidden sm:inline">Authenticate</span>
                  <span className="sm:hidden">Auth</span>
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
                className="w-full sm:w-auto"
              >
                <span className="hidden sm:inline">Full Access</span>
                <span className="sm:hidden">Auth</span>
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
              {authMode === 'expired'
                ? 'Session Expired'
                : authMode === 'guest'
                  ? 'Full Access Required'
                  : 'Authentication Required'}
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
                <li>
                  Check <code className="bg-themed-tertiary px-1 rounded">/data/api_key.txt</code>
                </li>
                <li>
                  Or check container logs:{' '}
                  <code className="bg-themed-tertiary px-1 rounded">
                    docker logs lancache-manager-api
                  </code>
                </li>
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
              {(authMode === 'unauthenticated' || authMode === 'expired') &&
                isGuestModeAvailable && (
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
            <span>Regenerate API Key</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Regenerating the API key will immediately log out all connected devices and guests,
            requiring everyone to re-authenticate with the new key.
          </p>

          <Alert color="yellow">
            <div>
              <p className="font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>The API key will be regenerated</li>
                <li>All users and guests will be logged out immediately</li>
                <li>Steam integration will be logged out</li>
                <li>
                  Check <code className="bg-themed-tertiary px-1 rounded">/data/api_key.txt</code>{' '}
                  for the new API key
                </li>
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
              Regenerate Key
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default AuthenticationManager;
