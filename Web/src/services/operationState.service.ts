import { fetchStateOrNull } from '../utils/silentFetch';
import authService from './auth.service';

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

const getApiUrl = (): string => {
  if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();

class OperationStateService {
  async getState(key: string): Promise<OperationState | null> {
    const result = await fetchStateOrNull(`${API_URL}/api/operationstate/${key}`);
    return result.ok ? result.data : null;
  }

  async saveState(
    key: string, 
    type: string, 
    data: any, 
    expirationMinutes: number = 30
  ): Promise<OperationState> {
    try {
      const response = await fetch(`${API_URL}/api/operationstate`, {
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
  }

  async updateState(key: string, updates: any): Promise<StateUpdateResponse | null> {
    try {
      const url = `${API_URL}/api/operationstate/${encodeURIComponent(key)}`;
      console.log('Updating state at:', url);
      
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
  }

  async removeState(key: string): Promise<{ success: boolean; message?: string }> {
    try {
      const response = await fetch(`${API_URL}/api/operationstate/${encodeURIComponent(key)}`, {
        method: 'DELETE',
        headers: authService.getAuthHeaders()
      });
      
      if (!response.ok) {
        throw new Error('Failed to remove state');
      }
      
      return await response.json();
    } catch (error: any) {
      console.error('Error removing state:', error);
      throw error;
    }
  }

  async getAllStates(type: string | null = null): Promise<OperationState[]> {
    try {
      const url = type 
        ? `${API_URL}/api/operationstate?type=${type}`
        : `${API_URL}/api/operationstate`;
        
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
    const keys = ['activeCacheClearOperation', 'activeLogProcessing', 'activeServiceRemoval'];
    let migrated = 0;
    
    for (const key of keys) {
      try {
        const localData = localStorage.getItem(key);
        if (localData) {
          const parsed = JSON.parse(localData);
          
          let type = 'general';
          if (key.includes('CacheClear')) type = 'cacheClearing';
          else if (key.includes('LogProcessing')) type = 'logProcessing';
          else if (key.includes('ServiceRemoval')) type = 'serviceRemoval';
          
          await this.saveState(key, type, parsed, 120);
          localStorage.removeItem(key);
          migrated++;
          console.log(`Migrated ${key} from localStorage to backend`);
        }
      } catch (err) {
        console.error(`Failed to migrate ${key}:`, err);
      }
    }
    
    if (migrated > 0) {
      console.log(`Successfully migrated ${migrated} operations to backend storage`);
    }
    
    return migrated;
  }
}

export default new OperationStateService();