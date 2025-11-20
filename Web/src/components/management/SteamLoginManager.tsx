import React, { useState, useEffect } from 'react';
import { Key, User } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SteamAuthModal } from '@components/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { storage } from '@utils/storage';

interface SteamLoginManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const SteamLoginManager: React.FC<SteamLoginManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const {
    steamAuthMode,
    username: authenticatedUsername,
    refreshSteamAuth,
    setSteamAuthMode: setContextSteamAuthMode,
    setUsername: setContextUsername
  } = useSteamAuth();
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(false);

  const { state, actions } = useSteamAuthentication({
    autoStartPics,
    onSuccess: (message) => {
      setContextSteamAuthMode('authenticated');
      setShowAuthModal(false);
      refreshSteamAuth(); // Refresh to get the authenticated username
      onSuccess?.(message);
    }
  });

  // PICS authentication logic:
  // - V2 available → PICS auth enabled (need Steam login to access all games)
  // - V2 unavailable, V1 key exists → PICS auth disabled (V1 API key can fetch all games)
  // - V2 unavailable, no V1 key → PICS auth disabled (no way to authenticate)
  const isPicsDisabled = !!(!webApiLoading && webApiStatus && !webApiStatus.isV2Available);
  const hasV1Fallback = !webApiLoading && webApiStatus && !webApiStatus.isV2Available && webApiStatus.hasApiKey;

  useEffect(() => {
    // Load auto-start preference from localStorage
    const savedPref = storage.getItem('autoStartPics');
    if (savedPref !== null) {
      setAutoStartPics(savedPref === 'true');
    }
  }, []);

  const handleAutoStartPicsChange = (enabled: boolean) => {
    setAutoStartPics(enabled);
    storage.setItem('autoStartPics', enabled.toString());
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
        setContextSteamAuthMode('anonymous');
        setContextUsername('');
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

  const handleCloseModal = () => {
    if (!state.loading) {
      setShowAuthModal(false);
      actions.resetAuthForm();
    }
  };

  const dropdownOptions = [
    { value: 'anonymous', label: 'Anonymous (Public Data Only)' },
    { value: 'authenticated', label: 'Account Login (Access All Games)' }
  ];

  return (
    <>
      <Card>
        <div className="flex items-center gap-2 mb-6">
          <Key className="w-5 h-5 icon-blue flex-shrink-0" />
          <h3 className="text-lg font-semibold text-themed-primary">Steam PICS Authentication</h3>
        </div>

        {/* Main auth mode selector */}
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
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
              disabled={loading || mockMode || authMode !== 'authenticated' || isPicsDisabled}
            />
          </div>
        </div>

        {/* Warning when PICS is disabled */}
        {isPicsDisabled && (
          <div className="mb-4">
            <Alert color={hasV1Fallback ? 'blue' : 'yellow'}>
              <p className="text-sm">
                <strong>Steam PICS Authentication Disabled:</strong>{' '}
                {hasV1Fallback ? (
                  <>
                    You have a Steam Web API V1 key configured. Steam PICS Authentication is not
                    needed - we can fetch all games using your API key without requiring a Steam
                    account login.
                  </>
                ) : (
                  <>
                    Web API V2 is unavailable and no V1 API key is configured. Please configure a
                    Steam Web API key in the Steam Web API Status section below to fetch game data.
                  </>
                )}
              </p>
            </Alert>
          </div>
        )}

        {/* Configuration section with unified background */}
        <div className="p-4 rounded-lg bg-themed-tertiary/30">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="flex-1">
              <p className="text-themed-primary font-medium text-sm mb-1">
                Depot Mapping After Login
              </p>
              <p className="text-xs text-themed-muted">
                {autoStartPics
                  ? 'Automatically rebuild depot mappings after login'
                  : 'Manually trigger depot mapping rebuild after login'}
              </p>
            </div>
            <div className="flex gap-2">
              <Button
                size="sm"
                variant={autoStartPics ? 'filled' : 'default'}
                color={autoStartPics ? 'blue' : undefined}
                onClick={() => handleAutoStartPicsChange(true)}
                disabled={loading || mockMode || isPicsDisabled}
              >
                Automatic
              </Button>
              <Button
                size="sm"
                variant={!autoStartPics ? 'filled' : 'default'}
                color={!autoStartPics ? 'blue' : undefined}
                onClick={() => handleAutoStartPicsChange(false)}
                disabled={loading || mockMode || isPicsDisabled}
              >
                Manual
              </Button>
            </div>
          </div>
        </div>

        {/* Authenticated status */}
        {steamAuthMode === 'authenticated' && (
          <div className="mt-4">
            <Alert color="green">
              <div className="flex items-center justify-between">
                <span className="text-sm">
                  <User className="w-4 h-4 inline mr-2" />
                  Authenticated as <strong>{authenticatedUsername || 'Steam User'}</strong>
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

      {/* Authentication Modal */}
      <SteamAuthModal
        opened={showAuthModal}
        onClose={handleCloseModal}
        state={state}
        actions={actions}
      />
    </>
  );
};

export default SteamLoginManager;
