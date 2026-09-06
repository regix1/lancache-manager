import {
  usePersistentPrefillAuth,
  type PersistentPrefillAuthActions
} from './usePersistentPrefillAuth';
import type { SteamAuthActions, SteamLoginFlowState } from './useSteamAuthentication';

interface PersistentSteamAuthState extends SteamLoginFlowState {
  error: string | null;
  authenticated: boolean;
  hasChallenge: boolean;
  dismissed: boolean;
}

interface PersistentSteamAuthActions extends SteamAuthActions {
  start: PersistentPrefillAuthActions['start'];
  cancel: PersistentPrefillAuthActions['cancel'];
}

interface UsePersistentSteamAuthOptions {
  timeoutSeconds?: number;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export function usePersistentSteamAuth(options: UsePersistentSteamAuthOptions = {}) {
  const { state: coreState, actions: coreActions } = usePersistentPrefillAuth({
    ...options,
    service: 'Steam'
  });

  const state: PersistentSteamAuthState = {
    loading: coreState.loading,
    needsTwoFactor: coreState.needsTwoFactor,
    needsEmailCode: coreState.needsEmailCode,
    waitingForMobileConfirmation: coreState.waitingForMobileConfirmation,
    useManualCode: coreState.useManualCode,
    username: coreState.username,
    password: coreState.password,
    twoFactorCode: coreState.twoFactorCode,
    emailCode: coreState.emailCode,
    needsAuthorizationCode: false,
    authorizationUrl: '',
    authorizationCode: '',
    needsDeviceCode: false,
    deviceUserCode: '',
    deviceVerificationUri: '',
    error: coreState.error,
    authenticated: coreState.authenticated,
    hasChallenge: coreState.hasChallenge,
    dismissed: coreState.dismissed
  };

  const actions: PersistentSteamAuthActions = {
    setUsername: coreActions.setUsername,
    setPassword: coreActions.setPassword,
    setTwoFactorCode: coreActions.setTwoFactorCode,
    setEmailCode: coreActions.setEmailCode,
    setUseManualCode: coreActions.setUseManualCode,
    setNeedsTwoFactor: coreActions.setNeedsTwoFactor,
    setWaitingForMobileConfirmation: coreActions.setWaitingForMobileConfirmation,
    setAuthorizationCode: coreActions.setAuthorizationCode,
    handleAuthenticate: coreActions.handleAuthenticate,
    resetAuthForm: coreActions.resetAuthForm,
    cancelPendingRequest: coreActions.cancelPendingRequest,
    start: coreActions.start,
    cancel: coreActions.cancel
  };

  return {
    state,
    actions,
    dismissModal: coreActions.dismissModal,
    resumeModal: coreActions.resumeModal
  };
}
