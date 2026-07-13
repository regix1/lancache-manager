import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { usePersistentXboxAuth } from '@hooks/usePersistentXboxAuth';
import { usePersistentLoginHost, type PersistentLoginHostProps } from './usePersistentLoginHost';

export function XboxPersistentLogin({
  isRunning,
  isAuthenticated,
  onAuthenticated,
  autoStart = false,
  onDismiss
}: PersistentLoginHostProps) {
  const { state, actions, startLogin, dismissModal, resumeModal } = usePersistentXboxAuth();
  const authModalOpened = usePersistentLoginHost({
    service: 'Xbox',
    state,
    startLogin,
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
    <XboxAuthModal
      opened={authModalOpened}
      onClose={dismissModal}
      state={state}
      actions={actions}
      dismissBehavior="keep-pending"
      onCancelLogin={onDismiss}
    />
  );
}
