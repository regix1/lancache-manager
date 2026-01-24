import { useCallback } from 'react';
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

  const authenticate = useCallback(async (mode: AuthMode) => {
    setAuthError(null);

    switch (mode) {
      case 'apiKey': {
        if (!apiKey.trim()) {
          setAuthError(t('initialization.apiKey.errors.required', 'API key is required'));
          return;
        }

        setAuthenticating(true);

        try {
          const result = await authService.register(apiKey, null);
          if (result.success) {
            // IMPORTANT: Set step BEFORE calling onAuthChanged to prevent race condition
            // onAuthChanged triggers refreshAuth which may re-render parent and remount this component
            setCurrentStep('import-historical-data');
            await checkPicsDataStatus();
            await onAuthChanged?.();
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

        const setupResponse = await fetch(
          '/api/system/setup',
          ApiService.getFetchOptions({ cache: 'no-store' })
        );
        const setupData = await setupResponse.json();

        if (setupData.isSetupCompleted) {
          onInitializationComplete();
        } else {
          // Set step BEFORE onAuthChanged to prevent race condition
          setCurrentStep('import-historical-data');
          await onAuthChanged?.();
        }
        break;
      }

      case 'admin': {
        // Set step BEFORE onAuthChanged to prevent race condition
        setCurrentStep('import-historical-data');
        await checkPicsDataStatus();
        await onAuthChanged?.();
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
    authenticate
  };
};
