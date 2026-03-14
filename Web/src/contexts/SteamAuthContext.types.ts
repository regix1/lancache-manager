import { createContext } from 'react';

type SteamAuthMode = 'anonymous' | 'authenticated';

interface SteamAuthContextType {
  steamAuthMode: SteamAuthMode;
  username: string;
  isLoading: boolean;
  autoLogoutMessage: string | null;
  refreshSteamAuth: () => Promise<void>;
  setSteamAuthMode: (mode: SteamAuthMode) => void;
  setUsername: (username: string) => void;
  clearAutoLogoutMessage: () => void;
}

export const SteamAuthContext = createContext<SteamAuthContextType | undefined>(undefined);
