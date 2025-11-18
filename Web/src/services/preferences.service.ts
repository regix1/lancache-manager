import { API_BASE } from '../utils/constants';
import authService from './auth.service';

export interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  hideAboutSections: boolean;
  disableStickyNotifications: boolean;
  useLocalTimezone: boolean;
}

class PreferencesService {
  private preferences: UserPreferences | null = null;
  private loading = false;
  private loaded = false;

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
      const response = await fetch(`${API_BASE}/userpreferences`, {
        headers: authService.getAuthHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        this.preferences = {
          selectedTheme: data.selectedTheme || null,
          sharpCorners: data.sharpCorners || false,
          disableFocusOutlines: data.disableFocusOutlines || false,
          disableTooltips: data.disableTooltips || false,
          picsAlwaysVisible: data.picsAlwaysVisible || false,
          hideAboutSections: data.hideAboutSections || false,
          disableStickyNotifications: data.disableStickyNotifications || false,
          useLocalTimezone: data.useLocalTimezone || false
        };
        this.loaded = true;
        console.log('[PreferencesService] Loaded preferences from API:', this.preferences);
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
   * Save preferences to the API
   */
  async savePreferences(preferences: UserPreferences): Promise<boolean> {
    try {
      const response = await fetch(`${API_BASE}/userpreferences`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...authService.getAuthHeaders()
        },
        body: JSON.stringify(preferences)
      });

      if (response.ok) {
        this.preferences = preferences;
        console.log('[PreferencesService] Saved preferences to API');
        return true;
      } else {
        console.error('[PreferencesService] Failed to save preferences:', response.status);
        return false;
      }
    } catch (error) {
      console.error('[PreferencesService] Error saving preferences:', error);
      return false;
    }
  }

  /**
   * Update a single preference
   */
  async updatePreference<K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): Promise<boolean> {
    try {
      const response = await fetch(`${API_BASE}/userpreferences/${key}`, {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json',
          ...authService.getAuthHeaders()
        },
        body: JSON.stringify(value)
      });

      if (response.ok) {
        if (this.preferences) {
          this.preferences[key] = value;
        }
        console.log(`[PreferencesService] Updated preference ${key}:`, value);
        return true;
      } else {
        console.error(`[PreferencesService] Failed to update preference ${key}:`, response.status);
        return false;
      }
    } catch (error) {
      console.error(`[PreferencesService] Error updating preference ${key}:`, error);
      return false;
    }
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
    console.log('[PreferencesService] Cache updated directly:', this.preferences);
  }

  /**
   * Setup SignalR listener for preference updates
   * Handles UserPreferencesUpdated, UserPreferencesReset, and UserSessionsCleared
   */
  setupSignalRListener(signalR: { on: (eventName: string, handler: (...args: any[]) => void) => void }): void {
    console.log('[PreferencesService] Setting up SignalR listeners');

    // Track if we're processing to prevent race conditions
    let isProcessingUpdate = false;
    let isProcessingReset = false;

    // Handle preference updates
    const handlePreferencesUpdated = (payload: any) => {
      if (isProcessingUpdate) {
        console.log('[PreferencesService] Already processing update, skipping duplicate');
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

        console.log('[PreferencesService] Preferences updated for current session, applying changes...');

        // Get old preferences before updating
        const oldPrefs = this.preferences;

        // Parse new preferences from SignalR payload
        const updatedPrefs: UserPreferences = {
          selectedTheme: newPreferences.selectedTheme || null,
          sharpCorners: newPreferences.sharpCorners || false,
          disableFocusOutlines: newPreferences.disableFocusOutlines || false,
          disableTooltips: newPreferences.disableTooltips || false,
          picsAlwaysVisible: newPreferences.picsAlwaysVisible || false,
          hideAboutSections: newPreferences.hideAboutSections || false,
          disableStickyNotifications: newPreferences.disableStickyNotifications || false,
          useLocalTimezone: newPreferences.useLocalTimezone || false
        };

        // Update cache directly with SignalR values (don't fetch from API to avoid race conditions)
        this.updateCache(updatedPrefs);

        // Only dispatch events for preferences that actually changed
        if (oldPrefs) {
          Object.keys(updatedPrefs).forEach((key) => {
            const typedKey = key as keyof UserPreferences;
            if (oldPrefs[typedKey] !== updatedPrefs[typedKey]) {
              console.log(`[PreferencesService] Preference changed: ${key} = ${updatedPrefs[typedKey]}`);
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
        console.log('[PreferencesService] Already processing reset, skipping duplicate');
        return;
      }

      try {
        isProcessingReset = true;
        console.log('[PreferencesService] UserPreferencesReset event received');

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
      console.log('[PreferencesService] UserSessionsCleared event received');

      // Dispatch custom event for App.tsx to handle (needs React context for refreshAuth)
      window.dispatchEvent(new CustomEvent('user-sessions-cleared'));
    };

    // Handle session revoked - check if it's our session and logout immediately
    const handleSessionRevoked = (payload: any) => {
      console.log('[PreferencesService] UserSessionRevoked event received:', payload);

      const { sessionId, sessionType } = payload;

      // Check if this is our session
      const ourDeviceId = authService.getDeviceId();
      const ourGuestSessionId = authService.getGuestSessionId();

      const isOurSession =
        (sessionType === 'authenticated' && sessionId === ourDeviceId) ||
        (sessionType === 'guest' && sessionId === ourGuestSessionId);

      if (isOurSession) {
        console.warn('[PreferencesService] Our session was revoked - forcing logout');

        // Dispatch custom event for App.tsx to handle (needs React context for refreshAuth)
        window.dispatchEvent(new CustomEvent('user-sessions-cleared'));
      }
    };

    // Handle default guest theme changed - auto-update guests using default theme
    const handleDefaultGuestThemeChanged = (payload: any) => {
      console.log('[PreferencesService] DefaultGuestThemeChanged event received:', payload);

      const { newThemeId } = payload;

      // Only apply to guest users
      if (authService.authMode !== 'guest') {
        console.log('[PreferencesService] Not a guest user, ignoring default theme change');
        return;
      }

      // Only apply if user is currently using the default theme (selectedTheme === null)
      const currentPrefs = this.getPreferencesSync();
      if (currentPrefs && currentPrefs.selectedTheme !== null) {
        console.log('[PreferencesService] Guest has custom theme selected, ignoring default theme change');
        return;
      }

      console.log(`[PreferencesService] Applying new default guest theme: ${newThemeId}`);

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

    console.log('[PreferencesService] SignalR listeners registered');
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
      hideAboutSections: false,
      disableStickyNotifications: false,
      useLocalTimezone: false // Default to server timezone
    };
  }

  /**
   * Get preferences for a specific session (admin only)
   */
  async getPreferencesForSession(sessionId: string): Promise<UserPreferences | null> {
    try {
      const response = await fetch(`${API_BASE}/userpreferences/session/${sessionId}`, {
        headers: authService.getAuthHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        return {
          selectedTheme: data.selectedTheme || null,
          sharpCorners: data.sharpCorners || false,
          disableFocusOutlines: data.disableFocusOutlines || false,
          disableTooltips: data.disableTooltips || false,
          picsAlwaysVisible: data.picsAlwaysVisible || false,
          hideAboutSections: data.hideAboutSections || false,
          disableStickyNotifications: data.disableStickyNotifications || false,
          useLocalTimezone: data.useLocalTimezone || false
        };
      } else {
        console.error(
          '[PreferencesService] Failed to load preferences for session:',
          response.status
        );
        return null;
      }
    } catch (error) {
      console.error('[PreferencesService] Error loading preferences for session:', error);
      return null;
    }
  }

  /**
   * Save preferences for a specific session (admin only)
   */
  async savePreferencesForSession(
    sessionId: string,
    preferences: UserPreferences
  ): Promise<boolean> {
    try {
      const response = await fetch(`${API_BASE}/userpreferences/session/${sessionId}`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...authService.getAuthHeaders()
        },
        body: JSON.stringify(preferences)
      });

      if (response.ok) {
        console.log('[PreferencesService] Saved preferences for session:', sessionId);
        return true;
      } else {
        console.error(
          '[PreferencesService] Failed to save preferences for session:',
          response.status
        );
        return false;
      }
    } catch (error) {
      console.error('[PreferencesService] Error saving preferences for session:', error);
      return false;
    }
  }
}

export default new PreferencesService();
