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
  const { isLoading: authLoading } = useAuth();

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
            isCompleted: true
          }
        : null
    );
  };

  const updateWizardState = async (updates: {
    currentSetupStep?: string | null;
    dataSourceChoice?: string | null;
    completedPlatforms?: string | null;
  }) => {
    try {
      const response = await fetch(
        `${API_BASE}/system/setup`,
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(updates)
        })
      );
      if (response.ok) {
        // Optimistically update local state instead of re-fetching.
        // Re-fetching created an infinite loop: PATCH -> fetchSetupStatus -> new
        // setupStatus object reference -> useInitializationFlow effects re-fire ->
        // another PATCH -> repeat. localStorage writes were synchronous and never
        // triggered re-renders; this mirrors that behavior.
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
      }
    } catch (error) {
      console.error('[SetupStatus] Failed to update wizard state:', error);
    }
  };

  // Initial fetch - setup status is public so we can check before auth
  useEffect(() => {
    // Wait for auth loading to settle, but don't require auth for setup check
    // The /api/system/setup endpoint is public so we can determine if setup wizard is needed
    if (authLoading) {
      return;
    }

    fetchSetupStatus();
  }, [authLoading]);

  return (
    <SetupStatusContext.Provider
      value={{ setupStatus, isLoading, refreshSetupStatus, markSetupCompleted, updateWizardState }}
    >
      {children}
    </SetupStatusContext.Provider>
  );
};
