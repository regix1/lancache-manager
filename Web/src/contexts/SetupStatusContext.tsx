import React, { useEffect, useState, type ReactNode } from 'react';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import { API_BASE } from '@utils/constants';
import { SetupStatusContext, type SetupStatus } from './SetupStatusContext.types';

interface SetupStatusProviderProps {
  children: ReactNode;
}

export const SetupStatusProvider: React.FC<SetupStatusProviderProps> = ({ children }) => {
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [syncError, setSyncError] = useState<string | null>(null);
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
      if (response.ok) {
        const data = await response.json();
        const isCompleted = data.isCompleted === true || data.setupCompleted === true;
        setSyncError(null);
        setSetupStatus({
          isCompleted,
          hasProcessedLogs: data.hasProcessedLogs === true,
          needsPostgresCredentials: data.needsPostgresCredentials === true,
          currentSetupStep: data.currentSetupStep ?? null,
          dataSourceChoice: data.dataSourceChoice ?? null,
          completedPlatforms: data.completedPlatforms ?? null
        });
      } else {
        // Non-OK response: default to showing the database-setup step so the
        // wizard doesn't silently skip it when credentials are actually needed.
        setSetupStatus({
          isCompleted: false,
          hasProcessedLogs: false,
          needsPostgresCredentials: true,
          currentSetupStep: null,
          dataSourceChoice: null,
          completedPlatforms: null
        });
      }
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        console.warn('[SetupStatus] fetchSetupStatus timed out after 10000ms');
      } else {
        console.error('[SetupStatus] Failed to fetch setup status:', error);
      }
      // On error/timeout: default to showing the database-setup step so the
      // wizard doesn't silently skip it when credentials are actually needed.
      setSetupStatus({
        isCompleted: false,
        hasProcessedLogs: false,
        needsPostgresCredentials: true,
        currentSetupStep: null,
        dataSourceChoice: null,
        completedPlatforms: null
      });
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

  const updateWizardState = async (updates: {
    currentSetupStep?: string | null;
    dataSourceChoice?: string | null;
    completedPlatforms?: string | null;
  }): Promise<boolean> => {
    const maxAttempts = 3;
    const baseDelayMs = 250;

    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
      try {
        const response = await fetch(
          `${API_BASE}/system/setup`,
          ApiService.getFetchOptions({
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(updates)
          })
        );
        if (!response.ok) {
          throw new Error(`PATCH /system/setup failed with status ${response.status}`);
        }

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
          setSyncError(
            'Failed to sync setup progress to the server. Your current step may not be saved.'
          );
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
  // synchronously — don't wait for the async re-fetch.
  useEffect(() => {
    if (authLoading) {
      return;
    }

    if (authMode === 'unauthenticated') {
      setSetupStatus(null);
      setIsLoading(true);
    }

    fetchSetupStatus();
  }, [authLoading, authMode]);

  return (
    <SetupStatusContext.Provider
      value={{
        setupStatus,
        isLoading,
        syncError,
        refreshSetupStatus,
        markSetupCompleted,
        updateWizardState
      }}
    >
      {children}
    </SetupStatusContext.Provider>
  );
};
