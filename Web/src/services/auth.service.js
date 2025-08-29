// services/auth.service.js

const getApiUrl = () => {
  if (import.meta.env.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();

class AuthService {
  constructor() {
    this.deviceId = this.getOrCreateDeviceId();
    this.isAuthenticated = false;
    this.authChecked = false;
  }

  // Generate or retrieve device ID
  getOrCreateDeviceId() {
    let deviceId = localStorage.getItem('lancache_device_id');
    if (!deviceId) {
      // Generate a UUID v4
      deviceId = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
        const r = Math.random() * 16 | 0;
        const v = c === 'x' ? r : (r & 0x3 | 0x8);
        return v.toString(16);
      });
      localStorage.setItem('lancache_device_id', deviceId);
    }
    return deviceId;
  }

  // Check if we're authenticated
  async checkAuth() {
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
    } catch (error) {
      console.error('Auth check failed:', error);
      this.authChecked = true;
      return { requiresAuth: false, isAuthenticated: false, error: error.message };
    }
  }

  // Register device with API key
  async register(apiKey, deviceName = null) {
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
    } catch (error) {
      console.error('Registration failed:', error);
      return { 
        success: false, 
        message: error.message || 'Network error during registration' 
      };
    }
  }

  // Regenerate API key on server (requires current authentication)
  async regenerateApiKey() {
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
        // Clear local authentication since the old key is now invalid
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
    } catch (error) {
      console.error('Failed to regenerate API key:', error);
      return { 
        success: false, 
        message: error.message || 'Network error while regenerating API key' 
      };
    }
  }

  // Get device name from browser info
  getDeviceName() {
    const userAgent = navigator.userAgent;
    let os = 'Unknown OS';
    let browser = 'Unknown Browser';

    // Detect OS
    if (userAgent.indexOf('Win') !== -1) os = 'Windows';
    else if (userAgent.indexOf('Mac') !== -1) os = 'macOS';
    else if (userAgent.indexOf('Linux') !== -1) os = 'Linux';
    else if (userAgent.indexOf('Android') !== -1) os = 'Android';
    else if (userAgent.indexOf('iOS') !== -1) os = 'iOS';

    // Detect Browser
    if (userAgent.indexOf('Chrome') !== -1) browser = 'Chrome';
    else if (userAgent.indexOf('Safari') !== -1) browser = 'Safari';
    else if (userAgent.indexOf('Firefox') !== -1) browser = 'Firefox';
    else if (userAgent.indexOf('Edge') !== -1) browser = 'Edge';

    return `${browser} on ${os}`;
  }

  // Get auth headers for API requests
  getAuthHeaders() {
    return {
      'X-Device-Id': this.deviceId
    };
  }

  // Handle 401 response
  handleUnauthorized() {
    this.isAuthenticated = false;
    // Don't clear the device ID, just mark as not authenticated
    localStorage.removeItem('lancache_auth_registered');
  }

  // Clear authentication (for logout)
  clearAuth() {
    this.isAuthenticated = false;
    localStorage.removeItem('lancache_auth_registered');
    // Optionally clear device ID to force re-registration
    // localStorage.removeItem('lancache_device_id');
  }

  // Check if we think we're registered (for quick UI checks)
  isRegistered() {
    return localStorage.getItem('lancache_auth_registered') === 'true';
  }
}

export default new AuthService();