import React, { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2, CheckCircle, ExternalLink, KeyRound, Shield } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';

interface EpicAuthStepProps {
  onComplete: () => void;
  onSkip: () => void;
}

export const EpicAuthStep: React.FC<EpicAuthStepProps> = ({ onComplete, onSkip }) => {
  const { t } = useTranslation();
  const [error, setError] = useState<string | null>(null);
  const [succeeded, setSucceeded] = useState(false);

  const handleSuccess = () => {
    setSucceeded(true);
  };

  const handleError = (message: string) => {
    setError(message);
  };

  const { state, actions, startLogin } = useEpicMappingAuth({
    onSuccess: handleSuccess,
    onError: handleError
  });

  useEffect(() => {
    if (succeeded) {
      const timer = setTimeout(() => {
        onComplete();
      }, 1500);
      return () => clearTimeout(timer);
    }
  }, [succeeded, onComplete]);

  const handleStartLogin = async () => {
    setError(null);
    await startLogin();
  };

  const handleAuthenticate = async () => {
    setError(null);
    await actions.handleAuthenticate();
  };

  const handleRetry = () => {
    setError(null);
    actions.resetAuthForm();
  };

  const handleAuthorizationCodeChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    actions.setAuthorizationCode(e.target.value);
  };

  // State 3: Success
  if (succeeded) {
    return (
      <div className="space-y-5">
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
            <CheckCircle className="w-7 h-7 icon-success" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">
            {t('initialization.epicAuth.success')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.epicAuth.successSubtitle')}
          </p>
        </div>
        <div className="flex justify-center">
          <Loader2 className="w-5 h-5 animate-spin text-themed-secondary" />
        </div>
      </div>
    );
  }

  // State 2: Waiting for authorization code
  if (state.needsAuthorizationCode) {
    return (
      <div className="space-y-5">
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
            <KeyRound className="w-7 h-7 icon-info" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">
            {t('initialization.epicAuth.enterCodeTitle')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.epicAuth.enterCodeSubtitle')}
          </p>
        </div>

        {/* Open Login Page */}
        <a
          href={state.authorizationUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="flex items-center justify-center gap-2 w-full px-4 py-2 bg-themed-tertiary hover:bg-themed-hover text-themed-primary border border-themed-secondary themed-button-radius font-medium smooth-transition"
        >
          <ExternalLink className="w-4 h-4" />
          {t('initialization.epicAuth.openEpicLogin')}
        </a>

        {/* Authorization Code Input */}
        <div>
          <label className="block text-sm font-medium text-themed-secondary mb-1.5">
            {t('initialization.epicAuth.codeLabel')}
          </label>
          <input
            type="password"
            value={state.authorizationCode}
            onChange={handleAuthorizationCodeChange}
            placeholder={t('initialization.epicAuth.codePlaceholder')}
            className="w-full px-3 py-2.5 themed-input"
            disabled={state.loading}
          />
        </div>

        {/* Error Display */}
        {error && (
          <div className="p-3 rounded-lg bg-themed-error">
            <p className="text-sm text-themed-error">{error}</p>
          </div>
        )}

        {/* Action Buttons */}
        <div className="flex gap-3">
          <Button
            variant="default"
            onClick={handleRetry}
            disabled={state.loading}
            className="flex-1"
          >
            {t('initialization.epicAuth.back')}
          </Button>
          <Button
            variant="filled"
            color="blue"
            onClick={handleAuthenticate}
            loading={state.loading}
            disabled={!state.authorizationCode.trim() || state.loading}
            className="flex-1"
          >
            {state.loading
              ? t('initialization.epicAuth.authenticating')
              : t('initialization.epicAuth.submitCode')}
          </Button>
        </div>
      </div>
    );
  }

  // State 1: Initial
  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <EpicIcon size={28} className="icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.epicAuth.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.epicAuth.subtitle')}
        </p>
      </div>

      {/* Security Note */}
      <div className="p-3 rounded-lg bg-themed-tertiary">
        <div className="flex items-start gap-2">
          <Shield className="w-4 h-4 flex-shrink-0 mt-0.5 icon-success" />
          <p className="text-sm text-themed-secondary">
            {t('initialization.epicAuth.securityNote')}
          </p>
        </div>
      </div>

      {/* Error Display */}
      {error && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{error}</p>
        </div>
      )}

      {/* Connect Button */}
      <Button
        variant="filled"
        color="blue"
        onClick={handleStartLogin}
        loading={state.loading}
        disabled={state.loading}
        fullWidth
      >
        {state.loading
          ? t('initialization.epicAuth.connecting')
          : t('initialization.epicAuth.connectButton')}
      </Button>

      {/* Skip */}
      <div className="text-center">
        <button
          onClick={onSkip}
          disabled={state.loading}
          className="text-sm text-themed-muted hover:text-themed-secondary smooth-transition disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {t('initialization.epicAuth.skipNote')}
        </button>
      </div>
    </div>
  );
};
