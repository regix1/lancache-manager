import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, Users, User } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { SelectableCard } from '@components/ui/SelectableCard';
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

  // The wizard runs the same in-process sign-in as the Management tab, so dismissing it has to
  // reach the server the same way. The phone-approval poll outlives the request that started it,
  // and until it gives up the account is marked as signing in and every later attempt is refused.
  // Best-effort: the poll ends on its own window if this request fails.
  const handleCancelLogin = () => {
    void ApiService.cancelSteamLogin().catch((err: unknown) => {
      console.error('Cancel Steam login failed:', getErrorMessage(err));
    });
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
        <fieldset className="grid gap-3">
          <legend className="sr-only">{t('initialization.steamPicsAuth.title')}</legend>
          <SelectableCard
            name="steam-auth-mode"
            value="anonymous"
            checked={selectedMode === 'anonymous'}
            onChange={() => handleModeSelect('anonymous')}
            icon={<Users className="icon-primary" />}
            title={t('initialization.steamPicsAuth.anonymousMode')}
            description={t('initialization.steamPicsAuth.anonymousModeDesc')}
          />
          <SelectableCard
            name="steam-auth-mode"
            value="account"
            checked={selectedMode === 'account'}
            onChange={() => handleModeSelect('account')}
            icon={<User className="icon-success" />}
            title={t('initialization.steamPicsAuth.accountMode')}
            description={t('initialization.steamPicsAuth.accountModeDesc')}
          />
        </fieldset>

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
        onCancelLogin={handleCancelLogin}
        loginDeadline={loginDeadline}
      />
    </>
  );
};
