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
  refreshRate?: string | null; // Refresh rate for guest users (null = use default)
  allowedTimeFormats?: string[] | null; // Allowed time formats for this user (null = all formats)
}

class PreferencesService {
  private preferences: UserPreferences | null = null;
  private loading = false;
  private loaded = false;
  private pendingUpdates: Map<string, Promise<boolean>> = new Map(); // Track in-flight updates

  /**
   * Load preferences from the API
   */
  async loadPreferences(): Promise<UserPreferences> {
    if (this.loaded && this.preferences) {
      return this.preferences;
    }

    if (this.loading) {
      // Wait for the current load to complete
      await new Promise((resolve) => setTimeout(resolve, 100));
      return this.loadPreferences();
    }

    this.loading = true;

    try {
      const response = await fetch(`${API_BASE}/user-preferences`, {
        credentials: 'include', // Important: include HttpOnly session cookies
        headers: authService.getAuthHeaders()
      });

      // Handle 401 Unauthorized - device was revoked or session expired
      if (response.status === 401) {
        console.warn('[PreferencesService] Unauthorized - triggering logout');
        authService.handleUnauthorized();
        this.preferences = this.getDefaultPreferences();
        return this.preferences;
      }

      if (response.ok) {
        const data = await response.json();
        this.preferences = {
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
        this.loaded = true;
        return this.preferences;
      } else {
        // Return defaults if API call fails
        console.warn('[PreferencesService] Failed to load preferences from API, using defaults');
        this.preferences = this.getDefaultPreferences();
        return this.preferences;
      }
    } catch (error) {
      console.error('[PreferencesService] Error loading preferences:', error);
      this.preferences = this.getDefaultPreferences();
      return this.preferences;
    } finally {
      this.loading = false;
    }
  }

  /**
   * Update a single preference
   * NOTE: This method no longer dispatches preference-changed events.
   * SessionPreferencesContext handles state management via SignalR.
   */
  async updatePreference<K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): Promise<boolean> {
    const keyStr = key as string;

    // CRITICAL: If there's already an update in-flight for this key, return that promise
    if (this.pendingUpdates.has(keyStr)) {
      return this.pendingUpdates.get(keyStr)!;
    }

    // Create the update promise
    const updatePromise = (async () => {
      try {
        const response = await fetch(`${API_BASE}/user-preferences/${key}`, {
          method: 'PATCH',
          credentials: 'include', // Important: include HttpOnly session cookies
          headers: {
            'Content-Type': 'application/json',
            ...authService.getAuthHeaders()
          },
          body: JSON.stringify(value)
        });

        // Handle 401 Unauthorized - device was revoked or session expired
        if (response.status === 401) {
          console.warn(`[PreferencesService] Unauthorized while updating ${key} - triggering logout`);
          authService.handleUnauthorized();
          return false;
        }

        if (response.ok) {
          if (this.preferences) {
            this.preferences[key] = value;
          }
          // No longer dispatching preference-changed events here
          // SessionPreferencesContext handles state management via SignalR
          return true;
        } else {
          console.error(`[PreferencesService] Failed to update preference ${key}:`, response.status);
          return false;
        }
      } catch (error) {
        console.error(`[PreferencesService] Error updating preference ${key}:`, error);
        return false;
      } finally {
        // Always remove from pending updates when done
        this.pendingUpdates.delete(keyStr);
      }
    })();

    // Store the promise
    this.pendingUpdates.set(keyStr, updatePromise);
    return updatePromise;
  }

  /**
   * Get current preferences (loads from API if not already loaded)
   */
  async getPreferences(): Promise<UserPreferences> {
    if (!this.preferences || !this.loaded) {
      return await this.loadPreferences();
    }
    return this.preferences;
  }

  /**
   * Get a specific preference value
   */
  async getPreference<K extends keyof UserPreferences>(key: K): Promise<UserPreferences[K]> {
    const prefs = await this.getPreferences();
    return prefs[key];
  }

  /**
   * Set a specific preference value
   */
  async setPreference<K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): Promise<boolean> {
    return await this.updatePreference(key, value);
  }

  /**
   * Get preferences synchronously (returns null if not loaded yet)
   */
  getPreferencesSync(): UserPreferences | null {
    return this.preferences;
  }

  /**
   * Clear loaded preferences (will force reload on next access)
   */
  clearCache(): void {
    this.preferences = null;
    this.loaded = false;
  }

  /**
   * Update the cached preferences directly (used by SignalR updates)
   */
  updateCache(preferences: UserPreferences): void {
    this.preferences = preferences;
    this.loaded = true;
  }

  /**
   * Setup SignalR listener for session-related events (NOT preference updates)
   * 
   * NOTE: UserPreferencesUpdated is now handled by SessionPreferencesContext.
   * This method only handles session management events:
   * - UserPreferencesReset
   * - UserSessionsCleared
   * - UserSessionRevoked
   * - DefaultGuestThemeChanged
   */
  setupSignalRListener(signalR: SignalRConnection): void {
    // Track processing flags to prevent race conditions
    let isProcessingReset = false;

    // CRITICAL: Track recently processed revocations to prevent duplicate SignalR events
    const recentRevocations = new Set<string>();

    // Track if we recently dispatched user-sessions-cleared to prevent duplicate events
    let recentlyDispatchedSessionsCleared = false;

    // Handle preference reset
    const handlePreferencesReset = () => {
      if (isProcessingReset) {
        return;
      }

      try {
        isProcessingReset = true;

        // Clear cached preferences
        this.clearCache();

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
      // CRITICAL: Prevent duplicate dispatches within 5 seconds
      if (recentlyDispatchedSessionsCleared) {
        return;
      }
      recentlyDispatchedSessionsCleared = true;

      // Dispatch custom event for App.tsx to handle (needs React context for refreshAuth)
      window.dispatchEvent(new CustomEvent('user-sessions-cleared'));

      // Reset flag after 5 seconds to allow future legitimate events
      setTimeout(() => {
        recentlyDispatchedSessionsCleared = false;
      }, 5000);
    };

    // Handle session revoked - check if it's our session and logout immediately
    const handleSessionRevoked = (data: UserSessionRevokedEvent) => {
      const { deviceId, sessionType } = data;
      const revocationKey = `${deviceId}-${sessionType}`;

      // CRITICAL: Skip if we just processed this revocation in the last 5 seconds
      if (recentRevocations.has(revocationKey)) {
        return;
      }

      try {
        // Add to recent set FIRST to block duplicates immediately
        recentRevocations.add(revocationKey);

        // Check if this is our session
        const ourDeviceId = authService.getDeviceId();
        const ourGuestSessionId = authService.getGuestSessionId();

        const isOurSession =
          (sessionType === 'authenticated' && deviceId === ourDeviceId) ||
          (sessionType === 'guest' && deviceId === ourGuestSessionId);

        if (isOurSession) {
          // Check if we already dispatched recently
          if (recentlyDispatchedSessionsCleared) {
            return;
          }
          recentlyDispatchedSessionsCleared = true;

          // Dispatch custom event for App.tsx to handle
          window.dispatchEvent(new CustomEvent('user-sessions-cleared'));

          // Reset flag after 5 seconds
          setTimeout(() => {
            recentlyDispatchedSessionsCleared = false;
          }, 5000);
        }
      } finally {
        // Remove from set after 5 seconds to allow future legitimate revocations
        setTimeout(() => {
          recentRevocations.delete(revocationKey);
        }, 5000);
      }
    };

    // Handle default guest theme changed - auto-update guests using default theme
    const handleDefaultGuestThemeChanged = (data: DefaultGuestThemeChangedEvent) => {
      const { newThemeId } = data;

      // Only apply to guest users
      if (authService.authMode !== 'guest') {
        return;
      }

      // Only apply if user is currently using the default theme (selectedTheme === null)
      const currentPrefs = this.getPreferencesSync();
      if (currentPrefs && currentPrefs.selectedTheme !== null) {
        return;
      }

      // Dispatch preference-changed event to trigger theme update
      // This is one of the few remaining uses of preference-changed, 
      // specifically for theme changes that need themeService to react
      window.dispatchEvent(
        new CustomEvent('preference-changed', {
          detail: { key: 'selectedTheme', value: newThemeId }
        })
      );
    };

    // NOTE: UserPreferencesUpdated is now handled by SessionPreferencesContext
    signalR.on('UserPreferencesReset', handlePreferencesReset);
    signalR.on('UserSessionsCleared', handleSessionsCleared);
    signalR.on('UserSessionRevoked', handleSessionRevoked);
    signalR.on('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
  }

  /**
   * Get default preferences
   */
  private getDefaultPreferences(): UserPreferences {
    return {
      selectedTheme: null,
      sharpCorners: false,
      disableFocusOutlines: true, // Default to disabled (no blue borders)
      disableTooltips: false,
      picsAlwaysVisible: false,
      disableStickyNotifications: false,
      useLocalTimezone: false, // Default to server timezone
      use24HourFormat: true, // Default to 24-hour format
      showDatasourceLabels: true, // Default to showing datasource labels when multiple datasources
      showYearInDates: false, // Default to hiding year for current year dates
      refreshRate: null, // Default refresh rate (null = use system default)
      allowedTimeFormats: null // Default to all time formats allowed
    };
  }
}

export default new PreferencesService();
