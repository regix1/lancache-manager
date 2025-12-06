import React, { useState } from 'react';
import { Shield, CheckCircle, Users, User, Loader2, Info } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import ApiService from '@services/api.service';

interface SteamPicsAuthStepProps {
  onComplete: (usingSteamAuth: boolean) => void;
}

type AuthMode = 'anonymous' | 'account';

export const SteamPicsAuthStep: React.FC<SteamPicsAuthStepProps> = ({ onComplete }) => {
  const [selectedMode, setSelectedMode] = useState<AuthMode>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();

  // Steam account login is optional - V1 API key provides sufficient access for most use cases
  const hasV1ApiKey = webApiStatus?.hasApiKey ?? false;

  const { state, actions } = useSteamAuthentication({
    autoStartPics: false,
    onSuccess: () => {
      setShowAuthModal(false);
      onComplete(true);
    },
    onError: () => {
      setShowAuthModal(false);
      actions.resetAuthForm();
      setSelectedMode('anonymous');
    }
  });

  const handleModeSelect = (mode: AuthMode) => {
    setSelectedMode(mode);
    setError(null);
    if (mode === 'account') {
      setShowAuthModal(true);
    }
  };

  const handleContinueAnonymous = async () => {
    setSaving(true);
    setError(null);

    try {
      // Save anonymous mode to backend
      const response = await fetch('/api/steam-auth/mode', {
        method: 'PUT',
        headers: {
          ...ApiService.getHeaders(),
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ mode: 'anonymous' })
      });

      if (response.ok) {
        onComplete(false);
      } else {
        const data = await response.json();
        setError(data.error || 'Failed to save authentication mode');
      }
    } catch (err: any) {
      setError(err.message || 'Network error - failed to save authentication mode');
    } finally {
      setSaving(false);
    }
  };

  const handleCloseModal = () => {
    if (!state.loading) {
      setShowAuthModal(false);
      actions.resetAuthForm();
      setSelectedMode('anonymous');
    }
  };

  return (
    <>
      <div className="space-y-5">
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div
            className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
            style={{ backgroundColor: 'var(--theme-info-bg)' }}
          >
            <Shield className="w-7 h-7" style={{ color: 'var(--theme-info)' }} />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">Steam PICS Authentication</h3>
          <p className="text-sm text-themed-secondary max-w-md">
            Choose how to authenticate with Steam for depot mapping data
          </p>
        </div>

        {/* Info Box */}
        <div
          className="p-3 rounded-lg text-sm"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <p className="text-themed-secondary">
            <strong className="text-themed-primary">What is depot mapping?</strong>{' '}
            Links cache files to games. Anonymous mode provides public games only.
            Account login enables access to playtest and restricted games.
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
              backgroundColor: selectedMode === 'anonymous' ? 'var(--theme-primary-bg, rgba(var(--theme-primary-rgb), 0.1))' : 'transparent'
            }}
          >
            <div className="flex items-center gap-3">
              <div
                className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <Users className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
              </div>
              <div className="flex-1 min-w-0">
                <h4 className="font-semibold text-themed-primary">Anonymous (Public Games)</h4>
                <p className="text-sm text-themed-secondary">No authentication required</p>
              </div>
              {selectedMode === 'anonymous' && (
                <CheckCircle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>

          {/* Account Login Mode */}
          <button
            onClick={() => handleModeSelect('account')}
            className="w-full p-4 rounded-lg border-2 text-left transition-all"
            style={{
              borderColor: selectedMode === 'account' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
              backgroundColor: selectedMode === 'account' ? 'var(--theme-primary-bg, rgba(var(--theme-primary-rgb), 0.1))' : 'transparent'
            }}
          >
            <div className="flex items-center gap-3">
              <div
                className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <User className="w-5 h-5" style={{ color: 'var(--theme-success)' }} />
              </div>
              <div className="flex-1 min-w-0">
                <h4 className="font-semibold text-themed-primary">
                  Account Login (Playtest/Restricted)
                </h4>
                <p className="text-sm text-themed-secondary">
                  Access playtest and restricted games
                </p>
              </div>
              {selectedMode === 'account' && (
                <CheckCircle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>
        </div>

        {/* Steam Login Info Banner */}
        {!webApiLoading && (
          <div
            className="p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-info-bg)',
              borderColor: 'var(--theme-info)'
            }}
          >
            <div className="flex items-start gap-3">
              <Info className="w-5 h-5 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-info)' }} />
              <div className="flex-1">
                <p className="text-xs" style={{ color: 'var(--theme-info-text)', opacity: 0.9 }}>
                  <strong>Steam account login is usually not needed.</strong>{' '}
                  {hasV1ApiKey
                    ? 'Your Steam Web API key already provides access to playtest and restricted games. Account login is only needed in rare cases.'
                    : 'A Steam Web API key is recommended for most use cases. Account login is only needed for specific restricted content.'}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Error Display */}
        {error && (
          <div
            className="p-3 rounded-lg"
            style={{ backgroundColor: 'var(--theme-error-bg)' }}
          >
            <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>{error}</p>
          </div>
        )}

        {/* Continue Button */}
        {selectedMode === 'anonymous' && (
          <Button
            variant="filled"
            color="blue"
            onClick={handleContinueAnonymous}
            disabled={saving}
            fullWidth
          >
            {saving && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
            {saving ? 'Saving...' : 'Continue with Anonymous Mode'}
          </Button>
        )}
      </div>

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
