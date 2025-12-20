import React, { useState, useEffect, useMemo, useRef } from 'react';
import { Clock, Calendar, Radio, Info, CalendarDays, ChevronDown } from 'lucide-react';
import { useTimeFilter, type TimeRange } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import DateRangePicker from './DateRangePicker';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';

interface TimeFilterProps {
  disabled?: boolean;
}

const TimeFilter: React.FC<TimeFilterProps> = ({ disabled = false }) => {
  const {
    timeRange,
    setTimeRange,
    customStartDate,
    customEndDate,
    setCustomStartDate,
    setCustomEndDate,
    setEventTimeRange
  } = useTimeFilter();

  const {
    events,
    selectedEventId,
    selectedEvent,
    setSelectedEventId
  } = useEvents();

  const [showDatePicker, setShowDatePicker] = useState(false);
  const [showEventDropdown, setShowEventDropdown] = useState(false);
  const eventDropdownRef = useRef<HTMLDivElement>(null);

  // Close event dropdown when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (eventDropdownRef.current && !eventDropdownRef.current.contains(event.target as Node)) {
        setShowEventDropdown(false);
      }
    };

    if (showEventDropdown) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [showEventDropdown]);

  // Sort events: active first, then upcoming, then past
  const sortedEvents = useMemo(() => {
    const now = new Date();
    return [...events].sort((a, b) => {
      const aStart = new Date(a.startTimeUtc);
      const aEnd = new Date(a.endTimeUtc);
      const bStart = new Date(b.startTimeUtc);
      const bEnd = new Date(b.endTimeUtc);

      const aIsActive = now >= aStart && now <= aEnd;
      const bIsActive = now >= bStart && now <= bEnd;
      const aIsUpcoming = now < aStart;
      const bIsUpcoming = now < bStart;

      // Active events first
      if (aIsActive && !bIsActive) return -1;
      if (!aIsActive && bIsActive) return 1;

      // Upcoming events second
      if (aIsUpcoming && !bIsUpcoming) return -1;
      if (!aIsUpcoming && bIsUpcoming) return 1;

      // Sort by start date within groups
      return aStart.getTime() - bStart.getTime();
    });
  }, [events]);

  // Sync event time range when event is selected
  useEffect(() => {
    if (timeRange === 'event' && selectedEvent) {
      const startTime = Math.floor(new Date(selectedEvent.startTimeUtc).getTime() / 1000);
      const endTime = Math.floor(new Date(selectedEvent.endTimeUtc).getTime() / 1000);
      setEventTimeRange(startTime, endTime);
    }
  }, [timeRange, selectedEvent, setEventTimeRange]);

  const timeOptions = [
    { value: 'live', label: 'Live', shortLabel: 'Live', description: 'Show real-time data updates', icon: Radio, rightLabel: 'Now' },
    { value: '1h', label: 'Last Hour', shortLabel: '1H', description: 'Show data from the last 1 hour', icon: Clock, rightLabel: '1h' },
    { value: '6h', label: 'Last 6 Hours', shortLabel: '6H', description: 'Show data from the last 6 hours', icon: Clock, rightLabel: '6h' },
    { value: '12h', label: 'Last 12 Hours', shortLabel: '12H', description: 'Show data from the last 12 hours', icon: Clock, rightLabel: '12h' },
    { value: '24h', label: 'Last 24 Hours', shortLabel: '24H', description: 'Show data from the last 24 hours', icon: Clock, rightLabel: '24h' },
    { value: '7d', label: 'Last 7 Days', shortLabel: '7D', description: 'Show data from the last 7 days', icon: Calendar, rightLabel: '7d' },
    { value: '30d', label: 'Last 30 Days', shortLabel: '30D', description: 'Show data from the last 30 days', icon: Calendar, rightLabel: '30d' },
    { value: 'event', label: 'Event', shortLabel: 'Event', description: 'Filter by a scheduled event', icon: CalendarDays, rightLabel: events.length > 0 ? `${events.length}` : '0' },
    { value: 'custom', label: 'Custom Range', shortLabel: 'Custom', description: 'Select a custom date range', icon: Calendar, rightLabel: '...' }
  ];

  const handleTimeRangeChange = (value: string) => {
    const timeValue = value as TimeRange;
    setTimeRange(timeValue);
    if (timeValue === 'custom') {
      setShowDatePicker(true);
      setShowEventDropdown(false);
    } else if (timeValue === 'event') {
      setShowEventDropdown(true);
      setShowDatePicker(false);
      // If no event is selected, select the first one
      if (!selectedEventId && sortedEvents.length > 0) {
        setSelectedEventId(sortedEvents[0].id);
      }
    } else {
      setShowEventDropdown(false);
      setShowDatePicker(false);
    }
  };

  const handleEventSelect = (eventId: number) => {
    setSelectedEventId(eventId);
    setShowEventDropdown(false);
  };

  const formatEventDateRange = (startUtc: string, endUtc: string) => {
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    const startStr = start.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    const endStr = end.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    return startStr === endStr ? startStr : `${startStr} - ${endStr}`;
  };

  const getEventStatus = (startUtc: string, endUtc: string) => {
    const now = new Date();
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    if (now >= start && now <= end) return 'active';
    if (now < start) return 'upcoming';
    return 'past';
  };

  // Generate custom label for date ranges or event
  const getCustomTriggerLabel = () => {
    if (timeRange === 'custom' && customStartDate && customEndDate) {
      const start = customStartDate.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric'
      });
      const end = customEndDate.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric'
      });
      return `${start} - ${end}`;
    }
    if (timeRange === 'event' && selectedEvent) {
      return selectedEvent.name;
    }
    return undefined;
  };

  return (
    <>
      <div className="flex items-center gap-2">
        <EnhancedDropdown
          options={timeOptions}
          value={timeRange}
          onChange={handleTimeRangeChange}
          disabled={disabled}
          placeholder="Select time range"
          compactMode={true}
          customTriggerLabel={getCustomTriggerLabel()}
          dropdownWidth="w-64"
          alignRight={true}
          dropdownTitle="Time Range"
          footerNote="Historical data helps identify trends and patterns over time"
          footerIcon={Info}
          cleanStyle={true}
        />

        {/* Event selector dropdown */}
        {timeRange === 'event' && events.length > 0 && (
          <div className="relative" ref={eventDropdownRef}>
            <button
              onClick={() => setShowEventDropdown(!showEventDropdown)}
              disabled={disabled}
              className="flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-lg transition-colors bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] hover:border-[var(--theme-primary)]/50 disabled:opacity-50"
            >
              {selectedEvent && (
                <div
                  className="w-2 h-2 rounded-full flex-shrink-0"
                  style={{ backgroundColor: selectedEvent.color }}
                />
              )}
              <span className="text-[var(--theme-text-primary)] max-w-[120px] truncate">
                {selectedEvent?.name || 'Select Event'}
              </span>
              <ChevronDown className={`w-3.5 h-3.5 text-[var(--theme-text-secondary)] transition-transform ${showEventDropdown ? 'rotate-180' : ''}`} />
            </button>

            {showEventDropdown && (
              <div className="absolute top-full right-0 mt-1 min-w-[240px] bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-50">
                <CustomScrollbar maxHeight="280px" paddingMode="none">
                  <div className="py-1">
                    {sortedEvents.map((event) => {
                      const status = getEventStatus(event.startTimeUtc, event.endTimeUtc);
                      const isSelected = event.id === selectedEventId;
                      return (
                        <button
                          key={event.id}
                          onClick={() => handleEventSelect(event.id)}
                          className={`w-full text-left px-3 py-2 transition-colors ${
                            isSelected
                              ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)]'
                              : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                          }`}
                        >
                          <div className="flex items-center gap-2">
                            <div
                              className="w-2.5 h-2.5 rounded-full flex-shrink-0"
                              style={{ backgroundColor: event.color }}
                            />
                            <div className="flex-1 min-w-0">
                              <div className="flex items-center gap-2">
                                <span className="font-medium truncate">{event.name}</span>
                                {status === 'active' && (
                                  <span className={`px-1.5 py-0.5 text-[10px] rounded-full ${
                                    isSelected
                                      ? 'bg-white/20 text-white'
                                      : 'bg-[var(--theme-status-success)]/20 text-[var(--theme-status-success)]'
                                  }`}>
                                    Active
                                  </span>
                                )}
                              </div>
                              <div className={`text-xs ${
                                isSelected ? 'text-white/70' : 'text-[var(--theme-text-secondary)]'
                              }`}>
                                {formatEventDateRange(event.startTimeUtc, event.endTimeUtc)}
                              </div>
                            </div>
                          </div>
                        </button>
                      );
                    })}
                  </div>
                </CustomScrollbar>
                {events.length === 0 && (
                  <div className="px-3 py-4 text-sm text-[var(--theme-text-secondary)] text-center">
                    No events found. Create one in the Events tab.
                  </div>
                )}
              </div>
            )}
          </div>
        )}

        {/* No events message */}
        {timeRange === 'event' && events.length === 0 && (
          <div className="text-sm text-[var(--theme-text-secondary)]">
            No events available
          </div>
        )}
      </div>

      {showDatePicker && (
        <DateRangePicker
          startDate={customStartDate}
          endDate={customEndDate}
          onStartDateChange={setCustomStartDate}
          onEndDateChange={setCustomEndDate}
          onClose={() => {
            setShowDatePicker(false);
            // If dates were cleared, switch back to live mode
            if (!customStartDate || !customEndDate) {
              setTimeRange('live');
            }
          }}
        />
      )}
    </>
  );
};

export default TimeFilter;
