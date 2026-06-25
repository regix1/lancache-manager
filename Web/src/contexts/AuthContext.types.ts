import { createContext } from 'react';
import type { AuthMode, SessionType } from '@services/auth.service';

interface AuthContextType {
  isAdmin: boolean;
  hasSession: boolean;
  authMode: AuthMode;
  sessionType: SessionType | null;
  sessionId: string | null;
  sessionExpiresAt: string | null;
  authenticationEnabled: boolean;
  isLoading: boolean;
  login: (apiKey: string) => Promise<{ success: boolean; message?: string }>;
  startGuestSession: () => Promise<{ success: boolean; message?: string }>;
  logout: () => Promise<void>;
  refreshAuth: () => Promise<void>;
  setAuthMode: (mode: AuthMode) => void;
  prefillEnabled: boolean;
  prefillTimeRemaining: number | null;
  steamPrefillEnabled: boolean;
  epicPrefillEnabled: boolean;
  battlenetPrefillEnabled: boolean;
  riotPrefillEnabled: boolean;
  xboxPrefillEnabled: boolean;
  isBanned: boolean;
}

export const AuthContext = createContext<AuthContextType | undefined>(undefined);
