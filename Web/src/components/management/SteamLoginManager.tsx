import React, { useState, useEffect } from 'react';
import { Key, User } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SteamAuthModal } from '@components/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
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
  const [authenticatedUsername, setAuthenticatedUsername] = useState<string>('');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(false);

  const { state, actions } = useSteamAuthentication({
    autoStartPics,
    onSuccess: (message) => {
      setSteamAuthMode('authenticated');
      setShowAuthModal(false);
      loadSteamAuthState(); // Refresh to get the authenticated username
      onSuccess?.(message);
    },
    onError: (message) => {
      onError?.(message);
    }
  });

  useEffect(() => {
    loadSteamAuthState();
    // Load auto-start preference from localStorage
    const savedPref = localStorage.getItem('autoStartPics');
    if (savedPref !== null) {
      setAutoStartPics(savedPref === 'true');
    }
  }, []);

  const loadSteamAuthState = async () => {
    try {
      const response = await fetch('/api/management/steam-auth-status', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const authState: SteamAuthState = await response.json();
        setSteamAuthMode(authState.mode);
        // Store the authenticated username
        if (authState.mode === 'authenticated' && authState.username) {
          setAuthenticatedUsername(authState.username);
        } else {
          setAuthenticatedUsername('');
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
        setAuthenticatedUsername('');
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
              disabled={loading || mockMode || authMode !== 'authenticated'}
            />
          </div>
        </div>

        {/* Configuration section with unified background */}
        <div className="p-4 rounded-lg bg-themed-tertiary/30">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="flex-1">
              <p className="text-themed-primary font-medium text-sm mb-1">
                Depot Mapping After Login
              </p>
              <p className="text-xs text-themed-muted">
                {autoStartPics ? 'Automatically rebuild depot mappings after login' : 'Manually trigger depot mapping rebuild after login'}
              </p>
            </div>
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
