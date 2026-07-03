import { useEffect, useRef, useState } from 'react';
import { SteamPersistentLogin } from './login/SteamPersistentLogin';
import { EpicPersistentLogin } from './login/EpicPersistentLogin';
import { XboxPersistentLogin } from './login/XboxPersistentLogin';
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
  const [stableRunning, setStableRunning] = useState(isRunning);

  useEffect(() => {
    dismissedRef.current = false;
  }, [serviceKey]);

  // Grace period applies unconditionally on every reported stop, regardless of whether a login is
  // active (diagnostic §3 fix direction) - a single transient container-list refresh reporting
  // isRunning=false must never itself unmount the login children, since that remount is exactly
  // what re-triggers diagnostic §2's automatic login wedge. The grace timer alone decides whether
  // a stop is real; it is cancelled below the moment isRunning flips back to true.
  useEffect(() => {
    if (isRunning) {
      setStableRunning(true);
      return;
    }

    const timer = setTimeout(
      () => setStableRunning(false),
      SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS
    );
    return () => clearTimeout(timer);
  }, [isRunning]);

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
