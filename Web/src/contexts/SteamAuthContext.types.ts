import { createContext } from 'react';
import type { IntegrationAccess } from '../types';

export type SteamAuthMode = 'anonymous' | 'authenticated';

interface SteamAuthContextType {
  steamAuthMode: SteamAuthMode;
  access: IntegrationAccess | null;
  username: string;
  isLoading: boolean;
  revision: number;
  autoLogoutMessage: string | null;
  /** The sentence for the last failed status read, `null` after a read that answered. */
  error: string | null;
  refreshSteamAuth: () => Promise<void>;
  setSteamAuthMode: (mode: SteamAuthMode) => void;
  setUsername: (username: string) => void;
  clearAutoLogoutMessage: () => void;
}

export const SteamAuthContext = createContext<SteamAuthContextType | undefined>(undefined);
