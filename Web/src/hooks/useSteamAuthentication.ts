import { useState, useEffect } from 'react';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/NotificationsContext';

export interface SteamAuthOptions {
  autoStartPics?: boolean;
  onSuccess?: (message: string) => void;
}

export interface SteamLoginFlowState {
  loading: boolean;
  needsTwoFactor: boolean;
  needsEmailCode: boolean;
  waitingForMobileConfirmation: boolean;
  useManualCode: boolean;
  username: string;
  password: string;
  twoFactorCode: string;
  emailCode: string;
}

export interface SteamAuthActions {
  setUsername: (value: string) => void;
  setPassword: (value: string) => void;
  setTwoFactorCode: (value: string) => void;
  setEmailCode: (value: string) => void;
  setUseManualCode: (value: boolean) => void;
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}

export function useSteamAuthentication(options: SteamAuthOptions = {}) {
  const { autoStartPics = false, onSuccess } = options;
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(false);
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [needsEmailCode, setNeedsEmailCode] = useState(false);
  const [waitingForMobileConfirmation, setWaitingForMobileConfirmation] = useState(false);
  const [useManualCode, setUseManualCode] = useState(false);
  const [abortController, setAbortController] = useState<AbortController | null>(null);

  // Form state
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [emailCode, setEmailCode] = useState('');

  // Cleanup: abort any pending requests when component unmounts
  useEffect(() => {
    return () => {
      if (abortController) {
        abortController.abort();
      }
    };
  }, [abortController]);

  const cancelPendingRequest = () => {
    if (abortController) {
      abortController.abort();
      setAbortController(null);
    }
  };

  const resetAuthForm = () => {
    cancelPendingRequest();
    setUsername('');
    setPassword('');
    setTwoFactorCode('');
    setEmailCode('');
    setNeedsTwoFactor(false);
    setNeedsEmailCode(false);
    setWaitingForMobileConfirmation(false);
    setUseManualCode(false);
    setLoading(false);
  };

  const handleAuthenticate = async (): Promise<boolean> => {
    if (!username.trim() || !password.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter both username and password',
        details: { notificationType: 'error' }
      });
      return false;
    }

    if (needsEmailCode && !emailCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter your email verification code',
        details: { notificationType: 'error' }
      });
      return false;
    }

    // If user chose manual code entry, require the code
    if (useManualCode && !twoFactorCode.trim()) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Please enter your 2FA code',
        details: { notificationType: 'error' }
      });
      return false;
    }

    setLoading(true);

    // Create abort controller for this request
    const controller = new AbortController();
    setAbortController(controller);

    // Show mobile confirmation waiting state for initial login (not when entering manual code)
    if (!needsTwoFactor && !needsEmailCode && !useManualCode) {
      setWaitingForMobileConfirmation(true);
    }

    try {
      const response = await fetch('/api/steam-auth/login', {
        method: 'POST',
        headers: ApiService.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({
          username,
          password,
          twoFactorCode: needsTwoFactor || useManualCode ? twoFactorCode : undefined,
          emailCode: needsEmailCode ? emailCode : undefined,
          // Allow mobile confirmation unless user explicitly chose manual code entry
          allowMobileConfirmation: !useManualCode,
          autoStartPicsRebuild: autoStartPics
        }),
        signal: controller.signal
      });

      let result;
      try {
        result = await response.json();
      } catch (jsonError) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: 'Invalid response from server',
          details: { notificationType: 'error' }
        });
        setLoading(false);
        setWaitingForMobileConfirmation(false);
        return false;
      }

      if (response.ok) {
        if (result.requiresTwoFactor) {
          setWaitingForMobileConfirmation(false);
          setNeedsTwoFactor(true);
          return false; // Stay in modal, show 2FA input
        }

        if (result.requiresEmailCode) {
          setWaitingForMobileConfirmation(false);
          setNeedsEmailCode(true);
          return false; // Stay in modal, wait for email code
        }

        if (result.success) {
          onSuccess?.(result.message || `Successfully authenticated as ${username}`);
          resetAuthForm();
          return true; // Success
        } else {
          setWaitingForMobileConfirmation(false);
          addNotification({
            type: 'generic',
            status: 'failed',
            message: result.message || 'Authentication failed',
            details: { notificationType: 'error' }
          });
          return false;
        }
      } else {
        setWaitingForMobileConfirmation(false);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: result.message || 'Authentication failed',
          details: { notificationType: 'error' }
        });
        return false;
      }
    } catch (err: any) {
      // Don't show error if request was aborted intentionally
      if (err.name !== 'AbortError') {
        setWaitingForMobileConfirmation(false);
        const errorMessage = err.message || 'Authentication failed';
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMessage,
          details: { notificationType: 'error' }
        });
      }
      return false;
    } finally {
      setLoading(false);
      setAbortController(null);
    }
  };

  const state: SteamLoginFlowState = {
    loading,
    needsTwoFactor,
    needsEmailCode,
    waitingForMobileConfirmation,
    useManualCode,
    username,
    password,
    twoFactorCode,
    emailCode
  };

  const actions: SteamAuthActions = {
    setUsername,
    setPassword,
    setTwoFactorCode,
    setEmailCode,
    setUseManualCode,
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return { state, actions };
}
