import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, CheckCircle, Users, User } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { StepHeader } from '@components/initialization/StepHeader';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';

interface SteamPicsAuthStepProps {
  onComplete: (usingSteamAuth: boolean) => void;
}

type AuthMode = 'anonymous' | 'account';

export const SteamPicsAuthStep: React.FC<SteamPicsAuthStepProps> = ({ onComplete }) => {
  const { t } = useTranslation();
  const [selectedMode, setSelectedMode] = useState<AuthMode>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // No onError handler on purpose. The modal keeps the reason and stays open, the same as every
  // other login surface: closing it here ran resetAuthForm, which nulls the error, so the sentence
  // explaining the refusal was thrown away before anyone could read it and the wizard dropped back
  // on the anonymous card saying nothing at all.
  const { state, actions, loginDeadline } = useSteamAuthentication({
    autoStartPics: false,
    onSuccess: () => {
      setShowAuthModal(false);
      onComplete(true);
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
      await ApiService.setSteamAuthMode('anonymous');
      onComplete(false);
    } catch (err: unknown) {
      setError(getErrorMessage(err) || t('initialization.steamPicsAuth.networkError'));
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
        <StepHeader
          icon={<Shield className="w-7 h-7 icon-info" />}
          iconBackground="bg-themed-info"
          title={t('initialization.steamPicsAuth.title')}
          description={t('initialization.steamPicsAuth.subtitle')}
        />

        {/* Info Box */}
        <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
          <p className="text-themed-secondary">
            <strong className="text-themed-primary">
              {t('initialization.steamPicsAuth.whatIsDepotMapping')}
            </strong>{' '}
            {t('initialization.steamPicsAuth.depotMappingDesc')}
          </p>
        </div>

        {/* Mode Selection Cards */}
        <div className="space-y-3">
          {/* Anonymous Mode */}
          <Button
            type="button"
            variant="transparent"
            onClick={() => setSelectedMode('anonymous')}
            className={`w-full p-4 rounded-lg border-2 text-left justify-start transition ${
              selectedMode === 'anonymous'
                ? 'border-[var(--theme-primary)] bg-themed-primary-subtle'
                : 'border-themed-primary bg-transparent'
            }`}
          >
            <div className="flex items-center gap-3">
              <div className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 bg-themed-tertiary">
                <Users className="w-5 h-5 icon-primary" />
              </div>
              <div className="flex-1 min-w-0">
                <h4 className="font-semibold text-themed-primary">
                  {t('initialization.steamPicsAuth.anonymousMode')}
                </h4>
                <p className="text-sm text-themed-secondary">
                  {t('initialization.steamPicsAuth.anonymousModeDesc')}
                </p>
              </div>
              {selectedMode === 'anonymous' && (
                <CheckCircle className="w-5 h-5 flex-shrink-0 icon-primary" />
              )}
            </div>
          </Button>

          {/* Account Login Mode */}
          <Button
            type="button"
            variant="transparent"
            onClick={() => handleModeSelect('account')}
            className={`w-full p-4 rounded-lg border-2 text-left justify-start transition ${
              selectedMode === 'account'
                ? 'border-[var(--theme-primary)] bg-themed-primary-subtle'
                : 'border-themed-primary bg-transparent'
            }`}
          >
            <div className="flex items-center gap-3">
              <div className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 bg-themed-tertiary">
                <User className="w-5 h-5 icon-success" />
              </div>
              <div className="flex-1 min-w-0">
                <h4 className="font-semibold text-themed-primary">
                  {t('initialization.steamPicsAuth.accountMode')}
                </h4>
                <p className="text-sm text-themed-secondary">
                  {t('initialization.steamPicsAuth.accountModeDesc')}
                </p>
              </div>
              {selectedMode === 'account' && (
                <CheckCircle className="w-5 h-5 flex-shrink-0 icon-primary" />
              )}
            </div>
          </Button>
        </div>

        {/* Error Display */}
        {error && <Alert color="error">{error}</Alert>}

        {/* Continue Button */}
        {selectedMode === 'anonymous' && (
          <Button
            variant="filled"
            color="primary"
            onClick={handleContinueAnonymous}
            loading={saving}
            disabled={saving}
            fullWidth
          >
            {saving
              ? t('initialization.steamPicsAuth.saving')
              : t('initialization.steamPicsAuth.continueAnonymous')}
          </Button>
        )}
      </div>

      {/* Authentication Modal */}
      <SteamAuthModal
        opened={showAuthModal}
        onClose={handleCloseModal}
        state={state}
        actions={actions}
        loginDeadline={loginDeadline}
      />
    </>
  );
};
