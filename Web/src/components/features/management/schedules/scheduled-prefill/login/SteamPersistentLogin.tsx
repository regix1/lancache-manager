import { useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
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
  const { state, actions, dismissModal, resumeModal } = usePersistentSteamAuth();
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
      await actions.start();
    } finally {
      startInFlightRef.current = false;
    }
  }, [actions, resumeModal, state.hasChallenge]);

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
      <SteamAuthModal
        opened={authModalOpened}
        onClose={dismissModal}
        state={state}
        actions={actions}
        isPrefillMode={true}
        dismissBehavior="keep-pending"
        disableAutoLogoutClose
        awaitingChallenge={state.loading && !state.hasChallenge}
        onCancelLogin={onDismiss}
      />
    </>
  );
}
