import { createContext } from 'react';
import type { Config } from '../types';

export interface ConfigContextType {
  config: Config;
  refreshConfig: () => Promise<void>;
  updateConfig: (patch: Partial<Config>) => void;
}

export const ConfigContext = createContext<ConfigContextType | undefined>(undefined);
