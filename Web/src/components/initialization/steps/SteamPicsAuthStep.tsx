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

  // Steam account login - V2 API check removed to allow login regardless of Web API status
  const isV2Available = webApiStatus?.isV2Available ?? false;
  const hasV1ApiKey = webApiStatus?.hasApiKey ?? false;
  const steamAuthDisabled = false; // Allow Steam login regardless of V2 API status

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
    // Block account login if V2 API is not available
    if (mode === 'account' && steamAuthDisabled) {
      setError('Steam account login requires V2 API which is currently unavailable');
      return;
    }
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
            className={`w-full p-4 rounded-lg border-2 text-left transition-all ${steamAuthDisabled ? 'opacity-50 cursor-not-allowed' : ''}`}
            style={{
              borderColor: selectedMode === 'account' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
              backgroundColor: selectedMode === 'account' ? 'var(--theme-primary-bg, rgba(var(--theme-primary-rgb), 0.1))' : 'transparent'
            }}
            disabled={steamAuthDisabled}
          >
            <div className="flex items-center gap-3">
              <div
                className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <User className="w-5 h-5" style={{ color: steamAuthDisabled ? 'var(--theme-muted)' : 'var(--theme-success)' }} />
              </div>
              <div className="flex-1 min-w-0">
                <h4 className={`font-semibold ${steamAuthDisabled ? 'text-themed-muted' : 'text-themed-primary'}`}>
                  {steamAuthDisabled ? 'Account Login (Requires V2 API)' : 'Account Login (Playtest/Restricted)'}
                </h4>
                <p className="text-sm text-themed-secondary">
                  {steamAuthDisabled
                    ? 'V2 API unavailable - use V1 API key instead'
                    : 'Access playtest and restricted games via V2'}
                </p>
              </div>
              {selectedMode === 'account' && !steamAuthDisabled && (
                <CheckCircle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>
        </div>

        {/* V2 API Required Info Banner */}
        {steamAuthDisabled && !webApiLoading && (
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
                  <strong>Steam account login requires V2 API</strong> which is currently unavailable.
                  {hasV1ApiKey
                    ? ' Your V1 API key already provides access to playtest/restricted games since it\'s tied to your Steam account.'
                    : ' You can configure a V1 API key later in Settings to access playtest/restricted games.'}
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
