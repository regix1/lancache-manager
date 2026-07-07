import { useSteamLoginFlow } from './useSteamLoginFlow';

export type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

interface SteamAuthOptions {
  autoStartPics?: boolean;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
  /** See useSteamLoginFlow: universal-notification login lifecycle (Integrations page only). */
  loginStatusNotifications?: boolean;
}

export function useSteamAuthentication(options: SteamAuthOptions = {}) {
  const { autoStartPics = false, onSuccess, onError, loginStatusNotifications } = options;

  return useSteamLoginFlow({
    loginUrl: '/api/steam-auth/login',
    onSuccess,
    onError,
    loginStatusNotifications,
    getExtraRequestBody: () => ({ autoStartPicsRebuild: autoStartPics })
  });
}
