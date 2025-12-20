import React, { useState, useMemo } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
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

  const monthNames = [
    'January', 'February', 'March', 'April', 'May', 'June',
    'July', 'August', 'September', 'October', 'November', 'December'
  ];

  const weekDays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

  const currentYear = new Date().getFullYear();
  const startYear = currentYear - 5;
  const endYear = currentYear + 5;

  // Generate month options for dropdown
  const monthOptions = monthNames.map((month, index) => ({
    value: String(index),
    label: month
  }));

  // Generate year options for dropdown
  const yearOptions = Array.from({ length: endYear - startYear + 1 }, (_, i) => ({
    value: String(startYear + i),
    label: String(startYear + i)
  }));

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
  };

  const handleMonthChange = (value: string) => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), parseInt(value), 1));
  };

  const handleYearChange = (value: string) => {
    setCurrentMonth(new Date(parseInt(value), currentMonth.getMonth(), 1));
  };

  const goToToday = () => {
    setCurrentMonth(new Date());
  };

  const daysInMonth = getDaysInMonth(currentMonth);
  const firstDayOfMonth = getFirstDayOfMonth(currentMonth);

  // Check if current view includes today
  const now = new Date();
  const isCurrentMonth = currentMonth.getFullYear() === now.getFullYear() && currentMonth.getMonth() === now.getMonth();

  return (
    <div className="select-none">
      {/* Header Navigation */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
        {/* Left: Month/Year Selection */}
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => changeMonth(-1)}
            className="!p-2"
          >
            <ChevronLeft className="w-4 h-4" />
          </Button>

          <div className="flex items-center gap-2">
            <EnhancedDropdown
              options={monthOptions}
              value={String(currentMonth.getMonth())}
              onChange={handleMonthChange}
              compactMode
              cleanStyle
            />
            <EnhancedDropdown
              options={yearOptions}
              value={String(currentMonth.getFullYear())}
              onChange={handleYearChange}
              compactMode
              cleanStyle
            />
          </div>

          <Button
            variant="outline"
            size="sm"
            onClick={() => changeMonth(1)}
            className="!p-2"
          >
            <ChevronRight className="w-4 h-4" />
          </Button>
        </div>

        {/* Right: Today Button */}
        {!isCurrentMonth && (
          <Button
            variant="subtle"
            size="sm"
            onClick={goToToday}
          >
            Today
          </Button>
        )}
      </div>

      {/* Week Days Header */}
      <div
        className="grid grid-cols-7 gap-1 mb-2 rounded-lg p-2"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
        {weekDays.map((day) => (
          <div
            key={day}
            className="text-center text-xs font-semibold py-2"
            style={{ color: 'var(--theme-text-secondary)' }}
          >
            {day}
          </div>
        ))}
      </div>

      {/* Calendar Grid */}
      <div className="grid grid-cols-7 gap-1">
        {/* Empty cells for days before first of month */}
        {Array.from({ length: firstDayOfMonth }).map((_, index) => (
          <div
            key={`empty-${index}`}
            className="min-h-[90px] sm:min-h-[100px] rounded-lg"
            style={{ backgroundColor: 'color-mix(in srgb, var(--theme-bg-tertiary) 30%, transparent)' }}
          />
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
              className="min-h-[90px] sm:min-h-[100px] p-1.5 sm:p-2 rounded-lg border transition-all duration-200 cursor-pointer group"
              style={{
                backgroundColor: today
                  ? 'color-mix(in srgb, var(--theme-primary) 8%, transparent)'
                  : 'var(--theme-bg-secondary)',
                borderColor: today ? 'var(--theme-primary)' : 'var(--theme-border-secondary)',
                borderWidth: today ? '2px' : '1px'
              }}
              onMouseEnter={(e) => {
                if (!today) {
                  e.currentTarget.style.borderColor = 'var(--theme-primary)';
                  e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
                }
              }}
              onMouseLeave={(e) => {
                if (!today) {
                  e.currentTarget.style.borderColor = 'var(--theme-border-secondary)';
                  e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)';
                }
              }}
            >
              {/* Day number */}
              <div className="flex items-center justify-between mb-1">
                <span
                  className={`text-sm font-semibold w-6 h-6 flex items-center justify-center rounded-full transition-colors ${
                    today ? '' : 'group-hover:bg-[var(--theme-bg-hover)]'
                  }`}
                  style={{
                    color: today ? 'var(--theme-primary)' : 'var(--theme-text-primary)',
                    backgroundColor: today ? 'color-mix(in srgb, var(--theme-primary) 15%, transparent)' : 'transparent'
                  }}
                >
                  {day}
                </span>
                {dayEvents.length > 0 && (
                  <span
                    className="text-[10px] font-medium px-1.5 rounded-full"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      color: 'var(--theme-text-secondary)'
                    }}
                  >
                    {dayEvents.length}
                  </span>
                )}
              </div>

              {/* Events */}
              <div className="space-y-0.5">
                {dayEvents.slice(0, 3).map((event) => (
                  <button
                    key={event.id}
                    onClick={(e) => {
                      e.stopPropagation();
                      onEventClick(event);
                    }}
                    className="w-full text-left px-1.5 py-0.5 text-[10px] sm:text-xs rounded-full truncate transition-all hover:scale-[1.02] font-medium"
                    style={{
                      backgroundColor: `${event.color}20`,
                      color: event.color,
                      border: `1px solid ${event.color}40`
                    }}
                    title={event.name}
                  >
                    {event.name}
                  </button>
                ))}
                {dayEvents.length > 3 && (
                  <div
                    className="text-[10px] font-medium px-1.5"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    +{dayEvents.length - 3} more
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {/* Legend/Help */}
      <div
        className="mt-4 pt-4 flex items-center justify-between text-xs"
        style={{
          borderTop: '1px solid var(--theme-border-secondary)',
          color: 'var(--theme-text-muted)'
        }}
      >
        <span>Click on a day to create an event</span>
        <span>Click on an event to edit</span>
      </div>
    </div>
  );
};

export default EventCalendar;
