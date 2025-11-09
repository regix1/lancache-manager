import { storage } from '@utils/storage';
import { BrowserFingerprint } from '@utils/browserFingerprint';

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
    // Initialize device ID synchronously with fingerprint
    this.deviceId = this.getOrCreateDeviceId();
    this.apiKey = storage.getItem('lancache_api_key');
    this.isAuthenticated = false;
    this.authChecked = false;

    // Log API key status for diagnostics
    if (this.apiKey) {
      console.log('[Auth] API key loaded from localStorage:', this.apiKey.substring(0, 20) + '...');
    } else {
      console.log('[Auth] No API key found in localStorage');
    }

    this.startGuestModeTimer();
  }

  private getOrCreateDeviceId(): string {
    try {
      // Use browser fingerprinting to generate stable device ID (synchronous)
      return BrowserFingerprint.getOrCreateDeviceId();
    } catch (error) {
      console.warn('[Auth] Failed to generate browser fingerprint, using fallback:', error);
      // Fallback to random UUID if fingerprinting fails
      let deviceId = storage.getItem('lancache_device_id');
      if (!deviceId) {
        deviceId = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
          const r = (Math.random() * 16) | 0;
          const v = c === 'x' ? r : (r & 0x3) | 0x8;
          return v.toString(16);
        });
        storage.setItem('lancache_device_id', deviceId);
      }
      return deviceId;
    }
  }

  private startGuestModeTimer(): void {
    // Check guest mode status every minute
    this.guestCheckInterval = setInterval(() => {
      this.checkGuestModeExpiry();
      this.checkDeviceStillValid();
    }, 60000); // Check every minute

    // Initial check
    this.checkGuestModeExpiry();
  }

  private async checkDeviceStillValid(): Promise<void> {
    // Only check if we're authenticated
    if (this.authMode !== 'authenticated' || !this.isAuthenticated) {
      return;
    }

    try {
      // Check if our device ID still exists in the sessions list
      const response = await fetch(`${API_URL}/api/auth/sessions`, {
        headers: this.getAuthHeaders()
      });

      if (response.ok) {
        const result = await response.json();
        const sessions = result.sessions || [];

        // Check if our device ID exists in the authenticated sessions
        const ourDeviceExists = sessions.some(
          (session: any) => session.type === 'authenticated' && session.id === this.deviceId
        );

        // If our device was deleted, force logout
        if (!ourDeviceExists) {
          console.warn('[Auth] Device session was deleted - forcing logout');
          this.logout();
          window.dispatchEvent(new CustomEvent('auth-session-revoked', {
            detail: { reason: 'Your device session was deleted by an administrator' }
          }));
          // Reload to show login screen
          window.location.reload();
        }
      }
    } catch (error) {
      // Silently fail - don't log out on network errors
      console.error('[Auth] Failed to validate device session:', error);
    }
  }

  private checkGuestModeExpiry(): void {
    const guestExpires = storage.getItem('lancache_guest_expires');
    if (!guestExpires) return;

    const expiryTime = parseInt(guestExpires);
    const now = Date.now();

    if (now >= expiryTime) {
      this.expireGuestMode();
    }
  }

  public async startGuestMode(): Promise<void> {
    const now = Date.now();

    // Fetch guest session duration from backend (default to 6 hours if fetch fails)
    let durationHours = 6;
    try {
      const durationResponse = await fetch(`${API_URL}/api/auth/guest/config/duration`);
      if (durationResponse.ok) {
        const durationData = await durationResponse.json();
        durationHours = durationData.durationHours || 6;
      }
    } catch (error) {
      console.warn('[Auth] Failed to fetch guest session duration, using default 6 hours:', error);
    }

    const durationInMs = durationHours * 60 * 60 * 1000;
    const expiryTime = now + durationInMs;

    // Generate a unique guest session ID
    const guestSessionId = `guest_${this.deviceId}_${now}`;

    storage.setItem('lancache_guest_session_id', guestSessionId);
    storage.setItem('lancache_guest_session_start', now.toString());
    storage.setItem('lancache_guest_expires', expiryTime.toString());

    this.authMode = 'guest';
    this.isAuthenticated = false; // Guest mode is not fully authenticated
    this.authChecked = true;

    // Register guest session with backend
    try {
      await fetch(`${API_URL}/api/auth/guest/register`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          sessionId: guestSessionId,
          deviceName: this.getDeviceName(),
          operatingSystem: this.getOperatingSystem(),
          browser: this.getBrowser()
        })
      });
      console.log(
        '[Auth] Guest session registered with backend:',
        guestSessionId,
        'Duration:',
        durationHours,
        'hours'
      );
    } catch (error) {
      console.warn('[Auth] Failed to register guest session with backend:', error);
      // Continue with guest mode even if backend registration fails
    }
  }

  public getGuestTimeRemaining(): number {
    const guestExpires = storage.getItem('lancache_guest_expires');
    if (!guestExpires) return 0;

    const expiryTime = parseInt(guestExpires);
    const now = Date.now();
    const remainingMs = Math.max(0, expiryTime - now);

    return Math.floor(remainingMs / (60 * 1000)); // Return minutes
  }

  public isGuestModeActive(): boolean {
    const guestExpires = storage.getItem('lancache_guest_expires');
    if (!guestExpires) return false;

    const expiryTime = parseInt(guestExpires);
    return Date.now() < expiryTime;
  }

  public expireGuestMode(): void {
    storage.removeItem('lancache_guest_session_start');
    storage.removeItem('lancache_guest_expires');
    storage.removeItem('lancache_guest_session_id'); // Remove session ID to prevent further requests
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
    storage.removeItem('lancache_guest_session_start');
    storage.removeItem('lancache_guest_expires');
    storage.removeItem('lancache_guest_session_id'); // Remove session ID to prevent conflicts
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
            headers: this.getAuthHeaders()
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
      const hasExpiredGuest = storage.getItem('lancache_guest_expires');
      if (hasExpiredGuest) {
        this.authMode = 'expired';
        this.isAuthenticated = false;
        this.authChecked = true;

        // Fetch hasData and hasEverBeenSetup
        try {
          const response = await fetch(`${API_URL}/api/auth/check`, {
            headers: this.getAuthHeaders()
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
        headers: this.getAuthHeaders()
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
        if (
          result.requiresAuth &&
          !result.isAuthenticated &&
          result.authenticationType !== 'device'
        ) {
          this.clearAuthAndDevice();
        }

        const currentAuthMode: AuthMode = this.authMode;
        return {
          requiresAuth: result.requiresAuth,
          isAuthenticated: result.isAuthenticated,
          authMode: currentAuthMode,
          guestTimeRemaining:
            currentAuthMode === 'guest' ? this.getGuestTimeRemaining() : undefined,
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
        storage.setItem('lancache_api_key', apiKey);

        this.isAuthenticated = true;
        this.authMode = 'authenticated';
        storage.setItem('lancache_auth_registered', 'true');
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

  async logout(): Promise<{ success: boolean; message: string }> {
    try {
      const response = await fetch(`${API_URL}/api/auth/logout`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...this.getAuthHeaders()
        }
      });

      const result = await response.json();

      if (response.ok && result.success) {
        this.clearAuthAndDevice();
        this.isAuthenticated = false;
        this.authMode = 'unauthenticated';

        return {
          success: true,
          message: result.message || 'Logged out successfully'
        };
      }

      return {
        success: false,
        message: result.error || result.message || 'Logout failed'
      };
    } catch (error: any) {
      console.error('Logout failed:', error);
      return {
        success: false,
        message: error.message || 'Network error during logout'
      };
    }
  }

  async regenerateApiKey(): Promise<RegenerateKeyResponse> {
    try {
      const response = await fetch(`${API_URL}/api/auth/regenerate-key`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...this.getAuthHeaders()
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

  private getOperatingSystem(): string {
    const userAgent = navigator.userAgent;

    // Windows detection with version
    if (userAgent.indexOf('Windows NT 10.0') !== -1) return 'Windows 10/11';
    if (userAgent.indexOf('Windows NT 6.3') !== -1) return 'Windows 8.1';
    if (userAgent.indexOf('Windows NT 6.2') !== -1) return 'Windows 8';
    if (userAgent.indexOf('Windows NT 6.1') !== -1) return 'Windows 7';
    if (userAgent.indexOf('Win') !== -1) return 'Windows';

    // macOS detection with version
    const macMatch = userAgent.match(/Mac OS X (\d+)[._](\d+)/);
    if (macMatch) {
      return `macOS ${macMatch[1]}.${macMatch[2]}`;
    }
    if (userAgent.indexOf('Mac') !== -1) return 'macOS';

    // Linux/Android/iOS
    if (userAgent.indexOf('Android') !== -1) {
      const androidMatch = userAgent.match(/Android (\d+(\.\d+)?)/);
      return androidMatch ? `Android ${androidMatch[1]}` : 'Android';
    }
    if (userAgent.indexOf('Linux') !== -1) return 'Linux';
    if (userAgent.indexOf('iOS') !== -1) return 'iOS';

    return 'Unknown OS';
  }

  private getBrowser(): string {
    const userAgent = navigator.userAgent;

    // Edge (check first as it also contains "Chrome")
    const edgeMatch = userAgent.match(/Edg\/(\d+\.\d+\.\d+\.\d+)/);
    if (edgeMatch) return `Edge ${edgeMatch[1]}`;

    // Chrome
    const chromeMatch = userAgent.match(/Chrome\/(\d+\.\d+\.\d+\.\d+)/);
    if (chromeMatch && userAgent.indexOf('Edg') === -1) return `Chrome ${chromeMatch[1]}`;

    // Firefox
    const firefoxMatch = userAgent.match(/Firefox\/(\d+\.\d+)/);
    if (firefoxMatch) return `Firefox ${firefoxMatch[1]}`;

    // Safari (check after Chrome as Chrome also contains "Safari")
    const safariMatch = userAgent.match(/Version\/(\d+\.\d+)/);
    if (safariMatch && userAgent.indexOf('Safari') !== -1 && userAgent.indexOf('Chrome') === -1) {
      return `Safari ${safariMatch[1]}`;
    }

    // Fallback
    if (userAgent.indexOf('Chrome') !== -1) return 'Chrome';
    if (userAgent.indexOf('Safari') !== -1) return 'Safari';
    if (userAgent.indexOf('Firefox') !== -1) return 'Firefox';
    if (userAgent.indexOf('Edge') !== -1) return 'Edge';

    return 'Unknown Browser';
  }

  getAuthHeaders(): Record<string, string> {
    const headers: Record<string, string> = {
      'X-Device-Id': this.deviceId // Always send device ID for identification
    };

    // Add API key for authenticated users
    if (this.apiKey) {
      headers['X-Api-Key'] = this.apiKey;
    }

    // Include guest session ID if in guest mode
    const guestSessionId = storage.getItem('lancache_guest_session_id');
    if (guestSessionId && this.authMode === 'guest') {
      headers['X-Guest-Session-Id'] = guestSessionId;
    }

    return headers;
  }

  getDeviceId(): string {
    return this.deviceId;
  }

  getGuestSessionId(): string | null {
    return storage.getItem('lancache_guest_session_id');
  }

  handleUnauthorized(): void {
    console.warn(
      '[Auth] Unauthorized access detected - device was likely revoked. Forcing reload...'
    );
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    storage.removeItem('lancache_auth_registered');
    storage.removeItem('lancache_api_key');
    this.apiKey = null;
    // Clear device ID so a new one is generated on next request
    // This handles API key regeneration scenarios where all devices are revoked
    storage.removeItem('lancache_device_id');
    this.deviceId = this.getOrCreateDeviceId(); // Re-generate with fingerprint (synchronous)

    // Force page reload to show authentication modal
    setTimeout(() => {
      window.location.reload();
    }, 500); // Small delay to ensure state is cleared
  }

  clearAuth(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    storage.removeItem('lancache_auth_registered');
    storage.removeItem('lancache_api_key');
    this.apiKey = null;
    this.exitGuestMode(); // Also clear guest mode
  }

  clearAuthAndDevice(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    storage.removeItem('lancache_auth_registered');
    storage.removeItem('lancache_api_key');
    this.apiKey = null;
    storage.removeItem('lancache_device_id');
    this.deviceId = this.getOrCreateDeviceId(); // Re-generate with fingerprint (synchronous)
    this.exitGuestMode(); // Also clear guest mode
  }

  cleanup(): void {
    if (this.guestCheckInterval) {
      clearInterval(this.guestCheckInterval);
      this.guestCheckInterval = null;
    }
  }

  isRegistered(): boolean {
    return storage.getItem('lancache_auth_registered') === 'true';
  }
}

export default new AuthService();
