import React, { useState, useEffect, useCallback, type ReactNode } from 'react';
import { ConfigContext } from './ConfigContext.types';
import type { Config } from '../types';
import ApiService from '../services/api.service';
import LoadingSpinner from '../components/common/LoadingSpinner';
import { Button } from '@components/ui/Button';
import { API_BASE } from '../utils/constants';

interface ConfigProviderProps {
  children: ReactNode;
}

interface ConfigLoadError {
  message: string;
  isTimeout: boolean;
}

const CONFIG_TIMEOUT_MS = 8000;

export const ConfigProvider: React.FC<ConfigProviderProps> = ({ children }) => {
  const [config, setConfig] = useState<Config | null>(null);
  const [error, setError] = useState<ConfigLoadError | null>(null);

  const loadConfig = useCallback(async (): Promise<void> => {
    setError(null);

    const controller = new AbortController();
    const timeoutId = window.setTimeout(() => {
      controller.abort();
    }, CONFIG_TIMEOUT_MS);

    try {
      const response = await fetch(
        `${API_BASE}/system/config`,
        ApiService.getFetchOptions({ signal: controller.signal })
      );
      const data = await ApiService.handleResponse<Config>(response);
      setConfig(data);
    } catch (err: unknown) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        console.error('[ConfigProvider] Config request timed out after', CONFIG_TIMEOUT_MS, 'ms');
        setError({
          message:
            'Configuration request timed out after 8 seconds. Please check your connection and try again.',
          isTimeout: true
        });
      } else {
        console.error('[ConfigProvider] Failed to load config:', err);
        const message =
          err instanceof Error
            ? err.message
            : 'Failed to load configuration. Please check your connection and try again.';
        setError({ message, isTimeout: false });
      }
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, []);

  const refreshConfig = useCallback(async (): Promise<void> => {
    await loadConfig();
  }, [loadConfig]);

  useEffect(() => {
    void loadConfig();
  }, [loadConfig]);

  if (error !== null && config === null) {
    return (
      <div className="config-error-screen">
        <div className="config-error-card">
          <h2 className="config-error-title">
            {error.isTimeout ? 'Configuration Request Timed Out' : 'Failed to Load Configuration'}
          </h2>
          <p className="config-error-message">{error.message}</p>
          <Button onClick={() => void loadConfig()}>Retry</Button>
        </div>
      </div>
    );
  }

  if (!config) {
    return <LoadingSpinner fullScreen message="Loading configuration..." />;
  }

  return (
    <ConfigContext.Provider value={{ config, refreshConfig }}>{children}</ConfigContext.Provider>
  );
};
