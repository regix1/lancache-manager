import { useEffect, useRef, useState } from 'react';
import { SteamPersistentLogin } from './login/SteamPersistentLogin';
import { EpicPersistentLogin } from './login/EpicPersistentLogin';
import { XboxPersistentLogin } from './login/XboxPersistentLogin';
import { getPersistentServiceId } from './scheduledPrefillPlatformUi';
import { usePersistentLoginStoreState } from './persistentLoginStore';
import { SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS } from './constants';
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
  const serviceId = getPersistentServiceId(serviceKey);
  const loginState = usePersistentLoginStoreState(serviceId);
  const hasActiveLogin = loginState.loading || loginState.pendingChallenge !== null;
  const [stableRunning, setStableRunning] = useState(isRunning);

  useEffect(() => {
    dismissedRef.current = false;
  }, [serviceKey]);

  useEffect(() => {
    if (isRunning) {
      setStableRunning(true);
      return;
    }

    if (!hasActiveLogin) {
      setStableRunning(false);
      return;
    }

    const timer = setTimeout(
      () => setStableRunning(false),
      SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS
    );
    return () => clearTimeout(timer);
  }, [isRunning, hasActiveLogin]);

  useEffect(() => {
    if (isAuthenticated && !dismissedRef.current) {
      dismissedRef.current = true;
      onDismiss();
    }
  }, [isAuthenticated, onDismiss]);

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

  if (!stableRunning || isAuthenticated) {
    return null;
  }

  if (serviceKey === 'steam') {
    return (
      <SteamPersistentLogin
        isRunning={stableRunning}
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
        isRunning={stableRunning}
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
        isRunning={stableRunning}
        isAuthenticated={isAuthenticated}
        onAuthenticated={handleAuthenticated}
        autoStart
        onDismiss={handleDismiss}
      />
    );
  }

  return null;
}
