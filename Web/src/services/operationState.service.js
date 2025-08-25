// services/operationState.service.js

import { fetchStateOrNull } from '../utils/silentFetch';

const API_URL = import.meta.env.VITE_API_URL || `http://${window.location.hostname}:8080`;

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
        },
        body: JSON.stringify({
          key,
          type,
          data,
          expirationMinutes
        })
      });
      
      if (!response.ok) {
        throw new Error('Failed to save state');
      }
      
      return await response.json();
    } catch (error) {
      console.error('Error saving state:', error);
      throw error;
    }
  }

  async updateState(key, updates) {
    try {
      const response = await fetch(`${API_URL}/api/operationstate/${key}`, {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          updates
        })
      });
      
      if (!response.ok) {
        throw new Error('Failed to update state');
      }
      
      return await response.json();
    } catch (error) {
      console.error('Error updating state:', error);
      throw error;
    }
  }

  async removeState(key) {
    try {
      const response = await fetch(`${API_URL}/api/operationstate/${key}`, {
        method: 'DELETE'
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
        
      const response = await fetch(url);
      
      if (!response.ok) {
        throw new Error('Failed to get states');
      }
      
      return await response.json();
    } catch (error) {
      console.error('Error getting all states:', error);
      return [];
    }
  }

  // One-time migration from localStorage to backend
  async migrateFromLocalStorage() {
    const keys = ['activeCacheClearOperation', 'activeLogProcessing', 'activeServiceRemoval'];
    let migrated = 0;
    
    for (const key of keys) {
      try {
        const localData = localStorage.getItem(key);
        if (localData) {
          const parsed = JSON.parse(localData);
          
          // Determine type based on key
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