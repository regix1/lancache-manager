import React, { createContext, useContext, useState, type ReactNode } from 'react';

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
  // NOTE: Removed localStorage persistence to fix cross-tab sync issues
  // Each browser tab now has independent time range state (default: 'live')
  const [timeRange, setTimeRange] = useState<TimeRange>('live');
  const [customStartDate, setCustomStartDate] = useState<Date | null>(null);
  const [customEndDate, setCustomEndDate] = useState<Date | null>(null);

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

      // Debug logging - uncomment if needed for debugging
      // console.log('📅 Custom Range Selected:', {
      //   startDate: customStartDate.toLocaleString(),
      //   endDate: endDate.toLocaleString(),
      //   startTime,
      //   endTime,
      //   daysDiff: Math.ceil((endDate.getTime() - customStartDate.getTime()) / (1000 * 60 * 60 * 24))
      // });

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
        getTimeRangeParams
      }}
    >
      {children}
    </TimeFilterContext.Provider>
  );
};
