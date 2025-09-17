import React, { createContext, useContext, useState, ReactNode } from 'react';

export type TimeRange = '1h' | '6h' | '12h' | '24h' | '7d' | '30d' | 'all' | 'custom';

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
  const [timeRange, setTimeRange] = useState<TimeRange>('24h');
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
      case 'all':
        return 999999; // Large number to represent all time
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
      return {
        startTime: Math.floor(customStartDate.getTime() / 1000),
        endTime: Math.floor(customEndDate.getTime() / 1000)
      };
    }

    // Return empty params for 'all' time to fetch everything
    if (timeRange === 'all') {
      return {};
    }

    const now = Date.now();
    const hoursMs = getTimeRangeInHours() * 60 * 60 * 1000;
    return {
      startTime: Math.floor((now - hoursMs) / 1000),
      endTime: Math.floor(now / 1000)
    };
  };

  return (
    <TimeFilterContext.Provider
      value={{
        timeRange,
        setTimeRange,
        customStartDate,
        customEndDate,
        setCustomStartDate,
        setCustomEndDate,
        getTimeRangeInHours,
        getTimeRangeParams
      }}
    >
      {children}
    </TimeFilterContext.Provider>
  );
};