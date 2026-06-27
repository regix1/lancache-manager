import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import { usePersistentEpicAuth } from '@hooks/usePersistentEpicAuth';

interface EpicPersistentLoginProps {
  isRunning: boolean;
  isAuthenticated: boolean;
  onAuthenticated: () => void;
}

export function EpicPersistentLogin({
  isRunning,
  isAuthenticated,
  onAuthenticated
}: EpicPersistentLoginProps) {
  const { t } = useTranslation();
  const { state, actions, startLogin } = usePersistentEpicAuth();
  const [authModalOpened, setAuthModalOpened] = useState(false);
  const handledAuthenticatedRef = useRef(false);
  const startInFlightRef = useRef(false);
  const showLoginButton = isRunning && !isAuthenticated;

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

  const handleLoginClick = () => {
    if (startInFlightRef.current) {
      return;
    }

    startInFlightRef.current = true;
    setAuthModalOpened(true);
    void startLogin().finally(() => {
      startInFlightRef.current = false;
    });
  };

  const handleAuthModalClose = () => {
    setAuthModalOpened(false);
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
      <EpicAuthModal
        opened={authModalOpened}
        onClose={handleAuthModalClose}
        state={state}
        actions={actions}
      />
    </>
  );
}
