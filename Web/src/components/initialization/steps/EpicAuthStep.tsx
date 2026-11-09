import { noAutofill } from '@utils/autofill';
import React, { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, ExternalLink, KeyRound, Shield } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import { ProgressBar } from '@components/ui/ProgressBar';
import FormField from '@components/ui/FormField';
import { StepHeader } from '@components/initialization/StepHeader';
import { SignInReason } from '@components/initialization/SignInReason';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';
import { describeEpicMappingRun } from '@hooks/useEpicMappingRun';
import { getIntegrationReasonKey } from '../../../types';

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

  const handleError = (message: string) => {
    setError(message);
  };

  const { state, actions, startLogin, authStatus, identity, refreshStatus } = useEpicMappingAuth({
    onError: handleError
  });

  // The step is done only when the mapping ended with its own done key. With no mapping operation known (after a
  // reload or a restart) a saved sign-in counts as done only when a mapping once finished: the server drops a null
  // collection time from the answer, so it arrives missing and is tested loosely. While the server has not said
  // whether a mapping runs, or a new sign-in is under way, the saved sign-in decides nothing.
  const mapping = state.mapping;
  const savedSignIn = authStatus?.canManage === true && authStatus.isAuthenticated;
  const mappingKnown =
    mapping !== undefined && mapping.phase !== 'none' && mapping.phase !== 'unknown';
  const mappingRunning = mappingKnown ? mapping.phase === 'running' : state.codeAccepted === true;
  const mappingEnded = mappingKnown && mapping.phase !== 'running';
  const savedSignInDecides =
    mapping?.phase === 'none' &&
    state.codeAccepted !== true &&
    !state.loading &&
    !state.needsAuthorizationCode &&
    savedSignIn;
  const succeededForCaller = mappingKnown
    ? mapping.phase === 'done'
    : savedSignInDecides && authStatus.lastCollectionUtc != null;
  const mappingFailed = !succeededForCaller && (mappingEnded || savedSignInDecides);
  const reason =
    state.canAuthenticate === false
      ? state.accessUnavailable
        ? t('errors.integration.statusUnavailable')
        : t(getIntegrationReasonKey(state.ownershipReason))
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

  // State 4: the mapping runs after the accepted code
  if (mappingRunning) {
    const line = mapping
      ? describeEpicMappingRun(mapping, t)
      : t('signalr.epicMapping.codeAccepted');
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<EpicIcon size={28} className="icon-info" />}
          iconBackground="bg-themed-info"
          title={t('initialization.epicAuth.title')}
          description={line}
        />
        <ProgressBar value={mapping ? mapping.percent : 0} height="md" label={line} />
      </div>
    );
  }

  // State 5: the mapping failed or was canceled, or an earlier sign-in was saved without a finished mapping
  if (mappingFailed) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<EpicIcon size={28} className="icon-info" />}
          iconBackground="bg-themed-info"
          title={t('initialization.epicAuth.title')}
          description={t('initialization.epicAuth.subtitle')}
        />
        <Alert color="error">
          {mapping?.phase === 'cancelled'
            ? t('signalr.epicMapping.cancelled')
            : t('signalr.epicMapping.mappingFailed')}
        </Alert>
        <Button
          variant="filled"
          color="primary"
          onClick={actions.retryLogin}
          loading={state.loading}
          fullWidth
        >
          {t('common.retry')}
        </Button>
      </div>
    );
  }

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
                disabled={state.canAuthenticate === false}
              />
            )}
          </FormField>
        </div>

        {/* Error Display */}
        {reason && (
          <SignInReason
            reason={reason}
            onRetry={state.canAuthenticate === false ? () => void refreshStatus() : undefined}
          />
        )}
        {error && <Alert color="error">{error}</Alert>}

        {/* Action Buttons */}
        <div className="setup-actions">
          <Button
            variant="default"
            onClick={handleRetry}
            disabled={state.loading && !state.awaitingEnding}
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
        <SignInReason
          reason={reason}
          onRetry={state.canAuthenticate === false ? () => void refreshStatus() : undefined}
        />
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
