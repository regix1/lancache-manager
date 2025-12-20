import React, { useState, useEffect, useMemo } from 'react';
import { Clock, Calendar, Radio, Info, CalendarDays } from 'lucide-react';
import { useTimeFilter, type TimeRange } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import DateRangePicker from './DateRangePicker';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';

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

      if (aIsActive && !bIsActive) return -1;
      if (!aIsActive && bIsActive) return 1;
      if (aIsUpcoming && !bIsUpcoming) return -1;
      if (!aIsUpcoming && bIsUpcoming) return 1;

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

  const getEventStatus = (startUtc: string, endUtc: string) => {
    const now = new Date();
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    if (now >= start && now <= end) return 'active';
    if (now < start) return 'upcoming';
    return 'past';
  };

  const formatEventDateRange = (startUtc: string, endUtc: string) => {
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    const startStr = start.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    const endStr = end.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    return startStr === endStr ? startStr : `${startStr} - ${endStr}`;
  };

  // Build time options with events submenu
  const timeOptions: DropdownOption[] = useMemo(() => {
    const options: DropdownOption[] = [
      { value: 'live', label: 'Live', shortLabel: 'Live', description: 'Show real-time data updates', icon: Radio, rightLabel: 'Now' },
      { value: '1h', label: 'Last Hour', shortLabel: '1H', description: 'Show data from the last 1 hour', icon: Clock, rightLabel: '1h' },
      { value: '6h', label: 'Last 6 Hours', shortLabel: '6H', description: 'Show data from the last 6 hours', icon: Clock, rightLabel: '6h' },
      { value: '12h', label: 'Last 12 Hours', shortLabel: '12H', description: 'Show data from the last 12 hours', icon: Clock, rightLabel: '12h' },
      { value: '24h', label: 'Last 24 Hours', shortLabel: '24H', description: 'Show data from the last 24 hours', icon: Clock, rightLabel: '24h' },
      { value: '7d', label: 'Last 7 Days', shortLabel: '7D', description: 'Show data from the last 7 days', icon: Calendar, rightLabel: '7d' },
      { value: '30d', label: 'Last 30 Days', shortLabel: '30D', description: 'Show data from the last 30 days', icon: Calendar, rightLabel: '30d' },
      { value: 'custom', label: 'Custom Range', shortLabel: 'Custom', description: 'Select a custom date range', icon: Calendar, rightLabel: '...' }
    ];

    // Add events option with submenu if there are events
    if (sortedEvents.length > 0) {
      options.push({
        value: 'event',
        label: 'Events',
        shortLabel: 'Event',
        description: 'Filter by a scheduled event',
        icon: CalendarDays,
        rightLabel: String(sortedEvents.length),
        submenuTitle: 'Select Event',
        submenu: sortedEvents.map(event => {
          const status = getEventStatus(event.startTimeUtc, event.endTimeUtc);
          return {
            value: String(event.id),
            label: event.name,
            description: formatEventDateRange(event.startTimeUtc, event.endTimeUtc),
            color: event.color,
            badge: status === 'active' ? 'Live' : undefined,
            badgeColor: 'var(--theme-status-success)'
          };
        })
      });
    }

    return options;
  }, [sortedEvents]);

  // Get the current dropdown value
  const getCurrentValue = () => {
    if (timeRange === 'event' && selectedEventId) {
      return `event:${selectedEventId}`;
    }
    return timeRange;
  };

  const handleTimeRangeChange = (value: string) => {
    // Check if it's an event selection (format: "event:123")
    if (value.startsWith('event:')) {
      const eventId = parseInt(value.split(':')[1], 10);
      setTimeRange('event');
      setSelectedEventId(eventId);
      setShowDatePicker(false);
    } else {
      const timeValue = value as TimeRange;
      setTimeRange(timeValue);
      if (timeValue === 'custom') {
        setShowDatePicker(true);
      } else {
        setShowDatePicker(false);
      }
    }
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
          value={getCurrentValue()}
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
      </div>

      {showDatePicker && (
        <DateRangePicker
          startDate={customStartDate}
          endDate={customEndDate}
          onStartDateChange={setCustomStartDate}
          onEndDateChange={setCustomEndDate}
          onClose={() => {
            setShowDatePicker(false);
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
