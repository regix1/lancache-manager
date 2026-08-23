import React, { useState, useMemo, useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import CalendarNavigation from '@components/common/CalendarNavigation';
import { useTimezone } from '@contexts/useTimezone';
import { useCalendarSettings } from '@contexts/useCalendarSettings';
import { getDaysInMonth } from '@utils/calendar';
import { getEffectiveTimezone, getDateInTimezone } from '@utils/timezone';
import {
  eventColorToken,
  themeColorVar,
  type ColorTier,
  type ColorToken
} from '@utils/eventColors';
import { clampToViewport, POPOVER_GUTTER_PX } from '@utils/viewportClamp';
import { type AnchorRect } from '@hooks/useAnchorFollow';
import { useAnchoredPanel, type PanelPlacement, type PanelSpace } from '@hooks/useAnchoredPanel';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { useMediaQuery } from '@hooks/useMediaQuery';
import CalendarSettingsPopover from './CalendarSettingsPopover';
import type { Event } from '../../../types';

// Gap between the anchor day column and the popover's near edge, preserved from
// the original percentage-based offset.
const EXPANDED_DAY_POPOVER_GAP_PX = 8;
// Drop below the week row's top edge so the row's own date numbers stay readable.
const EXPANDED_DAY_POPOVER_DROP_PX = 4;
// Pre-measurement guess, matching the className's max-w-[260px] below; the real
// width is measured off the rendered node once it exists.
const EXPANDED_DAY_POPOVER_MAX_WIDTH_PX = 260;
// Anchors the expanded-day popover near its day column, then keeps it on screen on
// both axes. Everything here is viewport-space, in and out; the shared hook converts
// the result to the document coordinates the absolutely positioned portal needs.
function computeExpandedDayPopoverPosition(
  isRightSide: boolean,
  adjustedIndex: number,
  totalCols: number,
  rowRect: AnchorRect,
  popoverWidth: number,
  popoverHeight: number,
  viewportWidth: number,
  viewportHeight: number
): PanelPlacement {
  const maxIndex = totalCols - 1;

  const desiredLeft = isRightSide
    ? rowRect.right -
      ((maxIndex - adjustedIndex) / totalCols) * rowRect.width -
      EXPANDED_DAY_POPOVER_GAP_PX -
      popoverWidth
    : rowRect.left + (adjustedIndex / totalCols) * rowRect.width + EXPANDED_DAY_POPOVER_GAP_PX;

  const left = clampToViewport(desiredLeft, popoverWidth, viewportWidth, POPOVER_GUTTER_PX);

  // A day in the last week row holds its popover below most of the viewport, so
  // clamping the top is what keeps a tall list of events fully on screen.
  const top = clampToViewport(
    rowRect.top + EXPANDED_DAY_POPOVER_DROP_PX,
    popoverHeight,
    viewportHeight,
    POPOVER_GUTTER_PX
  );

  return { left, top, openUpward: false };
}

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

const EventCalendar: React.FC<EventCalendarProps> = ({ events, onEventClick, onDayClick }) => {
  const { t } = useTranslation();
  const { useLocalTimezone, useUtcTimezone } = useTimezone();
  const { settings } = useCalendarSettings();
  // 639.98px, not 640px, because the stylesheet's own phone block ends there and its desktop
  // block starts at 640: a query that includes 640 puts the component in phone mode on the
  // exact pixel where the CSS has already switched to desktop tokens.
  const isMobile = useMediaQuery('(max-width: 639.98px)');
  const [currentMonth, setCurrentMonth] = useState(() => new Date());
  const [expandedDay, setExpandedDay] = useState<{ day: number; weekIndex: number } | null>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const weekRowRef = useRef<HTMLDivElement>(null);

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

  const monthNames = useMemo(
    () => [
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
    ],
    [t]
  );

  // A phone leaves the shared navigation about 68px for the month select once the Today
  // button and the gear share its row, which is not enough for a full month name.
  const shortMonthNames = useMemo(
    () => [
      t('events.calendar.monthsShort.january'),
      t('events.calendar.monthsShort.february'),
      t('events.calendar.monthsShort.march'),
      t('events.calendar.monthsShort.april'),
      t('events.calendar.monthsShort.may'),
      t('events.calendar.monthsShort.june'),
      t('events.calendar.monthsShort.july'),
      t('events.calendar.monthsShort.august'),
      t('events.calendar.monthsShort.september'),
      t('events.calendar.monthsShort.october'),
      t('events.calendar.monthsShort.november'),
      t('events.calendar.monthsShort.december')
    ],
    [t]
  );

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
      return events.filter((event) => !hasEventEnded(event));
    }
    return events;
  }, [events, settings.hideEndedEvents]);

  // Get week number for a date (ISO week number)
  const getWeekNumber = (date: Date): number => {
    const d = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()));
    const dayNum = d.getUTCDay() || 7;
    d.setUTCDate(d.getUTCDate() + 4 - dayNum);
    const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
    return Math.ceil(((d.getTime() - yearStart.getTime()) / 86400000 + 1) / 7);
  };

  // Which day counts as today, and which days are already gone, are read from the browser's own
  // calendar. The event modal and its two date pickers build their minimum date from a browser-local
  // midnight, so reading today through the display timezone instead let the grid grey out and refuse
  // a click on a day the modal was still happily accepting. currentMonth is itself a browser-local
  // date, so both sides of the comparison speak the same calendar and the numbers compare directly.
  // getEventsForDay keeps binning events with getDateInTimezone in the display timezone, because
  // that is what decides which cell an event belongs in.
  const isToday = (day: number): boolean => {
    const today = new Date();

    return (
      currentMonth.getFullYear() === today.getFullYear() &&
      currentMonth.getMonth() === today.getMonth() &&
      day === today.getDate()
    );
  };

  const isPastDay = (day: number): boolean => {
    const today = new Date();

    if (currentMonth.getFullYear() !== today.getFullYear()) {
      return currentMonth.getFullYear() < today.getFullYear();
    }
    if (currentMonth.getMonth() !== today.getMonth()) {
      return currentMonth.getMonth() < today.getMonth();
    }
    return day < today.getDate();
  };

  const goToToday = () => {
    setCurrentMonth(new Date());
  };

  const daysInMonth = getDaysInMonth(currentMonth);
  const firstDayOfMonth = getFirstDayOfMonth(currentMonth);

  // Build week rows with spanning events
  const weekRows = useMemo((): WeekRow[] => {
    const timezone = getEffectiveTimezone(useLocalTimezone, useUtcTimezone);
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
      const weekStartDate = new Date(year, month, days.find((d) => d !== null) || 1);
      const weekEndDate = new Date(
        year,
        month,
        [...days].reverse().find((d) => d !== null) || daysInMonth
      );

      const spanningEvents: SpanningEvent[] = [];

      filteredEvents.forEach((event) => {
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
  }, [
    filteredEvents,
    currentMonth,
    firstDayOfMonth,
    daysInMonth,
    useLocalTimezone,
    useUtcTimezone
  ]);

  /**
   * The popover hangs off the expanded WEEK ROW and its left edge comes from the day's
   * column index, so the shared below/above placement does not apply. The row itself is
   * the anchor the popover follows.
   */
  const placeExpandedDayPopover = useCallback(
    (space: PanelSpace): PanelPlacement => {
      const week = expandedDay
        ? weekRows.find((w) => w.weekIndex === expandedDay.weekIndex)
        : undefined;
      if (!expandedDay || !week) return { left: 0, top: 0, openUpward: false };

      const dayIndex = week.days.indexOf(expandedDay.day);
      const isRightSide = dayIndex >= 4;
      const totalCols = settings.showWeekNumbers ? 8 : 7;
      const adjustedIndex = settings.showWeekNumbers ? dayIndex + 1 : dayIndex;

      return computeExpandedDayPopoverPosition(
        isRightSide,
        adjustedIndex,
        totalCols,
        space.anchor,
        space.panelWidth || EXPANDED_DAY_POPOVER_MAX_WIDTH_PX,
        space.panelHeight,
        space.viewportWidth,
        space.viewportHeight
      );
    },
    [expandedDay, weekRows, settings.showWeekNumbers]
  );

  const closeExpandedDay = useCallback((): void => setExpandedDay(null), []);

  /**
   * The exit animation outlives `expandedDay`, so the closing frames render from the day
   * that was last open. Rendering straight off `expandedDay` unmounted the popover the
   * instant it cleared and the exit never played.
   */
  const lastExpandedDayRef = useRef<{ day: number; weekIndex: number } | null>(null);
  useLayoutEffect(() => {
    if (expandedDay !== null) lastExpandedDayRef.current = expandedDay;
  }, [expandedDay]);
  const renderedDay = expandedDay ?? lastExpandedDayRef.current;

  const {
    present: isPopoverPresent,
    closing: isPopoverClosing,
    position: popoverPos
  } = useAnchoredPanel({
    open: expandedDay !== null,
    anchorRef: weekRowRef,
    panelRef: popoverRef,
    onClose: closeExpandedDay,
    gutter: POPOVER_GUTTER_PX,
    place: placeExpandedDayPopover
  });

  // Check if current view includes today
  const now = new Date();
  const isCurrentMonth =
    currentMonth.getFullYear() === now.getFullYear() && currentMonth.getMonth() === now.getMonth();

  // Get events for a specific day
  const getEventsForDay = useMemo(() => {
    const timezone = getEffectiveTimezone(useLocalTimezone, useUtcTimezone);
    const year = currentMonth.getFullYear();
    const month = currentMonth.getMonth();

    return (day: number): Event[] => {
      const checkDate = new Date(year, month, day);

      return filteredEvents.filter((event) => {
        const eventStart = new Date(event.startTimeUtc);
        const eventEnd = new Date(event.endTimeUtc);

        const startParts = getDateInTimezone(eventStart, timezone);
        const endParts = getDateInTimezone(eventEnd, timezone);

        const eventStartDate = new Date(startParts.year, startParts.month, startParts.day);
        const eventEndDate = new Date(endParts.year, endParts.month, endParts.day);

        return checkDate >= eventStartDate && checkDate <= eventEndDate;
      });
    };
  }, [filteredEvents, currentMonth, useLocalTimezone, useUtcTimezone]);

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
      <div className="calendar-controls">
        <CalendarNavigation
          currentMonth={currentMonth}
          startYear={startYear}
          endYear={endYear}
          monthNames={isMobile ? shortMonthNames : monthNames}
          onChange={setCurrentMonth}
        >
          {!isCurrentMonth && (
            <Button
              variant="filled"
              color="secondary"
              size="md"
              className="max-sm:min-h-11"
              onClick={goToToday}
            >
              {t('events.calendar.today')}
            </Button>
          )}
          {/* The gear's own trigger is a fixed 32px square. This wrapper is the hook that lifts it
              to the height the rest of the row uses, without reaching into the popover component. */}
          <span className="calendar-gear">
            <CalendarSettingsPopover />
          </span>
        </CalendarNavigation>
      </div>

      <div
        className={`calendar-grid${settings.compactMode ? ' calendar-grid--compact' : ''}${settings.showWeekNumbers ? ' calendar-grid--weeks' : ''}`}
      >
        {/* Week Days Header */}
        <div
          className={`calendar-weekdays grid ${settings.showWeekNumbers ? 'grid-cols-8' : 'grid-cols-7'}`}
        >
          {settings.showWeekNumbers && (
            <Tooltip content={t('events.calendar.weekNumber')} position="top" className="block">
              <div className="calendar-weekday">{t('events.calendar.weekAbbrev')}</div>
            </Tooltip>
          )}
          {weekDays.map((day) => (
            <div key={day} className="calendar-weekday calendar-weekday--name">
              {day}
            </div>
          ))}
        </div>

        {/* Calendar Grid - Week by Week */}
        {weekRows.map((week) => {
          // Get the first valid day in this week for week number calculation
          const firstDayInWeek = week.days.find((d) => d !== null);
          const weekNumber = firstDayInWeek
            ? getWeekNumber(
                new Date(currentMonth.getFullYear(), currentMonth.getMonth(), firstDayInWeek)
              )
            : null;

          // The row's height, the overlay's top padding and the bar height are one derived set.
          // Bars live in an absolutely positioned overlay, so they add nothing to the natural
          // height of a cell: the row has to reserve the stack itself or the bars paint outside it.
          const stackedEvents = week.spanningEvents.slice(0, settings.compactMode ? 6 : 5);
          const denseStack = !settings.compactMode && week.spanningEvents.length > 3;
          const weekStyle = { '--calendar-stack': stackedEvents.length } as React.CSSProperties;

          return (
            <div
              key={week.weekIndex}
              ref={expandedDay?.weekIndex === week.weekIndex ? weekRowRef : undefined}
              className={`calendar-week grid ${settings.showWeekNumbers ? 'grid-cols-8' : 'grid-cols-7'}${denseStack ? ' calendar-week--dense' : ''}`}
              style={weekStyle}
            >
              {/* Week number cell */}
              {settings.showWeekNumbers && (
                <div className="calendar-cell calendar-cell--week">
                  <Badge
                    variant="neutral"
                    ariaLabel={`${t('events.calendar.weekNumber')} ${weekNumber}`}
                  >
                    {weekNumber}
                  </Badge>
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
                      const prevMonth = new Date(
                        currentMonth.getFullYear(),
                        currentMonth.getMonth() - 1,
                        1
                      );
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
                        className="calendar-cell calendar-cell--outside"
                      >
                        <span className="calendar-day-number calendar-day-number--outside">
                          {adjacentDay}
                        </span>
                      </div>
                    );
                  }

                  return (
                    <div
                      key={`empty-${week.weekIndex}-${colIndex}`}
                      className="calendar-cell calendar-cell--outside"
                    />
                  );
                }

                const today = isToday(day);
                const pastDay = isPastDay(day);
                const eventCount = getEventCountForDay(day);

                return (
                  <div
                    key={day}
                    onClick={() => {
                      if (!pastDay) {
                        onDayClick(
                          new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day)
                        );
                      }
                    }}
                    aria-current={today ? 'date' : undefined}
                    className={`calendar-cell calendar-day ${pastDay ? 'calendar-day--past' : 'group'}`}
                  >
                    {/* Day number */}
                    <div className="calendar-day-head">
                      <span
                        className={`calendar-day-number ${
                          today
                            ? 'calendar-day-number--today'
                            : pastDay
                              ? ''
                              : 'group-hover:bg-[var(--theme-bg-hover)]'
                        }`}
                      >
                        {day}
                      </span>
                      {eventCount > 0 &&
                        settings.eventDisplayStyle === 'spanning' &&
                        (eventCount > 5 ? (
                          <Tooltip
                            content={t('events.calendar.eventCountTooltip', {
                              count: eventCount
                            })}
                          >
                            <button
                              onClick={(e) => {
                                e.stopPropagation();
                                setExpandedDay(
                                  expandedDay?.day === day &&
                                    expandedDay?.weekIndex === week.weekIndex
                                    ? null
                                    : { day, weekIndex: week.weekIndex }
                                );
                              }}
                              className="themed-badge badge-count badge-count-on-color calendar-day-count font-semibold"
                            >
                              {eventCount}
                            </button>
                          </Tooltip>
                        ) : (
                          <Badge
                            variant="neutral"
                            className="badge-count"
                            ariaLabel={t('events.calendar.eventCount', { count: eventCount })}
                          >
                            {eventCount}
                          </Badge>
                        ))}
                    </div>
                  </div>
                );
              })}

              {/* Events overlay */}
              {(() => {
                // Offset for week numbers column
                const gridColOffset = settings.showWeekNumbers ? 1 : 0;

                // A 39px cell truncates a name to a fragment, so below the phone breakpoint the
                // bar carries no text and the tooltip carries the name instead.
                const showSpanLabel = !settings.compactMode && !isMobile;

                // Every size on a bar lives in the stylesheet. Only the colour tiers cannot be
                // derived from a colour variable in CSS, so those four ride in as properties.
                const getSpanColors = (
                  colorToken: ColorToken,
                  isEnded: boolean
                ): React.CSSProperties => {
                  const colorVar = themeColorVar(colorToken);
                  const tier = (name: ColorTier): string => themeColorVar(colorToken, name);
                  const solid = settings.eventOpacity === 'solid';
                  let from: string;
                  let to: string;
                  if (settings.compactMode) {
                    from = solid
                      ? isEnded
                        ? tier('emphasis')
                        : colorVar
                      : isEnded
                        ? tier('strong')
                        : tier('emphasis');
                    to = from;
                  } else if (solid) {
                    from = isEnded ? tier('on-bg-soft') : tier('on-bg-strong');
                    to = isEnded ? tier('on-bg-soft') : tier('on-bg');
                  } else {
                    from = isEnded ? tier('subtle') : tier('muted');
                    to = tier('subtle');
                  }
                  return {
                    '--event-span-from': from,
                    '--event-span-to': to,
                    '--event-span-rule': isEnded ? tier('muted') : tier('strong')
                  } as React.CSSProperties;
                };

                return (
                  <div
                    className={`calendar-events absolute inset-0 grid pointer-events-none ${settings.showWeekNumbers ? 'grid-cols-8' : 'grid-cols-7'}`}
                  >
                    {stackedEvents.map((spanEvent, eventIndex) => {
                      const isEnded = hasEventEnded(spanEvent.event);
                      const spanColors = getSpanColors(
                        eventColorToken(spanEvent.event.colorIndex),
                        isEnded
                      );
                      const label = showSpanLabel ? (
                        <span className="event-span-label">
                          {isEnded
                            ? `${t('events.ended')} ${spanEvent.event.name}`
                            : spanEvent.event.name}
                        </span>
                      ) : null;
                      // Also the bar's accessible name: the in-bar text is dropped on a phone and
                      // in compact mode, so without this the button would announce as unnamed.
                      const tooltipContent = isEnded
                        ? t('events.calendar.eventEnded', { name: spanEvent.event.name })
                        : spanEvent.event.name;

                      // Daily mode: render individual bars for each day
                      if (settings.eventDisplayStyle === 'daily') {
                        const dayBars = [];
                        for (
                          let col = spanEvent.startCol;
                          col < spanEvent.startCol + spanEvent.span;
                          col++
                        ) {
                          dayBars.push(
                            <Tooltip
                              key={`${spanEvent.event.id}-${week.weekIndex}-${col}`}
                              content={tooltipContent}
                              strategy="overlay"
                              className="pointer-events-auto event-span-slot event-span-slot--start event-span-slot--end"
                              style={{
                                gridColumn: `${col + gridColOffset} / span 1`,
                                gridRow: eventIndex + 1
                              }}
                            >
                              <button
                                onClick={(e) => {
                                  e.stopPropagation();
                                  onEventClick(spanEvent.event);
                                }}
                                aria-label={tooltipContent}
                                className={`event-span event-span--start event-span--end${isEnded ? ' event-span--ended' : ''}`}
                                style={spanColors}
                              >
                                {label}
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
                          content={tooltipContent}
                          strategy="overlay"
                          className={`pointer-events-auto event-span-slot${spanEvent.isStart ? ' event-span-slot--start' : ''}${spanEvent.isEnd ? ' event-span-slot--end' : ''}`}
                          style={{
                            gridColumn: `${spanEvent.startCol + gridColOffset} / span ${spanEvent.span}`,
                            gridRow: eventIndex + 1
                          }}
                        >
                          <button
                            onClick={(e) => {
                              e.stopPropagation();
                              onEventClick(spanEvent.event);
                            }}
                            aria-label={tooltipContent}
                            className={`event-span${spanEvent.isStart ? ' event-span--start' : ''}${spanEvent.isEnd ? ' event-span--end' : ''}${isEnded ? ' event-span--ended' : ''}`}
                            style={spanColors}
                          >
                            {label}
                          </button>
                        </Tooltip>
                      );
                    })}
                  </div>
                );
              })()}
            </div>
          );
        })}
      </div>

      {/* Expanded day events popover. Portalled to the body so no ancestor of the week
          row can clip it, and positioned in document coordinates so an ordinary scroll
          carries it and the row together. */}
      {renderedDay !== null &&
        isPopoverPresent &&
        (() => {
          const dayEvents = getEventsForDay(renderedDay.day);

          return createPortal(
            <div
              ref={popoverRef}
              className={`calendar-day-popover absolute z-[85] min-w-[200px] max-w-[260px] overflow-hidden motion-reduce:animate-none ${
                isPopoverClosing
                  ? 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                  : 'animate-fadeIn'
              }`}
              style={{
                left: popoverPos.left,
                top: popoverPos.top,
                pointerEvents: isPopoverClosing ? 'none' : undefined
              }}
            >
              {/* Header */}
              <div className="flex items-center justify-between px-3 py-2.5 border-b border-[var(--theme-border-secondary)] bg-[var(--theme-bg-tertiary)]">
                <div className="flex items-center gap-2">
                  <div className="w-6 h-6 rounded-md flex items-center justify-center text-xs font-bold bg-[var(--theme-primary)] text-[var(--theme-primary-text)]">
                    {renderedDay.day}
                  </div>
                  <span className="text-sm font-medium text-[var(--theme-text-primary)]">
                    {monthNames[currentMonth.getMonth()]}
                  </span>
                </div>
                <Button
                  type="button"
                  variant="transparent"
                  onClick={() => setExpandedDay(null)}
                  className="btn-icon-square btn-icon-square--sm w-6 h-6 text-[var(--theme-text-muted)] hover:bg-[var(--theme-bg-hover)] hover:text-[var(--theme-text-primary)]"
                  aria-label={t('common.close')}
                >
                  <svg
                    width="12"
                    height="12"
                    viewBox="0 0 12 12"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                  >
                    <path d="M2 2l8 8M10 2l-8 8" />
                  </svg>
                </Button>
              </div>

              {/* Events count */}
              <div className="px-3 py-1.5 text-[10px] font-medium uppercase tracking-wider text-[var(--theme-text-muted)] border-b border-[var(--theme-border-secondary)]">
                {t('events.calendar.eventCount', { count: dayEvents.length })}
              </div>

              {/* Events list */}
              <CustomScrollbar
                maxHeight="200px"
                paddingMode="compact"
                radius="none"
                className="p-2"
              >
                <div className="space-y-1.5">
                  {dayEvents.map((event) => {
                    const colorToken = eventColorToken(event.colorIndex);
                    const colorVar = themeColorVar(colorToken);
                    const subtleVar = themeColorVar(colorToken, 'subtle');
                    const mutedVar = themeColorVar(colorToken, 'muted');
                    const emphasisVar = themeColorVar(colorToken, 'emphasis');
                    const intenseVar = themeColorVar(colorToken, 'intense');
                    const isEnded = hasEventEnded(event);
                    const rowColors = {
                      '--popover-event-fill': isEnded ? subtleVar : mutedVar,
                      '--popover-event-edge': isEnded ? emphasisVar : colorVar,
                      '--popover-event-text': isEnded ? intenseVar : colorVar,
                      '--popover-event-dot': colorVar
                    } as React.CSSProperties;
                    return (
                      <Tooltip
                        key={event.id}
                        content={
                          isEnded
                            ? t('events.calendar.eventEnded', { name: event.name })
                            : event.name
                        }
                        className="w-full"
                      >
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            onEventClick(event);
                            setExpandedDay(null);
                          }}
                          className={`calendar-day-popover-event${isEnded ? ' calendar-day-popover-event--ended' : ''}`}
                          style={rowColors}
                        >
                          <span className="calendar-day-popover-dot" />
                          <span className="truncate">
                            {isEnded && (
                              <span className="calendar-day-popover-ended">
                                {t('events.ended')}{' '}
                              </span>
                            )}
                            {event.name}
                          </span>
                        </button>
                      </Tooltip>
                    );
                  })}
                </div>
              </CustomScrollbar>
            </div>,
            document.body
          );
        })()}

      {/* Empty month message */}
      {!hasEventsThisMonth && (
        <div className="mt-6 py-6 text-center rounded-lg border border-dashed border-[var(--theme-border-secondary)] bg-[var(--theme-bg-tertiary-strong)]">
          <p className="text-sm font-medium mb-1 text-[var(--theme-text-secondary)]">
            {t('events.calendar.emptyMonth', { month: monthNames[currentMonth.getMonth()] })}
          </p>
          <p className="text-xs text-[var(--theme-text-muted)]">
            {t('events.calendar.emptyMonthHint')}
          </p>
        </div>
      )}
    </div>
  );
};

export default EventCalendar;
