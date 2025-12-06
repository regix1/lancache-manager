import React, { useState, useEffect } from 'react';
import { Key, User, Info } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote, HelpKeyword, HelpDefinition } from '@components/ui/HelpPopover';
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
    refreshSteamAuth,
    setSteamAuthMode: setContextSteamAuthMode,
    setUsername: setContextUsername
  } = useSteamAuth();
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(false);

  // Steam account login is optional - V1 API key provides sufficient access for most use cases
  const hasV1ApiKey = webApiStatus?.hasApiKey ?? false;

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
              <HelpKeyword color="cyan">Automatic</HelpKeyword> rebuilds depot mappings after login.{' '}
              <HelpKeyword color="blue">Manual</HelpKeyword> lets you trigger the rebuild yourself.
            </HelpSection>

            <HelpNote type="info">
              V2 API is required for account login. V1 API key alone provides restricted game access.
            </HelpNote>
          </HelpPopover>
        </div>

        {/* Steam Login Info Banner */}
        {steamAuthMode === 'anonymous' && !webApiLoading && (
          <div
            className="mb-4 p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-info-bg)',
              borderColor: 'var(--theme-info)'
            }}
          >
            <div className="flex items-start gap-3">
              <Info className="w-5 h-5 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-info)' }} />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1" style={{ color: 'var(--theme-info-text)' }}>
                  Steam Account Login Usually Not Needed
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-info-text)', opacity: 0.9 }}>
                  {hasV1ApiKey
                    ? 'Your Steam Web API key already provides access to playtest and restricted games. Account login is only needed in rare cases where the API key isn\'t sufficient.'
                    : 'A Steam Web API key is recommended for most use cases. Account login is only needed in rare cases for specific restricted content.'}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Main auth mode selector */}
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
          <div className="flex-1">
            <p className="text-themed-secondary">
              {steamAuthMode === 'authenticated'
                ? 'Logged in with Steam account - can access playtest and restricted games'
                : 'Using anonymous mode - only public games available'}
            </p>
            <p className="text-xs text-themed-muted mt-1">
              {authMode === 'authenticated' && !mockMode
                ? 'Change login mode to access different depot mappings'
                : 'Login with your API key to change Steam authentication mode'}
            </p>
          </div>

          {authMode === 'authenticated' && !mockMode ? (
            <div className="w-full sm:w-64">
              <EnhancedDropdown
                options={dropdownOptions}
                value={steamAuthMode}
                onChange={handleModeChange}
                disabled={loading}
              />
            </div>
          ) : (
            <div className="w-full sm:w-64 px-3 py-2 rounded-lg border bg-themed-tertiary/30 opacity-50">
              <p className="text-sm text-themed-secondary">
                {steamAuthMode === 'authenticated' ? 'Account Login' : 'Anonymous'}
              </p>
            </div>
          )}
        </div>

        {/* Configuration section with unified background */}
        <div className={`p-4 rounded-lg bg-themed-tertiary/30 ${steamAuthMode === 'anonymous' ? 'opacity-50' : ''}`}>
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="flex-1">
              <p className="text-themed-primary font-medium text-sm mb-1">
                Depot Mapping After Login
              </p>
              <p className="text-xs text-themed-muted">
                {steamAuthMode === 'anonymous'
                  ? 'Only available when logged in with Steam account'
                  : autoStartPics
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
                disabled={loading || mockMode || steamAuthMode === 'anonymous'}
              >
                Automatic
              </Button>
              <Button
                size="sm"
                variant={!autoStartPics ? 'filled' : 'default'}
                color={!autoStartPics ? 'blue' : undefined}
                onClick={() => handleAutoStartPicsChange(false)}
                disabled={loading || mockMode || steamAuthMode === 'anonymous'}
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
