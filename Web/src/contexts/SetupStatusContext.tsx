import React, { useEffect, useState, type ReactNode } from 'react';
import i18n from '@/i18n';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { API_BASE } from '@utils/constants';
import { getErrorMessage } from '@utils/error';
import { SetupStatusContext, type SetupStatus } from './SetupStatusContext.types';

interface SetupStatusProviderProps {
  children: ReactNode;
}

// Used only when the status call fails and nothing has ever been read successfully, so the
// wizard gate stays closed for a genuine first run. It must not claim credentials are needed:
// a failed call is an absence of information, not evidence that setup is incomplete.
const UNREAD_SETUP_STATUS: SetupStatus = {
  isCompleted: false,
  hasProcessedLogs: false,
  needsPostgresCredentials: false,
  accountExists: null,
  mainAdminRecoveryAvailable: false,
  currentSetupStep: null,
  dataSourceChoice: null,
  completedPlatforms: null,
  mode: 'embedded',
  postgresHost: null,
  postgresPort: null,
  postgresDatabase: null,
  postgresUser: null
};

export const SetupStatusProvider: React.FC<SetupStatusProviderProps> = ({ children }) => {
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  // Set only where the route actually answered. The placeholder below is deliberately
  // indistinguishable from a real incomplete setup, so this flag is the only way a consumer can
  // tell "setup is not finished" from "nobody has managed to ask yet".
  const [isSetupStatusKnown, setIsSetupStatusKnown] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [syncError, setSyncError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { isLoading: authLoading, authMode } = useAuth();

  const fetchSetupStatus = async () => {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => {
      controller.abort();
    }, 10000);

    try {
      // This is a public endpoint - no auth required
      const response = await fetch(
        `${API_BASE}/system/setup`,
        ApiService.getFetchOptions({ cache: 'no-store', signal: controller.signal })
      );
      await assertOk(response);
      const body = await response.json();
      const isCompleted = body.isCompleted === true || body.setupCompleted === true;
      setSyncError(null);
      setError(null);
      setIsSetupStatusKnown(true);
      setSetupStatus({
        isCompleted,
        hasProcessedLogs: body.hasProcessedLogs === true,
        needsPostgresCredentials: body.needsPostgresCredentials === true,
        accountExists: typeof body.accountExists === 'boolean' ? body.accountExists : null,
        mainAdminRecoveryAvailable: body.mainAdminRecoveryAvailable === true,
        currentSetupStep: body.currentSetupStep ?? null,
        dataSourceChoice: body.dataSourceChoice ?? null,
        completedPlatforms: body.completedPlatforms ?? null,
        mode: body.mode === 'external' ? 'external' : 'embedded',
        postgresHost: body.postgresHost ?? null,
        postgresPort: typeof body.postgresPort === 'number' ? body.postgresPort : null,
        postgresDatabase: body.postgresDatabase ?? null,
        postgresUser: body.postgresUser ?? null
      });
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        // Own 10s timeout, not a user cancellation.
        console.warn('[SetupStatus] fetchSetupStatus timed out after 10000ms');
      } else {
        console.error('[SetupStatus] Failed to fetch setup status:', error);
      }
      // A failed call carries no information about setup, so the last successful status is kept
      // and only a never-read status falls back. Overwriting it here is what re-showed the
      // password wizard on a healthy install after any transient failure.
      setSetupStatus((prev) => prev ?? UNREAD_SETUP_STATUS);
      // AppSetup shows this on the startup card while no read has ever answered.
      setError(
        error instanceof Error && error.name === 'AbortError'
          ? i18n.t('errors.http.timeout')
          : getErrorMessage(error)
      );
    } finally {
      clearTimeout(timeoutId);
      setIsLoading(false);
    }
  };

  const refreshSetupStatus = async () => {
    await fetchSetupStatus();
  };

  const markSetupCompleted = () => {
    setSetupStatus((prev) =>
      prev
        ? {
            ...prev,
            isCompleted: true,
            needsPostgresCredentials: false,
            currentSetupStep: null,
            dataSourceChoice: null,
            completedPlatforms: null
          }
        : null
    );
  };

  const patchSetupState = async (body: Record<string, unknown>): Promise<Response> => {
    return fetch(
      `${API_BASE}/system/setup`,
      ApiService.getJsonFetchOptions(body, { method: 'PATCH' })
    );
  };

  const updateWizardState = async (updates: {
    currentSetupStep?: string | null;
    dataSourceChoice?: string | null;
    completedPlatforms?: string | null;
  }): Promise<boolean> => {
    const maxAttempts = 3;
    const baseDelayMs = 250;
    const legacyStepMap: Record<string, string> = {
      'external-db-form': 'database-setup',
      'external-db-confirm': 'database-setup'
    };

    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
      try {
        let response = await patchSetupState(updates);

        // Older API builds only knew database-setup — fall back so sync still works.
        if (
          !response.ok &&
          response.status === 400 &&
          updates.currentSetupStep &&
          legacyStepMap[updates.currentSetupStep]
        ) {
          response = await patchSetupState({
            ...updates,
            currentSetupStep: legacyStepMap[updates.currentSetupStep]
          });
        }

        await assertOk(response);

        // Optimistically update local state only after server confirms success.
        setSyncError(null);
        setSetupStatus((prev) =>
          prev
            ? {
                ...prev,
                ...(updates.currentSetupStep !== undefined && {
                  currentSetupStep: updates.currentSetupStep ?? null
                }),
                ...(updates.dataSourceChoice !== undefined && {
                  dataSourceChoice: updates.dataSourceChoice ?? null
                }),
                ...(updates.completedPlatforms !== undefined && {
                  completedPlatforms: updates.completedPlatforms ?? null
                })
              }
            : prev
        );
        return true;
      } catch (error) {
        const isLastAttempt = attempt === maxAttempts;
        if (isLastAttempt) {
          console.error(
            '[SetupStatus] Failed to update wizard state after retries:',
            error,
            updates
          );
          setSyncError(i18n.t('initialization.errors.syncProgressFailed'));
          return false;
        }

        const waitMs = baseDelayMs * Math.pow(2, attempt - 1);
        await new Promise((resolve) => setTimeout(resolve, waitMs));
      }
    }

    return false;
  };

  // Fetch setup status whenever auth settles or auth mode changes.
  // When auth is lost (e.g. data-folder deletion + restart), immediately
  // clear the cached setupStatus so the wizard gate in App.tsx activates
  // synchronously - don't wait for the async re-fetch.
  useEffect(() => {
    if (authLoading) {
      return;
    }

    if (authMode === 'unauthenticated') {
      setSetupStatus(null);
      setIsSetupStatusKnown(false);
      setIsLoading(true);
    }

    fetchSetupStatus();
  }, [authLoading, authMode]);

  return (
    <SetupStatusContext.Provider
      value={{
        setupStatus,
        isSetupStatusKnown,
        isLoading,
        syncError,
        error,
        refreshSetupStatus,
        markSetupCompleted,
        updateWizardState
      }}
    >
      {children}
    </SetupStatusContext.Provider>
  );
};
