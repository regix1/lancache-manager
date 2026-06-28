import { useState, useCallback, useEffect, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import type { ScheduledPrefillXboxAuthProgressEvent } from '../contexts/SignalRContext/types';

interface UseScheduledPrefillXboxAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

interface ScheduledPrefillXboxAuthState {
  loading: boolean;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
}

interface ScheduledPrefillXboxAuthActions {
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'cancelled']);

export function useScheduledPrefillXboxAuth(options: UseScheduledPrefillXboxAuthOptions = {}) {
  const { onSuccess, onError } = options;
  const { on, off } = useSignalR();

  const [loading, setLoading] = useState(false);
  const [needsDeviceCode, setNeedsDeviceCode] = useState(false);
  const [deviceUserCode, setDeviceUserCode] = useState('');
  const [deviceVerificationUri, setDeviceVerificationUri] = useState('');
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  const loginInProgressRef = useRef(false);

  const resetAuthForm = useCallback(() => {
    if (abortController) {
      abortController.abort();
    }
    loginInProgressRef.current = false;
    setLoading(false);
    setNeedsDeviceCode(false);
    setDeviceUserCode('');
    setDeviceVerificationUri('');
    setAbortController(null);
  }, [abortController]);

  useEffect(() => {
    const handleProgress = (event: ScheduledPrefillXboxAuthProgressEvent) => {
      if (!loginInProgressRef.current) return;
      if (!TERMINAL_STATUSES.has(event.status)) return;
      loginInProgressRef.current = false;
      setNeedsDeviceCode(false);
      setLoading(false);
      if (event.status === 'completed' && !event.cancelled) {
        onSuccess?.();
      } else {
        onError?.(event.message ?? 'Xbox authentication failed.');
      }
    };
    on('ScheduledPrefillXboxAuthProgress', handleProgress);
    return () => off('ScheduledPrefillXboxAuthProgress', handleProgress);
  }, [on, off, onSuccess, onError]);

  const startLogin = useCallback(async () => {
    resetAuthForm();
    loginInProgressRef.current = true;
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      const response = await ApiService.startScheduledPrefillXboxLogin(controller.signal);
      setDeviceUserCode(response.userCode);
      setDeviceVerificationUri(response.verificationUri);
      setNeedsDeviceCode(true);
      setLoading(false);
    } catch (error) {
      loginInProgressRef.current = false;
      setLoading(false);
      if (error instanceof Error && error.name === 'AbortError') return;
      const message = error instanceof Error ? error.message : 'Login failed';
      onError?.(message);
    } finally {
      setAbortController(null);
    }
  }, [resetAuthForm, onError]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    await startLogin();
    return false;
  }, [startLogin]);

  const cancelLogin = useCallback(async () => {
    try {
      await ApiService.cancelScheduledPrefillXboxLogin();
    } catch {
      // Ignore: the poll will expire on its own if the cancel request fails.
    }
  }, []);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const state: ScheduledPrefillXboxAuthState = {
    loading,
    needsDeviceCode,
    deviceUserCode,
    deviceVerificationUri
  };

  const actions: ScheduledPrefillXboxAuthActions = {
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin, cancelLogin };
}
