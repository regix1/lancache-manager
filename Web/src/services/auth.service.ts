import { BrowserFingerprint } from '@utils/browserFingerprint';
import { getErrorMessage } from '@utils/error';

export type AuthMode = 'authenticated' | 'guest' | 'expired' | 'unauthenticated';

// Session info returned from the sessions API
interface SessionInfo {
  id: string;
  type: 'authenticated' | 'guest';
  deviceName?: string;
  createdAt?: string;
  lastActivity?: string;
}

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
  // Prefill permission for guests
  prefillEnabled?: boolean;
  prefillTimeRemaining?: number; // minutes
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
  private onGuestExpiredCallback: (() => void) | null = null;
  private isUpgrading: boolean = false; // CRITICAL: Prevent race conditions during guest->authenticated upgrade

  constructor() {
    // Generate device ID from browser fingerprint (stable across sessions)
    this.deviceId = this.getOrCreateDeviceId();
    this.apiKey = null; // API key managed by backend via HttpOnly cookies
    this.isAuthenticated = false;
    this.authChecked = false;

    console.log('[Auth] Device ID generated from browser fingerprint:', this.deviceId.substring(0, 20) + '...');
    console.log('[Auth] Authentication managed via HttpOnly cookies');

    this.startGuestModeTimer();
  }

  private getOrCreateDeviceId(): string {
    // Use browser fingerprinting to generate stable device ID (synchronous)
    // Device ID is deterministic based on browser characteristics
    // No localStorage needed - fingerprint is stable across sessions
    return BrowserFingerprint.getDeviceId();
  }

  private startGuestModeTimer(): void {
    // Check guest mode status and device validity frequently
    setInterval(() => {
      this.checkGuestModeExpiry();
      this.checkDeviceStillValid();
    }, 15000); // Check every 15 seconds for faster device revocation detection

    // Initial check
    this.checkGuestModeExpiry();
    this.checkDeviceStillValid();
  }

  private async checkDeviceStillValid(): Promise<void> {
    // CRITICAL: Skip check if upgrade is in progress to prevent race conditions
    if (this.isUpgrading) {
      console.log('[Auth] Skipping device check - upgrade in progress');
      return;
    }

    // Only check if we're authenticated
    if (this.authMode !== 'authenticated' || !this.isAuthenticated) {
      return;
    }

    try {
      // Check if our device ID still exists in the sessions list
      const response = await fetch(`${API_URL}/api/sessions`, {
        headers: this.getAuthHeaders()
      });

      // Handle 401 Unauthorized - device/API key is invalid
      if (response.status === 401) {
        console.warn('[Auth] Received 401 during device validation - authentication is invalid');
        this.handleUnauthorized();
        window.dispatchEvent(new CustomEvent('auth-session-revoked', {
          detail: { reason: 'Your session is no longer valid. Please authenticate again.' }
        }));
        return;
      }

      if (response.ok) {
        const result = await response.json();
        const sessions = result.sessions || [];

        // Check if our device ID exists in the authenticated sessions
        const ourDeviceExists = sessions.some(
          (session: SessionInfo) => session.type === 'authenticated' && session.id === this.deviceId
        );

        // If our device was deleted, clear local auth state
        if (!ourDeviceExists) {
          console.warn('[Auth] Device session was deleted - clearing local auth');
          // Don't call logout() because that tries to hit the backend which will fail
          // with 400 since the device is already revoked. Just clear local state.
          this.handleUnauthorized();
          window.dispatchEvent(new CustomEvent('auth-session-revoked', {
            detail: { reason: 'Your device session was deleted by an administrator' }
          }));
          // State update will trigger authentication modal
        }
      }
    } catch (error) {
      // Silently fail - don't log out on network errors
      console.error('[Auth] Failed to validate device session:', error);
    }
  }

  private checkGuestModeExpiry(): void {
    // Guest expiry is managed by backend via HttpOnly cookies
    // Backend will return 401 when guest session expires
    // This method kept for compatibility but no action needed
  }

  public async startGuestMode(): Promise<void> {
    // Use device ID directly as guest session ID
    const guestSessionId = this.deviceId;

    // Register guest session with backend FIRST
    // RESTful endpoint: POST /api/sessions?type=guest
    const response = await fetch(`${API_URL}/api/sessions?type=guest`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        deviceId: guestSessionId, // Send deviceId (browser fingerprint)
        deviceName: this.getDeviceName(),
        operatingSystem: this.getOperatingSystem(),
        browser: this.getBrowser()
      })
    });

    // Check if backend rejected the request (e.g., guest mode locked)
    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      const errorMessage = errorData.error || 'Failed to start guest session';
      console.error('[Auth] Guest session registration failed:', response.status, errorMessage);
      throw new Error(errorMessage);
    }

    // Only set auth mode AFTER successful registration
    this.authMode = 'guest';
    this.isAuthenticated = false; // Guest mode is not fully authenticated
    this.authChecked = true;

    console.log('[Auth] Guest session registered with backend:', guestSessionId);

    // Dispatch event to trigger preference reload now that guest session exists
    window.dispatchEvent(new CustomEvent('guest-session-created'));
  }

  public getGuestTimeRemaining(): number {
    // Guest time remaining managed by backend via cookies
    // Return 0 since we don't track this client-side anymore
    // Backend will provide this info in auth status checks if needed
    return 0;
  }

  public isGuestModeActive(): boolean {
    // Guest mode status determined by authMode state variable
    // which is set by backend auth status checks
    return this.authMode === 'guest';
  }

  public expireGuestMode(): void {
    // Guest session managed by backend via HttpOnly cookies
    // Just update local state
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
    // Guest session managed by backend via HttpOnly cookies
    // Just update local state
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
          const response = await fetch(`${API_URL}/api/auth/status`, {
            headers: this.getAuthHeaders()
          });
          const result = response.ok ? await response.json() : {};

          return {
            requiresAuth: true,
            isAuthenticated: false,
            authMode: 'guest',
            guestTimeRemaining: result.guestTimeRemaining || this.getGuestTimeRemaining(),
            hasData: result.hasData || false,
            hasEverBeenSetup: result.hasEverBeenSetup || false,
            hasBeenInitialized: result.hasBeenInitialized || false,
            hasDataLoaded: result.hasDataLoaded || false,
            prefillEnabled: result.prefillEnabled ?? false,
            prefillTimeRemaining: result.prefillTimeRemaining
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
            hasDataLoaded: false,
            prefillEnabled: false,
            prefillTimeRemaining: undefined
          };
        }
      }

      // Guest mode expiry is managed by backend via cookies
      // Backend will set authMode to 'expired' when session expires

      // Standard authentication check
      const response = await fetch(`${API_URL}/api/auth/status`, {
        headers: this.getAuthHeaders()
      });

      // Handle 401 Unauthorized - device was revoked or removed from database
      if (response.status === 401) {
        console.warn('[Auth] Device not found or revoked during auth check - clearing credentials');
        // Clear stored credentials since the device is no longer valid on backend
        this.handleUnauthorized();
        this.authChecked = true;
        return {
          requiresAuth: true,
          isAuthenticated: false,
          authMode: 'unauthenticated',
          hasData: false,
          hasEverBeenSetup: false,
          hasBeenInitialized: false,
          hasDataLoaded: false
        };
      }

      if (response.ok) {
        const result = await response.json();
        this.isAuthenticated = result.isAuthenticated;
        this.authChecked = true;

        // Priority: Use backend's authMode if provided (handles guest sessions on refresh)
        if (result.authMode === 'guest') {
          this.authMode = 'guest' as AuthMode;
        } else if (result.authMode === 'expired') {
          this.authMode = 'expired' as AuthMode;
        } else if (result.isAuthenticated) {
          this.authMode = 'authenticated' as AuthMode;
        } else {
          this.authMode = 'unauthenticated' as AuthMode;
        }

        // If device is not authenticated but auth is required, clear the stored device ID
        // This handles the case where the backend was reset
        if (
          result.requiresAuth &&
          !result.isAuthenticated &&
          result.authenticationType !== 'device' &&
          !result.authMode // Don't clear for guest sessions
        ) {
          this.clearAuthAndDevice();
        }

        const currentAuthMode: AuthMode = this.authMode;
        return {
          requiresAuth: result.requiresAuth,
          isAuthenticated: result.isAuthenticated,
          authMode: currentAuthMode,
          guestTimeRemaining: result.guestTimeRemaining || (currentAuthMode === 'guest' ? this.getGuestTimeRemaining() : undefined),
          hasData: result.hasData || false,
          hasEverBeenSetup: result.hasEverBeenSetup || false,
          hasBeenInitialized: result.hasBeenInitialized || false,
          hasDataLoaded: result.hasDataLoaded || false,
          // Prefill permission for guests
          prefillEnabled: result.prefillEnabled ?? false,
          prefillTimeRemaining: result.prefillTimeRemaining
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
    } catch (error: unknown) {
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
        error: getErrorMessage(error)
      };
    }
  }

  async register(apiKey: string, deviceName: string | null = null): Promise<RegisterResponse> {
    // CRITICAL: Set flag BEFORE starting upgrade to prevent checkDeviceStillValid() interference
    this.isUpgrading = true;
    console.log('[Auth] Starting upgrade from guest to authenticated...');

    try {
      const response = await fetch(`${API_URL}/api/devices`, {
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

        // API key stored in HttpOnly cookie by backend
        // Just update local state
        this.apiKey = apiKey; // Keep in memory for header sending
        this.isAuthenticated = true;
        this.authMode = 'authenticated';

        // CRITICAL: Wait briefly for backend to complete cleanup (delete guest session, etc.)
        // This prevents race conditions where checkDeviceStillValid() runs before cleanup is done
        await new Promise(resolve => setTimeout(resolve, 500));

        console.log('[Auth] Upgrade to authenticated complete');
        return { success: true, message: result.message };
      }

      return {
        success: false,
        message: result.message || 'Registration failed'
      };
    } catch (error: unknown) {
      console.error('Registration failed:', error);
      return {
        success: false,
        message: getErrorMessage(error) || 'Network error during registration'
      };
    } finally {
      // CRITICAL: Always clear the flag when done
      this.isUpgrading = false;
      console.log('[Auth] Upgrade process completed');
    }
  }

  async logout(): Promise<{ success: boolean; message: string }> {
    try {
      // RESTful endpoint: DELETE /api/sessions/current revokes the current session
      // Uses device ID from X-Device-Id header instead of URL parameter
      const response = await fetch(`${API_URL}/api/sessions/current`, {
        method: 'DELETE',
        headers: this.getAuthHeaders()
      });

      const result = await response.json();

      if (response.ok && result.success) {
        // Backend clears HttpOnly cookie
        // Just clear local state
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
    } catch (error: unknown) {
      console.error('Logout failed:', error);
      return {
        success: false,
        message: getErrorMessage(error) || 'Network error during logout'
      };
    }
  }

  async regenerateApiKey(): Promise<RegenerateKeyResponse> {
    try {
      // RESTful endpoint: POST /api/api-keys/regenerate
      const response = await fetch(`${API_URL}/api/api-keys/regenerate`, {
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
    } catch (error: unknown) {
      console.error('Failed to regenerate API key:', error);
      return {
        success: false,
        message: getErrorMessage(error) || 'Network error while regenerating API key'
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

    // Add API key for authenticated users (if stored in memory)
    if (this.apiKey) {
      headers['X-Api-Key'] = this.apiKey;
    }

    // Guest session ID managed by backend via HttpOnly cookies
    // No need to send in headers

    return headers;
  }

  getDeviceId(): string {
    return this.deviceId;
  }

  getGuestSessionId(): string | null {
    // Guest session ID managed by backend via HttpOnly cookies
    // Return device ID since that's what we use as guest session ID
    return this.authMode === 'guest' ? this.deviceId : null;
  }

  handleUnauthorized(): void {
    // CRITICAL: Skip if upgrade is in progress to prevent false positives
    if (this.isUpgrading) {
      console.log('[Auth] Ignoring unauthorized during upgrade process');
      return;
    }

    // Check if we're already unauthenticated (prevent interference during re-authentication)
    if (this.authMode === 'unauthenticated' && !this.isAuthenticated && !this.apiKey) {
      console.log('[Auth] Already unauthenticated - skipping unauthorized handler');
      return;
    }

    // If no API key but still marked as authenticated, we're in a zombie state - clear it
    if (!this.apiKey) {
      if (this.isAuthenticated || this.authMode !== 'unauthenticated') {
        console.warn('[Auth] Zombie state detected - no API key but still authenticated. Clearing auth state.');
        this.isAuthenticated = false;
        this.authMode = 'unauthenticated';
        window.dispatchEvent(new CustomEvent('auth-state-changed'));
      } else {
        console.log('[Auth] No API key found and already unauthenticated - skipping unauthorized handler');
      }
      return;
    }

    console.warn('[Auth] Unauthorized access detected - device was likely revoked');
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    this.apiKey = null;

    // Backend manages session via HttpOnly cookies
    // Just clear local state, no need to regenerate device ID

    // Dispatch event to trigger auth state refresh
    window.dispatchEvent(new CustomEvent('auth-state-changed'));
  }

  clearAuth(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    this.apiKey = null;
    this.exitGuestMode(); // Also clear guest mode
  }

  clearAuthAndDevice(): void {
    this.isAuthenticated = false;
    this.authMode = 'unauthenticated';
    this.apiKey = null;

    // Backend manages session via HttpOnly cookies
    // Just clear local state, device ID stays the same (from fingerprint)
  }

  isRegistered(): boolean {
    // Check if currently authenticated instead of checking localStorage
    // Backend manages registration state via cookies
    return this.isAuthenticated && this.authMode === 'authenticated';
  }
}

export default new AuthService();
