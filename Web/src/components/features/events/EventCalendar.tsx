import React, { useState, useMemo, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { useTimezone } from '@contexts/TimezoneContext';
import { useCalendarSettings } from '@contexts/CalendarSettingsContext';
import { getEffectiveTimezone, getDateInTimezone } from '@utils/timezone';
import { getEventColorVar } from '@utils/eventColors';
import { Tooltip } from '@components/ui/Tooltip';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import CalendarSettingsPopover from './CalendarSettingsPopover';
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
  const { t } = useTranslation();
  const { useLocalTimezone } = useTimezone();
  const { settings } = useCalendarSettings();
  const [currentMonth, setCurrentMonth] = useState(() => new Date());
  const [expandedDay, setExpandedDay] = useState<{ day: number; weekIndex: number } | null>(null);
  const popoverRef = useRef<HTMLDivElement>(null);

  // Check if an event has ended
  const hasEventEnded = (event: Event): boolean => {
    return new Date(event.endTimeUtc) < new Date();
  };

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

  const monthNames = useMemo(() => [
    t('events.calendar.months.january'),
    t('events.calendar.months.february'),
    t('events.calendar.months.march'),
    t('events.calendar.months.april'),
    t('events.calendar.months.may'),
    t('events.calendar.months.june'),
    t('events.calendar.months.july'),
    t('events.calendar.months.august'),
    t('events.calendar.months.september'),
    t('events.calendar.months.october'),
    t('events.calendar.months.november'),
    t('events.calendar.months.december')
  ], [t]);

  // Week days order based on settings
  const weekDays = useMemo(() => {
    const days = [
      t('events.calendar.weekDays.sun'),
      t('events.calendar.weekDays.mon'),
      t('events.calendar.weekDays.tue'),
      t('events.calendar.weekDays.wed'),
      t('events.calendar.weekDays.thu'),
      t('events.calendar.weekDays.fri'),
      t('events.calendar.weekDays.sat')
    ];
    if (settings.weekStartDay === 'monday') {
      return [...days.slice(1), days[0]]; // Mon, Tue, Wed, Thu, Fri, Sat, Sun
    }
    return days;
  }, [settings.weekStartDay, t]);

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

  // Get first day of month adjusted for week start setting
  const getFirstDayOfMonth = (date: Date): number => {
    const dayOfWeek = new Date(date.getFullYear(), date.getMonth(), 1).getDay();
    if (settings.weekStartDay === 'monday') {
      // Adjust for Monday start: Sunday (0) becomes 6, Monday (1) becomes 0, etc.
      return dayOfWeek === 0 ? 6 : dayOfWeek - 1;
    }
    return dayOfWeek;
  };

  // Filter events based on settings
  const filteredEvents = useMemo(() => {
    if (settings.hideEndedEvents) {
      return events.filter(event => !hasEventEnded(event));
    }
    return events;
  }, [events, settings.hideEndedEvents]);

  // Get week number for a date (ISO week number)
  const getWeekNumber = (date: Date): number => {
    const d = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()));
    const dayNum = d.getUTCDay() || 7;
    d.setUTCDate(d.getUTCDate() + 4 - dayNum);
    const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
    return Math.ceil((((d.getTime() - yearStart.getTime()) / 86400000) + 1) / 7);
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

      filteredEvents.forEach(event => {
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
  }, [filteredEvents, currentMonth, firstDayOfMonth, daysInMonth, useLocalTimezone, settings.weekStartDay]);

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

      return filteredEvents.filter(event => {
        const eventStart = new Date(event.startTimeUtc);
        const eventEnd = new Date(event.endTimeUtc);

        const startParts = getDateInTimezone(eventStart, timezone);
        const endParts = getDateInTimezone(eventEnd, timezone);

        const eventStartDate = new Date(startParts.year, startParts.month, startParts.day);
        const eventEndDate = new Date(endParts.year, endParts.month, endParts.day);

        return checkDate >= eventStartDate && checkDate <= eventEndDate;
      });
    };
  }, [filteredEvents, currentMonth, useLocalTimezone]);

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

        {/* Right: Today Button + Settings */}
        <div className="flex items-center gap-2">
          {!isCurrentMonth && (
            <Button
              variant="subtle"
              size="sm"
              onClick={goToToday}
            >
              {t('events.calendar.today')}
            </Button>
          )}
          <CalendarSettingsPopover />
        </div>
      </div>

      {/* Week Days Header */}
      <div className={`grid gap-1 mb-2 rounded-lg p-2 bg-[var(--theme-bg-tertiary)] ${settings.showWeekNumbers ? 'grid-cols-8' : 'grid-cols-7'}`}>
        {settings.showWeekNumbers && (
          <div
            className="text-center text-xs font-semibold py-2 text-[var(--theme-text-muted)]"
            title={t('events.calendar.weekNumber')}
          >
            {t('events.calendar.weekAbbrev')}
          </div>
        )}
        {weekDays.map((day) => (
          <div
            key={day}
            className="text-center text-xs font-semibold py-2 text-[var(--theme-text-secondary)]"
          >
            {day}
          </div>
        ))}
      </div>

      {/* Calendar Grid - Week by Week */}
      <div className="space-y-1">
        {weekRows.map((week) => {
          // Get the first valid day in this week for week number calculation
          const firstDayInWeek = week.days.find(d => d !== null);
          const weekNumber = firstDayInWeek
            ? getWeekNumber(new Date(currentMonth.getFullYear(), currentMonth.getMonth(), firstDayInWeek))
            : null;

          return (
          <div key={week.weekIndex} className="relative">
            {/* Day cells grid */}
            <div className={`grid gap-1 ${settings.showWeekNumbers ? 'grid-cols-8' : 'grid-cols-7'}`}>
              {/* Week number cell */}
              {settings.showWeekNumbers && (
                <div
                  className={`${settings.compactMode ? 'min-h-[90px] sm:min-h-[100px]' : 'min-h-[130px] sm:min-h-[150px]'} rounded-lg flex items-start justify-center pt-2 bg-[color-mix(in_srgb,var(--theme-bg-tertiary)_50%,transparent)]`}
                >
                  <span className="text-xs font-semibold px-1.5 py-0.5 rounded text-[var(--theme-text-muted)] bg-[var(--theme-bg-tertiary)]">
                    {weekNumber}
                  </span>
                </div>
              )}
              {week.days.map((day, colIndex) => {
                if (day === null) {
                  // Calculate adjacent month day if setting is enabled
                  if (settings.showAdjacentMonths) {
                    const cellIndex = week.weekIndex * 7 + colIndex;
                    const isBeforeMonth = cellIndex < firstDayOfMonth;

                    let adjacentDay: number;

                    if (isBeforeMonth) {
                      // Previous month - calculate the day number
                      const prevMonth = new Date(currentMonth.getFullYear(), currentMonth.getMonth() - 1, 1);
                      const daysInPrevMonth = getDaysInMonth(prevMonth);
                      adjacentDay = daysInPrevMonth - (firstDayOfMonth - cellIndex - 1);
                    } else {
                      // Next month - calculate the day number
                      const cellsAfterLastDay = cellIndex - (firstDayOfMonth + daysInMonth);
                      adjacentDay = cellsAfterLastDay + 1;
                    }

                    return (
                      <div
                        key={`adjacent-${week.weekIndex}-${colIndex}`}
                        className={`${settings.compactMode ? 'min-h-[90px] sm:min-h-[100px]' : 'min-h-[130px] sm:min-h-[150px]'} p-1.5 sm:p-2 rounded-lg bg-[color-mix(in_srgb,var(--theme-bg-tertiary)_30%,transparent)]`}
                      >
                        <span className="text-sm font-medium w-6 h-6 flex items-center justify-center rounded-full text-[var(--theme-text-muted)] opacity-50">
                          {adjacentDay}
                        </span>
                      </div>
                    );
                  }

                  return (
                    <div
                      key={`empty-${week.weekIndex}-${colIndex}`}
                      className={`${settings.compactMode ? 'min-h-[90px] sm:min-h-[100px]' : 'min-h-[130px] sm:min-h-[150px]'} rounded-lg bg-[color-mix(in_srgb,var(--theme-bg-tertiary)_30%,transparent)]`}
                    />
                  );
                }

                const today = isToday(day);
                const eventCount = getEventCountForDay(day);

                return (
                  <div
                    key={day}
                    onClick={() => onDayClick(new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day))}
                    className={`${settings.compactMode ? 'min-h-[90px] sm:min-h-[100px]' : 'min-h-[130px] sm:min-h-[150px]'} p-1.5 sm:p-2 rounded-lg border transition-all duration-200 cursor-pointer group`}
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
                      {eventCount > 0 && settings.eventDisplayStyle === 'spanning' && (
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
                            title={t('events.calendar.eventCountTooltip', { count: eventCount })}
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

            {/* Events overlay */}
            {(() => {
              const eventCount = week.spanningEvents.length;
              const maxEvents = settings.compactMode ? 6 : 5;
              const visibleEvents = week.spanningEvents.slice(0, maxEvents);

              // Dynamic sizing based on event count and compact mode
              const getEventHeight = () => {
                if (settings.compactMode) {
                  return '5px'; // Just colored lines in compact mode
                }
                return eventCount <= 3 ? '24px' : '18px'; // Bigger events in normal mode
              };

              const getEventGap = () => {
                if (settings.compactMode) return '2px';
                if (eventCount <= 3) return '3px';
                return '2px';
              };

              const getFontSize = () => {
                if (settings.compactMode) {
                  return '0px'; // No text in compact mode
                }
                return eventCount <= 3 ? '13px' : '11px'; // Bigger text
              };

              const getPaddingTop = () => {
                if (settings.compactMode) return '32px';
                return eventCount <= 3 ? '42px' : '36px'; // More space for bigger cells
              };

              // Offset for week numbers column
              const gridColOffset = settings.showWeekNumbers ? 1 : 0;

              // Get event background style based on opacity setting
              const getEventBackground = (colorVar: string, isEnded: boolean) => {
                if (settings.eventOpacity === 'solid') {
                  // Solid mode: more vibrant, less transparent
                  if (isEnded) {
                    return `linear-gradient(90deg, color-mix(in srgb, ${colorVar} 45%, var(--theme-bg-secondary)) 0%, color-mix(in srgb, ${colorVar} 35%, var(--theme-bg-secondary)) 100%)`;
                  }
                  return `linear-gradient(90deg, color-mix(in srgb, ${colorVar} 65%, var(--theme-bg-secondary)) 0%, color-mix(in srgb, ${colorVar} 50%, var(--theme-bg-secondary)) 100%)`;
                }
                // Transparent mode (default): subtle, transparent
                return `linear-gradient(90deg, color-mix(in srgb, ${colorVar} ${isEnded ? '20%' : '30%'}, transparent) 0%, color-mix(in srgb, ${colorVar} ${isEnded ? '12%' : '20%'}, transparent) 100%)`;
              };

              return (
                <div
                  className={`absolute inset-0 grid pointer-events-none ${settings.showWeekNumbers ? 'grid-cols-8' : 'grid-cols-7'}`}
                  style={{
                    paddingTop: getPaddingTop(),
                    gap: `${getEventGap()} 4px`,
                    alignContent: 'start',
                    gridAutoRows: getEventHeight(),
                  }}
                >
                  {visibleEvents.map((spanEvent, eventIndex) => {
                    const colorVar = getEventColorVar(spanEvent.event.colorIndex);
                    const isEnded = hasEventEnded(spanEvent.event);

                    // Daily mode: render individual bars for each day
                    if (settings.eventDisplayStyle === 'daily') {
                      const dayBars = [];
                      for (let col = spanEvent.startCol; col < spanEvent.startCol + spanEvent.span; col++) {
                        dayBars.push(
                          <Tooltip
                            key={`${spanEvent.event.id}-${week.weekIndex}-${col}`}
                            content={isEnded ? t('events.calendar.eventEnded', { name: spanEvent.event.name }) : spanEvent.event.name}
                            strategy="overlay"
                            className="pointer-events-auto"
                            style={{
                              gridColumn: `${col + gridColOffset} / span 1`,
                              gridRow: eventIndex + 1,
                              marginLeft: '4px',
                              marginRight: '4px',
                            }}
                          >
                            <button
                              onClick={(e) => {
                                e.stopPropagation();
                                onEventClick(spanEvent.event);
                              }}
                              className="w-full h-full truncate font-bold"
                              style={{
                                display: 'flex',
                                alignItems: 'center',
                                height: getEventHeight(),
                                fontSize: getFontSize(),
                                lineHeight: '1',
                                textAlign: 'left',
                                paddingLeft: settings.compactMode ? '0' : '8px',
                                paddingRight: settings.compactMode ? '0' : '6px',
                                borderRadius: '4px',
                                background: settings.compactMode
                                  ? (settings.eventOpacity === 'solid'
                                      ? (isEnded ? `color-mix(in srgb, ${colorVar} 60%, transparent)` : colorVar)
                                      : (isEnded ? `color-mix(in srgb, ${colorVar} 35%, transparent)` : `color-mix(in srgb, ${colorVar} 55%, transparent)`))
                                  : getEventBackground(colorVar, isEnded),
                                borderLeft: settings.compactMode ? 'none' : `3px solid ${isEnded ? `color-mix(in srgb, ${colorVar} 60%, transparent)` : colorVar}`,
                                borderTop: settings.compactMode ? 'none' : `1px solid color-mix(in srgb, ${colorVar} ${isEnded ? '25%' : '40%'}, transparent)`,
                                borderBottom: settings.compactMode ? 'none' : `1px solid color-mix(in srgb, ${colorVar} ${isEnded ? '25%' : '40%'}, transparent)`,
                                borderRight: settings.compactMode ? 'none' : `1px solid color-mix(in srgb, ${colorVar} ${isEnded ? '25%' : '40%'}, transparent)`,
                                color: settings.compactMode ? 'transparent' : (isEnded ? 'rgba(255,255,255,0.7)' : '#ffffff'),
                                opacity: isEnded ? 0.7 : 1,
                              }}
                            >
                              {!settings.compactMode && (
                                <>
                                  {isEnded && <span style={{ marginRight: '4px' }}>{t('events.ended')}</span>}
                                  {spanEvent.event.name}
                                </>
                              )}
                            </button>
                          </Tooltip>
                        );
                      }
                      return dayBars;
                    }

                    // Spanning mode: render one bar across multiple days
                    return (
                      <Tooltip
                        key={`${spanEvent.event.id}-${week.weekIndex}`}
                        content={isEnded ? t('events.calendar.eventEnded', { name: spanEvent.event.name }) : spanEvent.event.name}
                        strategy="overlay"
                        className="pointer-events-auto"
                        style={{
                          gridColumn: `${spanEvent.startCol + gridColOffset} / span ${spanEvent.span}`,
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
                          className="w-full h-full truncate font-bold"
                          style={{
                            display: 'flex',
                            alignItems: 'center',
                            height: getEventHeight(),
                            fontSize: getFontSize(),
                            lineHeight: '1',
                            textAlign: 'left',
                            paddingLeft: settings.compactMode ? '0' : (spanEvent.isStart ? '8px' : '6px'),
                            paddingRight: settings.compactMode ? '0' : '6px',
                            borderRadius: settings.compactMode ? '2px' : (
                              spanEvent.isStart && spanEvent.isEnd
                                ? '4px'
                                : spanEvent.isStart
                                  ? '4px 0 0 4px'
                                  : spanEvent.isEnd
                                    ? '0 4px 4px 0'
                                    : '0'
                            ),
                            background: settings.compactMode
                              ? (settings.eventOpacity === 'solid'
                                  ? (isEnded ? `color-mix(in srgb, ${colorVar} 60%, transparent)` : colorVar)
                                  : (isEnded ? `color-mix(in srgb, ${colorVar} 35%, transparent)` : `color-mix(in srgb, ${colorVar} 55%, transparent)`))
                              : getEventBackground(colorVar, isEnded),
                            borderLeft: settings.compactMode ? 'none' : (spanEvent.isStart ? `3px solid ${isEnded ? `color-mix(in srgb, ${colorVar} 60%, transparent)` : colorVar}` : 'none'),
                            borderTop: settings.compactMode ? 'none' : `1px solid color-mix(in srgb, ${colorVar} ${isEnded ? '25%' : '40%'}, transparent)`,
                            borderBottom: settings.compactMode ? 'none' : `1px solid color-mix(in srgb, ${colorVar} ${isEnded ? '25%' : '40%'}, transparent)`,
                            borderRight: settings.compactMode ? 'none' : (spanEvent.isEnd ? `1px solid color-mix(in srgb, ${colorVar} ${isEnded ? '25%' : '40%'}, transparent)` : 'none'),
                            color: settings.compactMode ? 'transparent' : (isEnded ? 'rgba(255,255,255,0.7)' : '#ffffff'),
                            opacity: isEnded ? 0.7 : 1,
                          }}
                        >
                          {!settings.compactMode && spanEvent.isStart ? (
                            <>
                              {isEnded && <span style={{ marginRight: '4px' }}>(Ended)</span>}
                              {spanEvent.event.name}
                            </>
                          ) : ''}
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
              // Account for week numbers column in positioning
              const totalCols = settings.showWeekNumbers ? 8 : 7;
              const adjustedIndex = settings.showWeekNumbers ? dayIndex + 1 : dayIndex;
              const maxIndex = settings.showWeekNumbers ? 7 : 6;

              return (
                <div
                  ref={popoverRef}
                  className="absolute z-50 min-w-[200px] max-w-[260px] overflow-hidden animate-fadeIn"
                  style={{
                    top: '4px',
                    ...(isRightSide
                      ? { right: `calc(${((maxIndex - adjustedIndex) / totalCols) * 100}% + 8px)` }
                      : { left: `calc(${(adjustedIndex / totalCols) * 100}% + 8px)` }
                    ),
                    backgroundColor: 'var(--theme-card-bg)',
                    border: '1px solid var(--theme-card-border)',
                    borderRadius: '12px',
                    boxShadow: '0 20px 50px -12px rgba(0, 0, 0, 0.5), 0 8px 20px -8px rgba(0, 0, 0, 0.3)',
                  }}
                >
                  {/* Header */}
                  <div className="flex items-center justify-between px-3 py-2.5 border-b border-[var(--theme-border-secondary)] bg-[var(--theme-bg-tertiary)]">
                    <div className="flex items-center gap-2">
                      <div className="w-6 h-6 rounded-md flex items-center justify-center text-xs font-bold bg-[var(--theme-primary)] text-[var(--theme-primary-text)]">
                        {expandedDay.day}
                      </div>
                      <span className="text-sm font-medium text-[var(--theme-text-primary)]">
                        {monthNames[currentMonth.getMonth()]}
                      </span>
                    </div>
                    <button
                      onClick={() => setExpandedDay(null)}
                      className="w-6 h-6 flex items-center justify-center rounded-md transition-all text-[var(--theme-text-muted)] bg-transparent hover:bg-[var(--theme-bg-hover)] hover:text-[var(--theme-text-primary)]"
                    >
                      <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                        <path d="M2 2l8 8M10 2l-8 8" />
                      </svg>
                    </button>
                  </div>

                  {/* Events count */}
                  <div className="px-3 py-1.5 text-[10px] font-medium uppercase tracking-wider text-[var(--theme-text-muted)] border-b border-[var(--theme-border-secondary)]">
                    {t('events.calendar.eventCount', { count: dayEvents.length })}
                  </div>

                  {/* Events list */}
                  <CustomScrollbar maxHeight="200px" paddingMode="compact" className="p-2">
                    <div className="space-y-1.5">
                      {dayEvents.map((event) => {
                        const colorVar = getEventColorVar(event.colorIndex);
                        const isEnded = hasEventEnded(event);
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
                              backgroundColor: `color-mix(in srgb, ${colorVar} ${isEnded ? '12%' : '20%'}, transparent)`,
                              borderLeft: `3px solid ${isEnded ? `color-mix(in srgb, ${colorVar} 60%, transparent)` : colorVar}`,
                              color: isEnded ? `color-mix(in srgb, ${colorVar} 70%, transparent)` : colorVar,
                              opacity: isEnded ? 0.8 : 1,
                            }}
                            onMouseEnter={(e) => {
                              e.currentTarget.style.transform = 'translateX(3px)';
                              e.currentTarget.style.backgroundColor = `color-mix(in srgb, ${colorVar} ${isEnded ? '18%' : '30%'}, transparent)`;
                            }}
                            onMouseLeave={(e) => {
                              e.currentTarget.style.transform = 'translateX(0)';
                              e.currentTarget.style.backgroundColor = `color-mix(in srgb, ${colorVar} ${isEnded ? '12%' : '20%'}, transparent)`;
                            }}
                            title={isEnded ? t('events.calendar.eventEnded', { name: event.name }) : event.name}
                          >
                            <span
                              className="w-2 h-2 rounded-full flex-shrink-0"
                              style={{ backgroundColor: colorVar }}
                            />
                            <span className="truncate">
                              {hasEventEnded(event) && (
                                <span style={{ opacity: 0.7, marginRight: '4px' }}>(Ended)</span>
                              )}
                              {event.name}
                            </span>
                          </button>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              );
            })()}
          </div>
          );
        })}
      </div>

      {/* Empty month message */}
      {!hasEventsThisMonth && (
        <div className="mt-6 py-6 text-center rounded-lg border border-dashed border-[var(--theme-border-secondary)] bg-[color-mix(in_srgb,var(--theme-bg-tertiary)_50%,transparent)]">
          <p className="text-sm font-medium mb-1 text-[var(--theme-text-secondary)]">
            {t('events.calendar.emptyMonth', { month: monthNames[currentMonth.getMonth()] })}
          </p>
          <p className="text-xs text-[var(--theme-text-muted)]">
            {t('events.calendar.emptyMonthHint')}
          </p>
        </div>
      )}

      {/* Legend/Help */}
      <div className="mt-4 pt-4 flex flex-col gap-1 text-xs border-t border-[var(--theme-border-secondary)] text-[var(--theme-text-muted)] sm:flex-row sm:items-center sm:justify-between">
        <span>{t('events.calendar.legend.create')}</span>
        <span>{t('events.calendar.legend.edit')}</span>
      </div>
    </div>
  );
};

export default EventCalendar;
