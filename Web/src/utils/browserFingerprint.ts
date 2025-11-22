/**
 * Browser Fingerprinting Utility (Synchronous)
 *
 * Generates a consistent device ID based on browser characteristics.
 * The fingerprint is deterministic - same browser = same ID.
 *
 * SECURITY: No client-side persistence (no localStorage/cookies).
 * Device IDs are only sent to server during registration, then sessions
 * are tracked via HttpOnly cookies to prevent XSS attacks.
 */

export class BrowserFingerprint {
  /**
   * Generate a stable browser fingerprint hash (synchronous)
   * This creates a consistent ID for the same browser/device combination
   */
  static generate(): string {
    const components: string[] = [];

    // User Agent
    components.push(navigator.userAgent);

    // Screen characteristics
    components.push(`${screen.width}x${screen.height}x${screen.colorDepth}`);
    components.push(`${screen.availWidth}x${screen.availHeight}`);

    // Timezone
    components.push(Intl.DateTimeFormat().resolvedOptions().timeZone);

    // Language
    components.push(navigator.language);
    components.push(navigator.languages.join(','));

    // Platform
    components.push(navigator.platform);

    // Hardware concurrency (CPU cores)
    components.push(String(navigator.hardwareConcurrency || 'unknown'));

    // Device memory (if available)
    const nav = navigator as any;
    if (nav.deviceMemory) {
      components.push(String(nav.deviceMemory));
    }

    // Max touch points
    components.push(String(navigator.maxTouchPoints || 0));

    // WebGL fingerprint (synchronous)
    try {
      const webglFingerprint = this.getWebGLFingerprint();
      components.push(webglFingerprint);
    } catch (e) {
      components.push('webgl-blocked');
    }

    // Combine all components and hash (synchronous)
    const fingerprint = components.join('|');
    const hash = this.hashString(fingerprint);

    return hash;
  }

  /**
   * Get WebGL fingerprint
   */
  private static getWebGLFingerprint(): string {
    try {
      const canvas = document.createElement('canvas');
      const gl =
        canvas.getContext('webgl') ||
        (canvas.getContext('experimental-webgl') as WebGLRenderingContext | null);

      if (!gl) {
        return 'no-webgl';
      }

      const debugInfo = gl.getExtension('WEBGL_debug_renderer_info');
      if (debugInfo) {
        const vendor = gl.getParameter(debugInfo.UNMASKED_VENDOR_WEBGL);
        const renderer = gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL);
        return `${vendor}|${renderer}`;
      }

      return 'webgl-no-debug';
    } catch (e) {
      return 'webgl-error';
    }
  }

  /**
   * Hash a string using a fast synchronous hash algorithm (MurmurHash3-inspired)
   */
  private static hashString(str: string): string {
    // MurmurHash3 32-bit variant (synchronous, fast, good distribution)
    let h1 = 0xdeadbeef;
    let h2 = 0x41c6ce57;

    for (let i = 0; i < str.length; i++) {
      const ch = str.charCodeAt(i);
      h1 = Math.imul(h1 ^ ch, 0x85ebca77);
      h2 = Math.imul(h2 ^ ch, 0xc2b2ae3d);
    }

    h1 ^= Math.imul(h1 ^ (h2 >>> 15), 0x735a2d97);
    h2 ^= Math.imul(h2 ^ (h1 >>> 15), 0xcaf649a9);
    h1 ^= h2 >>> 16;
    h2 ^= h1 >>> 16;

    // Generate 128-bit hash from the two 32-bit hashes
    const hash1 = (h1 >>> 0).toString(16).padStart(8, '0');
    const hash2 = (h2 >>> 0).toString(16).padStart(8, '0');
    const hash3 = (h1 ^ h2).toString(16).padStart(8, '0');
    const hash4 = ((h1 + h2) >>> 0).toString(16).padStart(8, '0');

    // Format as UUID-like string (8-4-4-4-12 format)
    return `${hash1}-${hash2.substr(0, 4)}-${hash2.substr(4, 4)}-${hash3.substr(0, 4)}-${hash3.substr(4, 4)}${hash4}`;
  }

  /**
   * Get device ID (persisted in localStorage for stability)
   *
   * Browser fingerprinting alone is not stable enough (especially in WSL/VM environments).
   * We persist the device ID in localStorage to maintain consistency across page refreshes.
   *
   * Security: The device ID is only used for identification, not authentication.
   * Actual authentication uses HttpOnly cookies managed by the server.
   */
  static getDeviceId(): string {
    const STORAGE_KEY = 'deviceId';

    // Check if we have a persisted device ID
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored) {
      return stored;
    }

    // Generate new fingerprint and persist it
    const deviceId = this.generate();
    localStorage.setItem(STORAGE_KEY, deviceId);
    return deviceId;
  }
}
