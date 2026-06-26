import { useState, useCallback, useEffect, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import type { XboxMappingProgressEvent } from '../contexts/SignalRContext/types';

interface UseXboxMappingAuthOptions {
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

export interface XboxAuthState {
  loading: boolean;
  needsDeviceCode: boolean;
  deviceUserCode: string;
  deviceVerificationUri: string;
}

export interface XboxAuthActions {
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'cancelled']);

export function useXboxMappingAuth(options: UseXboxMappingAuthOptions = {}) {
  const { onSuccess, onError } = options;
  const { on, off } = useSignalR();

  const [loading, setLoading] = useState(false);
  const [needsDeviceCode, setNeedsDeviceCode] = useState(false);
  const [deviceUserCode, setDeviceUserCode] = useState('');
  const [deviceVerificationUri, setDeviceVerificationUri] = useState('');
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  // True while a manager-side login is in flight; gates the XboxMappingProgress listener so
  // unrelated terminal events (catalog refresh) do not close the modal prematurely.
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

  // Listen for the terminal XboxMappingProgress event that signals login success or failure.
  // The gate (loginInProgressRef) prevents catalog-refresh events from being mistaken for auth.
  useEffect(() => {
    const handleProgress = (event: XboxMappingProgressEvent) => {
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
    on('XboxMappingProgress', handleProgress);
    return () => off('XboxMappingProgress', handleProgress);
  }, [on, off, onSuccess, onError]);

  const startLogin = useCallback(async () => {
    resetAuthForm();
    loginInProgressRef.current = true;
    setLoading(true);
    const controller = new AbortController();
    setAbortController(controller);

    try {
      const response = await ApiService.startXboxMappingLogin(controller.signal);
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

  // The backend polls the device code automatically, so there is no code-paste "complete" step.
  // The modal's Continue button instead RE-STARTS the login: this gives a working retry if the
  // initial device-code request failed. It returns false so the modal stays open and the
  // loading/device-code state drives the UI; the backend supersedes any stale poll.
  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    await startLogin();
    return false;
  }, [startLogin]);

  // Cancels a pending login poll server-side when the modal is closed, so an abandoned device-code
  // poll stops immediately instead of hammering Microsoft until expiry. Best-effort: the client form
  // is already reset by resetAuthForm; an already-authenticated account is NOT signed out.
  const cancelLogin = useCallback(async () => {
    try {
      await ApiService.cancelXboxMappingLogin();
    } catch {
      // Ignore: the poll will expire on its own if the cancel request fails.
    }
  }, []);

  const cancelPendingRequest = useCallback(() => {
    resetAuthForm();
  }, [resetAuthForm]);

  const state: XboxAuthState = {
    loading,
    needsDeviceCode,
    deviceUserCode,
    deviceVerificationUri
  };

  const actions: XboxAuthActions = {
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions, startLogin, cancelLogin };
}
