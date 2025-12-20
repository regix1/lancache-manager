import React, { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import { storage } from '@utils/storage';

export type TimeRange = '1h' | '6h' | '12h' | '24h' | '7d' | '30d' | 'live' | 'custom' | 'event';

interface TimeFilterContextType {
  timeRange: TimeRange;
  setTimeRange: (range: TimeRange) => void;
  customStartDate: Date | null;
  customEndDate: Date | null;
  setCustomStartDate: (date: Date | null) => void;
  setCustomEndDate: (date: Date | null) => void;
  // Event time range (set by TimeFilter when event is selected)
  eventStartTime: number | null;
  eventEndTime: number | null;
  setEventTimeRange: (startTime: number | null, endTime: number | null) => void;
  getTimeRangeInHours: () => number;
  getTimeRangeParams: () => { startTime?: number; endTime?: number };
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
  const [timeRange, setTimeRange] = useState<TimeRange>(() => {
    const saved = storage.getItem('lancache_time_range');
    const savedStartDate = storage.getItem('lancache_custom_start_date');
    const savedEndDate = storage.getItem('lancache_custom_end_date');

    // If saved timeRange is 'custom' but dates are missing, fall back to 'live'
    if (saved === 'custom' && (!savedStartDate || !savedEndDate)) {
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

  // Event time range (Unix timestamps)
  const [eventStartTime, setEventStartTime] = useState<number | null>(() => {
    const saved = storage.getItem('lancache_event_start_time');
    return saved ? parseInt(saved, 10) : null;
  });

  const [eventEndTime, setEventEndTime] = useState<number | null>(() => {
    const saved = storage.getItem('lancache_event_end_time');
    return saved ? parseInt(saved, 10) : null;
  });

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

  // Persist event time range
  useEffect(() => {
    if (eventStartTime !== null) {
      storage.setItem('lancache_event_start_time', eventStartTime.toString());
    } else {
      storage.removeItem('lancache_event_start_time');
    }
  }, [eventStartTime]);

  useEffect(() => {
    if (eventEndTime !== null) {
      storage.setItem('lancache_event_end_time', eventEndTime.toString());
    } else {
      storage.removeItem('lancache_event_end_time');
    }
  }, [eventEndTime]);

  // Set event time range (called by TimeFilter when selecting an event)
  const setEventTimeRange = (startTime: number | null, endTime: number | null) => {
    setEventStartTime(startTime);
    setEventEndTime(endTime);
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
      case 'event':
        if (eventStartTime !== null && eventEndTime !== null) {
          const diffSeconds = eventEndTime - eventStartTime;
          return Math.ceil(diffSeconds / 3600);
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

    // Return event time range when event is selected
    if (timeRange === 'event' && eventStartTime !== null && eventEndTime !== null) {
      return { startTime: eventStartTime, endTime: eventEndTime };
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
        eventStartTime,
        eventEndTime,
        setEventTimeRange,
        getTimeRangeInHours,
        getTimeRangeParams
      }}
    >
      {children}
    </TimeFilterContext.Provider>
  );
};
