import { useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { usePersistentXboxAuth } from '@hooks/usePersistentXboxAuth';
import { consumeLoginAttemptNonce, usePersistentLoginRequestNonce } from '../persistentLoginStore';

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
  const loginRequestNonce = usePersistentLoginRequestNonce('Xbox');
  const handledAuthenticatedRef = useRef(false);
  const startInFlightRef = useRef(false);

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

  // Fires once on mount (nonce 0 is never pre-consumed - see consumeLoginAttemptNonce), and again
  // on every later explicit "Log in" click (ScheduledPrefillConfigModal's
  // requestPersistentLoginAttempt bumps loginRequestNonce) even when the click didn't change
  // persistentLoginTarget's value and so didn't remount this component. The nonce is consumed in
  // the STORE (not a component ref), so a remount can never re-fire this for a nonce already acted
  // on - see the store's doc comment for the wedge this closes.
  useEffect(() => {
    if (!autoStart || !isRunning || isAuthenticated) {
      return;
    }

    if (!consumeLoginAttemptNonce('Xbox', loginRequestNonce)) {
      return;
    }

    void beginLogin();
  }, [autoStart, beginLogin, isAuthenticated, isRunning, loginRequestNonce]);

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
