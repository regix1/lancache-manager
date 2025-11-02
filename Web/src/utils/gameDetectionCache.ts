/**
 * IndexedDB wrapper for caching game detection results
 * Supports much larger datasets than localStorage/sessionStorage
 */

import type { GameCacheInfo } from '../types';

const DB_NAME = 'LancacheManager';
const DB_VERSION = 1;
const STORE_NAME = 'detectedGames';

interface CachedGameData {
  games: GameCacheInfo[];
  totalGamesDetected: number;
  timestamp: number;
}

class GameDetectionCache {
  private dbPromise: Promise<IDBDatabase> | null = null;

  private async getDB(): Promise<IDBDatabase> {
    if (this.dbPromise) {
      return this.dbPromise;
    }

    this.dbPromise = new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION);

      request.onerror = () => {
        reject(new Error('Failed to open IndexedDB'));
      };

      request.onsuccess = () => {
        resolve(request.result);
      };

      request.onupgradeneeded = (event) => {
        const db = (event.target as IDBOpenDBRequest).result;

        // Create object store if it doesn't exist
        if (!db.objectStoreNames.contains(STORE_NAME)) {
          db.createObjectStore(STORE_NAME);
        }
      };
    });

    return this.dbPromise;
  }

  async saveGames(games: GameCacheInfo[], totalGamesDetected: number): Promise<void> {
    try {
      const db = await this.getDB();
      const transaction = db.transaction([STORE_NAME], 'readwrite');
      const store = transaction.objectStore(STORE_NAME);

      const data: CachedGameData = {
        games,
        totalGamesDetected,
        timestamp: Date.now()
      };

      store.put(data, 'current');

      return new Promise((resolve, reject) => {
        transaction.oncomplete = () => {
          console.log(`[IndexedDB] Saved ${games.length} games`);
          resolve();
        };
        transaction.onerror = () => {
          reject(new Error('Failed to save games to IndexedDB'));
        };
      });
    } catch (error) {
      console.error('[IndexedDB] Failed to save games:', error);
      throw error;
    }
  }

  async loadGames(): Promise<CachedGameData | null> {
    try {
      const db = await this.getDB();
      const transaction = db.transaction([STORE_NAME], 'readonly');
      const store = transaction.objectStore(STORE_NAME);
      const request = store.get('current');

      return new Promise((resolve, reject) => {
        request.onsuccess = () => {
          const data = request.result as CachedGameData | undefined;
          if (data) {
            console.log(`[IndexedDB] Loaded ${data.games.length} games`);
            resolve(data);
          } else {
            resolve(null);
          }
        };
        request.onerror = () => {
          reject(new Error('Failed to load games from IndexedDB'));
        };
      });
    } catch (error) {
      console.error('[IndexedDB] Failed to load games:', error);
      return null;
    }
  }

  // Load only summary data without game list (for quick initialization)
  async loadSummary(): Promise<{ totalGamesDetected: number; timestamp: number } | null> {
    try {
      const db = await this.getDB();
      const transaction = db.transaction([STORE_NAME], 'readonly');
      const store = transaction.objectStore(STORE_NAME);
      const request = store.get('current');

      return new Promise((resolve, reject) => {
        request.onsuccess = () => {
          const data = request.result as CachedGameData | undefined;
          if (data) {
            console.log(`[IndexedDB] Loaded summary: ${data.totalGamesDetected} games`);
            resolve({
              totalGamesDetected: data.totalGamesDetected,
              timestamp: data.timestamp
            });
          } else {
            resolve(null);
          }
        };
        request.onerror = () => {
          reject(new Error('Failed to load summary from IndexedDB'));
        };
      });
    } catch (error) {
      console.error('[IndexedDB] Failed to load summary:', error);
      return null;
    }
  }

  async removeGame(gameAppId: number): Promise<void> {
    try {
      const data = await this.loadGames();
      if (!data) return;

      const updatedGames = data.games.filter(g => g.game_app_id !== gameAppId);
      await this.saveGames(updatedGames, updatedGames.length);
      console.log(`[IndexedDB] Removed game ${gameAppId}`);
    } catch (error) {
      console.error('[IndexedDB] Failed to remove game:', error);
      throw error;
    }
  }

  async clear(): Promise<void> {
    try {
      const db = await this.getDB();
      const transaction = db.transaction([STORE_NAME], 'readwrite');
      const store = transaction.objectStore(STORE_NAME);
      store.delete('current');

      return new Promise((resolve, reject) => {
        transaction.oncomplete = () => {
          console.log('[IndexedDB] Cleared all games');
          resolve();
        };
        transaction.onerror = () => {
          reject(new Error('Failed to clear IndexedDB'));
        };
      });
    } catch (error) {
      console.error('[IndexedDB] Failed to clear:', error);
      throw error;
    }
  }
}

// Export singleton instance
export const gameDetectionCache = new GameDetectionCache();
