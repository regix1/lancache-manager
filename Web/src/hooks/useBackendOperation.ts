import { useState, useCallback, useEffect } from 'react';
import operationStateService from '../services/operationState.service';

interface OperationState {
  key: string;
  type: string;
  data: any;
  createdAt: string;
  updatedAt: string;
}

interface UseBackendOperationReturn {
  operation: OperationState | null;
  save: (data: any) => Promise<OperationState>;
  load: () => Promise<OperationState | null>;
  clear: () => Promise<void>;
  update: (updates: any) => Promise<void>;
  loading: boolean;
  error: string | null;
}

export const useBackendOperation = (
  key: string,
  type = 'general',
  expirationMinutes = 30
): UseBackendOperationReturn => {
  const [operation, setOperation] = useState<OperationState | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = useCallback(
    async (data: any): Promise<OperationState> => {
      setLoading(true);
      setError(null);

      try {
        await operationStateService.saveState(key, type, data, expirationMinutes);

        const newState: OperationState = {
          key,
          type,
          data,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString()
        };
        setOperation(newState);

        return newState;
      } catch (err: any) {
        console.error(`Failed to save ${key}:`, err);
        setError(err.message);
        throw err;
      } finally {
        setLoading(false);
      }
    },
    [key, type, expirationMinutes]
  );

  const load = useCallback(async (): Promise<OperationState | null> => {
    setLoading(true);
    setError(null);

    try {
      const state = await operationStateService.getState(key);

      if (state) {
        // Type the state as any to access properties
        const stateData = state as any;

        // Ensure the state has all required properties
        const operationState: OperationState = {
          key: stateData.key || key,
          type: stateData.type || type,
          data: stateData.data || {},
          createdAt: stateData.createdAt || new Date().toISOString(),
          updatedAt: stateData.updatedAt || stateData.createdAt || new Date().toISOString()
        };
        setOperation(operationState);
        return operationState;
      }

      return null;
    } catch (err: any) {
      console.error(`Failed to load ${key}:`, err);
      setError(err.message);
      return null;
    } finally {
      setLoading(false);
    }
  }, [key, type]);

  const clear = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);

    try {
      await operationStateService.removeState(key);
      setOperation(null);
    } catch (err: any) {
      console.error(`Failed to clear ${key}:`, err);
      setError(err.message);
      throw err;
    } finally {
      setLoading(false);
    }
  }, [key]);

  const update = useCallback(
    async (updates: any): Promise<void> => {
      setLoading(true);
      setError(null);

      try {
        await operationStateService.updateState(key, updates);

        setOperation((prev) => {
          if (!prev) return null;

          return {
            ...prev,
            data: { ...prev.data, ...updates },
            updatedAt: new Date().toISOString()
          };
        });
      } catch (err: any) {
        console.error(`Failed to update ${key}:`, err);
        setError(err.message);
        throw err;
      } finally {
        setLoading(false);
      }
    },
    [key]
  );

  useEffect(() => {
    if (key) {
      load();
    }
  }, [key]); // eslint-disable-line react-hooks/exhaustive-deps

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
