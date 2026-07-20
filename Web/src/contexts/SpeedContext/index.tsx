import React, { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useRefreshRate } from '@contexts/useRefreshRate';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import type { DownloadSpeedSnapshot, GameSpeedInfo, ClientSpeedInfo } from '../../types';
import type { SpeedContextType, SpeedProviderProps } from './types';
import { SpeedContext } from './SpeedContext.types';
import type { ShowToastEvent } from '@contexts/SignalRContext/types';
import { APP_EVENTS } from '@utils/constants';

export const SpeedProvider: React.FC<SpeedProviderProps> = ({ children }: SpeedProviderProps) => {
  const signalR = useSignalR();
  const { getRefreshInterval } = useRefreshRate();
  const { hasSession, isLoading: authLoading } = useAuth();
  const hasAccess = !authLoading && hasSession;
  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Throttling refs
  const lastSpeedUpdateRef = useRef<number>(0);
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

  // Apply a speed snapshot directly. The tracker's activity window reflects real
  // delivery cadence, so a reported zero is trustworthy and can render immediately.
  const applySpeedSnapshot = useCallback((data: DownloadSpeedSnapshot) => {
    lastActiveCountRef.current = data?.gameSpeeds?.length ?? 0;
    setSpeedSnapshot(data);
  }, []);

  // Fetch speed data from the API (used for initial load and manual refresh)
  const fetchSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      applySpeedSnapshot(data);
    } catch (error) {
      // Background poll (mount + SignalR reconnect + visibility change). Live SignalR
      // DownloadSpeedUpdate events keep speeds fresh even if one poll fails. Deliberately silent.
      console.error('[SpeedContext] Failed to fetch speed data:', error);
    } finally {
      setIsLoading(false);
    }
  }, [applySpeedSnapshot]);

  // Manual refresh function exposed to consumers (user-triggered) - unlike fetchSpeed's
  // background polling, a failure here has no other feedback path, so surface it.
  const refreshSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      applySpeedSnapshot(data);
    } catch (error) {
      console.error('[SpeedContext] Failed to refresh speed data:', error);
      // SpeedProvider is an ancestor of NotificationsProvider in AppProviders.tsx, so
      // useErrorHandler (useNotifications) is not reachable here. Use the existing show-toast
      // bridge instead (mirrors NotificationsContext.tsx:332-356).
      window.dispatchEvent(
        new CustomEvent<ShowToastEvent>(APP_EVENTS.SHOW_TOAST, {
          detail: { type: 'error', message: 'Failed to refresh download speeds.', duration: 4000 }
        })
      );
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
      lastActiveCountRef.current = newCount;

      // Count changes (including transitions to/from zero) apply immediately so new
      // downloads and completions show up without delay.
      if (previousCount !== newCount) {
        lastSpeedUpdateRef.current = Date.now();
        setSpeedSnapshot(speedData);
        setIsLoading(false);
        return;
      }

      // Throttle same-count (speed-value-only) updates to the user's refresh-rate setting:
      // LIVE (0) -> 500ms (instant), otherwise the chosen interval (e.g. 10s -> one update / 10s).
      // Leading throttle (fires once the interval has elapsed). Count changes bypass this above,
      // so new downloads / completion still appear promptly regardless of the setting.
      const maxRefreshRate = getRefreshIntervalRef.current();
      const now = Date.now();
      const timeSinceLastUpdate = now - lastSpeedUpdateRef.current;
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
    [
      speedSnapshot,
      gameSpeeds,
      clientSpeeds,
      activeDownloadCount,
      totalActiveClients,
      isLoading,
      refreshSpeed
    ]
  );

  return <SpeedContext.Provider value={value}>{children}</SpeedContext.Provider>;
};
