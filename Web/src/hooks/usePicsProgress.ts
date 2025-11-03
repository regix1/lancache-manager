import { useState, useEffect } from 'react';
import type { PicsProgress } from '@contexts/PicsProgressContext';

/**
 * @deprecated Use PicsProgressContext instead via usePicsProgress() hook from @contexts/PicsProgressContext
 * This hook is kept for backward compatibility but should not be used in new code.
 * The context provides real-time SignalR updates and centralized state management.
 */
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
    autoStart = true,
    mockMode = false
  } = options;

  const [progress, setProgress] = useState<PicsProgress | null>(null);
  const [error, setError] = useState<Error | null>(null);

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

  const refresh = () => {
    fetchProgress();
  };

  // Fetch initial state on mount (no polling - updates come from SignalR)
  useEffect(() => {
    if (mockMode) {
      return;
    }

    if (autoStart) {
      fetchProgress();
    }
  }, [mockMode, autoStart]);

  return {
    progress,
    error,
    refresh
  };
}
