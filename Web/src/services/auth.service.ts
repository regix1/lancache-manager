interface AuthCheckResponse {
  requiresAuth: boolean;
  isAuthenticated: boolean;
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
  public isAuthenticated: boolean;
  public authChecked: boolean;

  constructor() {
    this.deviceId = this.getOrCreateDeviceId();
    this.isAuthenticated = false;
    this.authChecked = false;
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

  async checkAuth(): Promise<AuthCheckResponse> {
    try {
      const response = await fetch(`${API_URL}/api/auth/check`, {
        headers: {
          'X-Device-Id': this.deviceId
        }
      });

      if (response.ok) {
        const result = await response.json();
        this.isAuthenticated = result.isAuthenticated;
        this.authChecked = true;
        return result;
      }

      this.isAuthenticated = false;
      this.authChecked = true;
      return { requiresAuth: true, isAuthenticated: false };
    } catch (error: any) {
      console.error('Auth check failed:', error);
      this.authChecked = true;
      return { requiresAuth: false, isAuthenticated: false, error: error.message };
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
        this.isAuthenticated = true;
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
        this.clearAuth();
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
    return {
      'X-Device-Id': this.deviceId
    };
  }

  handleUnauthorized(): void {
    this.isAuthenticated = false;
    localStorage.removeItem('lancache_auth_registered');
  }

  clearAuth(): void {
    this.isAuthenticated = false;
    localStorage.removeItem('lancache_auth_registered');
  }

  isRegistered(): boolean {
    return localStorage.getItem('lancache_auth_registered') === 'true';
  }
}

export default new AuthService();
