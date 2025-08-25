// hooks/useBackendOperation.js

import { useState, useCallback, useEffect } from 'react';
import operationStateService from '../services/operationState.service';

export const useBackendOperation = (key, type = 'general', expirationMinutes = 30) => {
  const [operation, setOperation] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const save = useCallback(async (data) => {
    setLoading(true);
    setError(null);
    
    try {
      await operationStateService.saveState(key, type, data, expirationMinutes);
      
      // Update local state
      const newState = {
        key,
        type,
        data,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      };
      setOperation(newState);
      
      return newState;
    } catch (err) {
      console.error(`Failed to save ${key}:`, err);
      setError(err.message);
      throw err;
    } finally {
      setLoading(false);
    }
  }, [key, type, expirationMinutes]);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    
    try {
      const state = await operationStateService.getState(key);
      
      if (state) {
        setOperation(state);
        return state;
      }
      
      return null;
    } catch (err) {
      console.error(`Failed to load ${key}:`, err);
      setError(err.message);
      return null;
    } finally {
      setLoading(false);
    }
  }, [key]);

  const clear = useCallback(async () => {
    setLoading(true);
    setError(null);
    
    try {
      await operationStateService.removeState(key);
      setOperation(null);
    } catch (err) {
      console.error(`Failed to clear ${key}:`, err);
      setError(err.message);
      throw err;
    } finally {
      setLoading(false);
    }
  }, [key]);

  const update = useCallback(async (updates) => {
    setLoading(true);
    setError(null);
    
    try {
      await operationStateService.updateState(key, updates);
      
      // Update local state
      setOperation(prev => {
        if (!prev) return null;
        
        return {
          ...prev,
          data: { ...prev.data, ...updates },
          updatedAt: new Date().toISOString()
        };
      });
    } catch (err) {
      console.error(`Failed to update ${key}:`, err);
      setError(err.message);
      throw err;
    } finally {
      setLoading(false);
    }
  }, [key]);

  // Load on mount
  useEffect(() => {
    // Only load if key is provided
    if (key) {
      load();
    }
  }, [key]);

  return { 
    operation, 
    save, 
    load, 
    clear, 
    update,
    loading,
    error
  };
};