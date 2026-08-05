import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, ExternalLink, KeyRound, Shield } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { XboxIcon } from '@components/ui/XboxIcon';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useXboxMappingAuth } from '@hooks/useXboxMappingAuth';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';

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
  const [succeeded, setSucceeded] = useState(false);

  const handleSuccess = () => {
    setSucceeded(true);
  };

  const handleError = (message: string) => {
    setError(message);
  };

  const { state, actions, startLogin, cancelLogin } = useXboxMappingAuth({
    onSuccess: handleSuccess,
    onError: handleError
  });

  useEffect(() => {
    const checkExistingAuth = async () => {
      try {
        const status = await ApiService.getXboxMappingAuthStatus();
        if (status.isAuthenticated) {
          setSucceeded(true);
        }
      } catch (err) {
        // Background mount check - if it fails the user just sees the normal (unauthenticated)
        // auth step instead of the already-authenticated shortcut. Explicit silent background.
        console.error('[XboxAuthStep] Failed to check auth status:', getErrorMessage(err));
      }
    };

    checkExistingAuth();
  }, []);

  useEffect(() => {
    // Xbox has no code-paste step: once the device code is issued the server actively polls
    // Microsoft, so that wait counts as busy too, not just the initial request for the code.
    onAuthStateChange?.(state.loading || state.needsDeviceCode);
    return () => {
      onAuthStateChange?.(false);
    };
  }, [state.loading, state.needsDeviceCode, onAuthStateChange]);

  useEffect(() => {
    if (succeeded) {
      const timer = setTimeout(() => {
        onComplete();
      }, 1500);
      return () => clearTimeout(timer);
    }
  }, [succeeded, onComplete]);

  // The unmount cleanup below keeps its mount-time closure, so the pending flags are mirrored
  // into refs that every render refreshes. Depending on them directly instead would re-run the
  // cleanup on the true-to-false success transition and cancel a login that had just completed.
  const needsDeviceCodeRef = useRef(state.needsDeviceCode);
  const loadingRef = useRef(state.loading);
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
        void cancelLogin();
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleStartLogin = async () => {
    setError(null);
    await startLogin();
  };

  const handleBackFromDeviceCode = () => {
    void cancelLogin();
    actions.resetAuthForm();
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
            {t('initialization.xboxAuth.success')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.xboxAuth.successSubtitle')}
          </p>
        </div>
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
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
            <KeyRound className="w-7 h-7 icon-info" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">
            {t('initialization.xboxAuth.deviceCodeTitle')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.xboxAuth.deviceCodeSubtitle')}
          </p>
        </div>

        {/* Device Code */}
        <div className="text-center">
          <label className="form-field-label">{t('initialization.xboxAuth.userCodeLabel')}</label>
          <div className="px-3 py-2.5 rounded-lg bg-themed-tertiary font-mono text-xl font-bold tracking-widest text-themed-primary select-all">
            {state.deviceUserCode}
          </div>
        </div>

        {/* Open Microsoft's verification page */}
        <a
          href={state.deviceVerificationUri}
          target="_blank"
          rel="noopener noreferrer"
          className="flex items-center justify-center gap-2 w-full px-4 py-2 bg-themed-tertiary hover:bg-themed-hover text-themed-primary border border-themed-secondary themed-button-radius font-medium smooth-transition"
        >
          <ExternalLink className="w-4 h-4" />
          {t('initialization.xboxAuth.openVerificationLink')}
        </a>

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
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <XboxIcon size={28} className="icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.xboxAuth.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.xboxAuth.subtitle')}
        </p>
      </div>

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
          ? t('initialization.xboxAuth.connecting')
          : t('initialization.xboxAuth.connectButton')}
      </Button>

      {/* Skip */}
      <div className="text-center">
        <button
          onClick={onSkip}
          disabled={state.loading}
          className="text-sm text-themed-muted hover:text-themed-secondary smooth-transition disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {t('initialization.xboxAuth.skipNote')}
        </button>
      </div>
    </div>
  );
};
