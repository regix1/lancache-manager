import { useState, useCallback } from 'react';
import ApiService from '@services/api.service';
import type { EpicAuthActions, EpicAuthState } from './useEpicMappingAuth';

interface UseScheduledPrefillEpicAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export function useScheduledPrefillEpicAuth(options: UseScheduledPrefillEpicAuthOptions = {}) {
  const { onSuccess, onError } = options;

  const [loading, setLoading] = useState(false);
  const [needsAuthorizationCode, setNeedsAuthorizationCode] = useState(false);
  const [authorizationUrl, setAuthorizationUrl] = useState('');
  const [authorizationCode, setAuthorizationCode] = useState('');
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  const resetAuthForm = useCallback(() => {
    if (abortController) {
      abortController.abort();
    }
    setLoading(false);
    setNeedsAuthorizationCode(false);
    setAuthorizationUrl('');
    setAuthorizationCode('');
    setAbortController(null);
  }, [abortController]);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    if (!authorizationCode.trim()) return false;

    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      await ApiService.completeScheduledPrefillEpicAuth(
        authorizationCode.trim(),
        controller.signal
      );
      onSuccess?.();
      return true;
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        return false;
      }
      const message = error instanceof Error ? error.message : 'Authentication failed';
      onError?.(message);
      return false;
    } finally {
      setLoading(false);
      setAbortController(null);
    }
  }, [authorizationCode, onSuccess, onError]);

  const startLogin = useCallback(async () => {
    resetAuthForm();
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      const response = await ApiService.startScheduledPrefillEpicLogin(controller.signal);
      setAuthorizationUrl(response.authorizationUrl);
      setNeedsAuthorizationCode(true);
      setLoading(false);
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        setLoading(false);
        return;
      }
      const message = error instanceof Error ? error.message : 'Login failed';
      onError?.(message);
      setLoading(false);
    } finally {
      setAbortController(null);
    }
  }, [resetAuthForm, onError]);

  const state: EpicAuthState = {
    loading,
    needsAuthorizationCode,
    authorizationUrl,
    authorizationCode
  };

  const actions: EpicAuthActions = {
    setAuthorizationCode,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin };
}
