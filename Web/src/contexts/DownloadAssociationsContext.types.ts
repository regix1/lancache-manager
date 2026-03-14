import { createContext } from 'react';
import type { EventSummary } from '../types';

interface DownloadAssociations {
  events: EventSummary[];
}

type AssociationsCache = Record<number, DownloadAssociations>;

export interface DownloadAssociationsContextType {
  associations: AssociationsCache;
  loading: boolean;
  fetchAssociations: (downloadIds: number[]) => Promise<void>;
  getAssociations: (downloadId: number) => DownloadAssociations;
  clearCache: () => void;
  /** Increments when cache is invalidated - include in useEffect deps to trigger re-fetch */
  refreshVersion: number;
}

export const DownloadAssociationsContext = createContext<
  DownloadAssociationsContextType | undefined
>(undefined);
