import React, { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import { storage } from '@utils/storage';

// Time range controls WHEN to look at data
// Event filter (selectedEventId) controls WHAT data to show (all or only tagged to event)
export type TimeRange = '1h' | '6h' | '12h' | '24h' | '7d' | '30d' | 'live' | 'custom';

interface TimeFilterContextType {
  timeRange: TimeRange;
  setTimeRange: (range: TimeRange) => void;
  customStartDate: Date | null;
  customEndDate: Date | null;
  setCustomStartDate: (date: Date | null) => void;
  setCustomEndDate: (date: Date | null) => void;
  getTimeRangeInHours: () => number;
  getTimeRangeParams: () => { startTime?: number; endTime?: number };
  // Event filter: optional filter to show only downloads tagged to specific events
  // This is independent of time range - you can combine any time range with an event filter
  selectedEventIds: number[];
  setSelectedEventIds: (ids: number[]) => void;
  toggleEventId: (id: number) => void;
  clearEventFilter: () => void;
}

const TimeFilterContext = createContext<TimeFilterContextType | undefined>(undefined);

export const useTimeFilter = () => {
  const context = useContext(TimeFilterContext);
  if (!context) {
    throw new Error('useTimeFilter must be used within a TimeFilterProvider');
  }
  return context;
};

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
  const [selectedEventIds, setSelectedEventIdsState] = useState<number[]>(() => {
    const saved = storage.getItem('lancache_selected_event_ids');
    if (saved) {
      try {
        const parsed = JSON.parse(saved);
        return Array.isArray(parsed) ? parsed : [];
      } catch {
        // Migrate from old single ID format
        const oldSaved = storage.getItem('lancache_selected_event_id');
        if (oldSaved) {
          const id = parseInt(oldSaved, 10);
          storage.removeItem('lancache_selected_event_id');
          return isNaN(id) ? [] : [id];
        }
        return [];
      }
    }
    // Migrate from old single ID format
    const oldSaved = storage.getItem('lancache_selected_event_id');
    if (oldSaved) {
      const id = parseInt(oldSaved, 10);
      storage.removeItem('lancache_selected_event_id');
      return isNaN(id) ? [] : [id];
    }
    return [];
  });

  // Wrapper for setTimeRange that validates the value
  const setTimeRange = (range: TimeRange) => {
    setTimeRangeState(range);
  };

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

  // Persist selected event IDs
  useEffect(() => {
    if (selectedEventIds.length > 0) {
      storage.setItem('lancache_selected_event_ids', JSON.stringify(selectedEventIds));
    } else {
      storage.removeItem('lancache_selected_event_ids');
    }
  }, [selectedEventIds]);

  // Set selected event IDs
  const setSelectedEventIds = (ids: number[]) => {
    setSelectedEventIdsState(ids);
  };

  // Toggle a single event ID
  const toggleEventId = (id: number) => {
    setSelectedEventIdsState(prev => {
      if (prev.includes(id)) {
        return prev.filter(eid => eid !== id);
      } else {
        return [...prev, id];
      }
    });
  };

  // Clear all event filters
  const clearEventFilter = () => {
    setSelectedEventIdsState([]);
  };

  const getTimeRangeInHours = (): number => {
    switch (timeRange) {
      case '1h':
        return 1;
      case '6h':
        return 6;
      case '12h':
        return 12;
      case '24h':
        return 24;
      case '7d':
        return 168;
      case '30d':
        return 720;
      case 'live':
        return 999999; // Large number to represent live data (all time)
      case 'custom':
        if (customStartDate && customEndDate) {
          const diffMs = customEndDate.getTime() - customStartDate.getTime();
          return Math.ceil(diffMs / (1000 * 60 * 60));
        }
        return 24;
      default:
        return 24;
    }
  };

  const getTimeRangeParams = (): { startTime?: number; endTime?: number } => {
    if (timeRange === 'custom' && customStartDate && customEndDate) {
      const startTime = Math.floor(customStartDate.getTime() / 1000);
      // Set end time to end of day (23:59:59) instead of start of day
      const endDate = new Date(customEndDate);
      endDate.setHours(23, 59, 59, 999);
      const endTime = Math.floor(endDate.getTime() / 1000);

      return { startTime, endTime };
    }

    // Return empty params for 'live' time to fetch everything
    if (timeRange === 'live') {
      return {};
    }

    const now = Date.now();
    const hoursMs = getTimeRangeInHours() * 60 * 60 * 1000;
    return {
      startTime: Math.floor((now - hoursMs) / 1000),
      endTime: Math.floor(now / 1000)
    };
  };

  // Wrapped setters with optional logging
  const setCustomStartDateWithLogging = (date: Date | null) => {
    // console.log('📅 Setting custom start date:', date?.toLocaleString() || 'null');
    setCustomStartDate(date);
  };

  const setCustomEndDateWithLogging = (date: Date | null) => {
    // console.log('📅 Setting custom end date:', date?.toLocaleString() || 'null');
    setCustomEndDate(date);
  };

  return (
    <TimeFilterContext.Provider
      value={{
        timeRange,
        setTimeRange,
        customStartDate,
        customEndDate,
        setCustomStartDate: setCustomStartDateWithLogging,
        setCustomEndDate: setCustomEndDateWithLogging,
        getTimeRangeInHours,
        getTimeRangeParams,
        selectedEventIds,
        setSelectedEventIds,
        toggleEventId,
        clearEventFilter
      }}
    >
      {children}
    </TimeFilterContext.Provider>
  );
};
