import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, ExternalLink, KeyRound, Shield } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { XboxIcon } from '@components/ui/XboxIcon';
import { StepHeader } from '@components/initialization/StepHeader';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useXboxMappingAuth } from '@hooks/useXboxMappingAuth';
import { getIntegrationReasonKey } from '../../../types';

interface XboxAuthStepProps {
  onComplete: () => void;
  onSkip: () => void;
  onAuthStateChange?: (busy: boolean) => void;
}

export const XboxAuthStep: React.FC<XboxAuthStepProps> = ({
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

  const { state, actions, startLogin, cancelLogin, authStatus, identity } = useXboxMappingAuth({
    onSuccess: handleSuccess,
    onError: handleError
  });

  const succeededForCaller =
    succeeded === identity || (authStatus?.canManage === true && authStatus.isAuthenticated);
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
    // Xbox has no code-paste step: once the device code is issued the server actively polls
    // Microsoft, so that wait counts as busy too, not just the initial request for the code.
    onAuthStateChange?.(state.loading || state.needsDeviceCode);
    return () => {
      onAuthStateChange?.(false);
    };
  }, [state.loading, state.needsDeviceCode, onAuthStateChange]);

  useEffect(() => {
    if (succeededForCaller) {
      const timer = setTimeout(() => {
        onComplete();
      }, 1500);
      return () => clearTimeout(timer);
    }
  }, [succeededForCaller, onComplete]);

  // The unmount cleanup below keeps its mount-time closure, so the pending flags are mirrored
  // into refs that every render refreshes. Depending on them directly instead would re-run the
  // cleanup on the true-to-false success transition and cancel a login that had just completed.
  const needsDeviceCodeRef = useRef(state.needsDeviceCode);
  const loadingRef = useRef(state.loading);
  const cancelLoginRef = useRef(cancelLogin);
  cancelLoginRef.current = cancelLogin;
  useEffect(() => {
    needsDeviceCodeRef.current = state.needsDeviceCode;
    loadingRef.current = state.loading;
  });

  useEffect(() => {
    // If the wizard closes or unmounts while the start-login request is in flight or a
    // device-code poll is still pending, tell the server to stop polling Microsoft instead of
    // leaving it running until the code expires. The request window counts because the server
    // has already asked Microsoft for a code by the time the response comes back.
    return () => {
      if (loadingRef.current || needsDeviceCodeRef.current) {
        void cancelLoginRef.current();
      }
    };
  }, []);

  const handleStartLogin = async () => {
    if (state.canAuthenticate === false) return;
    setError(null);
    await startLogin();
  };

  const handleBackFromDeviceCode = () => {
    void cancelLogin();
    actions.resetAuthForm();
  };

  // State 3: Success
  if (succeededForCaller) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<CheckCircle className="w-7 h-7 icon-success" />}
          iconBackground="bg-themed-success"
          title={t('initialization.xboxAuth.success')}
          description={t('initialization.xboxAuth.successSubtitle')}
        />
        <div className="flex justify-center">
          <LoadingSpinner inline size="md" className="text-themed-secondary" />
        </div>
      </div>
    );
  }

  // State 2: waiting for the user to approve the device code on Microsoft's site
  if (state.needsDeviceCode) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<KeyRound className="w-7 h-7 icon-info" />}
          iconBackground="bg-themed-info"
          title={t('initialization.xboxAuth.deviceCodeTitle')}
          description={t('initialization.xboxAuth.deviceCodeSubtitle')}
        />

        {/* Device Code */}
        <div className="text-center">
          <label className="form-field-label">{t('initialization.xboxAuth.userCodeLabel')}</label>
          <div className="px-3 py-2.5 rounded-lg bg-themed-tertiary font-mono text-xl font-bold tracking-widest text-themed-primary select-all">
            {state.deviceUserCode}
          </div>
        </div>

        {/* Open Microsoft's verification page */}
        <a
          href={state.canAuthenticate === false ? undefined : state.deviceVerificationUri}
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
          {t('initialization.xboxAuth.openVerificationLink')}
        </a>

        {reason && (
          <p className="text-sm text-themed-muted" role="status">
            {reason}
          </p>
        )}

        {/* Waiting for approval */}
        <div className="flex items-center justify-center gap-2 text-themed-muted">
          <LoadingSpinner inline size="sm" />
          <span className="text-sm">{t('initialization.xboxAuth.waitingMessage')}</span>
        </div>

        {/* Back */}
        <Button variant="default" onClick={handleBackFromDeviceCode} fullWidth>
          {t('initialization.xboxAuth.back')}
        </Button>
      </div>
    );
  }

  // State 1: Initial
  return (
    <div className="space-y-5">
      <StepHeader
        icon={<XboxIcon size={28} className="icon-info" />}
        iconBackground="bg-themed-info"
        title={t('initialization.xboxAuth.title')}
        description={t('initialization.xboxAuth.subtitle')}
      />

      {/* Security Note */}
      <div className="p-3 rounded-lg bg-themed-tertiary">
        <div className="flex items-start gap-2">
          <Shield className="w-4 h-4 flex-shrink-0 mt-0.5 icon-success" />
          <p className="text-sm text-themed-secondary">
            {t('initialization.xboxAuth.securityNote')}
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
          ? t('initialization.xboxAuth.connecting')
          : t('initialization.xboxAuth.connectButton')}
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
          {t('initialization.xboxAuth.skipNote')}
        </Button>
      </div>
    </div>
  );
};
