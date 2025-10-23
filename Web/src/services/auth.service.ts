export type AuthMode = 'authenticated' | 'guest' | 'expired' | 'unauthenticated';

interface AuthCheckResponse {
  requiresAuth: boolean;
  isAuthenticated: boolean;
  authMode: AuthMode;
  guestTimeRemaining?: number; // minutes
  hasData?: boolean; // Whether database has any data (for guest mode eligibility)
  hasEverBeenSetup?: boolean; // Whether anyone has ever authenticated (system is set up)
  hasBeenInitialized?: boolean; // Whether setup has been completed (persistent flag)
  hasDataLoaded?: boolean; // Whether depot data has been loaded from state.json
  error?: string;
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
  private deviceId: string;
  private apiKey: string | null = null;
  public isAuthenticated: boolean;
  public authChecked: boolean;
  public authMode: AuthMode = 'unauthenticated';
  private guestCheckInterval: NodeJS.Timeout | null = null;
  private onGuestExpiredCallback: (() => void) | null = null;

  constructor() {
    this.deviceId = this.getOrCreateDeviceId();
    this.apiKey = localStorage.getItem('lancache_api_key');
    this.isAuthenticated = false;
    this.authChecked = false;
    this.startGuestModeTimer();
  }

  private getOrCreateDeviceId(): string {
    let deviceId = localStorage.getItem('lancache_device_id');
    if (!deviceId) {
      deviceId = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
        const r = (Math.random() * 16) | 0;
        const v = c === 'x' ? r : (r & 0x3) | 0x8;
        return v.toString(16);
      });
      localStorage.setItem('lancache_device_id', deviceId);
    }
    return deviceId;
  }

  private startGuestModeTimer(): void {
    // Check guest mode status every minute
    this.guestCheckInterval = setInterval(() => {
      this.checkGuestModeExpiry();
    }, 60000); // Check every minute

    // Initial check
    this.checkGuestModeExpiry();
  }

  private checkGuestModeExpiry(): void {
    const guestExpires = localStorage.getItem('lancache_guest_expires');
    if (!guestExpires) return;

    const expiryTime = parseInt(guestExpires);
    const now = Date.now();

    if (now >= expiryTime) {
      this.expireGuestMode();
    }
  }

  public startGuestMode(): void {
    const now = Date.now();
    const sixHoursInMs = 6 * 60 * 60 * 1000; // 6 hours
    const expiryTime = now + sixHoursInMs;

    localStorage.setItem('lancache_guest_session_start', now.toString());
    localStorage.setItem('lancache_guest_expires', expiryTime.toString());

    this.authMode = 'guest';
    this.isAuthenticated = false; // Guest mode is not fully authenticated
    this.authChecked = true;
  }

  public getGuestTimeRemaining(): number {
    const guestExpires = localStorage.getItem('lancache_guest_expires');
    if (!guestExpires) return 0;

    const expiryTime = parseInt(guestExpires);
    const now = Date.now();
    const remainingMs = Math.max(0, expiryTime - now);

    return Math.floor(remainingMs / (60 * 1000)); // Return minutes
  }

  public isGuestModeActive(): boolean {
    const guestExpires = localStorage.getItem('lancache_guest_expires');
    if (!guestExpires) return false;

    const expiryTime = parseInt(guestExpires);
    return Date.now() < expiryTime;
  }

  public expireGuestMode(): void {
    localStorage.removeItem('lancache_guest_session_start');
    localStorage.removeItem('lancache_guest_expires');
    this.authMode = 'expired';
    this.isAuthenticated = false;

    // Notify callbacks about expiry
    if (this.onGuestExpiredCallback) {
      this.onGuestExpiredCallback();
    }
  }

  public onGuestExpired(callback: (() => void) | null): void {
    this.onGuestExpiredCallback = callback;
  }

  public exitGuestMode(): void {
    localStorage.removeItem('lancache_guest_session_start');
    localStorage.removeItem('lancache_guest_expires');
    this.authMode = 'unauthenticated';
    this.isAuthenticated = false;
  }

  async checkAuth(): Promise<AuthCheckResponse> {
    try {
      // Check if guest mode is active first (before making any network calls)
      if (this.isGuestModeActive()) {
        this.authMode = 'guest';
        this.isAuthenticated = false;
        this.authChecked = true;

        // Still need to fetch hasData and hasEverBeenSetup for guest mode eligibility
        try {
          const response = await fetch(`${API_URL}/api/auth/check`, {
            headers: { 'X-Device-Id': this.deviceId }
          });
          const result = response.ok ? await response.json() : {};

          return {
            requiresAuth: true,
            isAuthenticated: false,
            authMode: 'guest',
            guestTimeRemaining: this.getGuestTimeRemaining(),
            hasData: result.hasData || false,
            hasEverBeenSetup: result.hasEverBeenSetup || false,
            hasBeenInitialized: result.hasBeenInitialized || false,
            hasDataLoaded: result.hasDataLoaded || false
          };
        } catch {
          return {
            requiresAuth: true,
            isAuthenticated: false,
            authMode: 'guest',
            guestTimeRemaining: this.getGuestTimeRemaining(),
            hasData: false,
            hasEverBeenSetup: false,
            hasBeenInitialized: false,
            hasDataLoaded: false
          };
        }
      }

      // Check for expired guest mode
      const hasExpiredGuest = localStorage.getItem('lancache_guest_expires');
      if (hasExpiredGuest) {
        this.authMode = 'expired';
        this.isAuthenticated = false;
        this.authChecked = true;

        // Fetch hasData and hasEverBeenSetup
        try {
          const response = await fetch(`${API_URL}/api/auth/check`, {
            headers: { 'X-Device-Id': this.deviceId }
          });
          const result = response.ok ? await response.json() : {};

          return {
            requiresAuth: true,
            isAuthenticated: false,
            authMode: 'expired',
            hasData: result.hasData || false,
            hasEverBeenSetup: result.hasEverBeenSetup || false,
            hasBeenInitialized: result.hasBeenInitialized || false,
            hasDataLoaded: result.hasDataLoaded || false
          };
        } catch {
          return {
            requiresAuth: true,
            isAuthenticated: false,
            authMode: 'expired',
            hasData: false,
            hasEverBeenSetup: false,
            hasBeenInitialized: false,
            hasDataLoaded: false
          };
        }
      }

      // Standard authentication check
      const response = await fetch(`${API_URL}/api/auth/check`, {
        headers: {
          'X-Device-Id': this.deviceId
        }
      });

      if (response.ok) {
        const result = await response.json();
        this.isAuthenticated = result.isAuthenticated;
        this.authChecked = true;

        if (result.isAuthenticated) {
          this.authMode = 'authenticated' as AuthMode;
        } else {
          this.authMode = 'unauthenticated' as AuthMode;
        }

        // If device is not authenticated but auth is required, clear the stored device ID
        // This handles the case where the backend was reset
        if (result.requiresAuth && !result.isAuthenticated && result.authenticationType !== 'device') {
          this.clearAuthAndDevice();
        }

        const currentAuthMode: AuthMode = this.authMode;
        return {
          requiresAuth: result.requiresAuth,
          isAuthenticated: result.isAuthenticated,
          authMode: currentAuthMode,
          guestTimeRemaining: currentAuthMode === 'guest' ? this.getGuestTimeRemaining() : undefined,
          hasData: result.hasData || false,
          hasEverBeenSetup: result.hasEverBeenSetup || false,
          hasBeenInitialized: result.hasBeenInitialized || false,
          hasDataLoaded: result.hasDataLoaded || false
        };
      }

      this.isAuthenticated = false;
      this.authChecked = true;
      this.authMode = 'unauthenticated';
      return {
        requiresAuth: true,
        isAuthenticated: false,
        authMode: 'unauthenticated',
        hasData: false,
        hasEverBeenSetup: false,
        hasBeenInitialized: false,
        hasDataLoaded: false
      };
    } catch (error: any) {
      console.error('Auth check failed:', error);
      this.isAuthenticated = false;
      this.authChecked = true;
      this.authMode = 'unauthenticated';
      // If we can't reach the backend, assume authentication is required
      return {
        requiresAuth: true,
        isAuthenticated: false,
        authMode: this.authMode,
        hasData: false,
        hasEverBeenSetup: false,
        hasBeenInitialized: false,
        hasDataLoaded: false,
        error: error.message
      };
    }
  }

  async register(apiKey: string, deviceName: string | null = null): Promise<RegisterResponse> {
    try {
      const response = await fetch(`${API_URL}/api/auth/register`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          deviceId: this.deviceId,
          apiKey: apiKey,
          deviceName: deviceName || this.getDeviceName()
        })
      });

      const result = await response.json();

      if (response.ok && result.success) {
        // Clear any existing guest session FIRST when successfully authenticated
        this.exitGuestMode();

        // Store the API key for future requests
        this.apiKey = apiKey;
        localStorage.setItem('lancache_api_key', apiKey);

        this.isAuthenticated = true;
        this.authMode = 'authenticated';
        localStorage.setItem('lancache_auth_registered', 'true');
        return { success: true, message: result.message };
      }

      return {
        success: false,
        message: result.message || 'Registration failed'
      };
    } catch (error: any) {
      console.error('Registration failed:', error);
      return {
        success: false,
        message: error.message || 'Network error during registration'
      };
    }
  }

  async regenerateApiKey(): Promise<RegenerateKeyResponse> {
    try {
      const response = await fetch(`${API_URL}/api/auth/regenerate-key`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Device-Id': this.deviceId
        }
      });

      const result = await response.json();

      if (response.ok && result.success) {
        this.clearAuthAndDevice();
        this.isAuthenticated = false;

        return {
          success: true,
          message: result.message,
          warning: result.warning
        };
      }

      return {
        success: false,
        message: result.message || 'Failed to regenerate API key'
      };
    } catch (error: any) {
      console.error('Failed to regenerate API key:', error);
      return {
        success: false,
        message: error.message || 'Network error while regenerating API key'
      };
    }
  }

  private getDeviceName(): string {
    const userAgent = navigator.userAgent;
    let os = 'Unknown OS';
    let browser = 'Unknown Browser';

    if (userAgent.indexOf('Win') !== -1) os = 'Windows';
    else if (userAgent.indexOf('Mac') !== -1) os = 'macOS';
    else if (userAgent.indexOf('Linux') !== -1) os = 'Linux';
    else if (userAgent.indexOf('Android') !== -1) os = 'Android';
    else if (userAgent.indexOf('iOS') !== -1) os = 'iOS';

    if (userAgent.indexOf('Chrome') !== -1) browser = 'Chrome';
    else if (userAgent.indexOf('Safari') !== -1) browser = 'Safari';
    else if (userAgent.indexOf('Firefox') !== -1) browser = 'Firefox';
    else if (userAgent.indexOf('Edge') !== -1) browser = 'Edge';

    return `${browser} on ${os}`;
  }

  getAuthHeaders(): Record<string, string> {
    // Prefer API key over Device ID for authentication
    if (this.apiKey) {
      return {
        'X-Api-Key': this.apiKey
      };
    }
    // Fallback to Device ID for guest mode
    return {
      'X-Device-Id': this.deviceId
    };
  }

  getDeviceId(): string {
    return this.deviceId;
  }

  handleUnauthorized(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    localStorage.removeItem('lancache_auth_registered');
    localStorage.removeItem('lancache_api_key');
    this.apiKey = null;
    // Clear device ID so a new one is generated on next request
    // This handles API key regeneration scenarios where all devices are revoked
    localStorage.removeItem('lancache_device_id');
    this.deviceId = this.getOrCreateDeviceId();
  }

  clearAuth(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    localStorage.removeItem('lancache_auth_registered');
    localStorage.removeItem('lancache_api_key');
    this.apiKey = null;
    this.exitGuestMode(); // Also clear guest mode
  }

  clearAuthAndDevice(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    localStorage.removeItem('lancache_auth_registered');
    localStorage.removeItem('lancache_api_key');
    this.apiKey = null;
    localStorage.removeItem('lancache_device_id');
    this.deviceId = this.getOrCreateDeviceId();
    this.exitGuestMode(); // Also clear guest mode
  }

  cleanup(): void {
    if (this.guestCheckInterval) {
      clearInterval(this.guestCheckInterval);
      this.guestCheckInterval = null;
    }
  }

  isRegistered(): boolean {
    return localStorage.getItem('lancache_auth_registered') === 'true';
  }
}

export default new AuthService();
