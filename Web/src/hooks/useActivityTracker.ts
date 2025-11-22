import { useEffect, useRef } from 'react';
import ApiService from '@services/api.service';
import authService from '@services/auth.service';

/**
 * Hook to track user activity and send heartbeat to server
 * Detects mouse movement, clicks, keyboard input, scrolling, and touch gestures (mobile)
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
    // Desktop: mousemove, mousedown, keydown, scroll, click
    // Mobile: touchstart, touchmove, touchend, scroll
    const events = ['mousemove', 'mousedown', 'keydown', 'scroll', 'touchstart', 'touchmove', 'touchend', 'click'];

    events.forEach((event) => {
      window.addEventListener(event, updateActivity, { passive: true });
    });

    // Send heartbeat to server every 3 seconds if user is active
    const sendHeartbeat = async () => {
      const timeSinceLastActivity = Date.now() - lastActivityRef.current;

      // Only send heartbeat if user was active in the last 60 seconds (1 minute)
      if (timeSinceLastActivity <= 60000) {
        try {
          // Skip heartbeat for unauthenticated or expired sessions
          // Send heartbeats for both authenticated users and active guest sessions
          if (authService.authMode === 'unauthenticated' || authService.authMode === 'expired') {
            return;
          }

          // Get device ID for session heartbeat
          const deviceId = authService.getDeviceId();

          // If no device ID, skip heartbeat (session not established yet or already cleared)
          if (!deviceId) {
            return;
          }

          // Send heartbeat to update lastSeenAt on server
          // RESTful endpoint: PATCH /api/sessions/{id}/last-seen
          const response = await fetch(`/api/sessions/${deviceId}/last-seen`, {
            method: 'PATCH',
            headers: ApiService.getHeaders()
          });

          // If unauthorized (401), device was revoked - trigger auth check
          if (response.status === 401) {
            console.warn('[ActivityTracker] Device unauthorized - triggering auth state refresh');
            authService.handleUnauthorized();
            return;
          }

          // If not found (404), session doesn't exist - stop sending heartbeats
          if (response.status === 404) {
            console.debug('[ActivityTracker] Session not found - may have been revoked');
            return;
          }

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
