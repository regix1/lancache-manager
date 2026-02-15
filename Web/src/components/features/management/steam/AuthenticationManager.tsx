import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Key, Lock, Unlock, Eye, LogOut } from 'lucide-react';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { useGuestConfig } from '@contexts/GuestConfigContext';
import { useAuth } from '@contexts/AuthContext';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';

interface AuthenticationManagerProps {
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const AuthenticationManager: React.FC<AuthenticationManagerProps> = ({
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { guestDurationHours } = useGuestConfig();
  const { authMode, refreshAuth } = useAuth();
  const { refreshSteamAuth, setSteamAuthMode, setUsername } = useSteamAuth();
  const { refresh: refreshSteamWebApiStatus } = useSteamWebApiStatus();
  const [authChecking, setAuthChecking] = useState(true);
  const [showAuthModal, setShowAuthModal] = useState(false);
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
  }, []);

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
      setHasData(result.hasData || false);
      setHasBeenInitialized(result.hasBeenInitialized || false);

      // Refresh the global auth context
      await refreshAuth();

      if (!result.isAuthenticated && authService.isAuthenticated) {
        await authService.logout();
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
      const result = await authService.login(apiKey);

      if (result.success) {
        // Refresh auth context
        await refreshAuth();

        // Close modal and clear
        setShowAuthModal(false);
        setApiKey('');
        onSuccess?.('Authentication successful! You can now use management features.');
      } else {
        setAuthError(result.message || t('modals.steamAuth.errors.authenticationFailed'));
      }
    } catch (error: unknown) {
      console.error('Authentication error:', error);
      setAuthError((error instanceof Error ? error.message : String(error)) || t('modals.steamAuth.errors.authenticationFailed'));
    } finally {
      setAuthLoading(false);
    }
  };

  const handleStartGuestMode = async () => {
    await authService.startGuestSession();
    await refreshAuth();
    setShowAuthModal(false);
    setApiKey('');
    setAuthError('');
    onSuccess?.(
      `Guest mode activated! You have ${guestDurationHours} hour${guestDurationHours !== 1 ? 's' : ''} to view data before re-authentication is required.`
    );
  };


  const handleLogout = async () => {
    setAuthLoading(true);

    try {
      // First, clear ALL Steam auth (PICS login AND Web API key)
      try {
        // Clear Steam PICS authentication
        await fetch('/api/steam-auth', ApiService.getFetchOptions({
          method: 'DELETE'
        }));
        // Clear Steam Web API key
        await fetch('/api/steam-api-keys/current', ApiService.getFetchOptions({
          method: 'DELETE'
        }));
        // Update frontend state immediately
        setSteamAuthMode('anonymous');
        setUsername('');
      } catch (steamError) {
        console.warn('[AuthenticationManager] Failed to clear Steam auth during logout:', steamError);
      }

      await authService.logout();
      await refreshAuth();
      // Refresh Steam contexts to ensure UI is updated
      await refreshSteamAuth();
      refreshSteamWebApiStatus();
      onSuccess?.('Logged out successfully. This device slot is now available for another user.');
    } catch (error: unknown) {
      console.error('Error logging out:', error);
      onError?.('Failed to logout: ' + (error instanceof Error ? error.message : String(error)));
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
      default:
        return <Lock className="w-5 h-5" />;
    }
  };

  const getStatusText = () => {
    switch (authMode) {
      case 'authenticated':
        return t('management.auth.status.authenticated');
      case 'guest':
        return t('management.auth.status.guestMode');
      default:
        return t('management.auth.status.notAuthenticated');
    }
  };

  const getDescriptionText = () => {
    switch (authMode) {
      case 'authenticated':
        return t('management.auth.description.authenticated');
      case 'guest':
        return t('management.auth.description.guest');
      default: {
        // Show hint about guest mode if eligible
        if (hasData && hasBeenInitialized) {
          return t('management.auth.description.requiresKeyOrGuest');
        }
        return t('management.auth.description.requiresKey');
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
          </div>

          <div className="flex items-center gap-2 w-full sm:w-auto sm:ml-4">
            {authMode === 'authenticated' && (
              <Button
                variant="filled"
                color="gray"
                size="sm"
                leftSection={<LogOut className="w-3 h-3" />}
                onClick={handleLogout}
                loading={authLoading}
                className="flex-1 sm:flex-none"
              >
                <span className="hidden sm:inline">{t('management.auth.logout')}</span>
                <span className="sm:hidden">{t('management.auth.logout')}</span>
              </Button>
            )}

            {authMode === 'unauthenticated' && (
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
                    <span className="hidden sm:inline">{t('management.auth.guestMode')}</span>
                    <span className="sm:hidden">{t('management.auth.guest')}</span>
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
                  <span className="hidden sm:inline">{t('management.auth.authenticate')}</span>
                  <span className="sm:hidden">{t('management.auth.auth')}</span>
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
                <span className="hidden sm:inline">{t('management.auth.fullAccess')}</span>
                <span className="sm:hidden">{t('management.auth.auth')}</span>
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
              {authMode === 'guest'
                ? t('management.auth.modal.fullAccessRequired')
                : t('management.auth.modal.authenticationRequired')}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {authMode === 'guest'
              ? t('management.auth.modal.guestMessage')
              : t('management.auth.modal.unauthenticatedMessage')}
          </p>

          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-2">{t('management.auth.modal.apiKeyLabel')}</label>
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
              <p className="font-medium mb-2">{t('management.auth.modal.findApiKey')}</p>
              <ol className="list-decimal list-inside text-sm space-y-1 ml-2">
                <li>{t('management.auth.modal.step1')}</li>
                <li>
                  {t('management.auth.modal.step2Before')} <code className="bg-themed-tertiary px-1 rounded">/data/security/api_key.txt</code>
                </li>
                <li>
                  {t('management.auth.modal.step3Before')}{' '}
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
                {t('common.cancel')}
              </Button>
              {/* Show guest mode option only when not already in guest mode and guest mode is available */}
              {authMode === 'unauthenticated' && isGuestModeAvailable && (
                <Button
                  variant="filled"
                  color="blue"
                  leftSection={<Eye className="w-4 h-4" />}
                  onClick={handleStartGuestMode}
                  disabled={authLoading}
                >
                  {t('management.auth.modal.continueAsGuest')}
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
              {authMode === 'guest' ? t('management.auth.modal.upgradeToFullAccess') : t('management.auth.authenticate')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default AuthenticationManager;
