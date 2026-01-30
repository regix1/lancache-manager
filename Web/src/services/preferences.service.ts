import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import type {
  UserSessionRevokedEvent,
  DefaultGuestThemeChangedEvent
} from '../contexts/SignalRContext/types';

// SignalR connection interface - handler needs to accept any args for compatibility
interface SignalRConnection {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (eventName: string, handler: (...args: any[]) => void) => void;
}

interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  disableStickyNotifications: boolean;
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  refreshRate?: string | null;
  allowedTimeFormats?: string[] | null;
}

/**
 * Default preferences used when API calls fail
 */
const DEFAULT_PREFERENCES: UserPreferences = {
  selectedTheme: null,
  sharpCorners: false,
  disableFocusOutlines: true,
  disableTooltips: false,
  picsAlwaysVisible: false,
  disableStickyNotifications: false,
  useLocalTimezone: false,
  use24HourFormat: true,
  showDatasourceLabels: true,
  showYearInDates: false,
  refreshRate: null,
  allowedTimeFormats: null
};

/**
 * PreferencesService - Pure API layer for user preferences
 *
 * This service handles:
 * - API communication for loading/saving preferences
 * - SignalR session events (logout-related, NOT preference updates)
 *
 * State management is handled by SessionPreferencesContext.
 * UserPreferencesUpdated events are handled by SessionPreferencesContext.
 */
class PreferencesService {
  private pendingUpdates: Map<string, Promise<boolean>> = new Map();

  /**
   * Load preferences from the API (no caching)
   */
  async loadPreferences(): Promise<UserPreferences> {
    try {
      const response = await fetch(`${API_BASE}/user-preferences`, {
        credentials: 'include',
        headers: authService.getAuthHeaders()
      });

      if (response.status === 401) {
        console.warn('[PreferencesService] Unauthorized - triggering logout');
        authService.handleUnauthorized();
        return DEFAULT_PREFERENCES;
      }

      if (response.ok) {
        const data = await response.json();
        return {
          selectedTheme: data.selectedTheme || null,
          sharpCorners: data.sharpCorners || false,
          disableFocusOutlines: data.disableFocusOutlines || false,
          disableTooltips: data.disableTooltips || false,
          picsAlwaysVisible: data.picsAlwaysVisible || false,
          disableStickyNotifications: data.disableStickyNotifications || false,
          useLocalTimezone: data.useLocalTimezone || false,
          use24HourFormat: data.use24HourFormat || false,
          showDatasourceLabels: data.showDatasourceLabels ?? true,
          showYearInDates: data.showYearInDates || false,
          refreshRate: data.refreshRate || null,
          allowedTimeFormats: data.allowedTimeFormats || null
        };
      } else {
        console.warn('[PreferencesService] Failed to load preferences from API, using defaults');
        return DEFAULT_PREFERENCES;
      }
    } catch (error) {
      console.error('[PreferencesService] Error loading preferences:', error);
      return DEFAULT_PREFERENCES;
    }
  }

  /**
   * Update a single preference via API
   * NOTE: This method does NOT dispatch preference-changed events.
   * SessionPreferencesContext handles state management via SignalR.
   */
  async setPreference<K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): Promise<boolean> {
    const keyStr = key as string;

    // If there's already an update in-flight for this key, return that promise
    if (this.pendingUpdates.has(keyStr)) {
      return this.pendingUpdates.get(keyStr)!;
    }

    const updatePromise = (async () => {
      try {
        const response = await fetch(`${API_BASE}/user-preferences/${key}`, {
          method: 'PATCH',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            ...authService.getAuthHeaders()
          },
          body: JSON.stringify(value)
        });

        if (response.status === 401) {
          console.warn(`[PreferencesService] Unauthorized while updating ${key} - triggering logout`);
          authService.handleUnauthorized();
          return false;
        }

        if (response.ok) {
          return true;
        } else {
          console.error(`[PreferencesService] Failed to update preference ${key}:`, response.status);
          return false;
        }
      } catch (error) {
        console.error(`[PreferencesService] Error updating preference ${key}:`, error);
        return false;
      } finally {
        this.pendingUpdates.delete(keyStr);
      }
    })();

    this.pendingUpdates.set(keyStr, updatePromise);
    return updatePromise;
  }

  /**
   * Setup SignalR listener for session-related events (NOT preference updates)
   *
   * NOTE: UserPreferencesUpdated is handled by SessionPreferencesContext.
   * This method only handles session management events:
   * - UserPreferencesReset
   * - UserSessionsCleared
   * - UserSessionRevoked
   * - DefaultGuestThemeChanged
   */
  setupSignalRListener(signalR: SignalRConnection): void {
    let isProcessingReset = false;
    const recentRevocations = new Set<string>();
    let recentlyDispatchedSessionsCleared = false;

    // Handle preference reset
    const handlePreferencesReset = () => {
      if (isProcessingReset) return;

      try {
        isProcessingReset = true;
        // Dispatch a custom event for themeService to handle
        window.dispatchEvent(new CustomEvent('preferences-reset'));
      } finally {
        setTimeout(() => {
          isProcessingReset = false;
        }, 2000);
      }
    };

    // Handle session cleared - dispatch event for App.tsx to handle
    const handleSessionsCleared = () => {
      if (recentlyDispatchedSessionsCleared) return;
      recentlyDispatchedSessionsCleared = true;

      window.dispatchEvent(new CustomEvent('user-sessions-cleared'));

      setTimeout(() => {
        recentlyDispatchedSessionsCleared = false;
      }, 5000);
    };

    // Handle session revoked - check if it's our session and logout immediately
    const handleSessionRevoked = (data: UserSessionRevokedEvent) => {
      const { deviceId, sessionType } = data;
      const revocationKey = `${deviceId}-${sessionType}`;

      if (recentRevocations.has(revocationKey)) return;

      try {
        recentRevocations.add(revocationKey);

        const ourDeviceId = authService.getDeviceId();
        const ourGuestSessionId = authService.getGuestSessionId();

        const isOurSession =
          (sessionType === 'authenticated' && deviceId === ourDeviceId) ||
          (sessionType === 'guest' && deviceId === ourGuestSessionId);

        if (isOurSession) {
          if (recentlyDispatchedSessionsCleared) return;
          recentlyDispatchedSessionsCleared = true;

          window.dispatchEvent(new CustomEvent('user-sessions-cleared'));

          setTimeout(() => {
            recentlyDispatchedSessionsCleared = false;
          }, 5000);
        }
      } finally {
        setTimeout(() => {
          recentRevocations.delete(revocationKey);
        }, 5000);
      }
    };

    // Handle default guest theme changed - auto-update guests using default theme
    const handleDefaultGuestThemeChanged = (data: DefaultGuestThemeChangedEvent) => {
      const { newThemeId } = data;

      // Only apply to guest users
      if (authService.authMode !== 'guest') return;

      // Dispatch preference-changed event to trigger theme update
      // themeService listens for this to apply the new theme
      window.dispatchEvent(
        new CustomEvent('preference-changed', {
          detail: { key: 'selectedTheme', value: newThemeId }
        })
      );
    };

    signalR.on('UserPreferencesReset', handlePreferencesReset);
    signalR.on('UserSessionsCleared', handleSessionsCleared);
    signalR.on('UserSessionRevoked', handleSessionRevoked);
    signalR.on('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
  }
}

export default new PreferencesService();
