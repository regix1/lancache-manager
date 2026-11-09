import { createContext } from 'react';
import type { AccountMode, AuthMode, SessionType } from '@services/auth.service';
import type { LoginKind, LoginService } from '@utils/loginService';

/** Wire names of `loginServices[].environmentFields`: settings a `Security__*` variable supplies. */
export type EnvironmentField =
  | 'clientId'
  | 'clientSecret'
  | 'authority'
  | 'displayName'
  | 'allowedSubjects'
  | 'ownerSubject'
  | 'ownerIssuer'
  | 'tenant'
  | 'teamId'
  | 'keyId'
  | 'privateKey';

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
  /** True when `Security__SignInMode` decides the mode, so the mode picker is read-only. */
  signInModeFromEnvironment: boolean;
  oidcDisplayName: string;
  oidcPending: boolean;
  ownerOidcEnabled: boolean;
  /** False for a main administrator created by external sign-in who never set a local password. */
  ownerPasswordEnabled: boolean;
  /** Every tested connection, dormant ones included. Filter with signInServices() before showing. */
  loginServices: LoginService[];
  /** Services environment variables set but left incomplete, so they are not in loginServices. */
  incompleteLoginServices: LoginService[];
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
