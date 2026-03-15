import { createContext } from 'react';
import type { CacheSizeInfo } from '@/types';

export interface CacheSizeContextType {
  cacheSize: CacheSizeInfo | null;
  isLoading: boolean;
  error: string | null;
  fetchCacheSize: (force?: boolean) => Promise<void>;
  clearCacheSize: () => void;
}

export const CacheSizeContext = createContext<CacheSizeContextType | undefined>(undefined);
