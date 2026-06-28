import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePersistentSteamAuth } from '@hooks/usePersistentSteamAuth';

interface SteamPersistentLoginProps {
  isRunning: boolean;
  isAuthenticated: boolean;
  onAuthenticated: () => void;
  autoStart?: boolean;
  onDismiss?: () => void;
}

export function SteamPersistentLogin({
  isRunning,
  isAuthenticated,
  onAuthenticated,
  autoStart = false,
  onDismiss
}: SteamPersistentLoginProps) {
  const { t } = useTranslation();
  const { state, actions } = usePersistentSteamAuth();
  const [authModalOpened, setAuthModalOpened] = useState(false);
  const handledAuthenticatedRef = useRef(false);
  const startInFlightRef = useRef(false);
  const autoStartedRef = useRef(false);
  const showLoginButton = isRunning && !isAuthenticated && !autoStart;

  useEffect(() => {
    if (!state.authenticated) {
      handledAuthenticatedRef.current = false;
      return;
    }

    if (handledAuthenticatedRef.current) {
      return;
    }

    handledAuthenticatedRef.current = true;
    setAuthModalOpened(false);
    onAuthenticated();
  }, [state.authenticated, onAuthenticated]);

  const beginLogin = () => {
    if (startInFlightRef.current) {
      return;
    }

    startInFlightRef.current = true;
    setAuthModalOpened(true);
    void actions.start().finally(() => {
      startInFlightRef.current = false;
    });
  };

  useEffect(() => {
    if (!autoStart || !isRunning || isAuthenticated || autoStartedRef.current) {
      return;
    }

    autoStartedRef.current = true;
    beginLogin();
  }, [autoStart, isRunning, isAuthenticated]);

  const handleLoginClick = () => {
    beginLogin();
  };

  const handleAuthModalClose = () => {
    if (!state.loading) {
      setAuthModalOpened(false);
      actions.resetAuthForm();
      onDismiss?.();
    }
  };

  return (
    <>
      {state.error && (
        <Alert color="red" className="scheduled-prefill-service-row__auth-alert">
          {t('prefill.persistent.loginFailed', { error: state.error })}
        </Alert>
      )}
      {showLoginButton && (
        <Button
          type="button"
          variant="filled"
          color="blue"
          size="sm"
          onClick={handleLoginClick}
          loading={state.loading}
        >
          {state.loading ? t('prefill.persistent.authenticating') : t('prefill.persistent.logIn')}
        </Button>
      )}
      <SteamAuthModal
        opened={authModalOpened}
        onClose={handleAuthModalClose}
        state={state}
        actions={actions}
        isPrefillMode={true}
      />
    </>
  );
}
