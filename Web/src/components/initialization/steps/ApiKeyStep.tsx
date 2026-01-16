import React from 'react';
import { useTranslation } from 'react-i18next';
import { Key, Eye, Loader2, Shield } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { useGuestConfig } from '@contexts/GuestConfigContext';

interface ApiKeyStepProps {
  apiKey: string;
  setApiKey: (key: string) => void;
  authenticating: boolean;
  authError: string | null;
  dataAvailable: boolean;
  checkingDataAvailability: boolean;
  apiKeyOnlyMode?: boolean;
  authDisabled?: boolean;
  onAuthenticate: () => void;
  onStartGuestMode: () => void;
  onContinueAsAdmin?: () => void;
}

export const ApiKeyStep: React.FC<ApiKeyStepProps> = ({
  apiKey,
  setApiKey,
  authenticating,
  authError,
  dataAvailable,
  checkingDataAvailability,
  apiKeyOnlyMode = false,
  authDisabled = false,
  onAuthenticate,
  onStartGuestMode,
  onContinueAsAdmin
}) => {
  const { t } = useTranslation();
  const { guestDurationHours } = useGuestConfig();

  // Simplified UI when authentication is globally disabled
  if (authDisabled) {
    return (
      <div className="space-y-5">
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
            <Shield className="w-7 h-7 icon-info" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">{t('initialization.apiKey.titleChoice')}</h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.apiKey.subtitleDisabled')}
          </p>
        </div>

        {/* Options */}
        <div className="space-y-3">
          <Button
            variant="filled"
            color="blue"
            onClick={onContinueAsAdmin}
            fullWidth
          >
            {t('initialization.apiKey.continueAsAdmin')}
          </Button>

          <div className="flex items-center gap-4">
            <div className="flex-1 h-px bg-themed-border" />
            <span className="text-xs text-themed-muted">{t('initialization.apiKey.or')}</span>
            <div className="flex-1 h-px bg-themed-border" />
          </div>

          <Button
            variant="default"
            onClick={onStartGuestMode}
            disabled={checkingDataAvailability || !dataAvailable}
            fullWidth
            title={!dataAvailable ? t('initialization.apiKey.noDataTooltip') : undefined}
          >
            {!dataAvailable
              ? t('initialization.apiKey.continueAsGuestNoData')
              : t('initialization.apiKey.continueAsGuest', { hours: guestDurationHours })}
          </Button>
        </div>

        {/* Info */}
        <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
          <p className="text-themed-secondary">
            {t('initialization.apiKey.adminAccess')}{' '}
            {t('initialization.apiKey.guestAccess', { hours: guestDurationHours })}
          </p>
        </div>

        {/* Error */}
        {authError && (
          <div className="p-3 rounded-lg bg-themed-error">
            <p className="text-sm text-themed-error">{authError}</p>
          </div>
        )}
      </div>
    );
  }

  // Normal UI when authentication is required
  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-warning">
          <Key className="w-7 h-7 icon-warning" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">{t('initialization.apiKey.title')}</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {apiKeyOnlyMode
            ? t('initialization.apiKey.subtitleNewKey')
            : t('initialization.apiKey.subtitle')}
        </p>
      </div>

      {/* API Key Input */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">
          {t('initialization.apiKey.label')}
        </label>
        <input
          type="text"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder={t('initialization.apiKey.placeholder')}
          className="w-full px-3 py-2.5 themed-input"
          disabled={authenticating}
        />
      </div>

      {/* Buttons */}
      <div className="space-y-3">
        <Button
          variant="filled"
          color="blue"
          onClick={onAuthenticate}
          disabled={authenticating || !apiKey.trim()}
          fullWidth
        >
          {authenticating && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
          {authenticating ? t('initialization.apiKey.authenticating') : t('initialization.apiKey.authenticate')}
        </Button>

        {!apiKeyOnlyMode && (
          <>
            <div className="flex items-center gap-4">
              <div className="flex-1 h-px bg-themed-border" />
              <span className="text-xs text-themed-muted">{t('initialization.apiKey.or')}</span>
              <div className="flex-1 h-px bg-themed-border" />
            </div>

            <Button
              variant="default"
              leftSection={<Eye className="w-4 h-4" />}
              onClick={onStartGuestMode}
              disabled={authenticating || checkingDataAvailability || !dataAvailable}
              fullWidth
              title={!dataAvailable ? t('initialization.apiKey.noDataTooltip') : undefined}
            >
              {!dataAvailable
                ? t('initialization.apiKey.guestMode')
                : t('initialization.apiKey.guestModeHours', { hours: guestDurationHours })}
            </Button>
          </>
        )}
      </div>

      {/* Help Info */}
      <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">{t('initialization.apiKey.whereToFind')}</strong>{' '}
          {t('initialization.apiKey.whereToFindDesc')} <code className="text-themed-accent">data/api_key.txt</code>
        </p>
      </div>

      {/* Error */}
      {authError && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{authError}</p>
        </div>
      )}
    </div>
  );
};
