import { useEffect, useRef } from 'react';
import { useSignalR } from './index';
import { useAuth } from '@contexts/AuthContext';
import authService from '@services/auth.service';
import preferencesService from '@services/preferences.service';

/**
 * Hook that sets up SignalR listeners for user session synchronization.
 * Handles preferences updates, preference resets, and session clearing across devices.
 */
export const useSessionSync = () => {
  const signalR = useSignalR();
  const { refreshAuth } = useAuth();

  // Use ref to avoid dependency issues in SignalR subscriptions
  const refreshAuthRef = useRef(refreshAuth);

  // Keep ref up to date
  useEffect(() => {
    refreshAuthRef.current = refreshAuth;
  }, [refreshAuth]);

  useEffect(() => {
    if (!signalR) return;

    console.log('[useSessionSync] Setting up session sync SignalR listeners');

    // Use a ref to track if we're already processing to prevent duplicate handling
    let isProcessingUpdate = false;
    let isProcessingReset = false;

    const handlePreferencesUpdated = async (payload: any) => {
      if (isProcessingUpdate) {
        console.log('[useSessionSync] Already processing preferences update, skipping duplicate');
        return;
      }

      try {
        isProcessingUpdate = true;

        const { sessionId, preferences: newPreferences } = payload;

        // Check if this update is for the current user's session
        const deviceId = authService.getDeviceId();
        const guestSessionId = authService.getGuestSessionId();
        const currentSessionId = deviceId || guestSessionId;

        if (sessionId !== currentSessionId) {
          return;
        }

        console.log('[useSessionSync] Preferences updated for current session, reloading...');

        // Get old preferences before updating
        const oldPrefs = preferencesService.getPreferencesSync();

        // Parse new preferences
        const updatedPrefs = {
          selectedTheme: newPreferences.selectedTheme || null,
          sharpCorners: newPreferences.sharpCorners || false,
          disableFocusOutlines: newPreferences.disableFocusOutlines || false,
          disableTooltips: newPreferences.disableTooltips || false,
          picsAlwaysVisible: newPreferences.picsAlwaysVisible || false,
          hideAboutSections: newPreferences.hideAboutSections || false,
          disableStickyNotifications: newPreferences.disableStickyNotifications || false
        };

        // Update preferences service cache
        await preferencesService.loadPreferences();

        // Only dispatch events for preferences that actually changed
        if (oldPrefs) {
          Object.keys(updatedPrefs).forEach((key) => {
            const typedKey = key as keyof typeof updatedPrefs;
            if (oldPrefs[typedKey] !== updatedPrefs[typedKey]) {
              console.log(`[useSessionSync] Preference changed: ${key} = ${updatedPrefs[typedKey]}`);
              window.dispatchEvent(
                new CustomEvent('preference-changed', {
                  detail: { key, value: updatedPrefs[typedKey] }
                })
              );
            }
          });
        } else {
          // If no old prefs, dispatch all
          Object.keys(updatedPrefs).forEach((key) => {
            window.dispatchEvent(
              new CustomEvent('preference-changed', {
                detail: { key, value: updatedPrefs[key as keyof typeof updatedPrefs] }
              })
            );
          });
        }
      } finally {
        // Reset flag after a short delay
        setTimeout(() => {
          isProcessingUpdate = false;
        }, 500);
      }
    };

    const handlePreferencesReset = async () => {
      if (isProcessingReset) {
        console.log('[useSessionSync] Already processing preferences reset, skipping duplicate');
        return;
      }

      try {
        isProcessingReset = true;
        console.log('[useSessionSync] UserPreferencesReset event received');

        // Clear cached preferences
        preferencesService.clearCache();

        // Dispatch a custom event for themeService to handle
        window.dispatchEvent(new CustomEvent('preferences-reset'));
      } finally {
        // Reset flag after a delay to prevent duplicate processing
        setTimeout(() => {
          isProcessingReset = false;
        }, 2000);
      }
    };

    const handleSessionsCleared = async () => {
      console.log('[useSessionSync] UserSessionsCleared event received - forcing logout');

      // Clear all authentication data
      authService.clearAuthAndDevice();

      // Clear theme/preferences cache
      preferencesService.clearCache();

      // Refresh auth context to trigger authentication modal
      // This is the same pattern used by the logout button
      await refreshAuthRef.current();
    };

    signalR.on('UserPreferencesUpdated', handlePreferencesUpdated);
    signalR.on('UserPreferencesReset', handlePreferencesReset);
    signalR.on('UserSessionsCleared', handleSessionsCleared);

    console.log('[useSessionSync] Session sync SignalR listeners registered');

    return () => {
      console.log('[useSessionSync] Cleaning up session sync SignalR listeners');
      signalR.off('UserPreferencesUpdated', handlePreferencesUpdated);
      signalR.off('UserPreferencesReset', handlePreferencesReset);
      signalR.off('UserSessionsCleared', handleSessionsCleared);
    };
  }, [signalR]);
};
