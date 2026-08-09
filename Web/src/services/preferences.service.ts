import { API_BASE } from '../utils/constants';
import ApiService from './api.service';
import type { UserSessionRevokedEvent } from '../contexts/SignalRContext/types';
import { APP_EVENTS } from '@utils/constants';
import {
  CLOCK_KEYS,
  DEFAULT_PREFERENCES,
  type ClockPreferences,
  type UserPreferences as SessionUserPreferences
} from '@/types/userPreferences';

// SignalR connection interface - handler needs to accept any args for compatibility
interface SignalRConnection {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (eventName: string, handler: (...args: any[]) => void) => void;
}

type UserPreferences = Omit<SessionUserPreferences, 'refreshRateLocked'>;

type PreferenceValue = UserPreferences[keyof UserPreferences];

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
  private pendingUpdates = new Map<string, Promise<boolean>>();

  /**
   * Load preferences from the API (no caching)
   */
  async loadPreferences(): Promise<UserPreferences> {
    try {
      const response = await fetch(`${API_BASE}/user-preferences`, {
        credentials: 'include'
      });

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
          useUtcTimezone: data.useUtcTimezone || false,
          use24HourFormat: data.use24HourFormat || false,
          showDatasourceLabels: data.showDatasourceLabels ?? true,
          refreshRate: data.refreshRate || null,
          allowedTimeFormats: data.allowedTimeFormats || null
        };
      } else {
        console.warn('[PreferencesService] Failed to load preferences from API, using defaults');
        return DEFAULT_PREFERENCES;
      }
    } catch (error: unknown) {
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
    const inFlight = this.pendingUpdates.get(keyStr);

    // A second value for the same key waits behind the request already out rather than being
    // dropped for it, so the server ends on the value picked last and the caller is told what
    // happened to its own value instead of the earlier one's. [21]
    const send = inFlight
      ? inFlight.then(() => this.sendPreference(keyStr, value))
      : this.sendPreference(keyStr, value);

    const tracked: Promise<boolean> = send.finally(() => {
      // Only the tail clears the slot; an earlier link finishing must not free a key that a
      // later value is still queued behind.
      if (this.pendingUpdates.get(keyStr) === tracked) {
        this.pendingUpdates.delete(keyStr);
      }
    });

    this.pendingUpdates.set(keyStr, tracked);
    return tracked;
  }

  /**
   * Update the three clock flags in one request.
   *
   * They share the in-flight map with the per-key path, so a second click still waits behind the
   * request already out and the server ends on the clock picked last. What the single request buys is
   * that the three columns commit together: no second click can be applied between two writes of the
   * first and leave the row naming a clock nobody chose. [63]
   */
  async setClockPreferences(clock: ClockPreferences): Promise<boolean> {
    const inFlight = CLOCK_KEYS.map((key) => this.pendingUpdates.get(key)).filter(
      (update): update is Promise<boolean> => update !== undefined
    );

    const send =
      inFlight.length > 0
        ? Promise.all(inFlight).then(() => this.sendClockPreferences(clock))
        : this.sendClockPreferences(clock);

    const tracked: Promise<boolean> = send.finally(() => {
      // Only the tail clears a slot; an earlier link finishing must not free a key that a later
      // value is still queued behind.
      CLOCK_KEYS.forEach((key) => {
        if (this.pendingUpdates.get(key) === tracked) {
          this.pendingUpdates.delete(key);
        }
      });
    });

    CLOCK_KEYS.forEach((key) => this.pendingUpdates.set(key, tracked));
    return tracked;
  }

  /**
   * Send the clock flags to the API. Reports failure by resolving false rather than throwing, the same
   * way the per-key send does.
   */
  private async sendClockPreferences(clock: ClockPreferences): Promise<boolean> {
    try {
      const response = await fetch(
        `${API_BASE}/user-preferences/clock`,
        ApiService.getJsonFetchOptions(clock, { method: 'PATCH' })
      );

      if (response.ok) {
        return true;
      }

      console.error('[PreferencesService] Failed to update clock preferences:', response.status);
      return false;
    } catch (error: unknown) {
      console.error('[PreferencesService] Error updating clock preferences:', error);
      return false;
    }
  }

  /**
   * Send one preference to the API. Reports failure by resolving false rather than throwing, so
   * callers awaiting several of these have to read the results.
   */
  private async sendPreference(key: string, value: PreferenceValue): Promise<boolean> {
    try {
      const response = await fetch(
        `${API_BASE}/user-preferences/${key}`,
        ApiService.getJsonFetchOptions(value, { method: 'PATCH' })
      );

      if (response.ok) {
        return true;
      } else {
        console.error(`[PreferencesService] Failed to update preference ${key}:`, response.status);
        return false;
      }
    } catch (error: unknown) {
      console.error(`[PreferencesService] Error updating preference ${key}:`, error);
      return false;
    }
  }

  /**
   * Setup SignalR listener for session-related events (NOT preference updates)
   *
   * NOTE: UserPreferencesUpdated is handled by SessionPreferencesContext.
   * This method only handles session management events:
   * - UserPreferencesReset
   * - UserSessionRevoked
   *
   * Guest default preference events are handled by SessionPreferencesContext.
   */
  setupSignalRListener(signalR: SignalRConnection): void {
    let isProcessingReset = false;
    const recentRevocations = new Set<string>();

    // Handle preference reset
    const handlePreferencesReset = () => {
      if (isProcessingReset) return;

      try {
        isProcessingReset = true;
        // Dispatch a custom event for themeService to handle
        window.dispatchEvent(new CustomEvent(APP_EVENTS.PREFERENCES_RESET));
      } finally {
        setTimeout(() => {
          isProcessingReset = false;
        }, 2000);
      }
    };

    // Handle session revoked - server will handle session validation via cookies
    // If our session is revoked, subsequent API calls will return 401 and trigger logout
    const handleSessionRevoked = (data: UserSessionRevokedEvent) => {
      const { sessionId, sessionType } = data;
      const revocationKey = `${sessionId}-${sessionType}`;

      if (recentRevocations.has(revocationKey)) return;

      try {
        recentRevocations.add(revocationKey);

        // Server handles session identity via HttpOnly cookies
        // If this is our session, subsequent API calls will fail with 401
        // and authService.handleUnauthorized() will trigger logout
        // We just acknowledge the event here for consistency
      } finally {
        setTimeout(() => {
          recentRevocations.delete(revocationKey);
        }, 5000);
      }
    };

    signalR.on('UserPreferencesReset', handlePreferencesReset);
    signalR.on('UserSessionRevoked', handleSessionRevoked);
  }
}

export default new PreferencesService();
