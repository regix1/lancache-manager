import { useState, useCallback } from 'react';
import { type SteamLoginFlowState, type SteamAuthActions } from './useSteamAuthentication';
import ApiService from '@services/api.service';

interface UseEpicMappingAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export function useEpicMappingAuth(options: UseEpicMappingAuthOptions = {}) {
  const { onSuccess, onError } = options;

  const [loading, setLoading] = useState(false);
  const [needsAuthorizationCode, setNeedsAuthorizationCode] = useState(false);
  const [authorizationUrl, setAuthorizationUrl] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');

  const resetAuthForm = useCallback(() => {
    setLoading(false);
    setNeedsAuthorizationCode(false);
    setAuthorizationUrl('');
    setAuthorizationCode('');
  }, []);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (!authorizationCode.trim()) return false;

    setLoading(true);

    try {
      // Send the authorization code directly to the backend
      // Backend exchanges it for tokens, fetches games, saves credentials
      await ApiService.completeEpicMappingAuth(authorizationCode.trim());
      onSuccess?.();
      return true;
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Authentication failed';
      onError?.(message);
      return false;
    } finally {
      setLoading(false);
    }
  }, [authorizationCode, onSuccess, onError]);

  const startLogin = useCallback(async () => {
    resetAuthForm();
    setLoading(true);

    try {
      // Backend returns the Epic authorization URL directly (no Docker needed)
      const response = await ApiService.startEpicMappingLogin();
      setAuthorizationUrl(response.authorizationUrl);
      setNeedsAuthorizationCode(true);
      setLoading(false);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Login failed';
      onError?.(message);
      setLoading(false);
    }
  }, [resetAuthForm, onError]);

  // Build SteamLoginFlowState-compatible state
  const state: SteamLoginFlowState = {
    loading,
    needsTwoFactor: false,
    needsEmailCode: false,
    waitingForMobileConfirmation: false,
    useManualCode: false,
    username: '',
    password: '',
    twoFactorCode: '',
    emailCode: '',
    needsAuthorizationCode,
    authorizationUrl,
    authorizationCode
  };

  // Build SteamAuthActions-compatible actions
  const actions: SteamAuthActions = {
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setUsername: () => {},
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setPassword: () => {},
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setTwoFactorCode: () => {},
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setEmailCode: () => {},
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setUseManualCode: () => {},
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setNeedsTwoFactor: () => {},
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    setWaitingForMobileConfirmation: () => {},
    setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin };
}
