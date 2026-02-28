import React, {
  createContext,
  useContext,
  useEffect,
  useState,
  useCallback,
  type ReactNode
} from 'react';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import type {
  DepotMappingStartedEvent,
  DepotMappingProgressEvent,
  DepotMappingCompleteEvent
} from '@contexts/SignalRContext/types';

/**
 * PICS Progress Interface
 * Matches the structure returned by /api/depots/rebuild/progress
 */
interface PicsProgress {
  // Core status
  isProcessing: boolean;
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
}: PicsProgressProviderProps) => {
  const signalR = useSignalR();
  const { authMode, isLoading: authLoading } = useAuth();
  // PICS rebuild progress is admin-only; guests shouldn't poll it
  const hasAccess = authMode === 'authenticated';

  // Initialize progress from sessionStorage cache if available
  const [progress, setProgress] = useState<PicsProgress | null>(() => {
    try {
      const cached = sessionStorage.getItem('pics_progress_cache');
      if (cached) {
        return JSON.parse(cached);
      }
    } catch (_error) {
      // Ignore errors, return null
    }
    return null;
  });

  // Only show loading if we don't have cached data
  const [isLoading, setIsLoading] = useState(() => {
    try {
      const cached = sessionStorage.getItem('pics_progress_cache');
      return !cached; // Show loading only if no cache
    } catch (_error) {
      return true;
    }
  });

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
      const response = await fetch('/api/depots/rebuild/progress', {
        credentials: 'include'
      });
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

  const updateProgress = useCallback(
    (updater: (prev: PicsProgress | null) => PicsProgress | null) => {
      setProgress(updater);
    },
    []
  );

  // Initial fetch - only when auth is ready and user has access
  useEffect(() => {
    if (!mockMode && !authLoading && hasAccess) {
      fetchProgress();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode, authLoading, hasAccess]);

  // Monitor SignalR connection state - re-fetch state on reconnection
  // This ensures we recover from missed messages during connection loss
  useEffect(() => {
    if (mockMode) return;

    // Only refetch on reconnection if user has access
    if (authLoading || !hasAccess) return;

    // When SignalR reconnects, immediately fetch current state to recover any missed messages
    if (signalR.connectionState === 'connected') {
      fetchProgress();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.connectionState, mockMode, authLoading, hasAccess]);

  // Listen for real-time depot mapping updates via SignalR
  useEffect(() => {
    if (mockMode) return;

    const handleDepotMappingStarted = (event: DepotMappingStartedEvent) => {
      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isProcessing: true,
              status: event.status || 'Running',
              totalApps: event.totalApps || prev.totalApps,
              processedApps: event.processedApps || 0,
              // Backend sends 'percentComplete', map it to 'progressPercent'
              progressPercent: event.percentComplete ?? event.progressPercent ?? 0,
              startTime: event.startTime || new Date().toISOString()
            }
          : null
      );
    };

    const handleDepotMappingProgress = (event: DepotMappingProgressEvent) => {
      setProgress((prev) =>
        prev
          ? {
              ...prev,
              isProcessing: true,
              status: event.status || prev.status,
              totalApps: event.totalApps || prev.totalApps,
              processedApps: event.processedApps || prev.processedApps,
              totalBatches: event.totalBatches || prev.totalBatches,
              processedBatches: event.processedBatches || prev.processedBatches,
              // Backend sends 'percentComplete', map it to 'progressPercent'
              progressPercent:
                event.percentComplete ?? event.progressPercent ?? prev.progressPercent,
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
      const isSuccess = event.success !== false && !event.cancelled;
      const isCancelled = event.cancelled === true;

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
  }, [signalR, mockMode]);

  return (
    <PicsProgressContext.Provider value={{ progress, isLoading, refreshProgress, updateProgress }}>
      {children}
    </PicsProgressContext.Provider>
  );
};
