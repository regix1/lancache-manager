import { useCallback } from 'react';
import { usePersistentPrefillAuth } from './usePersistentPrefillAuth';
import {
  getPersistentLoginEditAction,
  getPersistentLoginSessionId,
  getPersistentLoginStartRequest,
  setPersistentLoginStartRequest
} from '@components/features/management/schedules/scheduled-prefill/persistentLoginStore';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import type { XboxAuthActions, XboxAuthState } from './useXboxMappingAuth';

interface PersistentXboxAuthState extends XboxAuthState {
  error: string | null;
  authenticated: boolean;
  hasChallenge: boolean;
  dismissed: boolean;
}

interface UsePersistentXboxAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export function usePersistentXboxAuth(options: UsePersistentXboxAuthOptions = {}) {
  const { state: coreState, actions: coreActions } = usePersistentPrefillAuth({
    ...options,
    service: 'Xbox'
  });

  // The device-code approval arrives as AuthStateChanged and any other ending as DaemonSessionUpdated
  // (usePersistentLoginChallengeSignalR), so nothing here asks the server for the result.
  const startLogin = useCallback(async (): Promise<CredentialChallenge | null> => {
    const activeEditAction = getPersistentLoginEditAction('Xbox');
    const activeSessionId = getPersistentLoginSessionId('Xbox');
    const requestedStart =
      getPersistentLoginStartRequest('Xbox') ??
      (activeEditAction && activeSessionId
        ? { sessionId: activeSessionId, ...activeEditAction }
        : undefined);
    coreActions.resetAuthForm();
    if (requestedStart) {
      setPersistentLoginStartRequest('Xbox', requestedStart);
    }

    return coreActions.start();
  }, [coreActions]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    await startLogin();
    return false;
  }, [startLogin]);

  const cancelPendingRequest = useCallback(() => {
    void coreActions.cancel();
  }, [coreActions]);

  const state: PersistentXboxAuthState = {
    loading: coreState.loading,
    needsDeviceCode: coreState.needsDeviceCode,
    deviceUserCode: coreState.deviceUserCode,
    deviceVerificationUri: coreState.deviceVerificationUri,
    error: coreState.error,
    authenticated: coreState.authenticated,
    hasChallenge: coreState.hasChallenge,
    dismissed: coreState.dismissed
  };

  const actions: XboxAuthActions = {
    handleAuthenticate,
    resetAuthForm: coreActions.resetAuthForm,
    cancelPendingRequest
  };

  return {
    state,
    actions,
    startLogin,
    cancelLogin: coreActions.cancel,
    dismissModal: coreActions.dismissModal,
    resumeModal: coreActions.resumeModal
  };
}
