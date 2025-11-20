import { useEffect, useRef } from 'react';
import ApiService from '@services/api.service';

/**
 * Hook to track user activity and send heartbeat to server
 * Detects mouse movement, clicks, keyboard input, and scrolling
 * Sends heartbeat every 3 seconds when user is active
 * Marks user inactive after 1 minute of no activity
 */
export const useActivityTracker = () => {
  const lastActivityRef = useRef<number>(Date.now());
  const heartbeatIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const isActiveRef = useRef<boolean>(true);

  useEffect(() => {
    // Track user activity
    const updateActivity = () => {
      lastActivityRef.current = Date.now();
      isActiveRef.current = true;
    };

    // Listen for various user interactions
    const events = ['mousemove', 'mousedown', 'keydown', 'scroll', 'touchstart', 'click'];

    events.forEach((event) => {
      window.addEventListener(event, updateActivity, { passive: true });
    });

    // Send heartbeat to server every 3 seconds if user is active
    const sendHeartbeat = async () => {
      const timeSinceLastActivity = Date.now() - lastActivityRef.current;

      // Only send heartbeat if user was active in the last 60 seconds (1 minute)
      if (timeSinceLastActivity <= 60000) {
        try {
          // Send heartbeat to update lastSeenAt on server
          await fetch('/api/auth/heartbeat', {
            method: 'POST',
            headers: ApiService.getHeaders()
          });
          isActiveRef.current = true;
        } catch (err) {
          // Silently fail - heartbeat is non-critical
          console.debug('[ActivityTracker] Heartbeat failed:', err);
        }
      } else {
        isActiveRef.current = false;
      }
    };

    // Start heartbeat interval - send every 3 seconds
    heartbeatIntervalRef.current = setInterval(sendHeartbeat, 3000);

    // Cleanup
    return () => {
      events.forEach((event) => {
        window.removeEventListener(event, updateActivity);
      });

      if (heartbeatIntervalRef.current) {
        clearInterval(heartbeatIntervalRef.current);
      }
    };
  }, []);

  return {
    isActive: isActiveRef.current
  };
};
