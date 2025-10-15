import { useState, useEffect, useRef } from 'react';

export interface PicsProgress {
  isRunning: boolean;
  status: string;
  totalApps: number;
  processedApps: number;
  totalBatches: number;
  processedBatches: number;
  progressPercent: number;
  depotMappingsFound: number;
  depotMappingsFoundInSession: number;
  isReady: boolean;
  lastCrawlTime?: string;
  nextCrawlIn: any;
  crawlIntervalHours: number;
  crawlIncrementalMode: boolean;
  lastScanWasForced?: boolean;
  isConnected: boolean;
  isLoggedOn: boolean;
}

export interface UsePicsProgressOptions {
  /** Polling interval in milliseconds (default: 2000) */
  pollingInterval?: number;
  /** Whether to start polling immediately (default: true) */
  autoStart?: boolean;
  /** Whether to skip polling in mock mode (default: false) */
  mockMode?: boolean;
}

export function usePicsProgress(options: UsePicsProgressOptions = {}) {
  const {
    pollingInterval = 2000,
    autoStart = true,
    mockMode = false
  } = options;

  const [progress, setProgress] = useState<PicsProgress | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const intervalRef = useRef<NodeJS.Timeout | null>(null);

  const fetchProgress = async () => {
    try {
      const response = await fetch('/api/gameinfo/steamkit/progress');
      if (response.ok) {
        const data: PicsProgress = await response.json();
        setProgress(data);
        setError(null);
      } else {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }
    } catch (err: any) {
      console.error('Failed to fetch PICS progress:', err);
      setError(err);
    }
  };

  const startPolling = () => {
    if (mockMode) return;

    // Clear any existing interval
    if (intervalRef.current) {
      clearInterval(intervalRef.current);
    }

    // Initial fetch
    fetchProgress();

    // Set up polling
    intervalRef.current = setInterval(() => {
      fetchProgress();
    }, pollingInterval);
  };

  const stopPolling = () => {
    if (intervalRef.current) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }
  };

  const refresh = () => {
    fetchProgress();
  };

  useEffect(() => {
    if (mockMode) {
      return;
    }

    if (autoStart) {
      startPolling();
    }

    return () => {
      stopPolling();
    };
  }, [mockMode]);

  return {
    progress,
    error,
    startPolling,
    stopPolling,
    refresh
  };
}
