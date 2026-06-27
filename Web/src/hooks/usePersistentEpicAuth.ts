import { useCallback } from 'react';
import { usePersistentPrefillAuth } from './usePersistentPrefillAuth';
import type { EpicAuthActions, EpicAuthState } from './useEpicMappingAuth';

interface PersistentEpicAuthState extends EpicAuthState {
  error: string | null;
  authenticated: boolean;
}

interface UsePersistentEpicAuthOptions {
  timeoutSeconds?: number;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export function usePersistentEpicAuth(options: UsePersistentEpicAuthOptions = {}) {
  const { state: coreState, actions: coreActions } = usePersistentPrefillAuth({
    ...options,
    service: 'Epic'
  });

  const startLogin = useCallback(async (): Promise<void> => {
    coreActions.resetAuthForm();
    await coreActions.start();
  }, [coreActions]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (!coreState.needsAuthorizationCode) {
      await startLogin();
      return false;
    }

    const authorizationCode = coreState.authorizationCode.trim();
    if (!authorizationCode) {
      return false;
    }

    return coreActions.submit(authorizationCode);
  }, [coreActions, coreState.authorizationCode, coreState.needsAuthorizationCode, startLogin]);

  const state: PersistentEpicAuthState = {
    loading: coreState.loading,
    needsAuthorizationCode: coreState.needsAuthorizationCode,
    authorizationUrl: coreState.authorizationUrl,
    authorizationCode: coreState.authorizationCode,
    error: coreState.error,
    authenticated: coreState.authenticated
  };

  const actions: EpicAuthActions = {
    setAuthorizationCode: coreActions.setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm: coreActions.resetAuthForm,
    cancelPendingRequest: coreActions.cancelPendingRequest
  };

  return { state, actions, startLogin };
}
