import React, { useEffect, useState, useCallback, type ReactNode } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import type {
  DepotMappingStartedEvent,
  DepotMappingProgressEvent,
  DepotMappingCompleteEvent
} from '@contexts/SignalRContext/types';
import { PicsProgressContext, type PicsProgress } from './PicsProgressContext.types';
import { sessionStore } from '@utils/storage';

interface PicsProgressProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const PicsProgressProvider: React.FC<PicsProgressProviderProps> = ({
  children,
  mockMode = false
}: PicsProgressProviderProps) => {
  const signalR = useSignalR();
  const { authMode, isLoading: authLoading } = useAuth();
  // PICS rebuild progress is admin-only; guests shouldn't poll it
  const hasAccess = authMode === 'authenticated';

  // Initialize progress from sessionStorage cache if available
  const [progress, setProgress] = useState<PicsProgress | null>(() =>
    sessionStore.getJSON<PicsProgress>('pics_progress_cache')
  );

  // The cache above keeps the last numbers on screen across a reload, so a non-null progress says
  // nothing about what this page load knows - the blob can predate an API key being added or a data
  // reset. This flag is the one signal that separates "not read yet" from "read, and the answer is
  // no", so it starts set on every page load whether or not a cache was restored.
  const [isLoading, setIsLoading] = useState(true);

  const fetchProgress = async (skipAuthCheck = false) => {
    if (mockMode) {
      setIsLoading(false);
      return;
    }

    // Skip fetch if auth is loading or user doesn't have access (unless explicitly skipped for SignalR recovery)
    if (!skipAuthCheck && (authLoading || !hasAccess)) {
      return;
    }

    try {
      const response = await fetch('/api/depots/rebuild/progress', ApiService.getFetchOptions());
      if (response.ok) {
        const data: PicsProgress = await response.json();
        setProgress(data);
        // Cache to sessionStorage to prevent loading flashes
        sessionStore.setItem('pics_progress_cache', JSON.stringify(data));
      }
    } catch (error) {
      // Background poll (mount + SignalR reconnect recovery). The sessionStorage cache and
      // SignalR push events keep the UI reasonably fresh even if one poll fails. Deliberately
      // silent - a toast on every transient poll failure would be noise, not signal.
      console.error('[PicsProgress] Failed to fetch progress:', error);
    } finally {
      setIsLoading(false);
    }
  };

  const refreshProgress = async () => {
    await fetchProgress();
  };

  const updateProgress = useCallback(
    (updater: (prev: PicsProgress | null) => PicsProgress | null) => {
      setProgress(updater);
    },
    []
  );

  const createDefaultProgress = useCallback(
    (): PicsProgress => ({
      isProcessing: false,
      status: 'Idle',
      totalApps: 0,
      processedApps: 0,
      totalBatches: 0,
      processedBatches: 0,
      progressPercent: 0,
      depotMappingsFound: 0,
      crawlIntervalHours: 0,
      crawlIncrementalMode: true
    }),
    []
  );

  // The cached blob above was written by a live session and holds real depot numbers, so it is
  // dropped the moment the reader switches to mock mode rather than staying on screen.
  useEffect(() => {
    if (mockMode) {
      setProgress(null);
    }
  }, [mockMode]);

  // Initial fetch - only when auth is ready and user has access
  useEffect(() => {
    if (authLoading) {
      return;
    }
    if (!mockMode && hasAccess) {
      fetchProgress();
      return;
    }
    // Nothing is going to answer on this page load: mock mode serves no progress and the route is
    // admin-only. Leaving the flag set would hold every consumer in a wait that never ends.
    setIsLoading(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode, authLoading, hasAccess]);

  // Re-fetch state on reconnection to recover from messages missed during connection loss.
  // Access changes while connected are already handled by the fetch effect above.
  useReconnectRefetch(signalR.isConnected, () => {
    if (mockMode || authLoading || !hasAccess) return;
    fetchProgress();
  });

  // Listen for real-time depot mapping updates via SignalR
  useEffect(() => {
    if (mockMode) return;

    const handleDepotMappingStarted = (event: DepotMappingStartedEvent) => {
      setProgress((prev) => ({
        ...(prev ?? createDefaultProgress()),
        isProcessing: true,
        status: event.status || 'Running',
        totalApps: event.totalApps || prev?.totalApps || 0,
        processedApps: event.processedApps || 0,
        // Backend sends 'percentComplete', map it to 'progressPercent'
        progressPercent: event.percentComplete ?? event.progressPercent ?? 0,
        startTime: event.startTime || new Date().toISOString()
      }));
    };

    const handleDepotMappingProgress = (event: DepotMappingProgressEvent) => {
      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isProcessing: true,
              status: event.status,
              totalApps: event.totalApps || prev.totalApps,
              processedApps: event.processedApps || prev.processedApps,
              totalBatches: event.totalBatches || prev.totalBatches,
              processedBatches: event.processedBatches || prev.processedBatches,
              // Backend sends 'percentComplete', map it to 'progressPercent'
              progressPercent: event.percentComplete,
              depotMappingsFound: event.depotMappingsFound || prev.depotMappingsFound,
              failedBatches: event.failedBatches,
              remainingApps: event.remainingApps
            }
          : null
      );
    };

    const handleDepotMappingComplete = (event: DepotMappingCompleteEvent) => {
      const now = new Date().toISOString();

      // Handle both success and failure cases
      const isSuccess = event.success && !event.cancelled;
      const isCancelled = event.cancelled;

      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isProcessing: false,
              status: isCancelled ? 'Cancelled' : isSuccess ? 'Completed' : 'Failed',
              progressPercent: isSuccess ? 100 : prev.progressPercent,
              processedApps: event.totalApps || prev.totalApps,
              processedBatches: event.totalBatches || prev.totalBatches,
              depotMappingsFound: event.depotMappingsFound || prev.depotMappingsFound,
              // Only update lastCrawlTime and nextCrawlIn on success
              lastCrawlTime: isSuccess ? now : prev.lastCrawlTime,
              nextCrawlIn:
                isSuccess && prev.crawlIntervalHours
                  ? prev.crawlIntervalHours * 3600
                  : prev.nextCrawlIn,
              // Store error message if present
              errorMessage: event.error || null
            }
          : null
      );
    };

    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handleDepotMappingComplete);

    return () => {
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handleDepotMappingComplete);
    };
  }, [signalR, mockMode, createDefaultProgress]);

  return (
    <PicsProgressContext.Provider value={{ progress, isLoading, refreshProgress, updateProgress }}>
      {children}
    </PicsProgressContext.Provider>
  );
};
