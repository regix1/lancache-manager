import { API_BASE } from '../utils/constants';
import authService from './auth.service';

// SignalR data types for preference updates
interface PreferencesUpdatedEvent {
  sessionId: string;
  preferences: UserPreferences;
}

interface SessionRevokedEvent {
  deviceId: string;
  sessionType: 'authenticated' | 'guest';
}

interface DefaultGuestThemeChangedEvent {
  newThemeId: string;
}

// SignalR connection interface - handler needs to accept any args for compatibility
interface SignalRConnection {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (eventName: string, handler: (...args: any[]) => void) => void;
}

export interface UserPreferences {
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
        // console.log('[PreferencesService] Loaded preferences from API:', this.preferences);
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
   */
  async updatePreference<K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): Promise<boolean> {
    const keyStr = key as string;

    // CRITICAL: If there's already an update in-flight for this key, return that promise
    if (this.pendingUpdates.has(keyStr)) {
      // console.log(`[PreferencesService] Update already in-flight for ${keyStr}, waiting...`);
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
          // console.log(`[PreferencesService] Updated preference ${key}:`, value);

          // Dispatch immediate local update (SignalR will also broadcast, but this gives instant feedback)
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: key as string, value }
            })
          );

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
    // console.log('[PreferencesService] Cache updated directly:', this.preferences);
  }

  /**
   * Setup SignalR listener for preference updates
   * Handles UserPreferencesUpdated, UserPreferencesReset, and UserSessionsCleared
   */
  setupSignalRListener(signalR: SignalRConnection): void {
    // console.log('[PreferencesService] Setting up SignalR listeners');

    // Track if we're processing to prevent race conditions
    let isProcessingUpdate = false;
    let isProcessingReset = false;

    // CRITICAL: Track recently processed revocations to prevent duplicate SignalR events
    const recentRevocations = new Set<string>();

    // Track if we recently dispatched user-sessions-cleared to prevent duplicate events
    let recentlyDispatchedSessionsCleared = false;

    // Handle preference updates
    const handlePreferencesUpdated = (data: PreferencesUpdatedEvent) => {
      if (isProcessingUpdate) {
        // console.log('[PreferencesService] Already processing update, skipping duplicate');
        return;
      }

      try {
        isProcessingUpdate = true;

        const { sessionId, preferences: newPreferences } = data;

        // Check if this update is for the current user's session
        const deviceId = authService.getDeviceId();
        const guestSessionId = authService.getGuestSessionId();
        const currentSessionId = deviceId || guestSessionId;

        if (sessionId !== currentSessionId) {
          return;
        }

        // console.log('[PreferencesService] Preferences updated for current session, applying changes...');

        // Get old preferences before updating
        const oldPrefs = this.preferences;

        // Parse new preferences from SignalR data
        const updatedPrefs: UserPreferences = {
          selectedTheme: newPreferences.selectedTheme || null,
          sharpCorners: newPreferences.sharpCorners || false,
          disableFocusOutlines: newPreferences.disableFocusOutlines || false,
          disableTooltips: newPreferences.disableTooltips || false,
          picsAlwaysVisible: newPreferences.picsAlwaysVisible || false,
          disableStickyNotifications: newPreferences.disableStickyNotifications || false,
          useLocalTimezone: newPreferences.useLocalTimezone || false,
          use24HourFormat: newPreferences.use24HourFormat || false,
          showDatasourceLabels: newPreferences.showDatasourceLabels ?? true,
          showYearInDates: newPreferences.showYearInDates || false,
          refreshRate: newPreferences.refreshRate || null,
          allowedTimeFormats: newPreferences.allowedTimeFormats || null
        };

        // Update cache directly with SignalR values (don't fetch from API to avoid race conditions)
        this.updateCache(updatedPrefs);

        // Only dispatch events for preferences that actually changed
        if (oldPrefs) {
          Object.keys(updatedPrefs).forEach((key) => {
            const typedKey = key as keyof UserPreferences;
            if (oldPrefs[typedKey] !== updatedPrefs[typedKey]) {
              // console.log(`[PreferencesService] Preference changed: ${key} = ${updatedPrefs[typedKey]}`);
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
                detail: { key, value: updatedPrefs[key as keyof UserPreferences] }
              })
            );
          });
        }
      } finally {
        setTimeout(() => {
          isProcessingUpdate = false;
        }, 500);
      }
    };

    // Handle preference reset
    const handlePreferencesReset = () => {
      if (isProcessingReset) {
        // console.log('[PreferencesService] Already processing reset, skipping duplicate');
        return;
      }

      try {
        isProcessingReset = true;
        // console.log('[PreferencesService] UserPreferencesReset event received');

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
      // Both UserSessionsCleared and UserSessionRevoked can trigger this
      if (recentlyDispatchedSessionsCleared) {
        console.log('[PreferencesService] Already dispatched sessions cleared recently - skipping duplicate');
        return;
      }

      console.log('[PreferencesService] UserSessionsCleared event received');
      recentlyDispatchedSessionsCleared = true;

      // Dispatch custom event for App.tsx to handle (needs React context for refreshAuth)
      window.dispatchEvent(new CustomEvent('user-sessions-cleared'));

      // Reset flag after 5 seconds to allow future legitimate events
      setTimeout(() => {
        recentlyDispatchedSessionsCleared = false;
      }, 5000);
    };

    // Handle session revoked - check if it's our session and logout immediately
    const handleSessionRevoked = (data: SessionRevokedEvent) => {
      const { deviceId, sessionType } = data;
      const revocationKey = `${deviceId}-${sessionType}`;

      // CRITICAL: Skip if we just processed this revocation in the last 5 seconds
      // This prevents duplicate SignalR events from causing multiple logout attempts
      if (recentRevocations.has(revocationKey)) {
        console.log('[PreferencesService] Already processed revocation for', revocationKey, '- skipping duplicate');
        return;
      }

      try {
        console.log('[PreferencesService] UserSessionRevoked event received:', data);

        // Add to recent set FIRST to block duplicates immediately
        recentRevocations.add(revocationKey);

        // Check if this is our session
        const ourDeviceId = authService.getDeviceId();
        const ourGuestSessionId = authService.getGuestSessionId();

        const isOurSession =
          (sessionType === 'authenticated' && deviceId === ourDeviceId) ||
          (sessionType === 'guest' && deviceId === ourGuestSessionId);

        if (isOurSession) {
          // Check if we already dispatched recently (e.g., from UserSessionsCleared event)
          if (recentlyDispatchedSessionsCleared) {
            console.log('[PreferencesService] Session revoked but already dispatched sessions cleared - skipping');
            return;
          }

          console.warn('[PreferencesService] Our session was revoked - forcing logout');
          recentlyDispatchedSessionsCleared = true;

          // Dispatch custom event for App.tsx to handle (needs React context for refreshAuth)
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
      // console.log('[PreferencesService] DefaultGuestThemeChanged event received:', data);

      const { newThemeId } = data;

      // Only apply to guest users
      if (authService.authMode !== 'guest') {
        // console.log('[PreferencesService] Not a guest user, ignoring default theme change');
        return;
      }

      // Only apply if user is currently using the default theme (selectedTheme === null)
      const currentPrefs = this.getPreferencesSync();
      if (currentPrefs && currentPrefs.selectedTheme !== null) {
        // console.log('[PreferencesService] Guest has custom theme selected, ignoring default theme change');
        return;
      }

      // console.log(`[PreferencesService] Applying new default guest theme: ${newThemeId}`);

      // Dispatch preference-changed event to trigger theme update
      window.dispatchEvent(
        new CustomEvent('preference-changed', {
          detail: { key: 'selectedTheme', value: newThemeId }
        })
      );
    };

    signalR.on('UserPreferencesUpdated', handlePreferencesUpdated);
    signalR.on('UserPreferencesReset', handlePreferencesReset);
    signalR.on('UserSessionsCleared', handleSessionsCleared);
    signalR.on('UserSessionRevoked', handleSessionRevoked);
    signalR.on('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);

    // console.log('[PreferencesService] SignalR listeners registered');
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
