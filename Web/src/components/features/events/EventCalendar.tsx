import React, { useState, useMemo } from 'react';
import { ChevronLeft, ChevronRight, ChevronDown } from 'lucide-react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { useTimezone } from '@contexts/TimezoneContext';
import type { Event } from '../../../types';

interface EventCalendarProps {
  events: Event[];
  onEventClick: (event: Event) => void;
  onDayClick: (date: Date) => void;
}

const EventCalendar: React.FC<EventCalendarProps> = ({
  events,
  onEventClick,
  onDayClick
}) => {
  const { useLocalTimezone } = useTimezone();
  const [currentMonth, setCurrentMonth] = useState(() => new Date());
  const [showYearDropdown, setShowYearDropdown] = useState(false);
  const [showMonthDropdown, setShowMonthDropdown] = useState(false);

  const monthNames = [
    'January', 'February', 'March', 'April', 'May', 'June',
    'July', 'August', 'September', 'October', 'November', 'December'
  ];

  const weekDays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

  const currentYear = new Date().getFullYear();
  const startYear = currentYear - 5;
  const endYear = currentYear + 5;
  const yearOptions = Array.from({ length: endYear - startYear + 1 }, (_, i) => startYear + i);

  // Get days in month
  const getDaysInMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
  };

  // Get first day of month (0 = Sunday)
  const getFirstDayOfMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth(), 1).getDay();
  };

  // Check if a day has events - respects timezone setting
  const getEventsForDay = useMemo(() => {
    return (day: number): Event[] => {
      if (useLocalTimezone) {
        // Local timezone: Compare using local dates
        const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
        checkDate.setHours(0, 0, 0, 0);
        const checkEnd = new Date(checkDate);
        checkEnd.setHours(23, 59, 59, 999);

        return events.filter(event => {
          const eventStart = new Date(event.startTimeUtc);
          const eventEnd = new Date(event.endTimeUtc);
          return eventStart <= checkEnd && eventEnd >= checkDate;
        });
      } else {
        // Server/UTC timezone: Compare using UTC dates
        const checkDateUtc = Date.UTC(currentMonth.getFullYear(), currentMonth.getMonth(), day, 0, 0, 0, 0);
        const checkEndUtc = Date.UTC(currentMonth.getFullYear(), currentMonth.getMonth(), day, 23, 59, 59, 999);

        return events.filter(event => {
          const eventStart = new Date(event.startTimeUtc).getTime();
          const eventEnd = new Date(event.endTimeUtc).getTime();
          return eventStart <= checkEndUtc && eventEnd >= checkDateUtc;
        });
      }
    };
  }, [events, currentMonth, useLocalTimezone]);

  const isToday = (day: number): boolean => {
    const now = new Date();
    if (useLocalTimezone) {
      // Local timezone
      return (
        currentMonth.getFullYear() === now.getFullYear() &&
        currentMonth.getMonth() === now.getMonth() &&
        day === now.getDate()
      );
    } else {
      // Server/UTC timezone
      return (
        currentMonth.getFullYear() === now.getUTCFullYear() &&
        currentMonth.getMonth() === now.getUTCMonth() &&
        day === now.getUTCDate()
      );
    }
  };

  const changeMonth = (increment: number) => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + increment, 1));
    setShowMonthDropdown(false);
    setShowYearDropdown(false);
  };

  const changeToMonth = (month: number) => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), month, 1));
    setShowMonthDropdown(false);
  };

  const changeYear = (year: number) => {
    setCurrentMonth(new Date(year, currentMonth.getMonth(), 1));
    setShowYearDropdown(false);
  };

  const daysInMonth = getDaysInMonth(currentMonth);
  const firstDayOfMonth = getFirstDayOfMonth(currentMonth);

  return (
    <div className="select-none">
      {/* Header */}
      <div className="flex items-center justify-between mb-4">
        <button
          onClick={() => changeMonth(-1)}
          className="p-2 hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors"
        >
          <ChevronLeft className="w-5 h-5 text-[var(--theme-text-primary)]" />
        </button>

        <div className="flex items-center gap-2">
          {/* Month Dropdown */}
          <div className="relative">
            <button
              onClick={() => {
                setShowMonthDropdown(!showMonthDropdown);
                setShowYearDropdown(false);
              }}
              className="flex items-center gap-1 px-3 py-1.5 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors"
            >
              {monthNames[currentMonth.getMonth()]}
              <ChevronDown className={`w-4 h-4 transition-transform ${showMonthDropdown ? 'rotate-180' : ''}`} />
            </button>

            {showMonthDropdown && (
              <div className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-50">
                <CustomScrollbar maxHeight="200px" paddingMode="none">
                  <div className="py-1">
                    {monthNames.map((month, index) => (
                      <button
                        key={month}
                        onClick={() => changeToMonth(index)}
                        className={`w-full text-left px-3 py-2 text-sm transition-colors whitespace-nowrap ${
                          index === currentMonth.getMonth()
                            ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-medium'
                            : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                        }`}
                      >
                        {month}
                      </button>
                    ))}
                  </div>
                </CustomScrollbar>
              </div>
            )}
          </div>

          {/* Year Dropdown */}
          <div className="relative">
            <button
              onClick={() => {
                setShowYearDropdown(!showYearDropdown);
                setShowMonthDropdown(false);
              }}
              className="flex items-center gap-1 px-3 py-1.5 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors"
            >
              {currentMonth.getFullYear()}
              <ChevronDown className={`w-4 h-4 transition-transform ${showYearDropdown ? 'rotate-180' : ''}`} />
            </button>

            {showYearDropdown && (
              <div className="absolute top-full right-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-50">
                <CustomScrollbar maxHeight="200px" paddingMode="none">
                  <div className="py-1">
                    {yearOptions.map((year) => (
                      <button
                        key={year}
                        onClick={() => changeYear(year)}
                        className={`w-full text-left px-3 py-2 text-sm transition-colors whitespace-nowrap ${
                          year === currentMonth.getFullYear()
                            ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-medium'
                            : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                        }`}
                      >
                        {year}
                      </button>
                    ))}
                  </div>
                </CustomScrollbar>
              </div>
            )}
          </div>
        </div>

        <button
          onClick={() => changeMonth(1)}
          className="p-2 hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors"
        >
          <ChevronRight className="w-5 h-5 text-[var(--theme-text-primary)]" />
        </button>
      </div>

      {/* Week Days Header */}
      <div className="grid grid-cols-7 gap-1 mb-2">
        {weekDays.map((day) => (
          <div
            key={day}
            className="text-center text-xs font-medium text-[var(--theme-text-secondary)] py-2"
          >
            {day}
          </div>
        ))}
      </div>

      {/* Calendar Grid */}
      <div className="grid grid-cols-7 gap-1">
        {/* Empty cells for days before first of month */}
        {Array.from({ length: firstDayOfMonth }).map((_, index) => (
          <div key={`empty-${index}`} className="min-h-[100px]" />
        ))}

        {/* Day cells */}
        {Array.from({ length: daysInMonth }).map((_, index) => {
          const day = index + 1;
          const dayEvents = getEventsForDay(day);
          const today = isToday(day);

          return (
            <div
              key={day}
              onClick={() => onDayClick(new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day))}
              className={`min-h-[100px] p-2 rounded-lg border transition-colors cursor-pointer ${
                today
                  ? 'border-[var(--theme-primary)] bg-[var(--theme-primary)]/5'
                  : 'border-[var(--theme-border-primary)] hover:border-[var(--theme-primary)]/50 hover:bg-[var(--theme-bg-tertiary)]'
              }`}
            >
              {/* Day number */}
              <div className={`text-sm font-medium mb-1 ${
                today ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'
              }`}>
                {day}
              </div>

              {/* Events */}
              <div className="space-y-1">
                {dayEvents.slice(0, 3).map((event) => (
                  <button
                    key={event.id}
                    onClick={(e) => {
                      e.stopPropagation();
                      onEventClick(event);
                    }}
                    className="w-full text-left px-1.5 py-0.5 text-xs rounded truncate transition-opacity hover:opacity-80"
                    style={{
                      backgroundColor: `${event.color}30`,
                      color: event.color,
                      borderLeft: `2px solid ${event.color}`
                    }}
                    title={event.name}
                  >
                    {event.name}
                  </button>
                ))}
                {dayEvents.length > 3 && (
                  <div className="text-xs text-[var(--theme-text-secondary)] px-1">
                    +{dayEvents.length - 3} more
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {/* Legend */}
      <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
        <div className="text-xs text-[var(--theme-text-secondary)]">
          Click on a day to create an event. Click on an event to edit it.
        </div>
      </div>
    </div>
  );
};

export default EventCalendar;
