import React, { useState, useMemo } from 'react';
import { Clock, Calendar, Radio, Info, CalendarDays, X } from 'lucide-react';
import { useTimeFilter, type TimeRange } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import DateRangePicker from './DateRangePicker';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { getEventColorVar } from '@utils/eventColors';

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
    selectedEventId,
    setSelectedEventId
  } = useTimeFilter();

  const { events } = useEvents();

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

  // Get selected event object
  const selectedEvent = useMemo(() => {
    if (!selectedEventId) return null;
    return events.find(e => e.id === selectedEventId) || null;
  }, [selectedEventId, events]);

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

  // Build time range options (without events - events are separate filter)
  const timeOptions: DropdownOption[] = useMemo(() => {
    return [
      { value: 'live', label: 'Live', shortLabel: 'Live', description: 'Show real-time data updates', icon: Radio, rightLabel: 'Now' },
      { value: '1h', label: 'Last Hour', shortLabel: '1H', description: 'Show data from the last 1 hour', icon: Clock, rightLabel: '1h' },
      { value: '6h', label: 'Last 6 Hours', shortLabel: '6H', description: 'Show data from the last 6 hours', icon: Clock, rightLabel: '6h' },
      { value: '12h', label: 'Last 12 Hours', shortLabel: '12H', description: 'Show data from the last 12 hours', icon: Clock, rightLabel: '12h' },
      { value: '24h', label: 'Last 24 Hours', shortLabel: '24H', description: 'Show data from the last 24 hours', icon: Clock, rightLabel: '24h' },
      { value: '7d', label: 'Last 7 Days', shortLabel: '7D', description: 'Show data from the last 7 days', icon: Calendar, rightLabel: '7d' },
      { value: '30d', label: 'Last 30 Days', shortLabel: '30D', description: 'Show data from the last 30 days', icon: Calendar, rightLabel: '30d' },
      { value: 'custom', label: 'Custom Range', shortLabel: 'Custom', description: 'Select a custom date range', icon: Calendar, rightLabel: '...' }
    ];
  }, []);

  // Build event filter options
  const eventOptions: DropdownOption[] = useMemo(() => {
    const options: DropdownOption[] = [
      { value: 'all', label: 'All Downloads', shortLabel: 'All', description: 'Show all downloads', icon: CalendarDays }
    ];

    sortedEvents.forEach(event => {
      const status = getEventStatus(event.startTimeUtc, event.endTimeUtc);
      options.push({
        value: String(event.id),
        label: event.name,
        shortLabel: event.name.length > 12 ? event.name.slice(0, 12) + '...' : event.name,
        description: formatEventDateRange(event.startTimeUtc, event.endTimeUtc),
        rightLabel: status === 'active' ? 'Live' : status === 'past' ? 'Ended' : undefined
      });
    });

    return options;
  }, [sortedEvents]);

  const handleTimeRangeChange = (value: string) => {
    const timeValue = value as TimeRange;
    setTimeRange(timeValue);
    if (timeValue === 'custom') {
      setShowDatePicker(true);
    } else {
      setShowDatePicker(false);
    }
  };

  const handleEventFilterChange = (value: string) => {
    if (value === 'all') {
      setSelectedEventId(null);
    } else {
      const eventId = parseInt(value, 10);
      setSelectedEventId(eventId);
    }
  };

  // Generate custom label for date ranges
  const getTimeRangeTriggerLabel = () => {
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
    return undefined;
  };

  return (
    <>
      <div className="flex items-center gap-2">
        {/* Time Range Dropdown */}
        <EnhancedDropdown
          options={timeOptions}
          value={timeRange}
          onChange={handleTimeRangeChange}
          disabled={disabled}
          placeholder="Select time range"
          compactMode={true}
          customTriggerLabel={getTimeRangeTriggerLabel()}
          dropdownWidth="w-64"
          alignRight={true}
          dropdownTitle="Time Range"
          footerNote="Historical data helps identify trends and patterns over time"
          footerIcon={Info}
          cleanStyle={true}
        />

        {/* Event Filter - Only show if there are events */}
        {sortedEvents.length > 0 && (
          <>
            {selectedEventId ? (
              // Show event chip when filtered
              <button
                onClick={() => setSelectedEventId(null)}
                disabled={disabled}
                className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-sm font-medium transition-all hover:opacity-80"
                style={{
                  backgroundColor: selectedEvent?.colorIndex
                    ? `color-mix(in srgb, ${getEventColorVar(selectedEvent.colorIndex)} 20%, transparent)`
                    : 'var(--theme-primary-muted)',
                  color: selectedEvent?.colorIndex
                    ? getEventColorVar(selectedEvent.colorIndex)
                    : 'var(--theme-primary)',
                  border: `1px solid ${selectedEvent?.colorIndex
                    ? `color-mix(in srgb, ${getEventColorVar(selectedEvent.colorIndex)} 40%, transparent)`
                    : 'var(--theme-primary-muted)'}`
                }}
                title="Click to remove event filter"
              >
                <div
                  className="w-2 h-2 rounded-full"
                  style={{
                    backgroundColor: selectedEvent?.colorIndex
                      ? getEventColorVar(selectedEvent.colorIndex)
                      : 'var(--theme-primary)'
                  }}
                />
                <span className="max-w-[100px] truncate">{selectedEvent?.name || 'Event'}</span>
                <X size={14} className="opacity-60" />
              </button>
            ) : (
              // Show dropdown to select event filter
              <EnhancedDropdown
                options={eventOptions}
                value="all"
                onChange={handleEventFilterChange}
                disabled={disabled}
                placeholder="Filter by event"
                compactMode={true}
                dropdownWidth="w-64"
                alignRight={true}
                dropdownTitle="Event Filter"
                footerNote="Filter to show only downloads tagged to a specific event"
                footerIcon={CalendarDays}
                cleanStyle={true}
              />
            )}
          </>
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
