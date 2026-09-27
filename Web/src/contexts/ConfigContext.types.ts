import { createContext } from 'react';
import type { Config } from '../types';

interface ConfigContextType {
  config: Config;
  refreshConfig: () => Promise<void>;
  updateConfig: (patch: Partial<Config> | ((current: Config) => Partial<Config> | null)) => void;
}

export const ConfigContext = createContext<ConfigContextType | undefined>(undefined);
