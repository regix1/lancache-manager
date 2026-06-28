import { useSteamLoginFlow } from './useSteamLoginFlow';

interface ScheduledPrefillSteamAuthOptions {
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
}

export function useScheduledPrefillSteamAuth(options: ScheduledPrefillSteamAuthOptions = {}) {
  const { onSuccess, onError } = options;

  return useSteamLoginFlow({
    loginUrl: '/api/system/schedules/scheduledPrefill/steam/login',
    onSuccess,
    onError
  });
}
