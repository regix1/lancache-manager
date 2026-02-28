export type AuthMode = 'authenticated' | 'guest' | 'unauthenticated';
export type SessionType = 'admin' | 'guest';

interface AuthStatusResponse {
  isAuthenticated: boolean;
  sessionType: SessionType | null;
  sessionId: string | null;
  expiresAt: string | null;
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
  token?: string;
}

interface LoginResponse {
  success: boolean;
  sessionType: string;
  expiresAt: string;
  token?: string;
  error?: string;
}

const getApiUrl = (): string => {
  if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();
const AUTH_CHECK_TIMEOUT_MS = 10000;

class AuthService {
  public isAuthenticated: boolean = false;
  public authChecked: boolean = false;
  public authMode: AuthMode = 'unauthenticated';
  public sessionType: SessionType | null = null;
  public sessionId: string | null = null;
  private sessionToken: string | null = null;

  async checkAuth(): Promise<AuthStatusResponse> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => {
      controller.abort();
    }, AUTH_CHECK_TIMEOUT_MS);

    try {
      const response = await fetch(`${API_URL}/api/auth/status`, {
        credentials: 'include',
        signal: controller.signal
      });

      if (!response.ok) {
        throw new Error(`Auth check failed: ${response.status}`);
      }

      const data: AuthStatusResponse = await response.json();

      this.isAuthenticated = data.isAuthenticated;
      this.sessionType = data.sessionType;
      this.sessionId = data.sessionId;
      this.authChecked = true;

      if (data.isAuthenticated && data.sessionType === 'admin') {
        this.authMode = 'authenticated';
      } else if (data.isAuthenticated && data.sessionType === 'guest') {
        this.authMode = 'guest';
      } else {
        this.authMode = 'unauthenticated';
      }

      // Store token for SignalR accessTokenFactory (survives page refresh via rotation)
      if (data.token) {
        this.sessionToken = data.token;
      }

      return data;
    } catch (error) {
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
        sessionType: null,
        sessionId: null,
        expiresAt: null,
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
      };
    } finally {
      clearTimeout(timeoutId);
    }
  }

  async login(apiKey: string): Promise<{ success: boolean; message?: string }> {
    try {
      const response = await fetch(`${API_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ apiKey }),
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        return { success: false, message: data.error || `Login failed: ${response.status}` };
      }

      const data: LoginResponse = await response.json();
      if (data.success) {
        this.isAuthenticated = true;
        this.authMode = 'authenticated';
        this.sessionType = 'admin';
        this.sessionToken = data.token || null;
      }

      return { success: data.success, message: data.error };
    } catch (error) {
      console.error('[AuthService] login error:', error);
      return { success: false, message: 'Network error during login' };
    }
  }

  async startGuestSession(): Promise<{ success: boolean; message?: string }> {
    try {
      const response = await fetch(`${API_URL}/api/auth/guest`, {
        method: 'POST',
        credentials: 'include',
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        return { success: false, message: data.error || 'Failed to start guest session' };
      }

      const data: LoginResponse = await response.json();
      if (data.success) {
        this.isAuthenticated = true;
        this.authMode = 'guest';
        this.sessionType = 'guest';
        this.sessionToken = data.token || null;
      }

      return { success: data.success, message: data.error };
    } catch (error) {
      console.error('[AuthService] startGuestSession error:', error);
      return { success: false, message: 'Network error' };
    }
  }

  async logout(): Promise<void> {
    try {
      await fetch(`${API_URL}/api/auth/logout`, {
        method: 'POST',
        credentials: 'include',
      });
    } catch (error) {
      console.error('[AuthService] logout error:', error);
    } finally {
      this.isAuthenticated = false;
      this.authMode = 'unauthenticated';
      this.sessionType = null;
      this.sessionId = null;
      this.sessionToken = null;
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

  getSessionToken(): string | null {
    return this.sessionToken;
  }
}

const authService = new AuthService();
export default authService;
