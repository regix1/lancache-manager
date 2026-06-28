import { useCallback, useEffect, useRef, useState } from 'react';
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

  const beginLogin = useCallback(async () => {
    if (startInFlightRef.current) {
      return;
    }

    startInFlightRef.current = true;
    try {
      const challenge = await actions.start();
      if (challenge) {
        setAuthModalOpened(true);
      }
    } finally {
      startInFlightRef.current = false;
    }
  }, [actions]);

  useEffect(() => {
    if (!autoStart || !isRunning || isAuthenticated || autoStartedRef.current) {
      return;
    }

    autoStartedRef.current = true;
    void beginLogin();
  }, [autoStart, beginLogin, isAuthenticated, isRunning]);

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
          onClick={() => void beginLogin()}
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
