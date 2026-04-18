import { useState, useEffect, useRef, useCallback } from 'react';

/**
 * User activity tracker for session presence.
 *
 * Monitors mouse/touch/keyboard/scroll/focus plus the Page Visibility API.
 * Fires a lightweight POST /api/auth/heartbeat every HEARTBEAT_INTERVAL_MS
 * while the tab is active so the server's LastSeenAtUtc stays fresh and the
 * presence dot (active / away / inactive) does not flicker when the user
 * clicks without triggering other API traffic.
 *
 * Based on patterns from the Page Visibility API docs and the activity-detector
 * npm package: throttle high-frequency events, debounce visibility transitions,
 * and treat visibilitychange as a *hint*, not an immediate state flip.
 */

const ACTIVITY_EVENTS = [
  'mousedown',
  'mousemove',
  'click',
  'touchstart',
  'touchmove',
  'touchend',
  'keydown',
  'keypress',
  'scroll',
  'wheel',
  'DOMMouseScroll',
  'focus'
] as const;

const IDLE_TIMEOUT_MS = 60_000;
const IDLE_CHECK_INTERVAL_MS = 5_000;
const ACTIVITY_THROTTLE_MS = 500;
const VISIBILITY_IDLE_DEBOUNCE_MS = 3_000;
const HEARTBEAT_INTERVAL_MS = 30_000;
const HEARTBEAT_URL = '/api/auth/heartbeat';

interface ActivityTrackerReturn {
  isActive: boolean;
  lastActivityTime: number;
}

const getApiBase = (): string => {
  if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
    return import.meta.env.VITE_API_URL as string;
  }
  return '';
};

const sendHeartbeat = (): void => {
  try {
    void fetch(`${getApiBase()}${HEARTBEAT_URL}`, {
      method: 'POST',
      credentials: 'include',
      cache: 'no-store',
      keepalive: true
    }).catch(() => {
      /* swallow — heartbeat is best-effort */
    });
  } catch {
    /* best-effort */
  }
};

export const useActivityTracker = (
  onActivity?: () => void,
  onIdle?: () => void
): ActivityTrackerReturn => {
  const [isActive, setIsActive] = useState<boolean>(true);
  const [lastActivityTime, setLastActivityTime] = useState<number>(() => Date.now());

  const lastActivityRef = useRef<number>(Date.now());
  const isActiveRef = useRef<boolean>(true);
  const lastEventDispatchRef = useRef<number>(0);
  const visibilityDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const idleCheckIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const heartbeatIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const onActivityRef = useRef<(() => void) | undefined>(onActivity);
  const onIdleRef = useRef<(() => void) | undefined>(onIdle);

  useEffect(() => {
    onActivityRef.current = onActivity;
    onIdleRef.current = onIdle;
  }, [onActivity, onIdle]);

  const goActive = useCallback((): void => {
    const wasActive = isActiveRef.current;
    if (!wasActive) {
      isActiveRef.current = true;
      setIsActive(true);
      setLastActivityTime(lastActivityRef.current);
      onActivityRef.current?.();
      sendHeartbeat();
    }
  }, []);

  const goIdle = useCallback((): void => {
    const wasActive = isActiveRef.current;
    if (wasActive) {
      isActiveRef.current = false;
      setIsActive(false);
      onIdleRef.current?.();
    }
  }, []);

  const handleActivity = useCallback((): void => {
    const now = Date.now();
    lastActivityRef.current = now;

    if (!isActiveRef.current) {
      goActive();
      return;
    }

    if (now - lastEventDispatchRef.current >= ACTIVITY_THROTTLE_MS) {
      lastEventDispatchRef.current = now;
      setLastActivityTime(now);
    }
  }, [goActive]);

  useEffect(() => {
    const checkIdleStatus = (): void => {
      if (!isActiveRef.current) return;
      const timeSinceActivity = Date.now() - lastActivityRef.current;
      if (timeSinceActivity > IDLE_TIMEOUT_MS) {
        goIdle();
      }
    };

    const clearVisibilityDebounce = (): void => {
      if (visibilityDebounceRef.current !== null) {
        clearTimeout(visibilityDebounceRef.current);
        visibilityDebounceRef.current = null;
      }
    };

    const handleVisibilityChange = (): void => {
      clearVisibilityDebounce();
      if (document.hidden) {
        visibilityDebounceRef.current = setTimeout(() => {
          visibilityDebounceRef.current = null;
          goIdle();
        }, VISIBILITY_IDLE_DEBOUNCE_MS);
        return;
      }
      lastActivityRef.current = Date.now();
      goActive();
    };

    const fireHeartbeatIfActive = (): void => {
      if (isActiveRef.current && !document.hidden) {
        sendHeartbeat();
      }
    };

    ACTIVITY_EVENTS.forEach((event) => {
      window.addEventListener(event, handleActivity, { passive: true });
    });
    document.addEventListener('visibilitychange', handleVisibilityChange);

    idleCheckIntervalRef.current = setInterval(checkIdleStatus, IDLE_CHECK_INTERVAL_MS);
    heartbeatIntervalRef.current = setInterval(fireHeartbeatIfActive, HEARTBEAT_INTERVAL_MS);

    lastActivityRef.current = Date.now();
    lastEventDispatchRef.current = Date.now();
    sendHeartbeat();

    return () => {
      ACTIVITY_EVENTS.forEach((event) => {
        window.removeEventListener(event, handleActivity);
      });
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      clearVisibilityDebounce();
      if (idleCheckIntervalRef.current !== null) {
        clearInterval(idleCheckIntervalRef.current);
        idleCheckIntervalRef.current = null;
      }
      if (heartbeatIntervalRef.current !== null) {
        clearInterval(heartbeatIntervalRef.current);
        heartbeatIntervalRef.current = null;
      }
    };
  }, [handleActivity, goActive, goIdle]);

  return {
    isActive,
    lastActivityTime
  };
};
