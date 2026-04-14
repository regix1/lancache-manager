/* eslint-disable no-console */
import React, { useState, useEffect, useCallback, useMemo, type ReactNode } from 'react';
import { storage } from '@utils/storage';
import { TimeFilterContext, type TimeRange } from './TimeFilterContext.types';
import { getTimeRangeHours, computeTimeRangeParams } from './TimeFilterContext.utils';

interface TimeFilterProviderProps {
  children: ReactNode;
}

export const TimeFilterProvider: React.FC<TimeFilterProviderProps> = ({ children }) => {
  // Restore time range from localStorage on initial load
  const [timeRange, setTimeRangeState] = useState<TimeRange>(() => {
    const saved = storage.getItem('lancache_time_range');
    const savedStartDate = storage.getItem('lancache_custom_start_date');
    const savedEndDate = storage.getItem('lancache_custom_end_date');

    // If saved timeRange is 'custom' but dates are missing, fall back to 'live'
    if (saved === 'custom' && (!savedStartDate || !savedEndDate)) {
      return 'live';
    }

    // Handle legacy 'event' time range - convert to 'live'
    if (saved === 'event') {
      return 'live';
    }

    return (saved as TimeRange) || 'live';
  });

  const [customStartDate, setCustomStartDate] = useState<Date | null>(() => {
    const saved = storage.getItem('lancache_custom_start_date');
    if (saved) {
      const date = new Date(saved);
      if (!isNaN(date.getTime())) {
        return date;
      }
    }
    return null;
  });

  const [customEndDate, setCustomEndDate] = useState<Date | null>(() => {
    const saved = storage.getItem('lancache_custom_end_date');
    if (saved) {
      const date = new Date(saved);
      if (!isNaN(date.getTime())) {
        return date;
      }
    }
    return null;
  });

  // Selected event IDs for filtering by tagged downloads (independent of time range)
  // Not persisted to localStorage - resets on page refresh to prevent zero-data bug
  const [selectedEventIds, setSelectedEventIdsState] = useState<number[]>([]);

  // Anchor time for rolling time ranges (1h, 6h, 12h, 24h, 7d, 30d)
  // When set, getTimeRangeParams uses this instead of Date.now() to prevent time drift
  const [rangeAnchorTime, setRangeAnchorTime] = useState<number | null>(() => {
    // Initialize anchor for rolling ranges on mount
    const saved = storage.getItem('lancache_time_range') as TimeRange;
    if (saved && saved !== 'live' && saved !== 'custom') {
      return Date.now();
    }
    return null;
  });

  // Wrapper for setTimeRange that validates the value and sets anchor time
  const setTimeRange = useCallback((range: TimeRange) => {
    setTimeRangeState((prev) => {
      console.log('[SPARKDBG] useTimeFilter/setTimeRange', { from: prev, to: range });
      return range;
    });
    // Set anchor time for rolling ranges, clear for live/custom
    if (range !== 'live' && range !== 'custom') {
      setRangeAnchorTime(Date.now());
    } else {
      setRangeAnchorTime(null);
    }
  }, []);

  // Persist timeRange to localStorage
  useEffect(() => {
    storage.setItem('lancache_time_range', timeRange);
  }, [timeRange]);

  // Persist custom dates to localStorage
  useEffect(() => {
    if (customStartDate) {
      storage.setItem('lancache_custom_start_date', customStartDate.toISOString());
    } else {
      storage.removeItem('lancache_custom_start_date');
    }
  }, [customStartDate]);

  useEffect(() => {
    if (customEndDate) {
      storage.setItem('lancache_custom_end_date', customEndDate.toISOString());
    } else {
      storage.removeItem('lancache_custom_end_date');
    }
  }, [customEndDate]);

  // Set selected event IDs
  const setSelectedEventIds = useCallback((ids: number[]) => {
    setSelectedEventIdsState(ids);
  }, []);

  // Toggle a single event ID
  const toggleEventId = useCallback((id: number) => {
    setSelectedEventIdsState((prev) => {
      if (prev.includes(id)) {
        return prev.filter((eid) => eid !== id);
      } else {
        return [...prev, id];
      }
    });
  }, []);

  // Clear all event filters
  const clearEventFilter = useCallback(() => {
    setSelectedEventIdsState([]);
  }, []);

  // Extend the time anchor forward (re-anchor to now)
  // Called by StatsContext/DownloadsContext when receiving SignalR events
  const extendTimeAnchor = useCallback(() => {
    if (timeRange !== 'live' && timeRange !== 'custom') {
      setRangeAnchorTime(Date.now());
    }
  }, [timeRange]);

  const getTimeRangeInHours = useCallback((): number => {
    if (timeRange === 'custom' && customStartDate && customEndDate) {
      const diffMs = customEndDate.getTime() - customStartDate.getTime();
      return Math.ceil(diffMs / (1000 * 60 * 60));
    }
    return getTimeRangeHours(timeRange);
  }, [timeRange, customStartDate, customEndDate]);

  const getTimeRangeParams = useCallback((): { startTime?: number; endTime?: number } => {
    const now = rangeAnchorTime ?? Date.now();
    return computeTimeRangeParams(
      timeRange,
      now,
      customStartDate?.getTime(),
      customEndDate?.getTime()
    );
  }, [timeRange, customStartDate, customEndDate, rangeAnchorTime]);

  // Wrapped setters with optional logging
  const setCustomStartDateWithLogging = useCallback((date: Date | null) => {
    setCustomStartDate(date);
  }, []);

  const setCustomEndDateWithLogging = useCallback((date: Date | null) => {
    setCustomEndDate(date);
  }, []);

  const value = useMemo(
    () => ({
      timeRange,
      setTimeRange,
      customStartDate,
      customEndDate,
      setCustomStartDate: setCustomStartDateWithLogging,
      setCustomEndDate: setCustomEndDateWithLogging,
      getTimeRangeInHours,
      getTimeRangeParams,
      rangeAnchorTime,
      extendTimeAnchor,
      selectedEventIds,
      setSelectedEventIds,
      toggleEventId,
      clearEventFilter
    }),
    [
      timeRange,
      setTimeRange,
      customStartDate,
      customEndDate,
      setCustomStartDateWithLogging,
      setCustomEndDateWithLogging,
      getTimeRangeInHours,
      getTimeRangeParams,
      rangeAnchorTime,
      extendTimeAnchor,
      selectedEventIds,
      setSelectedEventIds,
      toggleEventId,
      clearEventFilter
    ]
  );

  return <TimeFilterContext.Provider value={value}>{children}</TimeFilterContext.Provider>;
};
