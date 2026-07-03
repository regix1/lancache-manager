import { useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { usePersistentXboxAuth } from '@hooks/usePersistentXboxAuth';

interface XboxPersistentLoginProps {
  isRunning: boolean;
  isAuthenticated: boolean;
  onAuthenticated: () => void;
  autoStart?: boolean;
  onDismiss?: () => void;
}

export function XboxPersistentLogin({
  isRunning,
  isAuthenticated,
  onAuthenticated,
  autoStart = false,
  onDismiss
}: XboxPersistentLoginProps) {
  const { t } = useTranslation();
  const { state, actions, startLogin, dismissModal, resumeModal } = usePersistentXboxAuth();
  const handledAuthenticatedRef = useRef(false);
  const startInFlightRef = useRef(false);
  const autoStartedRef = useRef(false);

  useEffect(() => {
    if (!state.authenticated) {
      handledAuthenticatedRef.current = false;
      return;
    }

    if (handledAuthenticatedRef.current) {
      return;
    }

    handledAuthenticatedRef.current = true;
    onAuthenticated();
  }, [state.authenticated, onAuthenticated]);

  const beginLogin = useCallback(async () => {
    if (startInFlightRef.current) {
      return;
    }

    if (state.hasChallenge) {
      // Resume: a challenge is already pending for this service - just reveal it, no new login.
      resumeModal();
      return;
    }

    startInFlightRef.current = true;
    resumeModal(); // reveal the modal immediately in its "contacting daemon" state
    try {
      await startLogin();
    } finally {
      startInFlightRef.current = false;
    }
  }, [resumeModal, startLogin, state.hasChallenge]);

  useEffect(() => {
    if (!autoStart || !isRunning || isAuthenticated || autoStartedRef.current) {
      return;
    }

    autoStartedRef.current = true;
    void beginLogin();
  }, [autoStart, beginLogin, isAuthenticated, isRunning]);

  const authModalOpened =
    !state.dismissed && !state.authenticated && (state.loading || state.hasChallenge);
  const showLoginButton = isRunning && !isAuthenticated && !autoStart && !authModalOpened;

  return (
    <>
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
      <XboxAuthModal
        opened={authModalOpened}
        onClose={dismissModal}
        state={state}
        actions={actions}
        dismissBehavior="keep-pending"
        onCancelLogin={onDismiss}
      />
    </>
  );
}
