import type { ReactNode } from 'react';
import type { Download } from '../../types';

export interface DownloadsContextType {
  latestDownloads: Download[];
  loading: boolean;
  error: string | null;
  refreshDownloads: () => Promise<void>;
  updateDownloads: (updater: {
    latestDownloads?: (prev: Download[]) => Download[];
  }) => void;
}

export interface DownloadsProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}
