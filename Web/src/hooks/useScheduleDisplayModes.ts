import { useCallback, useEffect, useRef, useState } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { NotificationDisplayModeChangedEvent } from '@contexts/SignalRContext/types';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import type {
  NotificationDisplayMode,
  ServiceScheduleInfo
} from '@components/features/management/schedules/types';

type ScheduleDisplayModeMap = Record<string, NotificationDisplayMode>;

interface ScheduleDisplayModes {
  modes: ScheduleDisplayModeMap;
  defaultMode: NotificationDisplayMode;
  /** False until the first read of both settles, answered or not. */
  ready: boolean;
}

/**
 * A schedule that runs several platforms under one key contributes an entry per platform as well as
 * its own, keyed `<serviceKey>:<platform>[:<scheduleId>]`. Normalize the casing used by REST
 * dictionary keys and SignalR platform names. Scheduled prefill is the only one today.
 */
export const platformDisplayModeKey = (
  serviceKey: string,
  platform: string,
  scheduleId?: string
): string =>
  `${serviceKey}:${platform.toLowerCase()}${scheduleId ? `:${scheduleId.toLowerCase()}` : ''}`;

const toDisplayModeMap = (schedules: ServiceScheduleInfo[]): ScheduleDisplayModeMap => {
  const map: ScheduleDisplayModeMap = {};
  for (const schedule of schedules) {
    map[schedule.key] = schedule.notificationDisplayMode;
    for (const [platform, mode] of Object.entries(
      schedule.platformNotificationDisplayModes ?? {}
    )) {
      map[platformDisplayModeKey(schedule.key, platform)] = mode;
    }
  }
  return map;
};

/** A failed first read leaves notifications as full cards until a push or later read succeeds. */
export function useScheduleDisplayModes(): ScheduleDisplayModes {
  const [displayModes, setDisplayModes] = useState<ScheduleDisplayModeMap>({});
  const [defaultMode, setDefaultMode] = useState<NotificationDisplayMode>('full');
  const [ready, setReady] = useState(false);
  const { on, off, invoke, isConnected } = useSignalR();
  const schedulesGeneration = useRef(0);
  const defaultGeneration = useRef(0);
  const resyncPending = useRef({ schedules: false, defaultMode: false });
  const active = useRef(true);

  const applySchedules = useCallback((schedules: ServiceScheduleInfo[]): void => {
    schedulesGeneration.current += 1;
    resyncPending.current.schedules = false;
    setDisplayModes(toDisplayModeMap(schedules));
  }, []);

  const applyDefault = useCallback((event: NotificationDisplayModeChangedEvent): void => {
    defaultGeneration.current += 1;
    resyncPending.current.defaultMode = false;
    setDefaultMode(event.mode);
  }, []);

  const refresh = useCallback(async (): Promise<void> => {
    // Joining first closes the connection window in which a GET could miss a settings push.
    await Promise.allSettled([invoke('JoinAuthenticatedGroupAsync')]);
    if (!active.current) return;

    const schedulesRequest = schedulesGeneration.current;
    const defaultRequest = defaultGeneration.current;
    await Promise.all([
      ApiService.getSchedules()
        .then((schedules) => {
          if (active.current && schedulesGeneration.current === schedulesRequest) {
            applySchedules(schedules);
          }
        })
        .catch((error: unknown) => {
          if (active.current && schedulesGeneration.current === schedulesRequest) {
            resyncPending.current.schedules = true;
          }
          console.error('useScheduleDisplayModes seed failed:', error);
        }),
      ApiService.getGlobalNotificationDisplayMode()
        .then((mode) => {
          if (active.current && defaultGeneration.current === defaultRequest) {
            applyDefault({ mode });
          }
        })
        .catch((error: unknown) => {
          if (active.current && defaultGeneration.current === defaultRequest) {
            resyncPending.current.defaultMode = true;
          }
          console.error('useScheduleDisplayModes default seed failed:', error);
        })
    ]);
    // The bar draws no card before this, so a reload never shows one full and then moves it. [62]
    if (active.current) setReady(true);
  }, [invoke, applySchedules, applyDefault]);

  const retryPending = useCallback((): void => {
    if (resyncPending.current.schedules || resyncPending.current.defaultMode) {
      void refresh();
    }
  }, [refresh]);

  useEffect(() => {
    active.current = true;
    on('SchedulesUpdated', applySchedules);
    on('NotificationDisplayModeChanged', applyDefault);
    on('OperationUpdated', retryPending);
    const handleVisibility = (): void => {
      if (document.visibilityState === 'visible') retryPending();
    };
    document.addEventListener('visibilitychange', handleVisibility);
    void refresh();
    return () => {
      active.current = false;
      schedulesGeneration.current += 1;
      defaultGeneration.current += 1;
      off('SchedulesUpdated', applySchedules);
      off('NotificationDisplayModeChanged', applyDefault);
      off('OperationUpdated', retryPending);
      document.removeEventListener('visibilitychange', handleVisibility);
    };
  }, [on, off, applySchedules, applyDefault, retryPending, refresh]);

  useReconnectRefetch(isConnected, () => {
    void refresh();
  });

  return { modes: displayModes, defaultMode, ready };
}
