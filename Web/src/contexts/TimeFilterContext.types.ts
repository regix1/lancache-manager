import { createContext } from 'react';

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
  // Anchor time for rolling time ranges - prevents time drift between fetches
  rangeAnchorTime: number | null;
  // Extends the anchor forward by re-anchoring to current time (for SignalR updates)
  extendTimeAnchor: () => void;
  // Event filter: optional filter to show only downloads tagged to specific events
  // This is independent of time range - you can combine any time range with an event filter
  selectedEventIds: number[];
  setSelectedEventIds: (ids: number[]) => void;
  toggleEventId: (id: number) => void;
  clearEventFilter: () => void;
}

export const TimeFilterContext = createContext<TimeFilterContextType | undefined>(undefined);
