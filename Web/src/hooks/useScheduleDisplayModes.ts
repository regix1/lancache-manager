import { useCallback, useEffect, useRef, useState } from 'react';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  NotificationDisplayMode,
  ServiceScheduleInfo
} from '@components/features/management/schedules/types';

type ScheduleDisplayModeMap = Record<string, NotificationDisplayMode>;

const toDisplayModeMap = (schedules: ServiceScheduleInfo[]): ScheduleDisplayModeMap => {
  const map: ScheduleDisplayModeMap = {};
  for (const schedule of schedules) {
    map[schedule.key] = schedule.notificationDisplayMode;
  }
  return map;
};

/**
 * Per-service notification display mode ('full' | 'condensed'), keyed by schedule serviceKey.
 * Seeds once from GET /api/system/schedules and then follows the SchedulesUpdated broadcast, so a
 * toggle on the Schedules page reaches the notification bar live without polling. A serviceKey
 * absent from the map has no persisted preference and is treated as 'full' by the consumer, so a
 * failed initial fetch degrades to the default full-card rendering rather than an error surface.
 */
export function useScheduleDisplayModes(): ScheduleDisplayModeMap {
  const [displayModes, setDisplayModes] = useState<ScheduleDisplayModeMap>({});
  const { on, off, connectionState } = useSignalR();
  // Single freshness sequence shared by every writer below: each applied update bumps it, and a
  // fetch response only lands if the generation captured when the request was sent still matches
  // when it resolves. A GET snapshot resolving after newer data has already applied is discarded
  // instead of rolling the map back to its pre-toggle state. [20]
  const generationRef = useRef<number>(0);

  const applySchedules = useCallback((schedules: ServiceScheduleInfo[]): void => {
    generationRef.current += 1;
    setDisplayModes(toDisplayModeMap(schedules));
  }, []);

  useEffect(() => {
    let cancelled = false;
    // Subscribe before the seed fetch: a broadcast landing while the GET is in flight is newer
    // than the GET's snapshot and bumps the generation, so the seed response is then discarded.
    on('SchedulesUpdated', applySchedules);
    const requestGeneration = generationRef.current;
    ApiService.getSchedules()
      .then((schedules) => {
        if (!cancelled && generationRef.current === requestGeneration) {
          applySchedules(schedules);
        }
      })
      .catch((error: unknown) => {
        // Best-effort seed: an unreachable schedules endpoint leaves every service at the
        // 'full' default (an explicit, documented result), so no user-facing error is raised.
        console.error('useScheduleDisplayModes seed failed:', getErrorMessage(error));
      });
    return () => {
      cancelled = true;
      off('SchedulesUpdated', applySchedules);
    };
  }, [on, off, applySchedules]);

  // Refetch on reconnect: broadcasts sent while SignalR was down are gone for good, so the map
  // must be re-based on authoritative state (same recovery the Schedules page itself performs).
  useEffect(() => {
    if (connectionState !== 'connected') {
      return;
    }
    let cancelled = false;
    const requestGeneration = generationRef.current;
    ApiService.getSchedules()
      .then((schedules) => {
        if (!cancelled && generationRef.current === requestGeneration) {
          applySchedules(schedules);
        }
      })
      .catch((error: unknown) => {
        console.error('useScheduleDisplayModes reconnect refresh failed:', getErrorMessage(error));
      });
    return () => {
      cancelled = true;
    };
  }, [connectionState, applySchedules]);

  return displayModes;
}
