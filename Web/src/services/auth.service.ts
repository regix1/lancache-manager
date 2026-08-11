import { assertOk, type ApiErrorData } from './apiError';
import { getApiUrl } from '@utils/constants';
import { hasRecentUserInteraction } from '@utils/userInteractionTracker';

export type AuthMode = 'authenticated' | 'guest' | 'unauthenticated';
export type SessionType = 'admin' | 'user' | 'guest';

/**
 * Admin and user sessions both sign in as an account and have the same access; a guest has neither.
 * Mirrors SessionTypeExtensions.IsAccountHolder on the server.
 */
export function isAccountHolder(sessionType: SessionType | null): boolean {
  return sessionType === 'admin' || sessionType === 'user';
}

interface AuthStatusResponse {
  isAuthenticated: boolean;
  authenticationEnabled: boolean;
  sessionType: SessionType | null;
  sessionId: string | null;
  expiresAt: string | null;
  accountId: string | null;
  isMainAdmin: boolean;
  hasData: boolean;
  hasBeenInitialized: boolean;
  hasDataLoaded: boolean;
  guestAccessEnabled: boolean;
  guestDurationHours: number;
  prefillEnabled: boolean;
  prefillExpiresAt: string | null;
  steamPrefillEnabled: boolean;
  steamPrefillExpiresAt: string | null;
  epicPrefillEnabled: boolean;
  epicPrefillExpiresAt: string | null;
  battlenetPrefillEnabled: boolean;
  battlenetPrefillExpiresAt: string | null;
  riotPrefillEnabled: boolean;
  riotPrefillExpiresAt: string | null;
  xboxPrefillEnabled: boolean;
  xboxPrefillExpiresAt: string | null;
}

interface LoginResponse {
  success: boolean;
  sessionType: SessionType;
  expiresAt: string;
  error?: string;
}

const API_URL = getApiUrl();
const AUTH_CHECK_TIMEOUT_MS = 10000;

class AuthService {
  public isAuthenticated = false;
  public authChecked = false;
  public authMode: AuthMode = 'unauthenticated';
  public sessionType: SessionType | null = null;
  public sessionId: string | null = null;

  async checkAuth(): Promise<AuthStatusResponse> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => {
      controller.abort();
    }, AUTH_CHECK_TIMEOUT_MS);

    try {
      // Reachable from a SignalR-reconnect effect with no user action involved (a network blip or
      // laptop wake can reconnect while the tab is genuinely idle) - carry the same activity signal
      // as ApiService.getFetchOptions() so this doesn't keep LastSeenAtUtc artificially fresh.
      const response = await fetch(`${API_URL}/api/auth/status`, {
        credentials: 'include',
        signal: controller.signal,
        headers: {
          'X-User-Active': hasRecentUserInteraction(120_000) ? 'true' : 'false'
        }
      });

      await assertOk(response);

      const data: AuthStatusResponse = await response.json();

      this.isAuthenticated = data.isAuthenticated;
      this.sessionType = data.sessionType;
      this.sessionId = data.sessionId;
      this.authChecked = true;

      if (data.isAuthenticated && isAccountHolder(data.sessionType)) {
        this.authMode = 'authenticated';
      } else if (data.isAuthenticated && data.sessionType === 'guest') {
        this.authMode = 'guest';
      } else {
        this.authMode = 'unauthenticated';
      }

      return data;
    } catch (error: unknown) {
      if (error instanceof Error && error.name === 'AbortError') {
        console.warn(`[AuthService] checkAuth timed out after ${AUTH_CHECK_TIMEOUT_MS}ms`);
      }
      console.error('[AuthService] checkAuth error:', error);
      this.isAuthenticated = false;
      this.authMode = 'unauthenticated';
      this.sessionType = null;
      this.sessionId = null;
      this.authChecked = true;

      return {
        isAuthenticated: false,
        authenticationEnabled: true,
        sessionType: null,
        sessionId: null,
        expiresAt: null,
        accountId: null,
        isMainAdmin: false,
        hasData: false,
        hasBeenInitialized: false,
        hasDataLoaded: false,
        guestAccessEnabled: true,
        guestDurationHours: 6,
        prefillEnabled: false,
        prefillExpiresAt: null,
        steamPrefillEnabled: false,
        steamPrefillExpiresAt: null,
        epicPrefillEnabled: false,
        epicPrefillExpiresAt: null,
        battlenetPrefillEnabled: false,
        battlenetPrefillExpiresAt: null,
        riotPrefillEnabled: false,
        riotPrefillExpiresAt: null,
        xboxPrefillEnabled: false,
        xboxPrefillExpiresAt: null
      };
    } finally {
      clearTimeout(timeoutId);
    }
  }

  async login(
    apiKey: string,
    username: string,
    password: string
  ): Promise<{ success: boolean; message?: string }> {
    try {
      const response = await fetch(`${API_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ apiKey, username, password })
      });

      if (!response.ok) {
        // Deliberate exception to the "throw ApiError" rule (documented in the error-handling
        // standard): this method's contract is to RETURN {success,message} so the caller can render
        // a form error, not to throw. It still parses the shared ApiErrorData shape.
        const data: ApiErrorData = await response.json().catch(() => ({}) as ApiErrorData);
        return {
          success: false,
          message: data.error || data.message || `Login failed: ${response.status}`
        };
      }

      const data: LoginResponse = await response.json();
      if (data.success) {
        this.isAuthenticated = true;
        this.authMode = 'authenticated';
        // The role comes from the account that signed in, so a user session is not recorded as admin.
        this.sessionType = data.sessionType;
      }

      return { success: data.success, message: data.error };
    } catch (error: unknown) {
      console.error('[AuthService] login error:', error);
      return { success: false, message: 'Network error during login' };
    }
  }

  async startGuestSession(): Promise<{ success: boolean; message?: string }> {
    try {
      const response = await fetch(`${API_URL}/api/auth/guest`, {
        method: 'POST',
        credentials: 'include'
      });

      if (!response.ok) {
        // Same deliberate {success,message} return contract as login() above.
        const data: ApiErrorData = await response.json().catch(() => ({}) as ApiErrorData);
        return {
          success: false,
          message: data.error || data.message || 'Failed to start guest session'
        };
      }

      const data: LoginResponse = await response.json();
      if (data.success) {
        this.isAuthenticated = true;
        this.authMode = 'guest';
        this.sessionType = 'guest';
      }

      return { success: data.success, message: data.error };
    } catch (error: unknown) {
      console.error('[AuthService] startGuestSession error:', error);
      return { success: false, message: 'Network error' };
    }
  }

  async logout(): Promise<void> {
    try {
      await fetch(`${API_URL}/api/auth/logout`, {
        method: 'POST',
        credentials: 'include'
      });
    } catch (error: unknown) {
      console.error('[AuthService] logout error:', error);
    } finally {
      this.isAuthenticated = false;
      this.authMode = 'unauthenticated';
      this.sessionType = null;
      this.sessionId = null;
    }
  }

  isAdmin(): boolean {
    return this.sessionType === 'admin';
  }

  isGuest(): boolean {
    return this.sessionType === 'guest';
  }

  getSessionType(): SessionType | null {
    return this.sessionType;
  }

  isGuestModeActive(): boolean {
    return this.authMode === 'guest';
  }
}

const authService = new AuthService();
export default authService;
