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
}

class PreferencesService {
  private preferences: UserPreferences | null = null;
  private loading = false;
  private loaded = false;
  private handlerRegistered = false;

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
          disableStickyNotifications: data.disableStickyNotifications || false
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
   * Setup SignalR listener for preference updates (called from main.tsx)
   * Pass in the SignalR context's `on` method
   */
  setupSignalRListener(signalROn: (eventName: string, handler: (...args: any[]) => void) => void): void {
    this.handlerRegistered = true;

    signalROn('UserPreferencesUpdated', (payload: any) => {

      const { sessionId, preferences: newPreferences } = payload;

      // Check if this update is for the current user's session
      const deviceId = authService.getDeviceId();
      const guestSessionId = authService.getGuestSessionId();
      const currentSessionId = deviceId || guestSessionId;

      if (sessionId !== currentSessionId) {
        return;
      }

      // Parse preferences
      const updatedPrefs: UserPreferences = {
        selectedTheme: newPreferences.selectedTheme || null,
        sharpCorners: newPreferences.sharpCorners || false,
        disableFocusOutlines: newPreferences.disableFocusOutlines || false,
        disableTooltips: newPreferences.disableTooltips || false,
        picsAlwaysVisible: newPreferences.picsAlwaysVisible || false,
        hideAboutSections: newPreferences.hideAboutSections || false,
        disableStickyNotifications: newPreferences.disableStickyNotifications || false
      };

      // Check what changed and dispatch events
      const oldPrefs = this.preferences;
      this.preferences = updatedPrefs;

      if (oldPrefs) {
        if (oldPrefs.selectedTheme !== updatedPrefs.selectedTheme) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'selectedTheme', value: updatedPrefs.selectedTheme }
            })
          );
        }
        if (oldPrefs.sharpCorners !== updatedPrefs.sharpCorners) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'sharpCorners', value: updatedPrefs.sharpCorners }
            })
          );
        }
        if (oldPrefs.disableFocusOutlines !== updatedPrefs.disableFocusOutlines) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'disableFocusOutlines', value: updatedPrefs.disableFocusOutlines }
            })
          );
        }
        if (oldPrefs.disableTooltips !== updatedPrefs.disableTooltips) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'disableTooltips', value: updatedPrefs.disableTooltips }
            })
          );
        }
        if (oldPrefs.picsAlwaysVisible !== updatedPrefs.picsAlwaysVisible) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'picsAlwaysVisible', value: updatedPrefs.picsAlwaysVisible }
            })
          );
        }
        if (oldPrefs.hideAboutSections !== updatedPrefs.hideAboutSections) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'hideAboutSections', value: updatedPrefs.hideAboutSections }
            })
          );
        }
        if (oldPrefs.disableStickyNotifications !== updatedPrefs.disableStickyNotifications) {
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'disableStickyNotifications', value: updatedPrefs.disableStickyNotifications }
            })
          );
        }
      }
    });
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
      disableStickyNotifications: false
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
          disableStickyNotifications: data.disableStickyNotifications || false
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
