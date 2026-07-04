import { useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePersistentSteamAuth } from '@hooks/usePersistentSteamAuth';
import { consumeLoginAttemptNonce, usePersistentLoginRequestNonce } from '../persistentLoginStore';

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
  const loginRequestNonce = usePersistentLoginRequestNonce('Steam');
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
    // eslint-disable-next-line no-console
    console.log('[SteamAuthDebug] persistent Steam beginLogin() clicked', {
      startInFlight: startInFlightRef.current,
      hasChallenge: state.hasChallenge,
      authenticated: state.authenticated,
      loading: state.loading
    });
    if (startInFlightRef.current) {
      return;
    }

    if (state.hasChallenge) {
      // Resume: a challenge is already pending for this service - just reveal it, no new login.
      // eslint-disable-next-line no-console
      console.log(
        '[SteamAuthDebug] persistent Steam beginLogin: resume existing challenge (no new login)'
      );
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
  }, [actions, resumeModal, state.hasChallenge, state.authenticated, state.loading]);

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

    if (!consumeLoginAttemptNonce('Steam', loginRequestNonce)) {
      return;
    }

    void beginLogin();
  }, [autoStart, beginLogin, isAuthenticated, isRunning, loginRequestNonce]);

  const authModalOpened =
    !state.dismissed && !state.authenticated && (state.loading || state.hasChallenge);
  const showLoginButton = isRunning && !isAuthenticated && !autoStart && !authModalOpened;

  // eslint-disable-next-line no-console
  console.log('[SteamAuthDebug] persistent Steam render', {
    isRunning,
    isAuthenticated,
    autoStart,
    authModalOpened,
    showLoginButton,
    'state.loading': state.loading,
    'state.hasChallenge': state.hasChallenge,
    'state.authenticated': state.authenticated,
    'state.dismissed': state.dismissed
  });

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
