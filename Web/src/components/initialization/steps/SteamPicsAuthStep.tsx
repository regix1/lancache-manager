import React, { useState } from 'react';
import { Shield, CheckCircle, ChevronRight, Users, User } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { SteamAuthModal } from '@components/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';

interface SteamPicsAuthStepProps {
  onComplete: (usingSteamAuth: boolean) => void;
}

type AuthMode = 'anonymous' | 'account';

export const SteamPicsAuthStep: React.FC<SteamPicsAuthStepProps> = ({ onComplete }) => {
  const [selectedMode, setSelectedMode] = useState<AuthMode>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);

  const { state, actions } = useSteamAuthentication({
    autoStartPics: false, // Don't auto-start during initialization
    onSuccess: () => {
      setShowAuthModal(false);
      // Continue to next step - true = using Steam auth
      onComplete(true);
    },
    onError: (message) => {
      console.error('[SteamPicsAuthStep] Auth error:', message);
    }
  });

  const handleModeSelect = (mode: AuthMode) => {
    setSelectedMode(mode);
    if (mode === 'account') {
      setShowAuthModal(true);
    }
  };

  const handleContinueAnonymous = () => {
    onComplete(false); // false = not using Steam auth
  };

  const handleCloseModal = () => {
    if (!state.loading) {
      setShowAuthModal(false);
      actions.resetAuthForm();
      // Reset to anonymous if they cancel
      setSelectedMode('anonymous');
    }
  };

  return (
    <>
      <div className="space-y-6">
        <div className="text-center">
          <div
            className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
            style={{ backgroundColor: 'var(--theme-primary)/10' }}
          >
            <Shield size={32} style={{ color: 'var(--theme-primary)' }} />
          </div>
          <h2 className="text-2xl font-bold text-themed-primary mb-2">
            Steam PICS Authentication
          </h2>
          <p className="text-themed-secondary">
            Choose how to authenticate with Steam for depot mapping data
          </p>
        </div>

        <div
          className="p-4 rounded-lg"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderColor: 'var(--theme-info)',
            color: 'var(--theme-info-text)'
          }}
        >
          <p className="text-sm">
            <strong>What is depot mapping?</strong><br/>
            Depot mapping links cache files to games. Anonymous mode provides access to public games only.
            Logging in with your Steam account enables access to playtest and restricted games.
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
              backgroundColor: selectedMode === 'anonymous' ? 'var(--theme-primary)/10' : 'transparent'
            }}
          >
            <div className="flex items-start gap-3">
              <div
                className="p-2 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <Users className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
              </div>
              <div className="flex-1">
                <h3 className="font-semibold text-themed-primary mb-1">
                  Anonymous (Public Data Only)
                </h3>
                <p className="text-sm text-themed-secondary">
                  Access depot mappings for public games only. No Steam login required.
                </p>
              </div>
              {selectedMode === 'anonymous' && (
                <CheckCircle className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>

          {/* Account Login Mode */}
          <button
            onClick={() => handleModeSelect('account')}
            className="w-full p-4 rounded-lg border-2 text-left transition-all"
            style={{
              borderColor: selectedMode === 'account' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
              backgroundColor: selectedMode === 'account' ? 'var(--theme-primary)/10' : 'transparent'
            }}
          >
            <div className="flex items-start gap-3">
              <div
                className="p-2 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <User className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
              </div>
              <div className="flex-1">
                <h3 className="font-semibold text-themed-primary mb-1">
                  Account Login (All Games)
                </h3>
                <p className="text-sm text-themed-secondary">
                  Login with Steam account to access playtest and restricted games. Depot mapping will be configured in the next step.
                </p>
              </div>
              {selectedMode === 'account' && (
                <CheckCircle className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
              )}
            </div>
          </button>
        </div>

        {/* Continue Button */}
        {selectedMode === 'anonymous' && (
          <Button
            variant="filled"
            color="blue"
            leftSection={<ChevronRight className="w-4 h-4" />}
            onClick={handleContinueAnonymous}
            fullWidth
          >
            Continue with Anonymous Mode
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
