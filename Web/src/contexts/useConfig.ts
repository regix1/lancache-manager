import { useContext } from 'react';
import { ConfigContext, type ConfigContextType } from './ConfigContext.types';

export function useConfig(): ConfigContextType {
  const context = useContext(ConfigContext);
  if (!context) {
    throw new Error('useConfig must be used within ConfigProvider');
  }
  return context;
}
