import React, { useState, useMemo, useEffect, useRef } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { useTimezone } from '@contexts/TimezoneContext';
import { getEffectiveTimezone, getDateInTimezone } from '@utils/timezone';
import { getEventColorStyles, getEventColorVar } from '@utils/eventColors';
import { Tooltip } from '@components/ui/Tooltip';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import type { Event } from '../../../types';

interface EventCalendarProps {
  events: Event[];
  onEventClick: (event: Event) => void;
  onDayClick: (date: Date) => void;
}

interface SpanningEvent {
  event: Event;
  startCol: number; // 1-7 (grid columns are 1-indexed)
  span: number; // how many columns to span
  isStart: boolean; // true if this is the actual start of the event
  isEnd: boolean; // true if this is the actual end of the event
}

interface WeekRow {
  weekIndex: number;
  days: (number | null)[]; // null for empty cells, day number otherwise
  spanningEvents: SpanningEvent[];
}

const EventCalendar: React.FC<EventCalendarProps> = ({
  events,
  onEventClick,
  onDayClick
}) => {
  const { useLocalTimezone } = useTimezone();
  const [currentMonth, setCurrentMonth] = useState(() => new Date());
  const [expandedDay, setExpandedDay] = useState<{ day: number; weekIndex: number } | null>(null);
  const popoverRef = useRef<HTMLDivElement>(null);

  // Close popover when clicking outside
  useEffect(() => {
    if (expandedDay === null) return;

    const handleClickOutside = (e: MouseEvent) => {
      if (popoverRef.current && !popoverRef.current.contains(e.target as Node)) {
        setExpandedDay(null);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [expandedDay]);

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

  const isToday = (day: number): boolean => {
    const now = new Date();
    const timezone = getEffectiveTimezone(useLocalTimezone);
    const todayParts = getDateInTimezone(now, timezone);

    return (
      currentMonth.getFullYear() === todayParts.year &&
      currentMonth.getMonth() === todayParts.month &&
      day === todayParts.day
    );
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

  // Build week rows with spanning events
  const weekRows = useMemo((): WeekRow[] => {
    const timezone = getEffectiveTimezone(useLocalTimezone);
    const year = currentMonth.getFullYear();
    const month = currentMonth.getMonth();

    // Create array of all cells (empty + days)
    const totalCells = firstDayOfMonth + daysInMonth;
    const numWeeks = Math.ceil(totalCells / 7);
    const rows: WeekRow[] = [];

    for (let week = 0; week < numWeeks; week++) {
      const days: (number | null)[] = [];

      for (let dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++) {
        const cellIndex = week * 7 + dayOfWeek;
        if (cellIndex < firstDayOfMonth || cellIndex >= firstDayOfMonth + daysInMonth) {
          days.push(null);
        } else {
          days.push(cellIndex - firstDayOfMonth + 1);
        }
      }

      // Find events that span into this week
      const weekStartDate = new Date(year, month, days.find(d => d !== null) || 1);
      const weekEndDate = new Date(year, month, [...days].reverse().find(d => d !== null) || daysInMonth);

      const spanningEvents: SpanningEvent[] = [];

      events.forEach(event => {
        const eventStart = new Date(event.startTimeUtc);
        const eventEnd = new Date(event.endTimeUtc);

        const startParts = getDateInTimezone(eventStart, timezone);
        const endParts = getDateInTimezone(eventEnd, timezone);

        const eventStartDate = new Date(startParts.year, startParts.month, startParts.day);
        const eventEndDate = new Date(endParts.year, endParts.month, endParts.day);

        // Check if event overlaps with this week
        if (eventEndDate < weekStartDate || eventStartDate > weekEndDate) {
          return; // No overlap
        }

        // Calculate which columns this event spans in this week
        let startCol = 1;
        let endCol = 7;
        let isStart = true;
        let isEnd = true;

        for (let col = 0; col < 7; col++) {
          const dayNum = days[col];
          if (dayNum === null) continue;

          const cellDate = new Date(year, month, dayNum);

          if (cellDate.getTime() === eventStartDate.getTime()) {
            startCol = col + 1;
            isStart = true;
          } else if (cellDate < eventStartDate) {
            startCol = col + 2; // Event starts after this cell
          }

          if (cellDate.getTime() === eventEndDate.getTime()) {
            endCol = col + 1;
            isEnd = true;
          } else if (cellDate > eventEndDate) {
            endCol = col;
            break;
          }
        }

        // Adjust for events that start before this week
        if (eventStartDate < weekStartDate) {
          startCol = 1;
          isStart = false;
        }

        // Adjust for events that end after this week
        if (eventEndDate > weekEndDate) {
          endCol = 7;
          isEnd = false;
        }

        // Handle empty cells at start of month
        if (week === 0) {
          const firstDayCol = firstDayOfMonth + 1;
          if (startCol < firstDayCol) {
            startCol = firstDayCol;
          }
        }

        // Handle empty cells at end of month
        if (week === numWeeks - 1) {
          const lastDayCol = ((firstDayOfMonth + daysInMonth - 1) % 7) + 1;
          if (endCol > lastDayCol) {
            endCol = lastDayCol;
          }
        }

        const span = endCol - startCol + 1;
        if (span > 0 && startCol >= 1 && startCol <= 7) {
          spanningEvents.push({
            event,
            startCol,
            span,
            isStart,
            isEnd
          });
        }
      });

      // Sort spanning events by start column, then by span length (longer events first)
      spanningEvents.sort((a, b) => {
        if (a.startCol !== b.startCol) return a.startCol - b.startCol;
        return b.span - a.span;
      });

      rows.push({ weekIndex: week, days, spanningEvents });
    }

    return rows;
  }, [events, currentMonth, firstDayOfMonth, daysInMonth, useLocalTimezone]);

  // Check if current view includes today
  const now = new Date();
  const isCurrentMonth = currentMonth.getFullYear() === now.getFullYear() && currentMonth.getMonth() === now.getMonth();

  // Get events for a specific day
  const getEventsForDay = useMemo(() => {
    const timezone = getEffectiveTimezone(useLocalTimezone);
    const year = currentMonth.getFullYear();
    const month = currentMonth.getMonth();

    return (day: number): Event[] => {
      const checkDate = new Date(year, month, day);

      return events.filter(event => {
        const eventStart = new Date(event.startTimeUtc);
        const eventEnd = new Date(event.endTimeUtc);

        const startParts = getDateInTimezone(eventStart, timezone);
        const endParts = getDateInTimezone(eventEnd, timezone);

        const eventStartDate = new Date(startParts.year, startParts.month, startParts.day);
        const eventEndDate = new Date(endParts.year, endParts.month, endParts.day);

        return checkDate >= eventStartDate && checkDate <= eventEndDate;
      });
    };
  }, [events, currentMonth, useLocalTimezone]);

  // Get event count for a specific day
  const getEventCountForDay = useMemo(() => {
    return (day: number): number => getEventsForDay(day).length;
  }, [getEventsForDay]);

  // Check if current month has any events
  const hasEventsThisMonth = useMemo(() => {
    for (let day = 1; day <= daysInMonth; day++) {
      if (getEventsForDay(day).length > 0) {
        return true;
      }
    }
    return false;
  }, [daysInMonth, getEventsForDay]);

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
              className="w-[120px]"
            />
            <EnhancedDropdown
              options={yearOptions}
              value={String(currentMonth.getFullYear())}
              onChange={handleYearChange}
              compactMode
              cleanStyle
              className="w-[80px]"
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

      {/* Calendar Grid - Week by Week */}
      <div className="space-y-1">
        {weekRows.map((week) => (
          <div key={week.weekIndex} className="relative">
            {/* Day cells grid */}
            <div className="grid grid-cols-7 gap-1">
              {week.days.map((day, colIndex) => {
                if (day === null) {
                  return (
                    <div
                      key={`empty-${week.weekIndex}-${colIndex}`}
                      className="min-h-[90px] sm:min-h-[100px] rounded-lg"
                      style={{ backgroundColor: 'color-mix(in srgb, var(--theme-bg-tertiary) 30%, transparent)' }}
                    />
                  );
                }

                const today = isToday(day);
                const eventCount = getEventCountForDay(day);

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
                      {eventCount > 0 && (
                        eventCount > 5 ? (
                          <button
                            onClick={(e) => {
                              e.stopPropagation();
                              setExpandedDay(
                                expandedDay?.day === day && expandedDay?.weekIndex === week.weekIndex
                                  ? null
                                  : { day, weekIndex: week.weekIndex }
                              );
                            }}
                            className="text-[10px] font-semibold px-1.5 rounded-full transition-all hover:scale-105"
                            style={{
                              backgroundColor: 'var(--theme-primary)',
                              color: 'var(--theme-primary-text)'
                            }}
                            title={`${eventCount} events - click to see all`}
                          >
                            {eventCount}
                          </button>
                        ) : (
                          <span
                            className="text-[10px] font-medium px-1.5 rounded-full"
                            style={{
                              backgroundColor: 'var(--theme-bg-tertiary)',
                              color: 'var(--theme-text-secondary)'
                            }}
                          >
                            {eventCount}
                          </span>
                        )
                      )}
                    </div>
                  </div>
                );
              })}
            </div>

            {/* Spanning events overlay */}
            {(() => {
              const eventCount = week.spanningEvents.length;
              const maxEvents = 5;
              const visibleEvents = week.spanningEvents.slice(0, maxEvents);

              // Dynamic sizing - only shrink when more than 3
              const getEventHeight = () => {
                if (eventCount <= 3) return '18px';
                return '12px';
              };

              const getEventGap = () => {
                if (eventCount <= 3) return '2px';
                return '0.5px';
              };

              const getFontSize = () => {
                if (eventCount <= 3) return '11px';
                return '9px';
              };

              const getPaddingTop = () => {
                if (eventCount <= 3) return '34px';
                return '28px';
              };

              return (
                <div
                  className="absolute inset-0 grid grid-cols-7 pointer-events-none"
                  style={{
                    paddingTop: getPaddingTop(),
                    gap: `${getEventGap()} 4px`,
                    alignContent: 'start',
                    gridAutoRows: getEventHeight(),
                  }}
                >
                  {visibleEvents.map((spanEvent, eventIndex) => {
                    const colorVar = getEventColorVar(spanEvent.event.colorIndex);

                    return (
                      <Tooltip
                        key={`${spanEvent.event.id}-${week.weekIndex}`}
                        content={spanEvent.event.name}
                        strategy="overlay"
                        className="pointer-events-auto"
                        style={{
                          gridColumn: `${spanEvent.startCol} / span ${spanEvent.span}`,
                          gridRow: eventIndex + 1,
                          marginLeft: spanEvent.isStart ? '4px' : '0',
                          marginRight: spanEvent.isEnd ? '4px' : '0',
                        }}
                      >
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            onEventClick(spanEvent.event);
                          }}
                          className="w-full h-full truncate font-medium"
                          style={{
                            height: getEventHeight(),
                            fontSize: getFontSize(),
                            lineHeight: getEventHeight(),
                            textAlign: 'left',
                            paddingLeft: spanEvent.isStart ? '8px' : '6px',
                            paddingRight: '6px',
                            borderRadius: spanEvent.isStart && spanEvent.isEnd
                              ? '4px'
                              : spanEvent.isStart
                                ? '4px 0 0 4px'
                                : spanEvent.isEnd
                                  ? '0 4px 4px 0'
                                  : '0',
                            background: `linear-gradient(90deg, color-mix(in srgb, ${colorVar} 30%, transparent) 0%, color-mix(in srgb, ${colorVar} 20%, transparent) 100%)`,
                            borderLeft: spanEvent.isStart ? `3px solid ${colorVar}` : 'none',
                            borderTop: `1px solid color-mix(in srgb, ${colorVar} 40%, transparent)`,
                            borderBottom: `1px solid color-mix(in srgb, ${colorVar} 40%, transparent)`,
                            borderRight: spanEvent.isEnd ? `1px solid color-mix(in srgb, ${colorVar} 40%, transparent)` : 'none',
                            color: colorVar,
                          }}
                        >
                          {spanEvent.isStart ? spanEvent.event.name : ''}
                        </button>
                      </Tooltip>
                    );
                  })}

                </div>
              );
            })()}


            {/* Expanded day events popover */}
            {expandedDay?.weekIndex === week.weekIndex && (() => {
              const dayEvents = getEventsForDay(expandedDay.day);
              const dayIndex = week.days.indexOf(expandedDay.day);
              // Position popover to avoid going off-screen
              const isRightSide = dayIndex >= 4;

              return (
                <div
                  ref={popoverRef}
                  className="absolute z-50 min-w-[200px] max-w-[260px] overflow-hidden animate-fadeIn"
                  style={{
                    top: '4px',
                    ...(isRightSide
                      ? { right: `calc(${((6 - dayIndex) / 7) * 100}% + 8px)` }
                      : { left: `calc(${(dayIndex / 7) * 100}% + 8px)` }
                    ),
                    backgroundColor: 'var(--theme-card-bg)',
                    border: '1px solid var(--theme-card-border)',
                    borderRadius: '12px',
                    boxShadow: '0 20px 50px -12px rgba(0, 0, 0, 0.5), 0 8px 20px -8px rgba(0, 0, 0, 0.3)',
                  }}
                >
                  {/* Header */}
                  <div
                    className="flex items-center justify-between px-3 py-2.5"
                    style={{
                      borderBottom: '1px solid var(--theme-border-secondary)',
                      backgroundColor: 'var(--theme-bg-tertiary)',
                    }}
                  >
                    <div className="flex items-center gap-2">
                      <div
                        className="w-6 h-6 rounded-md flex items-center justify-center text-xs font-bold"
                        style={{
                          backgroundColor: 'var(--theme-primary)',
                          color: 'var(--theme-primary-text)'
                        }}
                      >
                        {expandedDay.day}
                      </div>
                      <span
                        className="text-sm font-medium"
                        style={{ color: 'var(--theme-text-primary)' }}
                      >
                        {monthNames[currentMonth.getMonth()]}
                      </span>
                    </div>
                    <button
                      onClick={() => setExpandedDay(null)}
                      className="w-6 h-6 flex items-center justify-center rounded-md transition-all"
                      style={{
                        color: 'var(--theme-text-muted)',
                        backgroundColor: 'transparent'
                      }}
                      onMouseEnter={(e) => {
                        e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                        e.currentTarget.style.color = 'var(--theme-text-primary)';
                      }}
                      onMouseLeave={(e) => {
                        e.currentTarget.style.backgroundColor = 'transparent';
                        e.currentTarget.style.color = 'var(--theme-text-muted)';
                      }}
                    >
                      <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                        <path d="M2 2l8 8M10 2l-8 8" />
                      </svg>
                    </button>
                  </div>

                  {/* Events count */}
                  <div
                    className="px-3 py-1.5 text-[10px] font-medium uppercase tracking-wider"
                    style={{
                      color: 'var(--theme-text-muted)',
                      borderBottom: '1px solid var(--theme-border-secondary)'
                    }}
                  >
                    {dayEvents.length} Event{dayEvents.length !== 1 ? 's' : ''}
                  </div>

                  {/* Events list */}
                  <CustomScrollbar maxHeight="200px" paddingMode="compact" className="p-2">
                    <div className="space-y-1.5">
                      {dayEvents.map((event) => {
                        const colorVar = getEventColorVar(event.colorIndex);
                        return (
                          <button
                            key={event.id}
                            onClick={(e) => {
                              e.stopPropagation();
                              onEventClick(event);
                              setExpandedDay(null);
                            }}
                            className="w-full text-left px-3 py-2.5 text-xs font-medium truncate transition-all rounded-lg flex items-center gap-2"
                            style={{
                              backgroundColor: `color-mix(in srgb, ${colorVar} 20%, transparent)`,
                              borderLeft: `3px solid ${colorVar}`,
                              color: colorVar,
                            }}
                            onMouseEnter={(e) => {
                              e.currentTarget.style.transform = 'translateX(3px)';
                              e.currentTarget.style.backgroundColor = `color-mix(in srgb, ${colorVar} 30%, transparent)`;
                            }}
                            onMouseLeave={(e) => {
                              e.currentTarget.style.transform = 'translateX(0)';
                              e.currentTarget.style.backgroundColor = `color-mix(in srgb, ${colorVar} 20%, transparent)`;
                            }}
                            title={event.name}
                          >
                            <span
                              className="w-2 h-2 rounded-full flex-shrink-0"
                              style={{ backgroundColor: colorVar }}
                            />
                            <span className="truncate">{event.name}</span>
                          </button>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              );
            })()}
          </div>
        ))}
      </div>

      {/* Empty month message */}
      {!hasEventsThisMonth && (
        <div
          className="mt-6 py-6 text-center rounded-lg"
          style={{
            backgroundColor: 'color-mix(in srgb, var(--theme-bg-tertiary) 50%, transparent)',
            border: '1px dashed var(--theme-border-secondary)'
          }}
        >
          <p
            className="text-sm font-medium mb-1"
            style={{ color: 'var(--theme-text-secondary)' }}
          >
            No events in {monthNames[currentMonth.getMonth()]}
          </p>
          <p
            className="text-xs"
            style={{ color: 'var(--theme-text-muted)' }}
          >
            Click on any day to create an event
          </p>
        </div>
      )}

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
