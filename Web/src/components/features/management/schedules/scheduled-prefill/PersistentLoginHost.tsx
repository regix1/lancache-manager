import { useEffect, useRef } from 'react';
import { SteamPersistentLogin } from './login/SteamPersistentLogin';
import { EpicPersistentLogin } from './login/EpicPersistentLogin';
import { XboxPersistentLogin } from './login/XboxPersistentLogin';
import type { ScheduledPrefillServiceKey } from './types';

interface PersistentLoginHostProps {
  serviceKey: ScheduledPrefillServiceKey;
  isRunning: boolean;
  isAuthenticated: boolean;
  onAuthenticated: () => void;
  onDismiss: () => void;
}

/**
 * Hidden host that opens the persistent-container login modal when the parent
 * requests interactive login (after start or game selection). No visible button.
 */
export function PersistentLoginHost({
  serviceKey,
  isRunning,
  isAuthenticated,
  onAuthenticated,
  onDismiss
}: PersistentLoginHostProps) {
  const dismissedRef = useRef(false);

  useEffect(() => {
    dismissedRef.current = false;
  }, [serviceKey]);

  const handleAuthenticated = () => {
    onAuthenticated();
    onDismiss();
  };

  const handleDismiss = () => {
    if (!dismissedRef.current) {
      dismissedRef.current = true;
      onDismiss();
    }
  };

  if (!isRunning || isAuthenticated) {
    return null;
  }

  if (serviceKey === 'steam') {
    return (
      <SteamPersistentLogin
        isRunning={isRunning}
        isAuthenticated={isAuthenticated}
        onAuthenticated={handleAuthenticated}
        autoStart
        onDismiss={handleDismiss}
      />
    );
  }

  if (serviceKey === 'epic') {
    return (
      <EpicPersistentLogin
        isRunning={isRunning}
        isAuthenticated={isAuthenticated}
        onAuthenticated={handleAuthenticated}
        autoStart
        onDismiss={handleDismiss}
      />
    );
  }

  if (serviceKey === 'xbox') {
    return (
      <XboxPersistentLogin
        isRunning={isRunning}
        isAuthenticated={isAuthenticated}
        onAuthenticated={handleAuthenticated}
        autoStart
        onDismiss={handleDismiss}
      />
    );
  }

  return null;
}
