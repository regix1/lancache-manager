import { createContext } from 'react';
import type { CacheSizeInfo } from '@/types';

export interface CacheSizeContextType {
  cacheSize: CacheSizeInfo | null;
  isLoading: boolean;
  hasFetched: boolean;
  error: string | null;
  /**
   * Why the server declined to scan, when it declined rather than failed. Kept apart from
   * `error` so a refusal shows as a notice beside the last known size instead of replacing it,
   * and so it cannot block the consumer's one-shot mount fetch.
   */
  denialReason: string | null;
  fetchCacheSize: (force?: boolean) => Promise<void>;
  clearCacheSize: () => void;
}

export const CacheSizeContext = createContext<CacheSizeContextType | undefined>(undefined);
