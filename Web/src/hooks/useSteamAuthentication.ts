import { useSteamLoginFlow } from './useSteamLoginFlow';

export type { SteamAuthActions, SteamLoginFlowState } from './steamAuthTypes';

interface SteamAuthOptions {
  autoStartPics?: boolean;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
}

export function useSteamAuthentication(options: SteamAuthOptions = {}) {
  const { autoStartPics = false, onSuccess, onError } = options;

  return useSteamLoginFlow({
    loginUrl: '/api/steam-auth/login',
    onSuccess,
    onError,
    getExtraRequestBody: () => ({ autoStartPicsRebuild: autoStartPics })
  });
}
