import React, { createContext, useContext, useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import ApiService from '@services/api.service';
import type { DownloadSpeedSnapshot, GameSpeedInfo, ClientSpeedInfo } from '../../types';
import type { SpeedContextType, SpeedProviderProps } from './types';

const SpeedContext = createContext<SpeedContextType | undefined>(undefined);

export const useSpeed = (): SpeedContextType => {
  const context = useContext(SpeedContext);
  if (!context) {
    throw new Error('useSpeed must be used within SpeedProvider');
  }
  return context;
};

export const SpeedProvider: React.FC<SpeedProviderProps> = ({ children }: SpeedProviderProps) => {
  const signalR = useSignalR();
  const { getRefreshInterval } = useRefreshRate();
  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Debouncing refs for throttled updates
  const lastSpeedUpdateRef = useRef<number>(0);
  const pendingSpeedUpdateRef = useRef<NodeJS.Timeout | null>(null);
  const lastActiveCountRef = useRef<number | null>(null);

  // Keep getRefreshInterval in a ref to avoid stale closure issues
  const getRefreshIntervalRef = useRef(getRefreshInterval);
  getRefreshIntervalRef.current = getRefreshInterval;

  // Calculate derived values from the speed snapshot
  const gameSpeeds: GameSpeedInfo[] = useMemo(() => {
    return speedSnapshot?.gameSpeeds ?? [];
  }, [speedSnapshot]);

  const clientSpeeds: ClientSpeedInfo[] = useMemo(() => {
    return speedSnapshot?.clientSpeeds ?? [];
  }, [speedSnapshot]);

  const activeDownloadCount = useMemo(() => {
    return gameSpeeds.length;
  }, [gameSpeeds]);

  const totalActiveClients = useMemo(() => {
    return clientSpeeds.length;
  }, [clientSpeeds]);

  // Fetch speed data from the API (used for initial load and manual refresh)
  const fetchSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      setSpeedSnapshot(data);
      lastActiveCountRef.current = data?.gameSpeeds?.length ?? 0;
    } catch (error) {
      console.error('[SpeedContext] Failed to fetch speed data:', error);
    } finally {
      setIsLoading(false);
    }
  }, []);

  // Manual refresh function exposed to consumers
  const refreshSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      setSpeedSnapshot(data);
      lastActiveCountRef.current = data?.gameSpeeds?.length ?? 0;
    } catch (error) {
      console.error('[SpeedContext] Failed to refresh speed data:', error);
    }
  }, []);

  // Fetch initial data on mount
  useEffect(() => {
    fetchSpeed();
  }, [fetchSpeed]);

  // Re-fetch data when SignalR reconnects to recover from missed messages
  useEffect(() => {
    if (signalR.connectionState === 'connected') {
      fetchSpeed();
    }
  }, [signalR.connectionState, fetchSpeed]);

  // Re-fetch data when page becomes visible (handles tab switching / mobile backgrounding)
  useEffect(() => {
    const handleVisibilityChange = () => {
      if (!document.hidden) {
        // Page became visible - refresh data with a small delay to let SignalR reconnect
        setTimeout(() => {
          fetchSpeed();
        }, 500);
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [fetchSpeed]);

  // Listen for real-time speed updates via SignalR with debouncing
  // Uses the same pattern as Dashboard.tsx for consistent behavior
  useEffect(() => {
    const handleSpeedUpdate = (speedData: DownloadSpeedSnapshot) => {
      // Clear any pending update
      if (pendingSpeedUpdateRef.current) {
        clearTimeout(pendingSpeedUpdateRef.current);
      }

      const newCount = speedData.gameSpeeds?.length ?? 0;

      // ALWAYS accept updates immediately when active games count changes
      // This ensures "download finished" events are never throttled
      const countChanged = lastActiveCountRef.current !== null &&
        lastActiveCountRef.current !== newCount;

      if (countChanged) {
        lastSpeedUpdateRef.current = Date.now();
        lastActiveCountRef.current = newCount;
        setSpeedSnapshot(speedData);
        setIsLoading(false);
        return;
      }

      // Debounce: wait 100ms for more events before applying throttle
      pendingSpeedUpdateRef.current = setTimeout(() => {
        const maxRefreshRate = getRefreshIntervalRef.current();
        const now = Date.now();
        const timeSinceLastUpdate = now - lastSpeedUpdateRef.current;

        // User's setting controls max refresh rate
        // LIVE mode (0) = minimum 500ms to prevent UI thrashing
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

        if (timeSinceLastUpdate >= minInterval) {
          lastSpeedUpdateRef.current = now;
          lastActiveCountRef.current = newCount;
          setSpeedSnapshot(speedData);
          setIsLoading(false);
        }
        pendingSpeedUpdateRef.current = null;
      }, 100);
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);

    return () => {
      signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
      if (pendingSpeedUpdateRef.current) {
        clearTimeout(pendingSpeedUpdateRef.current);
      }
    };
  }, [signalR]);

  const value: SpeedContextType = useMemo(
    () => ({
      speedSnapshot,
      gameSpeeds,
      clientSpeeds,
      activeDownloadCount,
      totalActiveClients,
      isLoading,
      refreshSpeed
    }),
    [speedSnapshot, gameSpeeds, clientSpeeds, activeDownloadCount, totalActiveClients, isLoading, refreshSpeed]
  );

  return <SpeedContext.Provider value={value}>{children}</SpeedContext.Provider>;
};
