import { useEffect, useRef, useState } from 'react';
import { SteamPersistentLogin } from './login/SteamPersistentLogin';
import { EpicPersistentLogin } from './login/EpicPersistentLogin';
import { XboxPersistentLogin } from './login/XboxPersistentLogin';
import { SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS } from './constants';
import { getPersistentServiceId } from './scheduledPrefillPlatformUi';
import {
  hasUnconsumedLoginAttempt,
  usePersistentLoginRequestNonce,
  usePersistentLoginStoreState
} from './persistentLoginStore';
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
  const serviceId = getPersistentServiceId(serviceKey);
  const loginState = usePersistentLoginStoreState(serviceId);
  // Subscribed for its re-render alone, not its value: hasUnconsumedLoginAttempt() below is a
  // plain Map peek, not itself reactive. Without this subscription, a "Log in" click that bumps
  // the nonce without changing persistentLoginTarget (the target was already this service - a
  // dismissed-but-pending challenge, or a retry after a wedge) would never make this component
  // re-render, so autoStart would stay on its stale value and the login component's own nonce
  // effect would see a stale `false` and silently skip beginLogin().
  const loginAttemptNonce = usePersistentLoginRequestNonce(serviceId);
  // Structural (not incidental) explicit-click-only gate: this host only exists to bridge two
  // legitimate triggers into the login components' own autoStart effect - an explicit "Log in"
  // click (bumps the nonce these components consume exactly once - see
  // `requestPersistentLoginAttempt`/`consumeLoginAttemptNonce`) or a reconcile-confirmed cached
  // challenge already applied to the store (`pendingChallenge !== null` - see
  // `reconcilePersistentLoginFromServer`). `persistentLoginTarget` is in fact only ever set to this
  // service by those same two callers, so this mirrors the current invariant exactly; deriving it
  // here instead of hardcoding `true` means a future caller that sets the target without either
  // signal gets a visible "Log in" button (the login components' own showLoginButton fallback)
  // instead of silently auto-firing a real daemon login.
  const autoStart =
    loginState.pendingChallenge !== null || hasUnconsumedLoginAttempt(serviceId, loginAttemptNonce);
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
        autoStart={autoStart}
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
        autoStart={autoStart}
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
        autoStart={autoStart}
        onDismiss={handleDismiss}
      />
    );
  }

  return null;
}
