import { fetchStateOrNull } from '../utils/silentFetch';
import authService from './auth.service';

const getApiUrl = () => {
  if (import.meta.env.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  return '';
};

const API_URL = getApiUrl();

class OperationStateService {
  async getState(key) {
    const result = await fetchStateOrNull(`${API_URL}/api/operationstate/${key}`);
    return result.ok ? result.data : null;
  }

  async saveState(key, type, data, expirationMinutes = 30) {
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
    } catch (error) {
      console.error('Error saving state:', error);
      throw error;
    }
  }

  async updateState(key, updates) {
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
    } catch (error) {
      console.error('Error updating state:', error);
      // Don't throw - just log and continue
      return null;
    }
  }

  async removeState(key) {
    try {
      const response = await fetch(`${API_URL}/api/operationstate/${encodeURIComponent(key)}`, {
        method: 'DELETE',
        headers: authService.getAuthHeaders()
      });
      
      if (!response.ok) {
        throw new Error('Failed to remove state');
      }
      
      return await response.json();
    } catch (error) {
      console.error('Error removing state:', error);
      throw error;
    }
  }

  async getAllStates(type = null) {
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
    } catch (error) {
      console.error('Error getting all states:', error);
      return [];
    }
  }

  async migrateFromLocalStorage() {
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