import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { ConfigContext } from './ConfigContext.types';
import type { Config } from '../types';
import ApiService from '../services/api.service';
import LoadingSpinner from '../components/common/LoadingSpinner';
import { Button } from '@components/ui/Button';
import { API_BASE } from '../utils/constants';
import { getErrorMessage } from '../utils/error';

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
  const configRef = useRef<Config | null>(null);
  configRef.current = config;

  const loadConfig = useCallback(async (options?: { isRefresh?: boolean }): Promise<void> => {
    const isRefresh = options?.isRefresh ?? false;
    if (!isRefresh) {
      setError(null);
    }

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
      if (isRefresh && configRef.current) {
        // Background refresh - keep serving the last-good cached config. Deliberately silent;
        // not user-actionable and the app already has working config to render.
        console.warn('[ConfigProvider] Config refresh failed, keeping cached config:', err);
        return;
      }

      if (err instanceof DOMException && err.name === 'AbortError') {
        console.error('[ConfigProvider] Config request timed out after', CONFIG_TIMEOUT_MS, 'ms');
        setError({
          message:
            'Configuration request timed out after 8 seconds. Please check your connection and try again.',
          isTimeout: true
        });
      } else {
        console.error('[ConfigProvider] Failed to load config:', err);
        // Never render the raw error message - extract via the shared helper so an ApiError's
        // parsed backend body wins over a generic Error/TypeError string.
        const message =
          getErrorMessage(err) ||
          'Failed to load configuration. Please check your connection and try again.';
        setError({ message, isTimeout: false });
      }
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, []);

  const refreshConfig = useCallback(async (): Promise<void> => {
    await loadConfig({ isRefresh: true });
  }, [loadConfig]);

  const updateConfig = useCallback((patch: Partial<Config>) => {
    setConfig((prev) => (prev ? { ...prev, ...patch } : prev));
  }, []);

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
    <ConfigContext.Provider value={{ config, refreshConfig, updateConfig }}>
      {children}
    </ConfigContext.Provider>
  );
};
