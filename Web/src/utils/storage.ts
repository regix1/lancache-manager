// storage.ts - Production-safe localStorage/sessionStorage wrapper

/**
 * Safe storage wrapper that handles exceptions gracefully.
 * Prevents app crashes in private browsing mode, quota exceeded, or security restrictions.
 * The backing store is read through a thunk because even the property access
 * `localStorage` / `sessionStorage` throws when site data is blocked.
 */
class SafeStorage {
  private available: boolean;
  private memoryFallback: Map<string, string>;
  private backing: () => Storage;

  constructor(backing: () => Storage) {
    this.backing = backing;
    this.available = this.checkAvailability();
    this.memoryFallback = new Map<string, string>();
  }

  /**
   * Check if the backing store is available
   */
  private checkAvailability(): boolean {
    try {
      const test = '__storage_test__';
      this.backing().setItem(test, test);
      this.backing().removeItem(test);
      return true;
    } catch (error) {
      console.warn('Browser storage is not available, using memory fallback:', error);
      return false;
    }
  }

  /**
   * Get an item from storage
   */
  getItem(key: string): string | null {
    if (this.available) {
      try {
        return this.backing().getItem(key);
      } catch (error) {
        console.error(`Failed to get item from storage (${key}):`, error);
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
        this.backing().setItem(key, value);
        // Also update memory fallback as backup
        this.memoryFallback.set(key, value);
      } catch (error) {
        // QuotaExceededError or SecurityError
        console.error(`Failed to set item in storage (${key}):`, error);
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
        this.backing().removeItem(key);
      } catch (error) {
        console.error(`Failed to remove item from storage (${key}):`, error);
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
        this.backing().clear();
      } catch (error) {
        console.error('Failed to clear storage:', error);
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
      console.error(`Failed to parse JSON from storage (${key}):`, error);
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
      console.error(`Failed to stringify JSON for storage (${key}):`, error);
    }
  }

  /**
   * Check if storage is available
   */
  isAvailable(): boolean {
    return this.available;
  }
}

// Export singleton instances
export const storage = new SafeStorage(() => localStorage);
export const sessionStore = new SafeStorage(() => sessionStorage);
