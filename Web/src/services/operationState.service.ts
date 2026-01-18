import authService from './auth.service';
import { storage } from '@utils/storage';

// Operation state data can be various shapes depending on the operation type
type OperationStateData = Record<string, unknown>;

interface OperationState {
  key: string;
  type: string;
  data: OperationStateData;
  expirationMinutes?: number;
  createdAt?: string;
  expiresAt?: string;
}



interface QueuedRequest<T = unknown> {
  execute: () => Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
}

const getApiUrl = (): string => {
  if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();

class OperationStateService {
  private requestQueue: QueuedRequest<unknown>[] = [];
  private activeRequests = 0;
  private readonly MAX_CONCURRENT_REQUESTS = 3;
  private readonly REQUEST_DELAY_MS = 100;

  

  async saveState(
    key: string,
    type: string,
    data: OperationStateData,
    expirationMinutes = 30
  ): Promise<OperationState> {
    return this.queueRequest(async () => {
      try {
        const response = await this.fetchWithRetry(`${API_URL}/api/operation-state`, {
          method: 'POST',
          credentials: 'include',
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
      } catch (error: unknown) {
        console.error('Error saving state:', error);
        throw error;
      }
    });
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
      const queuedRequest: QueuedRequest<T> = {
        execute: request,
        resolve,
        reject
      };

      this.requestQueue.push(queuedRequest as QueuedRequest<unknown>);
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
    let lastError: unknown;

    for (let attempt = 0; attempt < maxRetries; attempt++) {
      try {
        const response = await fetch(url, options);

        if (response.ok || response.status < 500) {
          return response;
        }

        lastError = new Error(`HTTP ${response.status}`);
      } catch (error: unknown) {
        lastError = error;

        // Check for network error that should trigger retry
        if (
          error instanceof TypeError &&
          error.message.includes('Failed to fetch')
        ) {
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
