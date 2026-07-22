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

// Expiry for an accepted ACTIVE snapshot: the remaining server-side rolling window plus a
// grace period, capped near the tracker's maximum adaptive window (15s) plus grace. If no
// newer snapshot arrives within that time, the active data is cleared instead of lingering
// forever (SignalR gap, tracker death, dropped trailing-zero broadcast).
const EXPIRY_GRACE_MS = 2000;
const EXPIRY_CAP_MS = 17000;
// REST fallback cadence while the SignalR socket is not connected.
const DISCONNECTED_POLL_MS = 5000;

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
  // Newest accepted server timestamp. Snapshots older than this are dropped so a delayed
  // REST response can never overwrite newer SignalR data or resurrect expired traffic.
  const latestAcceptedTimestampRef = useRef<number>(0);
  const expiryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

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

  const clearExpiryTimer = useCallback(() => {
    if (expiryTimerRef.current !== null) {
      clearTimeout(expiryTimerRef.current);
      expiryTimerRef.current = null;
    }
  }, []);

  const expireActiveSnapshot = useCallback(() => {
    expiryTimerRef.current = null;
    lastActiveCountRef.current = 0;
    // Zero the live fields but keep the snapshot object and the accepted-timestamp guard,
    // so a stale response arriving after expiry cannot resurrect the old activity.
    setSpeedSnapshot((prev) =>
      prev === null
        ? prev
        : {
            ...prev,
            totalBytesPerSecond: 0,
            entriesInWindow: 0,
            hasActiveDownloads: false,
            gameSpeeds: [],
            clientSpeeds: []
          }
    );
  }, []);

  // Single acceptance path for REST and SignalR snapshots. Timestamps are validated
  // monotonically, the expiry timer is (re)armed for every ACCEPTED snapshot (including
  // ones the render throttle skips - otherwise a slow refresh-rate setting would let
  // active data expire between rendered updates), and only then may state update. The
  // tracker's activity window reflects real delivery cadence, so a reported zero is
  // trustworthy and renders immediately (count changes bypass the throttle).
  const acceptSnapshot = useCallback(
    (data: DownloadSpeedSnapshot, options: { throttle: boolean }) => {
      const timestampMs = Date.parse(data?.timestampUtc ?? '');
      if (!Number.isFinite(timestampMs)) return;
      if (timestampMs < latestAcceptedTimestampRef.current) return;
      latestAcceptedTimestampRef.current = timestampMs;

      const isActive = (data.entriesInWindow ?? 0) > 0 || (data.gameSpeeds?.length ?? 0) > 0;
      clearExpiryTimer();
      if (isActive) {
        const windowMs = (data.windowSeconds || 2) * 1000;
        const remainingMs = Math.max(0, windowMs - (Date.now() - timestampMs));
        expiryTimerRef.current = setTimeout(
          expireActiveSnapshot,
          Math.min(remainingMs + EXPIRY_GRACE_MS, EXPIRY_CAP_MS)
        );
      }

      const newCount = data.gameSpeeds?.length ?? 0;
      const previousCount = lastActiveCountRef.current ?? 0;
      lastActiveCountRef.current = newCount;

      // Throttle same-count (speed-value-only) updates to the user's refresh-rate setting:
      // LIVE (0) -> 500ms (instant), otherwise the chosen interval (e.g. 10s). Count
      // changes render immediately so new downloads and completions appear promptly.
      if (options.throttle && previousCount === newCount) {
        const maxRefreshRate = getRefreshIntervalRef.current();
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;
        if (Date.now() - lastSpeedUpdateRef.current < minInterval) {
          return;
        }
      }

      lastSpeedUpdateRef.current = Date.now();
      setSpeedSnapshot(data);
      setIsLoading(false);
    },
    [clearExpiryTimer, expireActiveSnapshot]
  );

  // Fetch speed data from the API (used for initial load and manual refresh)
  const fetchSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      acceptSnapshot(data, { throttle: false });
    } catch (error) {
      // Background poll (mount + SignalR reconnect + visibility change). Live SignalR
      // DownloadSpeedUpdate events keep speeds fresh even if one poll fails. Deliberately silent.
      console.error('[SpeedContext] Failed to fetch speed data:', error);
    } finally {
      setIsLoading(false);
    }
  }, [acceptSnapshot]);

  // Manual refresh function exposed to consumers (user-triggered) - unlike fetchSpeed's
  // background polling, a failure here has no other feedback path, so surface it.
  const refreshSpeed = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      acceptSnapshot(data, { throttle: false });
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
  }, [acceptSnapshot]);

  // Fetch initial data on mount (only when authenticated or guest)
  useEffect(() => {
    if (hasAccess) {
      fetchSpeed();
    } else if (!authLoading) {
      setIsLoading(false);
    }
  }, [fetchSpeed, hasAccess, authLoading]);

  // Clear all snapshot state and guards when access is lost so a re-login (possibly as a
  // different user) starts clean instead of briefly showing the previous session's traffic.
  useEffect(() => {
    if (!hasAccess && !authLoading) {
      clearExpiryTimer();
      setSpeedSnapshot(null);
      latestAcceptedTimestampRef.current = 0;
      lastActiveCountRef.current = null;
      lastSpeedUpdateRef.current = 0;
    }
  }, [hasAccess, authLoading, clearExpiryTimer]);

  // Cancel the expiry timer on unmount so it cannot fire into unmounted state.
  useEffect(() => () => clearExpiryTimer(), [clearExpiryTimer]);

  // While the SignalR socket is not connected, poll REST at a low frequency so activity
  // keeps updating (and can expire) instead of freezing at the last pushed snapshot. The
  // monotonic guard in acceptSnapshot keeps these polls from overwriting newer pushed data.
  useEffect(() => {
    if (!hasAccess || signalR.connectionState === 'connected') return;
    const interval = setInterval(() => {
      fetchSpeed();
    }, DISCONNECTED_POLL_MS);
    return () => clearInterval(interval);
  }, [signalR.connectionState, hasAccess, fetchSpeed]);

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

  // Listen for real-time speed updates via SignalR. Validation, expiry scheduling, and
  // render throttling all live in the shared acceptSnapshot path.
  useEffect(() => {
    const handleSpeedUpdate = (speedData: DownloadSpeedSnapshot) => {
      acceptSnapshot(speedData, { throttle: true });
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);

    return () => {
      signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
    };
  }, [signalR, acceptSnapshot]);

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
