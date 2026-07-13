import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePersistentSteamAuth } from '@hooks/usePersistentSteamAuth';
import { usePersistentLoginHost, type PersistentLoginHostProps } from './usePersistentLoginHost';

export function SteamPersistentLogin({
  isRunning,
  isAuthenticated,
  onAuthenticated,
  autoStart = false,
  onDismiss
}: PersistentLoginHostProps) {
  const { state, actions, dismissModal, resumeModal } = usePersistentSteamAuth();
  const authModalOpened = usePersistentLoginHost({
    service: 'Steam',
    state,
    startLogin: actions.start,
    resumeModal,
    isRunning,
    isAuthenticated,
    onAuthenticated,
    autoStart
  });

  // This host is invisible by design - the card's own "Log in" button is the only entry point, and
  // it always drives an autoStart. So only the modal renders here; there is no on-card fallback
  // button (an earlier one leaked onto the collapsed schedule card behind this modal on dismiss).
  return (
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
  );
}
