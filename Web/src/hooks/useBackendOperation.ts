import { useState, useCallback, useEffect } from 'react';
import operationStateService from '../services/operationState.service';

interface OperationState<T = unknown> {
  key: string;
  type: string;
  data: T;
  createdAt: string;
  updatedAt: string;
}

interface UseBackendOperationReturn<T = unknown> {
  operation: OperationState<T> | null;
  save: (data: T) => Promise<OperationState<T>>;
  load: () => Promise<OperationState<T> | null>;
  clear: () => Promise<void>;
  update: (updates: Partial<T>) => Promise<void>;
  loading: boolean;
  error: string | null;
}

export const useBackendOperation = <T = unknown>(
  key: string,
  type = 'general',
  expirationMinutes = 30
): UseBackendOperationReturn<T> => {
  const [operation, setOperation] = useState<OperationState<T> | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = useCallback(
    async (data: T): Promise<OperationState<T>> => {
      setLoading(true);
      setError(null);

      try {
        await operationStateService.saveState(key, type, data, expirationMinutes);

        const newState: OperationState<T> = {
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

  const load = useCallback(async (): Promise<OperationState<T> | null> => {
    setLoading(true);
    setError(null);

    try {
      const state = await operationStateService.getState(key);

      if (state) {
        // Use type assertion with proper validation
        const stateData = state as unknown as Record<string, unknown>;

        // Ensure the state has all required properties
        const operationState: OperationState<T> = {
          key: (stateData.key as string) || key,
          type: (stateData.type as string) || type,
          data: (stateData.data as T) ?? ({} as T),
          createdAt: (stateData.createdAt as string) || new Date().toISOString(),
          updatedAt: (stateData.updatedAt as string) || (stateData.createdAt as string) || new Date().toISOString()
        };
        setOperation(operationState);
        return operationState;
      }

      return null;
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Unknown error';
      console.error(`Failed to load ${key}:`, err);
      setError(errorMessage);
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
    async (updates: Partial<T>): Promise<void> => {
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
