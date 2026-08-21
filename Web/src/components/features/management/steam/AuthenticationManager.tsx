import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Key, Lock, Unlock, Eye } from 'lucide-react';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { ApiKeyRotatedModal } from '@components/modals/auth/ApiKeyRotatedModal';
import { LoadingState } from '@components/ui/ManagerCard';
import { useGuestConfig } from '@contexts/useGuestConfig';
import { useAuth } from '@contexts/useAuth';
import { formatSessionTimeRemaining } from '@utils/timeFormatters';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { getErrorMessage } from '@utils/error';

interface AuthenticationManagerProps {
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const AuthenticationManager: React.FC<AuthenticationManagerProps> = ({ onError, onSuccess }) => {
  const { t } = useTranslation();
  const { guestDurationHours } = useGuestConfig();
  const {
    authMode,
    refreshAuth,
    startGuestSession,
    sessionExpiresAt,
    authenticationEnabled,
    isMainAdmin
  } = useAuth();
  const { refreshSteamAuth, setSteamAuthMode, setUsername } = useSteamAuth();
  const { refresh: refreshSteamWebApiStatus } = useSteamWebApiStatus();
  const [authChecking, setAuthChecking] = useState(true);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [apiKey, setApiKey] = useState('');
  // The Steam login username arrives from useSteamAuth above, so the account username needs its
  // own name here.
  const [accountUsername, setAccountUsername] = useState('');
  const [password, setPassword] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);
  // Null until a status call answers. A failed checkAuth used to write false here, which is the
  // same value as "no download rows / setup unfinished", and that hid both guest buttons.
  const [hasData, setHasData] = useState<boolean | null>(null);
  const [hasBeenInitialized, setHasBeenInitialized] = useState<boolean | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<string | null>(null);
  const [showRegenerateConfirm, setShowRegenerateConfirm] = useState(false);
  const [regenerating, setRegenerating] = useState(false);
  const [regenerateError, setRegenerateError] = useState('');
  // Held from the rotation's own answer. There is no second chance to read it: the request that
  // produced it ended this session, so everything after it is answered 401.
  const [rotatedKey, setRotatedKey] = useState<string | null>(null);

  useEffect(() => {
    if (authMode !== 'guest' || !sessionExpiresAt) {
      setTimeRemaining(null);
      return;
    }
    setTimeRemaining(formatSessionTimeRemaining(sessionExpiresAt));
    const interval = setInterval(() => {
      setTimeRemaining(formatSessionTimeRemaining(sessionExpiresAt));
    }, 30_000);
    return () => clearInterval(interval);
  }, [authMode, sessionExpiresAt]);

  // Track previous auth mode to detect unexpected logouts
  const prevAuthMode = useRef<typeof authMode>(authMode);
  // Track if we've already shown the revocation modal (to prevent repeated triggers)
  const hasShownRevocationModal = useRef(false);

  useEffect(() => {
    checkAuth();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Auto-show auth modal when unexpectedly logged out
  useEffect(() => {
    // Skip during initial auth check
    if (authChecking) {
      prevAuthMode.current = authMode;
      return;
    }

    // The rotation ended this session on purpose, and its answer carried the only copy of the new
    // key. Opening the sign-in modal over that display would take the copy with it, so the detector
    // stays quiet while the key is on screen and the dismiss handler lands on sign-in itself.
    if (rotatedKey !== null) {
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
      onError?.(t('management.auth.notifications.sessionRevoked'));
    }

    // Update ref for next check
    prevAuthMode.current = authMode;
  }, [authMode, authChecking, onError, rotatedKey, t]);

  const checkAuth = async () => {
    setAuthChecking(true);
    try {
      const result = await authService.checkAuth();
      if (result.reachable !== false) {
        setHasData(result.hasData);
        setHasBeenInitialized(result.hasBeenInitialized);
      }

      // Refresh the global auth context
      await refreshAuth();

      if (!result.isAuthenticated && authService.isAuthenticated) {
        await authService.logout();
      }
    } catch (error) {
      // Intentional silent probe: a failed auth check degrades gracefully to the
      // unauthenticated state (visible via the auth UI itself), not an error dialog.
      console.error('Auth check failed:', error);
      await refreshAuth();
    } finally {
      setAuthChecking(false);
    }
  };

  const clearCredentials = () => {
    setApiKey('');
    setAccountUsername('');
    setPassword('');
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      setAuthError(t('auth.errors.missingKey'));
      return;
    }

    setAuthLoading(true);
    setAuthError('');

    try {
      const result = await authService.login(apiKey, accountUsername.trim(), password);

      if (result.success) {
        // Refresh auth context
        await refreshAuth();

        // Close modal and clear
        setShowAuthModal(false);
        clearCredentials();
        onSuccess?.(t('management.auth.notifications.authenticated'));
      } else {
        setAuthError(result.message || t('modals.steamAuth.errors.authenticationFailed'));
      }
    } catch (error: unknown) {
      console.error('Authentication error:', error);
      setAuthError(getErrorMessage(error) || t('modals.steamAuth.errors.authenticationFailed'));
    } finally {
      setAuthLoading(false);
    }
  };

  const credentialsFilled =
    apiKey.trim() !== '' && accountUsername.trim() !== '' && password !== '';

  const handleCredentialKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' && credentialsFilled && !authLoading) {
      handleAuthenticate();
    }
  };

  const handleStartGuestMode = async () => {
    const result = await startGuestSession();
    if (!result.success) {
      const message = result.message || t('modals.auth.errors.guestModeUnavailable');
      setAuthError(message);
      onError?.(message);
      return;
    }

    setShowAuthModal(false);
    clearCredentials();
    setAuthError('');
    onSuccess?.(t('management.auth.notifications.guestModeStarted', { count: guestDurationHours }));
  };

  const handleRegenerate = async () => {
    setRegenerating(true);
    setRegenerateError('');

    try {
      const response = await fetch(
        '/api/api-keys/regenerate',
        ApiService.getFetchOptions({ method: 'POST' })
      );
      const rotated = await ApiService.handleResponse<{ apiKey: string }>(response);

      setShowRegenerateConfirm(false);
      setRotatedKey(rotated.apiKey);
    } catch (error: unknown) {
      // Shown in the confirmation rather than as a toast: the refusal is about the button that was
      // just pressed, and the dialog stays open to carry it.
      setRegenerateError(getErrorMessage(error));
    } finally {
      setRegenerating(false);
    }
  };

  const handleRotatedKeyDismissed = async () => {
    // The server ended this session when the key rotated, so this is the local half of the same
    // logout the component already runs elsewhere. It happens before the key is cleared, so the
    // unexpected-logout detector sees the change while it is still held off and the sign-in surface
    // goes up because this handler puts it up, not because a 401 tripped something.
    await authService.logout();
    await refreshAuth();
    setRotatedKey(null);
    setShowAuthModal(true);
  };

  const handleLogout = async () => {
    setAuthLoading(true);

    try {
      // First, clear ALL Steam auth (PICS login AND Web API key)
      try {
        // Clear Steam PICS authentication
        await fetch(
          '/api/steam-auth',
          ApiService.getFetchOptions({
            method: 'DELETE'
          })
        );
        // Clear Steam Web API key
        await fetch(
          '/api/steam-api-keys/current',
          ApiService.getFetchOptions({
            method: 'DELETE'
          })
        );
        // Update frontend state immediately
        setSteamAuthMode('anonymous');
        setUsername('');
      } catch (steamError) {
        console.warn(
          '[AuthenticationManager] Failed to clear Steam auth during logout:',
          steamError
        );
      }

      await authService.logout();
      await refreshAuth();
      // Refresh Steam contexts to ensure UI is updated
      await refreshSteamAuth();
      refreshSteamWebApiStatus();
      onSuccess?.(t('management.auth.notifications.loggedOut'));
    } catch (error: unknown) {
      console.error('Error logging out:', error);
      onError?.(t('management.auth.errors.logoutFailed', { message: getErrorMessage(error) }));
    } finally {
      setAuthLoading(false);
    }
  };

  if (authChecking) {
    return <LoadingState message={t('management.auth.checkingStatus')} shape="settings" rows={1} />;
  }

  // When authentication is disabled via Security:EnableAuthentication, the auth
  // context forces authMode to 'authenticated'. Surface a distinct DISABLED state
  // instead of falsely claiming the user is "Authenticated" with management features.
  if (authenticationEnabled === false) {
    return (
      <Alert color="blue" icon={<Lock className="w-5 h-5" />}>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-medium text-sm sm:text-base">
              {t('management.auth.status.disabled')}
            </span>
          </div>
          <p className="text-xs mt-1 opacity-75">{t('management.auth.description.disabled')}</p>
        </div>
      </Alert>
    );
  }

  const getAlertColor = () => {
    switch (authMode) {
      case 'authenticated':
        return 'success';
      case 'guest':
        return 'info';
      default:
        return 'warning';
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
        return timeRemaining
          ? t('management.auth.status.guestMode', { time: timeRemaining })
          : t('management.auth.status.guestModeNoTime');
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
        if (hasData !== false && hasBeenInitialized !== false) {
          return t('management.auth.description.requiresKeyOrGuest');
        }
        return t('management.auth.description.requiresKey');
      }
    }
  };

  // Check if guest mode should be available
  // Requires: 1) Database has data, 2) Setup has been completed (persistent initialization flag)
  const isGuestModeAvailable = hasData !== false && hasBeenInitialized !== false;

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

          <div className="flex items-center gap-2 w-full sm:w-auto">
            {authMode === 'authenticated' && (
              <>
                {/* Rotating signs everyone out and hands the new key to whoever asked, so only the
                    account that owns the installation gets the control. The server refuses anyone
                    else as well - a hidden button is a courtesy, not a permission. */}
                {isMainAdmin && (
                  <Button
                    variant="filled"
                    color="destructive"
                    size="sm"
                    onClick={() => {
                      setRegenerateError('');
                      setShowRegenerateConfirm(true);
                    }}
                    disabled={authLoading}
                    className="flex-1 sm:flex-none"
                  >
                    {t('management.auth.regenerate')}
                  </Button>
                )}
                <Button
                  variant="filled"
                  color="secondary"
                  size="sm"
                  onClick={handleLogout}
                  loading={authLoading}
                  className="flex-1 sm:flex-none"
                >
                  <span className="hidden sm:inline">{t('management.auth.logout')}</span>
                  <span className="sm:hidden">{t('management.auth.logout')}</span>
                </Button>
              </>
            )}

            {authMode === 'unauthenticated' && (
              <>
                {isGuestModeAvailable && (
                  <Button
                    variant="filled"
                    color="secondary"
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
                  color="primary"
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
                color="primary"
                size="sm"
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
          clearCredentials();
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
              : isGuestModeAvailable
                ? t('management.auth.modal.unauthenticatedMessage')
                : t('management.auth.modal.apiKeyOnlyMessage')}
          </p>

          <div>
            <label className="form-field-label">{t('management.auth.modal.apiKeyLabel')}</label>
            <input
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              onKeyDown={handleCredentialKeyDown}
              placeholder="lm_xxxxxxxxxxxxxxxxxxxxx"
              className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted"
              autoComplete="off"
              disabled={authLoading}
            />
          </div>

          <div>
            <label className="form-field-label">{t('modals.auth.labels.username')}</label>
            <input
              type="text"
              value={accountUsername}
              onChange={(e) => setAccountUsername(e.target.value)}
              onKeyDown={handleCredentialKeyDown}
              placeholder={t('modals.auth.placeholders.enterUsername')}
              className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted"
              autoComplete="username"
              disabled={authLoading}
            />
          </div>

          <div>
            <label className="form-field-label">{t('modals.auth.labels.password')}</label>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              onKeyDown={handleCredentialKeyDown}
              placeholder={t('modals.auth.placeholders.enterPassword')}
              className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted"
              autoComplete="current-password"
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
                  {t('management.auth.modal.step2Before')}{' '}
                  <code className="bg-themed-tertiary px-1 rounded">
                    /data/security/api_key.txt
                  </code>
                </li>
                <li>{t('management.auth.modal.step3Before')}</li>
              </ol>
            </div>
          </Alert>

          <div className="flex flex-col sm:flex-row justify-between gap-3 pt-4 border-t border-themed-secondary">
            <div className="flex gap-3">
              <Button
                variant="default"
                onClick={() => {
                  setShowAuthModal(false);
                  clearCredentials();
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
                  color="secondary"
                  onClick={handleStartGuestMode}
                  disabled={authLoading}
                >
                  {t('management.auth.modal.continueAsGuest')}
                </Button>
              )}
            </div>

            <Button
              variant="filled"
              color="primary"
              onClick={handleAuthenticate}
              loading={authLoading}
              disabled={!credentialsFilled}
            >
              {authMode === 'guest'
                ? t('management.auth.modal.upgradeToFullAccess')
                : t('management.auth.authenticate')}
            </Button>
          </div>
        </div>
      </Modal>

      <ConfirmationModal
        opened={showRegenerateConfirm}
        onClose={() => setShowRegenerateConfirm(false)}
        onConfirm={handleRegenerate}
        loading={regenerating}
        title={t('management.auth.regenerateModal.title')}
      >
        <p className="text-themed-secondary">
          <span className="font-medium text-themed-primary">
            {t('management.auth.regenerateModal.important')}
          </span>{' '}
          {t('management.auth.regenerateModal.message')}
        </p>

        {regenerateError && <Alert color="red">{regenerateError}</Alert>}
      </ConfirmationModal>

      {rotatedKey !== null && (
        <ApiKeyRotatedModal opened apiKey={rotatedKey} onClose={handleRotatedKeyDismissed} />
      )}
    </>
  );
};

export default AuthenticationManager;
