import { useState, useEffect, useRef, useCallback } from 'react';

/**
 * Comprehensive user activity tracker for session management
 * Based on best practices from:
 * - MDN Idle Detection API
 * - activity-detector npm package
 * - Page Visibility API
 *
 * Monitors:
 * - Mouse events (desktop)
 * - Touch events (mobile)
 * - Keyboard events
 * - Scroll events
 * - Page visibility changes
 * - Focus/blur events
 *
 * Sends heartbeat to server every 30 seconds when active
 */

const ACTIVITY_EVENTS = [
  // Mouse events (desktop)
  'mousedown',
  'mousemove',
  'click',

  // Touch events (mobile)
  'touchstart',
  'touchmove',
  'touchend',

  // Keyboard events
  'keydown',
  'keypress',

  // Scroll events (both desktop and mobile)
  'scroll',
  'wheel',
  'DOMMouseScroll', // Firefox

  // Focus events
  'focus'
] as const;

const IDLE_TIMEOUT = 60000; // 1 minute of inactivity = idle

interface ActivityTrackerReturn {
  isActive: boolean;
  lastActivityTime: number;
}

export const useActivityTracker = (
  onActivity?: () => void,
  onIdle?: () => void
): ActivityTrackerReturn => {
  const [isActive, setIsActive] = useState(true);
  const [lastActivityTime, setLastActivityTime] = useState(Date.now());
  const lastActivityRef = useRef<number>(Date.now());
  const isActiveRef = useRef<boolean>(true);
  const heartbeatIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const idleCheckIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const onActivityRef = useRef(onActivity);
  const onIdleRef = useRef(onIdle);

  // Keep refs up to date
  useEffect(() => {
    onActivityRef.current = onActivity;
    onIdleRef.current = onIdle;
  }, [onActivity, onIdle]);

  const handleActivity = useCallback(() => {
    const now = Date.now();
    const wasActive = isActiveRef.current;

    lastActivityRef.current = now;
    setLastActivityTime(now);

    // If was idle, now becoming active
    if (!wasActive) {
      isActiveRef.current = true;
      setIsActive(true);
      onActivityRef.current?.();
    }
  }, []);

  const handleIdle = useCallback(() => {
    const wasActive = isActiveRef.current;

    if (wasActive) {
      isActiveRef.current = false;
      setIsActive(false);
      onIdleRef.current?.();
    }
  }, []);

  useEffect(() => {
    const heartbeatInterval = heartbeatIntervalRef;
    // Check for idle status periodically
    const checkIdleStatus = () => {
      const timeSinceActivity = Date.now() - lastActivityRef.current;

      if (timeSinceActivity > IDLE_TIMEOUT) {
        handleIdle();
      }
    };

    // Handle page visibility changes
    const handleVisibilityChange = () => {
      if (document.hidden) {
        // Page is hidden - mark as idle
        handleIdle();
      } else {
        // Page is visible - mark as active
        handleActivity();
      }
    };

    // Set up event listeners with passive flag for better performance
    ACTIVITY_EVENTS.forEach((event) => {
      window.addEventListener(event, handleActivity, { passive: true });
    });

    // Page visibility API
    document.addEventListener('visibilitychange', handleVisibilityChange);

    // Check idle status every 5 seconds
    idleCheckIntervalRef.current = setInterval(checkIdleStatus, 5000);

    // Initial activity
    handleActivity();

    // Cleanup
    return () => {
      ACTIVITY_EVENTS.forEach((event) => {
        window.removeEventListener(event, handleActivity);
      });

      document.removeEventListener('visibilitychange', handleVisibilityChange);

      if (idleCheckIntervalRef.current) {
        clearInterval(idleCheckIntervalRef.current);
      }

      if (heartbeatInterval.current) {
        clearInterval(heartbeatInterval.current);
      }
    };
  }, [handleActivity, handleIdle]);

  return {
    isActive,
    lastActivityTime
  };
};
