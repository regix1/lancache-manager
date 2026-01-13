// storage.ts - Production-safe localStorage wrapper

/**
 * Safe localStorage wrapper that handles exceptions gracefully.
 * Prevents app crashes in private browsing mode, quota exceeded, or security restrictions.
 */
class SafeStorage {
  private available: boolean;
  private memoryFallback: Map<string, string>;

  constructor() {
    this.available = this.checkAvailability();
    this.memoryFallback = new Map<string, string>();
  }

  /**
   * Check if localStorage is available
   */
  private checkAvailability(): boolean {
    try {
      const test = '__storage_test__';
      localStorage.setItem(test, test);
      localStorage.removeItem(test);
      return true;
    } catch (error) {
      console.warn('localStorage is not available, using memory fallback:', error);
      return false;
    }
  }

  /**
   * Get an item from storage
   */
  getItem(key: string): string | null {
    if (this.available) {
      try {
        return localStorage.getItem(key);
      } catch (error) {
        console.error(`Failed to get item from localStorage (${key}):`, error);
        return this.memoryFallback.get(key) || null;
      }
    }
    return this.memoryFallback.get(key) || null;
  }

  /**
   * Set an item in storage
   */
  setItem(key: string, value: string): void {
    if (this.available) {
      try {
        localStorage.setItem(key, value);
        // Also update memory fallback as backup
        this.memoryFallback.set(key, value);
      } catch (error) {
        // QuotaExceededError or SecurityError
        console.error(`Failed to set item in localStorage (${key}):`, error);
        // Fall back to memory storage
        this.memoryFallback.set(key, value);
      }
    } else {
      this.memoryFallback.set(key, value);
    }
  }

  /**
   * Remove an item from storage
   */
  removeItem(key: string): void {
    if (this.available) {
      try {
        localStorage.removeItem(key);
      } catch (error) {
        console.error(`Failed to remove item from localStorage (${key}):`, error);
      }
    }
    this.memoryFallback.delete(key);
  }

  /**
   * Clear all items from storage
   */
  clear(): void {
    if (this.available) {
      try {
        localStorage.clear();
      } catch (error) {
        console.error('Failed to clear localStorage:', error);
      }
    }
    this.memoryFallback.clear();
  }

  /**
   * Get a parsed JSON object from storage
   */
  getJSON<T>(key: string, defaultValue?: T): T | null {
    const item = this.getItem(key);
    if (!item) {
      return defaultValue !== undefined ? defaultValue : null;
    }

    try {
      return JSON.parse(item) as T;
    } catch (error) {
      console.error(`Failed to parse JSON from localStorage (${key}):`, error);
      return defaultValue !== undefined ? defaultValue : null;
    }
  }

  /**
   * Set a JSON object in storage
   */
  setJSON<T>(key: string, value: T): void {
    try {
      const json = JSON.stringify(value);
      this.setItem(key, json);
    } catch (error) {
      console.error(`Failed to stringify JSON for localStorage (${key}):`, error);
    }
  }

  /**
   * Check if storage is available
   */
  isAvailable(): boolean {
    return this.available;
  }
}

// Export a singleton instance
export const storage = new SafeStorage();

