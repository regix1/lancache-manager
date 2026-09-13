import { noAutofill } from '@utils/autofill';
import React, { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, ExternalLink, KeyRound, Shield } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import FormField from '@components/ui/FormField';
import { StepHeader } from '@components/initialization/StepHeader';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';
import { integrationReasonKeys } from '../../../types';

interface EpicAuthStepProps {
  onComplete: () => void;
  onSkip: () => void;
  onAuthStateChange?: (busy: boolean) => void;
}

export const EpicAuthStep: React.FC<EpicAuthStepProps> = ({
  onComplete,
  onSkip,
  onAuthStateChange
}) => {
  const { t } = useTranslation();
  const [error, setError] = useState<string | null>(null);
  const [succeeded, setSucceeded] = useState<string | null>(null);

  const handleSuccess = () => {
    setSucceeded(identity);
  };

  const handleError = (message: string) => {
    setError(message);
  };

  const { state, actions, startLogin, authStatus, identity } = useEpicMappingAuth({
    onSuccess: handleSuccess,
    onError: handleError
  });

  const succeededForCaller =
    succeeded === identity || (authStatus?.canManage === true && authStatus.isAuthenticated);
  const reason =
    state.canAuthenticate === false
      ? t(
          integrationReasonKeys[state.ownershipReason ?? ''] ??
            'errors.integration.statusUnavailable'
        )
      : state.recovering
        ? t('errors.integration.recovery')
        : null;
  useEffect(() => {
    setError(null);
  }, [identity]);

  useEffect(() => {
    onAuthStateChange?.(state.loading);
    return () => {
      onAuthStateChange?.(false);
    };
  }, [state.loading, onAuthStateChange]);

  useEffect(() => {
    if (succeededForCaller) {
      const timer = setTimeout(() => {
        onComplete();
      }, 1500);
      return () => clearTimeout(timer);
    }
  }, [succeededForCaller, onComplete]);

  const handleStartLogin = async () => {
    if (state.canAuthenticate === false) return;
    setError(null);
    await startLogin();
  };

  const handleAuthenticate = async () => {
    if (state.canAuthenticate === false) return;
    setError(null);
    await actions.handleAuthenticate();
  };

  const handleRetry = () => {
    setError(null);
    actions.cancelPendingRequest();
    actions.resetAuthForm();
    actions.cancelLogin?.();
  };

  const handleAuthorizationCodeChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    actions.setAuthorizationCode(e.target.value);
  };

  // State 3: Success
  if (succeededForCaller) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<CheckCircle className="w-7 h-7 icon-success" />}
          iconBackground="bg-themed-success"
          title={t('initialization.epicAuth.success')}
          description={t('initialization.epicAuth.successSubtitle')}
        />
        <div className="flex justify-center">
          <LoadingSpinner inline size="md" className="text-themed-secondary" />
        </div>
      </div>
    );
  }

  // State 2: Waiting for authorization code
  if (state.needsAuthorizationCode) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<KeyRound className="w-7 h-7 icon-info" />}
          iconBackground="bg-themed-info"
          title={t('initialization.epicAuth.enterCodeTitle')}
          description={t('initialization.epicAuth.enterCodeSubtitle')}
        />

        {/* Open Login Page */}
        <a
          href={state.canAuthenticate === false ? undefined : state.authorizationUrl}
          aria-disabled={state.canAuthenticate === false}
          tabIndex={state.canAuthenticate === false ? -1 : undefined}
          onClick={(event) => {
            if (state.canAuthenticate === false) event.preventDefault();
          }}
          target="_blank"
          rel="noopener noreferrer"
          className="flex items-center justify-center gap-2 w-full px-4 py-2 bg-themed-tertiary hover:bg-themed-hover text-themed-primary border border-themed-secondary themed-button-radius font-medium smooth-transition"
        >
          <ExternalLink className="w-4 h-4" />
          {t('initialization.epicAuth.openEpicLogin')}
        </a>

        {/* Authorization Code Input */}
        <div>
          <FormField label={t('initialization.epicAuth.codeLabel')}>
            {(field) => (
              <input
                {...noAutofill}
                {...field}
                type="password"
                value={state.authorizationCode}
                onChange={handleAuthorizationCodeChange}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && state.authorizationCode.trim() && !state.loading) {
                    event.preventDefault();
                    void handleAuthenticate();
                  }
                }}
                placeholder={t('initialization.epicAuth.codePlaceholder')}
                className="themed-input setup-input"
                disabled={state.canAuthenticate === false || state.loading}
              />
            )}
          </FormField>
        </div>

        {/* Error Display */}
        {reason && (
          <p className="text-sm text-themed-muted" role="status">
            {reason}
          </p>
        )}
        {error && <Alert color="error">{error}</Alert>}

        {/* Action Buttons */}
        <div className="setup-actions">
          <Button
            variant="default"
            onClick={handleRetry}
            disabled={state.canAuthenticate === false || state.loading}
            className="flex-1"
          >
            {t('initialization.epicAuth.back')}
          </Button>
          <Button
            variant="filled"
            color="primary"
            onClick={handleAuthenticate}
            loading={state.loading}
            disabled={
              state.canAuthenticate === false || !state.authorizationCode.trim() || state.loading
            }
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
      <StepHeader
        icon={<EpicIcon size={28} className="icon-info" />}
        iconBackground="bg-themed-info"
        title={t('initialization.epicAuth.title')}
        description={t('initialization.epicAuth.subtitle')}
      />

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
      {reason && (
        <p className="text-sm text-themed-muted" role="status">
          {reason}
        </p>
      )}
      {error && <Alert color="error">{error}</Alert>}

      {/* Connect Button */}
      <Button
        variant="filled"
        color="primary"
        onClick={handleStartLogin}
        loading={state.loading}
        disabled={state.canAuthenticate === false || state.loading}
        fullWidth
      >
        {state.loading
          ? t('initialization.epicAuth.connecting')
          : t('initialization.epicAuth.connectButton')}
      </Button>

      {/* Skip */}
      <div className="text-center">
        <Button
          type="button"
          variant="transparent"
          size="sm"
          onClick={onSkip}
          disabled={state.loading}
          className="text-sm text-themed-muted hover:text-themed-secondary smooth-transition"
        >
          {t('initialization.epicAuth.skipNote')}
        </Button>
      </div>
    </div>
  );
};
