import { useSteamLoginFlow } from './useSteamLoginFlow';
import { useAuth } from '@contexts/useAuth';
import { useSteamAuth } from '@contexts/useSteamAuth';

export type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

interface SteamAuthOptions {
  autoStartPics?: boolean;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
}

export function useSteamAuthentication(options: SteamAuthOptions = {}) {
  const { autoStartPics = false, onSuccess, onError } = options;
  const { authenticationEnabled, authMode, accountId, sessionId } = useAuth();
  const { access, refreshSteamAuth } = useSteamAuth();

  return useSteamLoginFlow({
    loginUrl: '/api/steam-auth/login',
    onSuccess,
    onError,
    integration: {
      identity: JSON.stringify([authenticationEnabled, authMode, accountId, sessionId]),
      access,
      refresh: refreshSteamAuth
    },
    getExtraRequestBody: () => ({ autoStartPicsRebuild: autoStartPics })
  });
}
