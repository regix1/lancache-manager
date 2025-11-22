import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import { useSignalR } from '@contexts/SignalRContext';

/**
 * PICS Progress Interface
 * Matches the structure returned by /api/depots/rebuild/progress
 */
export interface PicsProgress {
  // Core status
  isRunning: boolean;
  status: string;

  // Progress metrics
  totalApps: number;
  processedApps: number;
  totalBatches: number;
  processedBatches: number;
  progressPercent: number;

  // Depot information
  depotMappingsFound: number;
  depotMappingsFoundInSession?: number;

  // Scheduling (what the API actually returns)
  crawlIntervalHours: number;
  crawlIncrementalMode: boolean | string; // true (incremental), false (full), or "github" (PICS only)
  lastCrawlTime?: string; // ISO 8601 datetime string
  nextCrawlIn?: number; // Seconds remaining until next crawl

  // Additional metadata
  startTime?: string;
  lastChangeNumber?: number;
  failedBatches?: number;
  remainingApps?: number[];

  // Scan flags
  isReady?: boolean;
  lastScanWasForced?: boolean;
  automaticScanSkipped?: boolean;

  // Connection status
  isConnected?: boolean;
  isLoggedOn?: boolean;

  // Web API availability (for Full/Incremental scans)
  isWebApiAvailable?: boolean;

  // Error handling
  errorMessage?: string | null;
}

interface PicsProgressContextType {
  progress: PicsProgress | null;
  isLoading: boolean;
  refreshProgress: () => Promise<void>;
  updateProgress: (updater: (prev: PicsProgress | null) => PicsProgress | null) => void;
}

const PicsProgressContext = createContext<PicsProgressContextType | undefined>(undefined);

export const usePicsProgress = () => {
  const context = useContext(PicsProgressContext);
  if (!context) {
    throw new Error('usePicsProgress must be used within PicsProgressProvider');
  }
  return context;
};

interface PicsProgressProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const PicsProgressProvider: React.FC<PicsProgressProviderProps> = ({
  children,
  mockMode = false
}) => {
  const signalR = useSignalR();

  // Initialize progress from sessionStorage cache if available
  const [progress, setProgress] = useState<PicsProgress | null>(() => {
    try {
      const cached = sessionStorage.getItem('pics_progress_cache');
      if (cached) {
        return JSON.parse(cached);
      }
    } catch (error) {
      // Ignore errors, return null
    }
    return null;
  });

  // Only show loading if we don't have cached data
  const [isLoading, setIsLoading] = useState(() => {
    try {
      const cached = sessionStorage.getItem('pics_progress_cache');
      return !cached; // Show loading only if no cache
    } catch (error) {
      return true;
    }
  });

  const fetchProgress = async () => {
    if (mockMode) {
      setIsLoading(false);
      return;
    }

    try {
      const response = await fetch('/api/depots/rebuild/progress');
      if (response.ok) {
        const data: PicsProgress = await response.json();
        setProgress(data);
        // Cache to sessionStorage to prevent loading flashes
        sessionStorage.setItem('pics_progress_cache', JSON.stringify(data));
      }
    } catch (error) {
      console.error('[PicsProgress] Failed to fetch progress:', error);
    } finally {
      setIsLoading(false);
    }
  };

  const refreshProgress = async () => {
    await fetchProgress();
  };

  const updateProgress = useCallback((updater: (prev: PicsProgress | null) => PicsProgress | null) => {
    setProgress(updater);
  }, []);

  // Initial fetch
  useEffect(() => {
    fetchProgress();
  }, [mockMode]);

  // Monitor SignalR connection state - re-fetch state on reconnection
  // This ensures we recover from missed messages during connection loss
  useEffect(() => {
    if (mockMode) return;

    console.log('[PicsProgress] SignalR connection state:', signalR.connectionState);

    // When SignalR reconnects, immediately fetch current state to recover any missed messages
    if (signalR.connectionState === 'connected') {
      console.log('[PicsProgress] SignalR connected/reconnected - fetching current state');
      fetchProgress();
    }
  }, [signalR.connectionState, mockMode]);

  // Listen for real-time depot mapping updates via SignalR
  useEffect(() => {
    if (mockMode) return;

    const handleDepotMappingStarted = (payload: any) => {
      console.log('[PicsProgress] Depot mapping started:', payload);
      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isRunning: true,
              status: payload.status || 'Running',
              totalApps: payload.totalApps || prev.totalApps,
              processedApps: payload.processedApps || 0,
              progressPercent: payload.progressPercent || 0,
              startTime: payload.startTime || new Date().toISOString()
            }
          : null
      );
    };

    const handleDepotMappingProgress = (payload: any) => {
      console.log('[PicsProgress] Depot mapping progress:', payload);
      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isRunning: true,
              status: payload.status || prev.status,
              totalApps: payload.totalApps || prev.totalApps,
              processedApps: payload.processedApps || prev.processedApps,
              totalBatches: payload.totalBatches || prev.totalBatches,
              processedBatches: payload.processedBatches || prev.processedBatches,
              progressPercent: payload.progressPercent || prev.progressPercent,
              depotMappingsFound: payload.depotMappingsFound || prev.depotMappingsFound,
              failedBatches: payload.failedBatches,
              remainingApps: payload.remainingApps
            }
          : null
      );
    };

    const handleDepotMappingComplete = (payload: any) => {
      console.log('[PicsProgress] Depot mapping complete:', payload);
      const now = new Date().toISOString();
      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isRunning: false,
              status: 'Completed',
              progressPercent: 100,
              processedApps: payload.totalApps || prev.totalApps,
              processedBatches: payload.totalBatches || prev.totalBatches,
              depotMappingsFound: payload.depotMappingsFound || prev.depotMappingsFound,
              lastCrawlTime: now,
              // Calculate next crawl time (convert hours to seconds)
              nextCrawlIn: prev.crawlIntervalHours ? prev.crawlIntervalHours * 3600 : undefined
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
  }, [signalR, mockMode]);

  return (
    <PicsProgressContext.Provider value={{ progress, isLoading, refreshProgress, updateProgress }}>
      {children}
    </PicsProgressContext.Provider>
  );
};
