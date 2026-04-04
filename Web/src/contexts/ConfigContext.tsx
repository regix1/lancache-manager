import React, { useState, useEffect, useCallback, type ReactNode } from 'react';
import { ConfigContext } from './ConfigContext.types';
import type { Config } from '../types';
import ApiService from '../services/api.service';
import LoadingSpinner from '../components/common/LoadingSpinner';
import { Button } from '@components/ui/Button';

interface ConfigProviderProps {
  children: ReactNode;
}

export const ConfigProvider: React.FC<ConfigProviderProps> = ({ children }) => {
  const [config, setConfig] = useState<Config | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refreshConfig = useCallback(async () => {
    try {
      setError(null);
      const data = await ApiService.getConfig();
      setConfig(data);
    } catch (err: unknown) {
      console.error('[ConfigProvider] Failed to load config:', err);
      setError('Failed to load configuration. Please check your connection and try again.');
    }
  }, []);

  useEffect(() => {
    refreshConfig();
  }, [refreshConfig]);

  if (error) {
    return (
      <div className="config-error-screen">
        <div className="config-error-card">
          <p className="config-error-message">{error}</p>
          <Button onClick={refreshConfig}>Retry</Button>
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
