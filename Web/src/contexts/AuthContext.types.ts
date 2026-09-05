import { createContext } from 'react';
import type { AccountMode, AuthMode, SessionType } from '@services/auth.service';
import type { LoginKind, LoginService } from '@utils/loginService';

interface AuthContextType {
  isAdmin: boolean;
  hasSession: boolean;
  authMode: AuthMode;
  sessionType: SessionType | null;
  sessionId: string | null;
  sessionExpiresAt: string | null;
  accountId: string | null;
  isMainAdmin: boolean;
  authenticationEnabled: boolean;
  accountMode: AccountMode;
  authenticationSetupRequired: boolean;
  oidcDisplayName: string;
  oidcPending: boolean;
  ownerOidcEnabled: boolean;
  /** False for a main administrator created by external sign-in who never set a local password. */
  ownerPasswordEnabled: boolean;
  /** Every tested connection, dormant ones included. Filter with signInServices() before showing. */
  loginServices: LoginService[];
  /** Ids from loginServices the stored owner may reauthenticate through. */
  ownerLoginServices: string[];
  loginSetupPending: boolean;
  pendingLoginKind: LoginKind | null;
  isLoading: boolean;
  login: (
    apiKey: string,
    username: string,
    password: string
  ) => Promise<{ success: boolean; message?: string }>;
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
