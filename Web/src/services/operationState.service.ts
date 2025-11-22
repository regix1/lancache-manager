import authService from './auth.service';
import { storage } from '@utils/storage';

interface OperationState {
  key: string;
  type: string;
  data: any;
  expirationMinutes?: number;
  createdAt?: string;
  expiresAt?: string;
}

interface StateUpdateResponse {
  success: boolean;
  state?: OperationState;
}

interface QueuedRequest {
  execute: () => Promise<any>;
  resolve: (value: any) => void;
  reject: (reason?: any) => void;
}

const getApiUrl = (): string => {
  if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();

class OperationStateService {
  private requestQueue: QueuedRequest[] = [];
  private activeRequests = 0;
  private readonly MAX_CONCURRENT_REQUESTS = 3;
  private readonly REQUEST_DELAY_MS = 100;
  private updateDebounceTimers = new Map<string, NodeJS.Timeout>();
  private readonly UPDATE_DEBOUNCE_MS = 500;
  async getState(key: string): Promise<OperationState | null> {
    try {
      const response = await fetch(`${API_URL}/api/operation-state/${key}`, {
        signal: AbortSignal.timeout(5000)
      });

      if (response.ok) {
        return await response.json();
      }

      return null;
    } catch (error: any) {
      // Silently handle errors - operation state checks are non-critical
      return null;
    }
  }

  async saveState(
    key: string,
    type: string,
    data: any,
    expirationMinutes = 30
  ): Promise<OperationState> {
    return this.queueRequest(async () => {
      try {
        const response = await this.fetchWithRetry(`${API_URL}/api/operation-state`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...authService.getAuthHeaders()
          },
          body: JSON.stringify({
            key,
            type,
            data,
            expirationMinutes
          })
        });

        if (!response.ok) {
          const error = await response.text();
          throw new Error(`Failed to save state: ${error}`);
        }

        return await response.json();
      } catch (error: any) {
        console.error('Error saving state:', error);
        throw error;
      }
    });
  }

  async updateState(key: string, updates: any): Promise<StateUpdateResponse | null> {
    return new Promise((resolve) => {
      const existingTimer = this.updateDebounceTimers.get(key);
      if (existingTimer) {
        clearTimeout(existingTimer);
      }

      const timer = setTimeout(() => {
        this.updateDebounceTimers.delete(key);
        this.queueRequest(async () => {
          try {
            const url = `${API_URL}/api/operation-state/${encodeURIComponent(key)}`;

            const response = await fetch(url, {
              method: 'PATCH',
              headers: {
                'Content-Type': 'application/json',
                ...authService.getAuthHeaders()
              },
              body: JSON.stringify({
                updates: updates || {}
              })
            });

            if (!response.ok) {
              const error = await response.text();
              console.error('Update state response:', response.status, error);
              throw new Error(`Failed to update state: ${response.status} - ${error}`);
            }

            return await response.json();
          } catch (error: any) {
            console.error('Error updating state:', error);
            return null;
          }
        }).then(resolve);
      }, this.UPDATE_DEBOUNCE_MS);

      this.updateDebounceTimers.set(key, timer);
    });
  }

  async removeState(key: string): Promise<{ success: boolean; message?: string }> {
    return this.queueRequest(async () => {
      try {
        const response = await this.fetchWithRetry(
          `${API_URL}/api/operation-state/${encodeURIComponent(key)}`,
          {
            method: 'DELETE',
            headers: authService.getAuthHeaders()
          }
        );

        if (!response.ok) {
          throw new Error('Failed to remove state');
        }

        return await response.json();
      } catch (error: any) {
        console.error('Error removing state:', error);
        throw error;
      }
    });
  }

  async getAllStates(type: string | null = null): Promise<OperationState[]> {
    try {
      const url = type
        ? `${API_URL}/api/operation-state?type=${type}`
        : `${API_URL}/api/operation-state`;

      const response = await fetch(url, {
        headers: authService.getAuthHeaders()
      });

      if (!response.ok) {
        throw new Error('Failed to get states');
      }

      return await response.json();
    } catch (error: any) {
      console.error('Error getting all states:', error);
      return [];
    }
  }

  async migrateFromLocalStorage(): Promise<number> {
    const keys = [
      'activeCacheClearOperation',
      'activeLogProcessing',
      'activeServiceRemoval',
      'activeDepotMapping'
    ];
    let migrated = 0;

    for (const key of keys) {
      try {
        const localData = storage.getItem(key);
        if (localData) {
          const parsed = JSON.parse(localData);

          let type = 'general';
          if (key.includes('CacheClear')) type = 'cacheClearing';
          else if (key.includes('LogProcessing')) type = 'logProcessing';
          else if (key.includes('ServiceRemoval')) type = 'serviceRemoval';
          else if (key.includes('DepotMapping')) type = 'depotMapping';

          await this.saveState(key, type, parsed, 120);
          storage.removeItem(key);
          migrated++;
        }
      } catch (err) {
        console.error(`Failed to migrate ${key}:`, err);
      }
    }

    if (migrated > 0) {
    }

    return migrated;
  }

  private async queueRequest<T>(request: () => Promise<T>): Promise<T> {
    return new Promise((resolve, reject) => {
      const queuedRequest: QueuedRequest = {
        execute: request,
        resolve,
        reject
      };

      this.requestQueue.push(queuedRequest);
      this.processQueue();
    });
  }

  private async processQueue(): Promise<void> {
    if (this.activeRequests >= this.MAX_CONCURRENT_REQUESTS || this.requestQueue.length === 0) {
      return;
    }

    const request = this.requestQueue.shift();
    if (!request) return;

    this.activeRequests++;

    try {
      await new Promise((resolve) => setTimeout(resolve, this.REQUEST_DELAY_MS));
      const result = await request.execute();
      request.resolve(result);
    } catch (error) {
      request.reject(error);
    } finally {
      this.activeRequests--;
      this.processQueue();
    }
  }

  private async fetchWithRetry(
    url: string,
    options: RequestInit,
    maxRetries = 3,
    initialDelay = 1000
  ): Promise<Response> {
    let lastError: any;

    for (let attempt = 0; attempt < maxRetries; attempt++) {
      try {
        const response = await fetch(url, options);

        if (response.ok || response.status < 500) {
          return response;
        }

        lastError = new Error(`HTTP ${response.status}`);
      } catch (error: any) {
        lastError = error;

        if (error.name === 'TypeError' && error.message.includes('Failed to fetch')) {
          console.log(`Retry attempt ${attempt + 1}/${maxRetries} after network error`);
        } else {
          throw error;
        }
      }

      if (attempt < maxRetries - 1) {
        const delay = initialDelay * Math.pow(2, attempt);
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }

    throw lastError;
  }
}

export default new OperationStateService();
