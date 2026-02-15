import React, { createContext, useContext, useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import { useAuth } from '@contexts/AuthContext';
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
  const { isAuthenticated, authMode, isLoading: authLoading } = useAuth();
  const hasAccess = !authLoading && (isAuthenticated || authMode === 'guest');
  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Throttling refs
  const lastSpeedUpdateRef = useRef<number>(0);
  const lastActiveCountRef = useRef<number | null>(null);
  // Grace period ref to prevent flicker when transitioning TO zero (depot switches)
  const zeroGracePeriodRef = useRef<NodeJS.Timeout | null>(null);

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

  // Apply speed snapshot with grace period protection for zero transitions.
  // Prevents tab-switch flicker where REST fetch returns momentary zero
  // while downloads are still active (Rust tracker's 2-second window gap).
  const applySpeedSnapshot = useCallback((data: DownloadSpeedSnapshot) => {
    const newCount = data?.gameSpeeds?.length ?? 0;
    const previousCount = lastActiveCountRef.current ?? 0;

    // If going from active to zero, apply same grace period as SignalR handler
    if (newCount === 0 && previousCount > 0) {
      lastActiveCountRef.current = newCount;
      // Only schedule if no grace period is already running
      if (!zeroGracePeriodRef.current) {
        const scheduledData = data;
        zeroGracePeriodRef.current = setTimeout(() => {
          // Only apply zero-state if count is still zero
          if ((lastActiveCountRef.current ?? 0) === 0) {
            setSpeedSnapshot(scheduledData);
          }
          zeroGracePeriodRef.current = null;
        }, 1500);
      }
      return;
    }

    // Non-zero data: clear any pending zero-grace timeout and apply immediately
    if (newCount > 0 && zeroGracePeriodRef.current) {
      clearTimeout(zeroGracePeriodRef.current);
      zeroGracePeriodRef.current = null;
    }

    setSpeedSnapshot(data);
    lastActiveCountRef.current = newCount;
  }, []);

  // Fetch speed data from the API (used for initial load and manual refresh)
  const fetchSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      applySpeedSnapshot(data);
    } catch (error) {
      console.error('[SpeedContext] Failed to fetch speed data:', error);
    } finally {
      setIsLoading(false);
    }
  }, [applySpeedSnapshot]);

  // Manual refresh function exposed to consumers
  const refreshSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      applySpeedSnapshot(data);
    } catch (error) {
      console.error('[SpeedContext] Failed to refresh speed data:', error);
    }
  }, [applySpeedSnapshot]);

  // Fetch initial data on mount (only when authenticated or guest)
  useEffect(() => {
    if (hasAccess) {
      fetchSpeed();
    } else if (!authLoading) {
      setIsLoading(false);
    }
  }, [fetchSpeed, hasAccess, authLoading]);

  // Re-fetch data when SignalR reconnects to recover from missed messages
  useEffect(() => {
    if (signalR.connectionState === 'connected' && hasAccess) {
      fetchSpeed();
    }
  }, [signalR.connectionState, fetchSpeed, hasAccess]);

  // Re-fetch data when page becomes visible (handles tab switching / mobile backgrounding)
  useEffect(() => {
    const handleVisibilityChange = () => {
      if (!document.hidden && hasAccess) {
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
  }, [fetchSpeed, hasAccess]);

  // Listen for real-time speed updates via SignalR with throttling
  useEffect(() => {
    const handleSpeedUpdate = (speedData: DownloadSpeedSnapshot) => {
      const newCount = speedData.gameSpeeds?.length ?? 0;
      const previousCount = lastActiveCountRef.current ?? 0;

      // CRITICAL: Update lastActiveCountRef FIRST, before any grace period logic
      // This ensures the ref always reflects the actual count from the latest message,
      // preventing race conditions where stale ref values cause incorrect behavior
      lastActiveCountRef.current = newCount;

      // If count is now > 0, clear any pending zero-grace timeout immediately
      // This must happen before the grace period check to prevent race conditions
      if (newCount > 0 && zeroGracePeriodRef.current) {
        clearTimeout(zeroGracePeriodRef.current);
        zeroGracePeriodRef.current = null;
      }

      // Grace period logic: prevent flicker when transitioning to zero
      // This handles depot switches where count goes 1 → 0 → 1 quickly
      if (newCount === 0 && previousCount > 0) {
        // Transitioning TO zero - add a grace period delay (1.5 seconds)
        // This allows depot transitions to complete without showing "0 active downloads"

        // Capture the speedData and timestamp when scheduling the timeout
        // This allows us to validate the callback is still relevant when it fires
        const scheduledSpeedData = speedData;
        const scheduledTimestamp = Date.now();

        zeroGracePeriodRef.current = setTimeout(() => {
          // Only apply zero-state if:
          // 1. Current count is actually still zero (check the ref which is always up-to-date)
          // 2. This prevents stale speedData from overwriting current state in race conditions
          const currentCount = lastActiveCountRef.current ?? 0;
          if (currentCount === 0) {
            lastSpeedUpdateRef.current = scheduledTimestamp;
            setSpeedSnapshot(scheduledSpeedData);
            setIsLoading(false);
          }
          zeroGracePeriodRef.current = null;
        }, 1500);
        return;
      }

      // ALWAYS accept updates immediately when active games count changes (and it's not going to zero)
      // This ensures new downloads appear instantly
      const countChanged = previousCount !== newCount;

      if (countChanged) {
        lastSpeedUpdateRef.current = Date.now();
        setSpeedSnapshot(speedData);
        setIsLoading(false);
        return;
      }

      // Throttle check - apply update if enough time has passed
      const maxRefreshRate = getRefreshIntervalRef.current();
      const now = Date.now();
      const timeSinceLastUpdate = now - lastSpeedUpdateRef.current;

      // User's setting controls max refresh rate
      // LIVE mode (0) = 500ms minimum to prevent UI thrashing
      const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

      if (timeSinceLastUpdate >= minInterval) {
        lastSpeedUpdateRef.current = now;
        setSpeedSnapshot(speedData);
        setIsLoading(false);
      }
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);

    return () => {
      signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
      // Clean up zero-grace timeout on unmount
      if (zeroGracePeriodRef.current) {
        clearTimeout(zeroGracePeriodRef.current);
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
