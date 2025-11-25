import React from 'react';
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
  const { guestDurationHours } = useGuestConfig();

  // Simplified UI when authentication is globally disabled
  if (authDisabled) {
    return (
      <div className="space-y-5">
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div
            className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
            style={{ backgroundColor: 'var(--theme-info-bg)' }}
          >
            <Shield className="w-7 h-7" style={{ color: 'var(--theme-info)' }} />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">Choose Access Mode</h3>
          <p className="text-sm text-themed-secondary max-w-md">
            Authentication is disabled. Select how you'd like to proceed.
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
            Continue as Admin
          </Button>

          <div className="flex items-center gap-4">
            <div className="flex-1 h-px bg-themed-border" />
            <span className="text-xs text-themed-muted">OR</span>
            <div className="flex-1 h-px bg-themed-border" />
          </div>

          <Button
            variant="default"
            onClick={onStartGuestMode}
            disabled={checkingDataAvailability || !dataAvailable}
            fullWidth
            title={!dataAvailable ? 'No data available. Complete setup first.' : undefined}
          >
            {!dataAvailable
              ? 'Continue as Guest (No Data)'
              : `Continue as Guest (${guestDurationHours}h)`}
          </Button>
        </div>

        {/* Info */}
        <div
          className="p-3 rounded-lg text-sm"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <p className="text-themed-secondary">
            <strong className="text-themed-primary">Admin:</strong> Full management access.{' '}
            <strong className="text-themed-primary">Guest:</strong> Read-only for {guestDurationHours}h.
          </p>
        </div>

        {/* Error */}
        {authError && (
          <div
            className="p-3 rounded-lg"
            style={{ backgroundColor: 'var(--theme-error-bg)' }}
          >
            <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>{authError}</p>
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
        <div
          className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
          style={{ backgroundColor: 'var(--theme-warning-bg)' }}
        >
          <Key className="w-7 h-7" style={{ color: 'var(--theme-warning)' }} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">Authentication Required</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {apiKeyOnlyMode
            ? 'Enter your new API key to continue'
            : 'Enter your API key for full access'}
        </p>
      </div>

      {/* API Key Input */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">
          API Key
        </label>
        <input
          type="text"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder="Enter your API key"
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
          {authenticating ? 'Authenticating...' : 'Authenticate'}
        </Button>

        {!apiKeyOnlyMode && (
          <>
            <div className="flex items-center gap-4">
              <div className="flex-1 h-px bg-themed-border" />
              <span className="text-xs text-themed-muted">OR</span>
              <div className="flex-1 h-px bg-themed-border" />
            </div>

            <Button
              variant="default"
              leftSection={<Eye className="w-4 h-4" />}
              onClick={onStartGuestMode}
              disabled={authenticating || checkingDataAvailability || !dataAvailable}
              fullWidth
              title={!dataAvailable ? 'No data available. Complete setup first.' : undefined}
            >
              {!dataAvailable
                ? 'Guest Mode (No Data)'
                : `Continue as Guest (${guestDurationHours}h)`}
            </Button>
          </>
        )}
      </div>

      {/* Help Info */}
      <div
        className="p-3 rounded-lg text-sm"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">Where to find your API key:</strong>{' '}
          Check your server logs for "API Key:" or look in <code className="text-themed-accent">data/api_key.txt</code>
        </p>
      </div>

      {/* Error */}
      {authError && (
        <div
          className="p-3 rounded-lg"
          style={{ backgroundColor: 'var(--theme-error-bg)' }}
        >
          <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>{authError}</p>
        </div>
      )}
    </div>
  );
};
