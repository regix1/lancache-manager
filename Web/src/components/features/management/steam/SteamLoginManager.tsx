import React, { useState, useEffect } from 'react';
import { Key, User, Info, AlertTriangle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
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
    autoLogoutMessage,
    refreshSteamAuth,
    setSteamAuthMode: setContextSteamAuthMode,
    setUsername: setContextUsername,
    clearAutoLogoutMessage
  } = useSteamAuth();
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(false);

  const hasV1ApiKey = webApiStatus?.hasApiKey ?? false;
  const steamAuthDisabled = false;

  const { state, actions } = useSteamAuthentication({
    autoStartPics,
    onSuccess: (message) => {
      setContextSteamAuthMode('authenticated');
      setShowAuthModal(false);
      refreshSteamAuth(); // Refresh to get the authenticated username
      onSuccess?.(message);
    }
  });

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
      // Block if V2 API is not available
      if (steamAuthDisabled) {
        onError?.('Steam account login requires V2 API which is currently unavailable');
        return;
      }
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
      const response = await fetch('/api/steam-auth', {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        // Update context directly - no need to refresh from backend
        // The backend has already cleared the Steam auth, just update local state
        setContextSteamAuthMode('anonymous');
        setContextUsername('');
        onSuccess?.('Switched to anonymous Steam mode. Depot mappings preserved.');
      } else {
        const error = await response.json();
        onError?.(error.message || 'Failed to switch to anonymous mode');
      }
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to switch to anonymous mode');
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
    { value: 'anonymous', label: 'Anonymous (Public Games Only)' },
    { value: 'authenticated', label: 'Account Login (Playtest/Restricted Games)' }
  ];

  return (
    <>
      <Card>
        <div className="flex items-center gap-3 mb-6">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-blue">
            <Key className="w-5 h-5 icon-blue" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Steam PICS Authentication</h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title="Authentication Modes">
              <div className="space-y-1.5">
                <HelpDefinition term="Anonymous" termColor="blue">
                  Only public games — no Steam account needed
                </HelpDefinition>
                <HelpDefinition term="Account Login" termColor="green">
                  Access playtest and restricted games via your Steam account
                </HelpDefinition>
              </div>
            </HelpSection>

            <HelpSection title="Depot Mapping" variant="subtle">
              Automatic mode rebuilds depot mappings after login.
              Manual mode lets you trigger the rebuild yourself.
            </HelpSection>

            <HelpNote type="info">
              V2 API is required for account login. V1 API key alone provides restricted game access.
            </HelpNote>
          </HelpPopover>
        </div>

        {/* Auto-logout warning banner */}
        {autoLogoutMessage && (
          <Alert color="red" className="mb-4" icon={<AlertTriangle className="w-5 h-5" />}>
            <div className="flex items-start gap-3">
              <div className="flex-1">
                <p className="font-medium text-sm mb-1">Steam Session Auto-Logout</p>
                <p className="text-xs opacity-90">{autoLogoutMessage}</p>
              </div>
              <Button
                size="xs"
                variant="filled"
                onClick={clearAutoLogoutMessage}
                className="bg-white/20 text-themed-button border-none hover:!bg-white/30"
              >
                Dismiss
              </Button>
            </div>
          </Alert>
        )}

        {/* V2 API Required Info Banner */}
        {steamAuthDisabled && !webApiLoading && (
          <div className="mb-4 p-3 rounded-lg border bg-themed-info border-info">
            <div className="flex items-start gap-3">
              <Info className="w-5 h-5 flex-shrink-0 mt-0.5 icon-info" />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1 text-themed-info">
                  Steam Account Login Unavailable
                </p>
                <p className="text-xs text-themed-info opacity-90">
                  Steam account login requires V2 API which is currently unavailable.
                  {hasV1ApiKey
                    ? ' Your V1 API key already provides access to playtest/restricted games since it\'s tied to your Steam account.'
                    : ' Configure a V1 API key above to access playtest/restricted games.'}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Main auth mode selector */}
        <div className={`p-4 rounded-lg mb-4 bg-themed-tertiary ${steamAuthDisabled ? 'opacity-50' : ''}`}>
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary text-sm font-medium mb-1">
                {steamAuthMode === 'authenticated'
                  ? 'Logged in with Steam account'
                  : 'Using anonymous mode'}
              </p>
              <p className="text-xs text-themed-muted">
                {steamAuthMode === 'authenticated'
                  ? 'Can access playtest and restricted games'
                  : 'Only public games available'}
              </p>
            </div>

            {authMode === 'authenticated' && !mockMode ? (
              <div className="w-full sm:w-auto sm:min-w-[220px]">
                <EnhancedDropdown
                  options={dropdownOptions}
                  value={steamAuthMode}
                  onChange={handleModeChange}
                  disabled={loading || steamAuthDisabled}
                />
              </div>
            ) : (
              <div className="w-full sm:w-auto sm:min-w-[180px] px-3 py-2 rounded-lg text-center bg-themed-secondary border border-themed-primary">
                <p className="text-sm text-themed-muted">
                  {steamAuthMode === 'authenticated' ? 'Account Login' : 'Anonymous'}
                </p>
              </div>
            )}
          </div>
        </div>

        {/* Configuration section */}
        <div className={`p-4 rounded-lg bg-themed-tertiary ${steamAuthDisabled ? 'opacity-50' : ''}`}>
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary font-medium text-sm mb-1">
                Depot Mapping After Login
              </p>
              <p className="text-xs text-themed-muted">
                {autoStartPics
                  ? 'Automatically rebuild depot mappings after login'
                  : 'Manually trigger depot mapping rebuild after login'}
              </p>
            </div>
            <div className="inline-flex rounded-lg p-0.5 bg-themed-secondary">
              <button
                onClick={() => handleAutoStartPicsChange(true)}
                disabled={loading || mockMode || steamAuthDisabled}
                className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all ${
                  loading || mockMode || steamAuthDisabled ? 'opacity-50 cursor-not-allowed' : ''
                } ${autoStartPics ? 'toggle-btn-active' : 'toggle-btn-inactive'}`}
              >
                Automatic
              </button>
              <button
                onClick={() => handleAutoStartPicsChange(false)}
                disabled={loading || mockMode || steamAuthDisabled}
                className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all ${
                  loading || mockMode || steamAuthDisabled ? 'opacity-50 cursor-not-allowed' : ''
                } ${!autoStartPics ? 'toggle-btn-active' : 'toggle-btn-inactive'}`}
              >
                Manual
              </button>
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
