import { useCallback, useRef } from 'react';
import { usePersistentPrefillAuth } from './usePersistentPrefillAuth';
import type { XboxAuthActions, XboxAuthState } from './useXboxMappingAuth';

interface PersistentXboxAuthState extends XboxAuthState {
  error: string | null;
  authenticated: boolean;
}

interface UsePersistentXboxAuthOptions {
  timeoutSeconds?: number;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export function usePersistentXboxAuth(options: UsePersistentXboxAuthOptions = {}) {
  const { state: coreState, actions: coreActions } = usePersistentPrefillAuth({
    ...options,
    service: 'Xbox'
  });
  const pollGenerationRef = useRef(0);

  const pollUntilAuthenticated = useCallback(
    async (generation: number): Promise<boolean> => {
      while (pollGenerationRef.current === generation) {
        const result = await coreActions.poll();
        if (result.status === 'authenticated') {
          return true;
        }
      }

      return false;
    },
    [coreActions]
  );

  const startLogin = useCallback(async (): Promise<void> => {
    pollGenerationRef.current += 1;
    const generation = pollGenerationRef.current;
    coreActions.resetAuthForm();

    const challenge = await coreActions.start();
    if (challenge?.credentialType === 'device-code') {
      void pollUntilAuthenticated(generation).catch(() => {
        /* poll failure already surfaced via state.error */
      });
    }
  }, [coreActions, pollUntilAuthenticated]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    await startLogin();
    return false;
  }, [startLogin]);

  const resetAuthForm = useCallback(() => {
    pollGenerationRef.current += 1;
    coreActions.resetAuthForm();
  }, [coreActions]);

  const cancelLogin = useCallback(async (): Promise<void> => {
    pollGenerationRef.current += 1;
    await coreActions.cancel();
  }, [coreActions]);

  const cancelPendingRequest = useCallback(() => {
    void cancelLogin();
  }, [cancelLogin]);

  const state: PersistentXboxAuthState = {
    loading: coreState.loading,
    needsDeviceCode: coreState.needsDeviceCode,
    deviceUserCode: coreState.deviceUserCode,
    deviceVerificationUri: coreState.deviceVerificationUri,
    error: coreState.error,
    authenticated: coreState.authenticated
  };

  const actions: XboxAuthActions = {
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin, cancelLogin };
}
