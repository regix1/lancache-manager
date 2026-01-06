import React, { useState, useMemo, useRef, useLayoutEffect, useEffect, useCallback } from 'react';
import { Clock, Calendar, Radio, Info, ChevronDown, Check, X } from 'lucide-react';
import { useTimeFilter, type TimeRange } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import DateRangePicker from './DateRangePicker';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
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
    selectedEventIds,
    toggleEventId,
    clearEventFilter
  } = useTimeFilter();

  const { events } = useEvents();

  const [isOpen, setIsOpen] = useState(false);
  const [showDatePicker, setShowDatePicker] = useState(false);
  const [dropdownStyle, setDropdownStyle] = useState<{ animation: string; transform?: string }>({ animation: '' });
  const [openUpward, setOpenUpward] = useState(false);
  const [horizontalPosition, setHorizontalPosition] = useState<'left' | 'right'>('right');

  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

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

  // Get selected events for display
  const selectedEvents = useMemo(() => {
    return events.filter(e => selectedEventIds.includes(e.id));
  }, [events, selectedEventIds]);

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

  // Time range options - matching old structure
  const timeOptions = useMemo(() => [
    { value: 'live', label: 'Live', shortLabel: 'Live', description: 'Show real-time data updates', icon: Radio, rightLabel: 'Now' },
    { value: '1h', label: 'Last Hour', shortLabel: '1H', description: 'Show data from the last 1 hour', icon: Clock, rightLabel: '1h' },
    { value: '6h', label: 'Last 6 Hours', shortLabel: '6H', description: 'Show data from the last 6 hours', icon: Clock, rightLabel: '6h' },
    { value: '12h', label: 'Last 12 Hours', shortLabel: '12H', description: 'Show data from the last 12 hours', icon: Clock, rightLabel: '12h' },
    { value: '24h', label: 'Last 24 Hours', shortLabel: '24H', description: 'Show data from the last 24 hours', icon: Clock, rightLabel: '24h' },
    { value: '7d', label: 'Last 7 Days', shortLabel: '7D', description: 'Show data from the last 7 days', icon: Calendar, rightLabel: '7d' },
    { value: '30d', label: 'Last 30 Days', shortLabel: '30D', description: 'Show data from the last 30 days', icon: Calendar, rightLabel: '30d' },
    { value: 'custom', label: 'Custom Range', shortLabel: 'Custom', description: 'Select a custom date range', icon: Calendar, rightLabel: '...' }
  ], []);

  // Calculate position before paint
  useLayoutEffect(() => {
    if (!isOpen || !buttonRef.current) return;

    const rect = buttonRef.current.getBoundingClientRect();
    const dropdownHeight = 240;
    const spaceBelow = window.innerHeight - rect.bottom;
    const spaceAbove = rect.top;
    const shouldOpenUpward = spaceBelow < dropdownHeight && spaceAbove > dropdownHeight;
    setOpenUpward(shouldOpenUpward);

    const dropdownEl = dropdownRef.current;
    const dropdownWidthPx = dropdownEl?.offsetWidth || 256;
    const viewportWidth = window.innerWidth;

    let transform = '';
    const wouldOverflowLeft = rect.right - dropdownWidthPx < 0;
    if (wouldOverflowLeft) {
      setHorizontalPosition('left');
      if (rect.left + dropdownWidthPx > viewportWidth) {
        transform = `translateX(${viewportWidth - (rect.left + dropdownWidthPx) - 16}px)`;
      }
    } else {
      setHorizontalPosition('right');
    }

    setDropdownStyle({
      animation: `${shouldOpenUpward ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`,
      transform: transform || undefined
    });
  }, [isOpen]);

  // Event listeners
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (!dropdownRef.current?.contains(target) && !buttonRef.current?.contains(target)) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscape);
    };
  }, [isOpen]);

  const handleTimeRangeChange = useCallback((value: string) => {
    const timeValue = value as TimeRange;
    setTimeRange(timeValue);
    if (timeValue === 'custom') {
      setShowDatePicker(true);
    } else {
      setShowDatePicker(false);
    }
    setIsOpen(false);
  }, [setTimeRange]);

  const handleEventToggle = useCallback((e: React.MouseEvent, eventId: number) => {
    e.stopPropagation();
    toggleEventId(eventId);
  }, [toggleEventId]);

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
    const option = timeOptions.find(o => o.value === timeRange);
    return option?.shortLabel || 'Live';
  };

  const selectedTimeOption = timeOptions.find(o => o.value === timeRange);

  return (
    <>
      <div className="flex items-center gap-2">
        {/* Main Trigger Button - matching EnhancedDropdown style */}
        <div className="relative">
          <button
            ref={buttonRef}
            type="button"
            onClick={() => !disabled && setIsOpen(!isOpen)}
            disabled={disabled}
            className={`ed-trigger w-full px-3 py-2 rounded-lg border text-left flex items-center justify-between text-sm ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
            style={{
              backgroundColor: 'var(--theme-card-bg)',
              borderColor: isOpen ? 'var(--theme-border-focus)' : 'var(--theme-border-primary)',
              color: 'var(--theme-text-primary)'
            }}
          >
            <div className="flex items-center gap-1.5 flex-1 truncate">
              {selectedTimeOption?.icon && (
                <selectedTimeOption.icon className="flex-shrink-0" size={16} style={{ color: 'var(--theme-primary)' }} />
              )}
              <span className="font-medium">{getTimeRangeTriggerLabel()}</span>
            </div>
            <ChevronDown
              size={16}
              className={`flex-shrink-0 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
              style={{ color: 'var(--theme-text-primary)' }}
            />
          </button>

          {/* Dropdown - matching EnhancedDropdown structure */}
          {isOpen && (
            <div
              ref={dropdownRef}
              className={`ed-dropdown absolute w-64 ${horizontalPosition === 'right' ? 'right-0' : 'left-0'} rounded-lg border z-[9999] overflow-hidden ${openUpward ? 'bottom-full mb-2' : 'mt-2'}`}
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                borderColor: 'var(--theme-border-primary)',
                maxWidth: 'calc(100vw - 32px)',
                transform: dropdownStyle.transform,
                boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2)',
                animation: dropdownStyle.animation
              }}
            >
              {/* Title */}
              <div
                className="px-3 py-2 text-sm font-medium border-b"
                style={{ color: 'var(--theme-text-secondary)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-secondary)' }}
              >
                Time Range
              </div>

              <CustomScrollbar maxHeight="none" paddingMode="compact">
                {/* Time Range Options */}
                <div className="py-1">
                  {timeOptions.map((option) => {
                    const isSelected = option.value === timeRange;
                    return (
                      <button
                        key={option.value}
                        type="button"
                        onClick={() => handleTimeRangeChange(option.value)}
                        className={`ed-option w-full px-3 py-2.5 text-left text-sm cursor-pointer ${isSelected ? 'ed-option-selected' : ''}`}
                        title={option.description}
                      >
                        <div className="flex items-start gap-3">
                          <div className="flex flex-col flex-1 min-w-0">
                            <span className={`font-medium truncate ${isSelected ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                              {option.label}
                            </span>
                            {option.description && (
                              <span className="text-xs mt-0.5 leading-relaxed" style={{ color: 'var(--theme-text-secondary)' }}>
                                {option.description}
                              </span>
                            )}
                          </div>
                          {option.rightLabel && (
                            <span className="flex-shrink-0 text-xs font-medium" style={{ color: isSelected ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}>
                              {option.rightLabel}
                            </span>
                          )}
                        </div>
                      </button>
                    );
                  })}
                </div>

                {/* Events Section - Only show if there are events */}
                {sortedEvents.length > 0 && (
                  <>
                    <div
                      className="px-3 py-2 text-xs font-medium border-t mt-1 mb-1 flex items-center justify-between"
                      style={{ color: 'var(--theme-text-muted)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <span>Filter by Events</span>
                      {selectedEventIds.length > 0 && (
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            clearEventFilter();
                          }}
                          className="text-xs px-1.5 py-0.5 rounded hover:opacity-80 transition-opacity"
                          style={{ color: 'var(--theme-primary)', backgroundColor: 'var(--theme-primary-muted)' }}
                        >
                          Clear
                        </button>
                      )}
                    </div>
                    <div className="py-1">
                      {sortedEvents.map((event) => {
                        const isSelected = selectedEventIds.includes(event.id);
                        const status = getEventStatus(event.startTimeUtc, event.endTimeUtc);
                        const colorVar = event.colorIndex ? getEventColorVar(event.colorIndex) : 'var(--theme-primary)';

                        return (
                          <button
                            key={event.id}
                            type="button"
                            onClick={(e) => handleEventToggle(e, event.id)}
                            className={`ed-option w-full px-3 py-2.5 text-left text-sm cursor-pointer ${isSelected ? 'ed-option-selected' : ''}`}
                          >
                            <div className="flex items-center gap-3">
                              {/* Checkbox */}
                              <div
                                className="flex-shrink-0 w-4 h-4 rounded flex items-center justify-center"
                                style={{
                                  backgroundColor: isSelected ? colorVar : 'transparent',
                                  border: isSelected ? 'none' : '2px solid var(--theme-border-primary)'
                                }}
                              >
                                {isSelected && <Check size={12} style={{ color: 'white' }} strokeWidth={3} />}
                              </div>

                              {/* Color dot */}
                              <div
                                className="w-2 h-2 rounded-full flex-shrink-0"
                                style={{ backgroundColor: colorVar }}
                              />

                              {/* Content */}
                              <div className="flex flex-col flex-1 min-w-0">
                                <div className="flex items-center gap-1.5">
                                  <span className={`font-medium truncate ${isSelected ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                                    {event.name}
                                  </span>
                                  {status === 'active' && (
                                    <span
                                      className="px-1.5 py-0.5 text-[10px] rounded-full font-medium"
                                      style={{
                                        backgroundColor: 'color-mix(in srgb, var(--theme-success) 20%, transparent)',
                                        color: 'var(--theme-success)'
                                      }}
                                    >
                                      Live
                                    </span>
                                  )}
                                </div>
                                <span className="text-xs mt-0.5" style={{ color: 'var(--theme-text-secondary)' }}>
                                  {formatEventDateRange(event.startTimeUtc, event.endTimeUtc)}
                                </span>
                              </div>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  </>
                )}
              </CustomScrollbar>

              {/* Footer Note */}
              <div
                className="px-3 py-2.5 text-xs border-t flex items-start gap-2"
                style={{ color: 'var(--theme-text-secondary)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <Info className="flex-shrink-0 mt-0.5" size={14} style={{ color: 'var(--theme-warning)' }} />
                <span className="leading-relaxed">
                  {sortedEvents.length > 0
                    ? 'Time range and event filters can be combined'
                    : 'Historical data helps identify trends and patterns over time'}
                </span>
              </div>
            </div>
          )}
        </div>

        {/* Selected Event Chips - show outside dropdown when events are selected */}
        {selectedEvents.length > 0 && selectedEvents.length <= 2 && (
          <div className="hidden sm:flex items-center gap-1.5">
            {selectedEvents.map(event => (
              <button
                key={event.id}
                onClick={() => toggleEventId(event.id)}
                disabled={disabled}
                className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-sm font-medium transition-all hover:opacity-80"
                style={{
                  backgroundColor: event.colorIndex
                    ? `color-mix(in srgb, ${getEventColorVar(event.colorIndex)} 20%, transparent)`
                    : 'var(--theme-primary-muted)',
                  color: event.colorIndex
                    ? getEventColorVar(event.colorIndex)
                    : 'var(--theme-primary)',
                  border: `1px solid ${event.colorIndex
                    ? `color-mix(in srgb, ${getEventColorVar(event.colorIndex)} 40%, transparent)`
                    : 'var(--theme-primary-muted)'}`
                }}
                title="Click to remove event filter"
              >
                <div
                  className="w-2 h-2 rounded-full"
                  style={{
                    backgroundColor: event.colorIndex
                      ? getEventColorVar(event.colorIndex)
                      : 'var(--theme-primary)'
                  }}
                />
                <span className="max-w-[100px] truncate">{event.name}</span>
                <X size={14} className="opacity-60" />
              </button>
            ))}
          </div>
        )}

        {/* Clear all events button when more than 2 */}
        {selectedEvents.length > 2 && (
          <button
            onClick={() => clearEventFilter()}
            disabled={disabled}
            className="hidden sm:flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-sm font-medium transition-all hover:opacity-80"
            style={{
              backgroundColor: 'var(--theme-primary-muted)',
              color: 'var(--theme-primary)',
              border: '1px solid var(--theme-primary-muted)'
            }}
            title="Clear all event filters"
          >
            <span>{selectedEvents.length} events</span>
            <X size={14} className="opacity-60" />
          </button>
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
