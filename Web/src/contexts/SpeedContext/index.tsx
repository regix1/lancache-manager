import React, { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import i18n from '@/i18n';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useRefreshRate } from '@contexts/useRefreshRate';
import { useAuth } from '@contexts/useAuth';
import { useMockMode } from '@contexts/useMockMode';
import ApiService from '@services/api.service';
import MockDataService from '@/test/mockData.service';
import type { DownloadSpeedSnapshot, GameSpeedInfo, ClientSpeedInfo } from '../../types';
import type { SpeedContextType, SpeedProviderProps } from './types';
import { SpeedContext } from './SpeedContext.types';
import type { ShowToastEvent } from '@contexts/SignalRContext/types';
import { APP_EVENTS } from '@utils/constants';
import { getErrorMessage } from '@utils/error';
import { DISCONNECTED_POLL_MS } from './constants';
import {
  canAcceptRestSnapshot,
  canAcceptSignalRSnapshot,
  hasImmediateSnapshotChange,
  isDownloadSpeedSnapshot
} from './snapshot';

export const SpeedProvider: React.FC<SpeedProviderProps> = ({ children }: SpeedProviderProps) => {
  const signalR = useSignalR();
  const { getRefreshInterval } = useRefreshRate();
  const { hasSession, isLoading: authLoading } = useAuth();
  const { mockMode } = useMockMode();
  const hasAccess = !authLoading && hasSession;
  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const acceptedSnapshotRef = useRef<DownloadSpeedSnapshot | null>(null);
  const renderedSnapshotRef = useRef<DownloadSpeedSnapshot | null>(null);
  const pendingSnapshotRef = useRef<DownloadSpeedSnapshot | null>(null);
  const lastSpeedUpdateRef = useRef(0);
  const throttleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const requestOwnerRef = useRef(0);
  const mountedRef = useRef(true);
  const inFlightRef = useRef<{
    owner: number;
    promise: Promise<void>;
    trailing: boolean;
    notifyFailure: boolean;
    nextThrottle: boolean;
  } | null>(null);

  const getRefreshIntervalRef = useRef(getRefreshInterval);
  getRefreshIntervalRef.current = getRefreshInterval;

  const gameSpeeds: GameSpeedInfo[] = useMemo(
    () => speedSnapshot?.gameSpeeds ?? [],
    [speedSnapshot]
  );
  const clientSpeeds: ClientSpeedInfo[] = useMemo(
    () => speedSnapshot?.clientSpeeds ?? [],
    [speedSnapshot]
  );
  const activeDownloadCount = gameSpeeds.length;
  const totalActiveClients = clientSpeeds.length;

  const clearThrottle = useCallback(() => {
    if (throttleTimerRef.current !== null) {
      clearTimeout(throttleTimerRef.current);
      throttleTimerRef.current = null;
    }
    pendingSnapshotRef.current = null;
  }, []);

  const commitSnapshot = useCallback(
    (snapshot: DownloadSpeedSnapshot) => {
      clearThrottle();
      renderedSnapshotRef.current = snapshot;
      lastSpeedUpdateRef.current = Date.now();
      setSpeedSnapshot(snapshot);
      setIsLoading(false);
    },
    [clearThrottle]
  );

  const renderAcceptedSnapshot = useCallback(
    (snapshot: DownloadSpeedSnapshot, throttle: boolean) => {
      const current = renderedSnapshotRef.current;
      if (!throttle || hasImmediateSnapshotChange(current, snapshot)) {
        commitSnapshot(snapshot);
        return;
      }

      const configuredInterval = getRefreshIntervalRef.current();
      const minimumInterval = configuredInterval === 0 ? 500 : configuredInterval;
      const elapsed = Date.now() - lastSpeedUpdateRef.current;
      if (elapsed >= minimumInterval) {
        commitSnapshot(snapshot);
        return;
      }

      pendingSnapshotRef.current = snapshot;
      if (throttleTimerRef.current !== null) return;
      throttleTimerRef.current = setTimeout(
        () => {
          throttleTimerRef.current = null;
          const pending = pendingSnapshotRef.current;
          pendingSnapshotRef.current = null;
          if (pending !== null) {
            renderedSnapshotRef.current = pending;
            lastSpeedUpdateRef.current = Date.now();
            setSpeedSnapshot(pending);
            setIsLoading(false);
          }
        },
        Math.max(0, minimumInterval - elapsed)
      );
    },
    [commitSnapshot]
  );

  const acceptSnapshot = useCallback(
    (value: unknown, owner: number, throttle: boolean): boolean => {
      if (owner !== requestOwnerRef.current || !mountedRef.current || mockMode) return false;
      if (!isDownloadSpeedSnapshot(value)) {
        throw new TypeError('Invalid current download activity response');
      }

      const current = acceptedSnapshotRef.current;
      if (!canAcceptRestSnapshot(current, value)) return false;

      acceptedSnapshotRef.current = value;
      renderAcceptedSnapshot(value, throttle);
      return true;
    },
    [mockMode, renderAcceptedSnapshot]
  );

  const applyMockSnapshot = useCallback(() => {
    const snapshot: unknown = MockDataService.generateMockSpeedSnapshot();
    if (!isDownloadSpeedSnapshot(snapshot)) {
      throw new TypeError('Invalid mock download activity response');
    }
    acceptedSnapshotRef.current = snapshot;
    commitSnapshot(snapshot);
  }, [commitSnapshot]);

  const requestSpeed = useCallback(
    (options: { notifyFailure: boolean; throttle: boolean }): Promise<void> => {
      if (mockMode) {
        applyMockSnapshot();
        return Promise.resolve();
      }

      const owner = requestOwnerRef.current;
      const activeRequest = inFlightRef.current;
      if (activeRequest?.owner === owner) {
        activeRequest.trailing = true;
        activeRequest.notifyFailure ||= options.notifyFailure;
        activeRequest.nextThrottle &&= options.throttle;
        return activeRequest.promise;
      }

      const work = {
        owner,
        promise: Promise.resolve(),
        trailing: false,
        notifyFailure: options.notifyFailure,
        nextThrottle: options.throttle
      };

      work.promise = (async () => {
        do {
          work.trailing = false;
          const notifyFailure = work.notifyFailure;
          const throttle = work.nextThrottle;
          work.notifyFailure = false;
          work.nextThrottle = true;

          try {
            const value: unknown = await ApiService.getCurrentSpeeds();
            if (owner !== requestOwnerRef.current || !mountedRef.current) return;
            acceptSnapshot(value, owner, throttle);
          } catch (error) {
            if (owner !== requestOwnerRef.current || !mountedRef.current) return;
            console.error('[SpeedContext] Failed to fetch speed data:', error);
            if (notifyFailure) {
              window.dispatchEvent(
                new CustomEvent<ShowToastEvent>(APP_EVENTS.SHOW_TOAST, {
                  detail: {
                    type: 'error',
                    message: i18n.t('dashboard.errors.refreshSpeedsFailed'),
                    error: getErrorMessage(error)
                  }
                })
              );
            }
          } finally {
            if (owner === requestOwnerRef.current && mountedRef.current) {
              setIsLoading(false);
            }
          }
        } while (work.trailing && owner === requestOwnerRef.current && mountedRef.current);
      })().finally(() => {
        if (inFlightRef.current === work) {
          inFlightRef.current = null;
        }
      });

      inFlightRef.current = work;
      return work.promise;
    },
    [acceptSnapshot, applyMockSnapshot, mockMode]
  );

  const fetchSpeed = useCallback(
    (throttle: boolean) => requestSpeed({ notifyFailure: false, throttle }),
    [requestSpeed]
  );

  const refreshSpeed = useCallback(
    () => requestSpeed({ notifyFailure: true, throttle: false }),
    [requestSpeed]
  );

  useEffect(() => {
    requestOwnerRef.current += 1;
    clearThrottle();
    acceptedSnapshotRef.current = null;
    renderedSnapshotRef.current = null;
    lastSpeedUpdateRef.current = 0;
    setSpeedSnapshot(null);

    if (authLoading) {
      setIsLoading(true);
      return;
    }
    if (!hasAccess) {
      setIsLoading(false);
      return;
    }
    if (mockMode) {
      applyMockSnapshot();
      return;
    }
    setIsLoading(true);
  }, [applyMockSnapshot, authLoading, clearThrottle, hasAccess, mockMode]);

  useEffect(() => {
    if (hasAccess && !mockMode) {
      void fetchSpeed(false);
    }
  }, [fetchSpeed, hasAccess, mockMode]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      requestOwnerRef.current += 1;
      clearThrottle();
    };
  }, [clearThrottle]);

  useEffect(() => {
    if (!hasAccess || mockMode) return;
    const interval = setInterval(() => {
      void fetchSpeed(true);
    }, DISCONNECTED_POLL_MS);
    return () => clearInterval(interval);
  }, [fetchSpeed, hasAccess, mockMode]);

  useReconnectRefetch(signalR.isConnected, () => {
    if (hasAccess && !mockMode) {
      void fetchSpeed(false);
    }
  });

  useEffect(() => {
    let visibilityTimer: ReturnType<typeof setTimeout> | null = null;
    const handleVisibilityChange = () => {
      if (!document.hidden && hasAccess && !mockMode) {
        visibilityTimer = setTimeout(() => {
          void fetchSpeed(false);
        }, 500);
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      if (visibilityTimer !== null) clearTimeout(visibilityTimer);
    };
  }, [fetchSpeed, hasAccess, mockMode]);

  useEffect(() => {
    if (mockMode) return;

    const handleSpeedUpdate = (value: unknown) => {
      if (!isDownloadSpeedSnapshot(value)) {
        void fetchSpeed(false);
        return;
      }

      const current = acceptedSnapshotRef.current;
      if (current === null || current.streamId !== value.streamId) {
        void fetchSpeed(false);
        return;
      }
      if (!canAcceptSignalRSnapshot(current, value)) return;

      acceptedSnapshotRef.current = value;
      renderAcceptedSnapshot(value, true);
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);
    return () => signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
  }, [fetchSpeed, mockMode, renderAcceptedSnapshot, signalR]);

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
