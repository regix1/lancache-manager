import { useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';

export type AuthMode = 'apiKey' | 'guest' | 'admin';

interface UseInitializationAuthProps {
  apiKey: string;
  setAuthError: (error: string | null) => void;
  setAuthenticating: (authenticating: boolean) => void;
  onAuthChanged?: () => Promise<void> | void;
  checkPicsDataStatus: () => Promise<unknown>;
  checkDataAvailability: () => Promise<boolean>;
  setCurrentStep: (step: 'import-historical-data') => void;
  onInitializationComplete: () => void;
}

export const useInitializationAuth = ({
  apiKey,
  setAuthError,
  setAuthenticating,
  onAuthChanged,
  checkPicsDataStatus,
  checkDataAvailability,
  setCurrentStep,
  onInitializationComplete
}: UseInitializationAuthProps) => {
  const { t } = useTranslation();
  const authSuccessRef = useRef(false);

  const authenticate = useCallback(async (mode: AuthMode) => {
    setAuthError(null);

    switch (mode) {
      case 'apiKey': {
        if (!apiKey.trim()) {
          setAuthError(t('initialization.apiKey.errors.required', 'API key is required'));
          return;
        }

        setAuthenticating(true);
        authSuccessRef.current = false;

        try {
          const result = await authService.register(apiKey, null);
          if (result.success) {
            // Registration succeeded - authService already set isAuthenticated=true
            // No need to double-check with checkAuth() which can cause race conditions
            authSuccessRef.current = true;
            await onAuthChanged?.();
            await checkPicsDataStatus();
            setCurrentStep('import-historical-data');
          } else {
            setAuthError(result.message);
          }
        } catch (error: unknown) {
          setAuthError(
            (error instanceof Error ? error.message : String(error)) ||
            t('modals.auth.errors.authenticationFailed')
          );
        } finally {
          setAuthenticating(false);
        }
        break;
      }

      case 'guest': {
        const hasData = await checkDataAvailability();
        if (!hasData) {
          setAuthError(t('modals.auth.errors.guestModeNoData'));
          return;
        }

        await authService.startGuestMode();
        await onAuthChanged?.();

        const setupResponse = await fetch(
          '/api/system/setup',
          ApiService.getFetchOptions({ cache: 'no-store' })
        );
        const setupData = await setupResponse.json();

        if (setupData.isSetupCompleted) {
          onInitializationComplete();
        } else {
          setCurrentStep('import-historical-data');
        }
        break;
      }

      case 'admin': {
        await onAuthChanged?.();
        await checkPicsDataStatus();
        setCurrentStep('import-historical-data');
        break;
      }
    }
  }, [
    apiKey,
    setAuthError,
    setAuthenticating,
    onAuthChanged,
    checkPicsDataStatus,
    checkDataAvailability,
    setCurrentStep,
    onInitializationComplete,
    t
  ]);

  return {
    authenticate,
    authSuccessRef
  };
};
