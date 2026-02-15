export type AuthMode = 'authenticated' | 'guest' | 'expired' | 'unauthenticated';

interface AuthCheckResponse {
  requiresAuth: boolean;
  isAuthenticated: boolean;
  authMode: AuthMode;
  authenticationType?: string;
  guestTimeRemaining?: number;
  hasData?: boolean;
  hasEverBeenSetup?: boolean;
  hasBeenInitialized?: boolean;
  hasDataLoaded?: boolean;
  error?: string;
  prefillEnabled?: boolean;
  prefillTimeRemaining?: number;
  isBanned?: boolean;
}

interface RegisterResponse {
  success: boolean;
  message: string;
}

interface RegenerateKeyResponse {
  success: boolean;
  message: string;
  warning?: string;
}

const getApiUrl = (): string => {
  if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();

class AuthService {
  private deviceId: string = 'default';
  public isAuthenticated: boolean = true;
  public authChecked: boolean = true;
  public authMode: AuthMode = 'authenticated';

  async checkAuth(): Promise<AuthCheckResponse> {
    try {
      const response = await fetch(`${API_URL}/api/auth/status`, {
        credentials: 'include',
      });

      if (!response.ok) {
        throw new Error(`Auth check failed: ${response.status}`);
      }

      const data = await response.json();

      return {
        requiresAuth: false,
        isAuthenticated: true,
        authMode: 'authenticated' as AuthMode,
        authenticationType: 'disabled',
        hasData: data.hasData,
        hasEverBeenSetup: data.hasEverBeenSetup ?? true,
        hasBeenInitialized: data.hasBeenInitialized,
        hasDataLoaded: data.hasDataLoaded,
        prefillEnabled: true,
        isBanned: false,
      };
    } catch (error) {
      console.error('[AuthService] checkAuth error:', error);
      return {
        requiresAuth: false,
        isAuthenticated: true,
        authMode: 'authenticated',
        prefillEnabled: true,
        isBanned: false,
      };
    }
  }

  getAuthHeaders(): Record<string, string> {
    return {};
  }

  async register(_apiKey: string, _localIp?: string | null): Promise<RegisterResponse> {
    try {
      const response = await fetch(`${API_URL}/api/devices`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({
          deviceId: this.deviceId,
          apiKey: _apiKey,
          deviceName: navigator.userAgent,
        }),
      });
      return await response.json();
    } catch {
      return { success: true, message: 'Registered' };
    }
  }

  async regenerateApiKey(): Promise<RegenerateKeyResponse> {
    try {
      const response = await fetch(`${API_URL}/api/api-keys/regenerate`, {
        method: 'POST',
        credentials: 'include',
      });
      return await response.json();
    } catch {
      return { success: false, message: 'Failed to regenerate API key' };
    }
  }

  getDeviceId(): string {
    return this.deviceId;
  }

  isRegistered(): boolean {
    return true;
  }

  getGuestSessionId(): string | null {
    return null;
  }

  isGuestModeActive(): boolean {
    return false;
  }

  getGuestTimeRemaining(): number {
    return 0;
  }

  onGuestExpired(_callback: (() => void) | null): void {
    // no-op
  }

  // No-ops
  startGuestMode(): void {}
  logout(): { success: boolean; message: string } {
    return { success: true, message: 'Logged out' };
  }
  handleUnauthorized(): void {}
  clearAuth(): void {}
  clearAuthAndDevice(): void {}
  expireGuestMode(): void {}
  exitGuestMode(): void {}
  setOnGuestExpired(_callback: () => void): void {}
}

const authService = new AuthService();
export default authService;
