import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
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
  const { t } = useTranslation();
  const [selectedMode, setSelectedMode] = useState<AuthMode>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();

  const hasV1ApiKey = webApiStatus?.hasApiKey ?? false;
  const steamAuthDisabled = false;

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
      setError(t('initialization.steamPicsAuth.v2Required'));
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
      const response = await fetch('/api/steam-auth/mode', ApiService.getFetchOptions({
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ mode: 'anonymous' })
      }));

      if (response.ok) {
        onComplete(false);
      } else {
        const data = await response.json();
        setError(data.error || t('initialization.steamPicsAuth.failedToSave'));
      }
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || t('initialization.steamPicsAuth.networkError'));
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
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
            <Shield className="w-7 h-7 icon-info" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">{t('initialization.steamPicsAuth.title')}</h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.steamPicsAuth.subtitle')}
          </p>
        </div>

        {/* Info Box */}
        <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
          <p className="text-themed-secondary">
            <strong className="text-themed-primary">{t('initialization.steamPicsAuth.whatIsDepotMapping')}</strong>{' '}
            {t('initialization.steamPicsAuth.depotMappingDesc')}
          </p>
        </div>

        {/* Mode Selection Cards */}
        <div className="space-y-3">
          {/* Anonymous Mode */}
          <button
            onClick={() => setSelectedMode('anonymous')}
            className={`w-full p-4 rounded-lg border-2 text-left transition-all ${
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
                <h4 className="font-semibold text-themed-primary">{t('initialization.steamPicsAuth.anonymousMode')}</h4>
                <p className="text-sm text-themed-secondary">{t('initialization.steamPicsAuth.anonymousModeDesc')}</p>
              </div>
              {selectedMode === 'anonymous' && (
                <CheckCircle className="w-5 h-5 flex-shrink-0 icon-primary" />
              )}
            </div>
          </button>

          {/* Account Login Mode */}
          <button
            onClick={() => handleModeSelect('account')}
            className={`w-full p-4 rounded-lg border-2 text-left transition-all ${
              steamAuthDisabled ? 'opacity-50 cursor-not-allowed' : ''
            } ${
              selectedMode === 'account'
                ? 'border-[var(--theme-primary)] bg-themed-primary-subtle'
                : 'border-themed-primary bg-transparent'
            }`}
            disabled={steamAuthDisabled}
          >
            <div className="flex items-center gap-3">
              <div className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 bg-themed-tertiary">
                <User className={`w-5 h-5 ${steamAuthDisabled ? 'icon-muted' : 'icon-success'}`} />
              </div>
              <div className="flex-1 min-w-0">
                <h4 className={`font-semibold ${steamAuthDisabled ? 'text-themed-muted' : 'text-themed-primary'}`}>
                  {steamAuthDisabled ? t('initialization.steamPicsAuth.accountModeDisabled') : t('initialization.steamPicsAuth.accountMode')}
                </h4>
                <p className="text-sm text-themed-secondary">
                  {steamAuthDisabled
                    ? t('initialization.steamPicsAuth.accountModeUnavailable')
                    : t('initialization.steamPicsAuth.accountModeDesc')}
                </p>
              </div>
              {selectedMode === 'account' && !steamAuthDisabled && (
                <CheckCircle className="w-5 h-5 flex-shrink-0 icon-primary" />
              )}
            </div>
          </button>
        </div>

        {/* V2 API Required Info Banner */}
        {steamAuthDisabled && !webApiLoading && (
          <div className="p-3 rounded-lg border bg-themed-info border-[var(--theme-info)]">
            <div className="flex items-start gap-3">
              <Info className="w-5 h-5 flex-shrink-0 mt-0.5 icon-info" />
              <div className="flex-1">
                <p className="text-xs text-themed-info opacity-90">
                  <strong>{t('initialization.steamPicsAuth.v2Required')}</strong>
                  {hasV1ApiKey
                    ? ' ' + t('initialization.steamPicsAuth.v2RequiredWithV1')
                    : ' ' + t('initialization.steamPicsAuth.v2RequiredNoV1')}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Error Display */}
        {error && (
          <div className="p-3 rounded-lg bg-themed-error">
            <p className="text-sm text-themed-error">{error}</p>
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
            {saving ? t('initialization.steamPicsAuth.saving') : t('initialization.steamPicsAuth.continueAnonymous')}
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
