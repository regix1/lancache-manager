import React, { useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Calendar } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import { useEvents } from '@contexts/useEvents';
import { useReaderClock } from '@hooks/useReaderClock';
import { getDaysInMonth, getFirstDayOfMonth } from '@utils/calendar';
import { getEventColorVar } from '@utils/eventColors';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import {
  getDateInTimezone,
  getDayBoundsInTimezone,
  getDayStartInTimezone,
  getEffectiveTimezone,
  parseUtcDate
} from '@utils/timezone';
import { formatEventDateRange } from '@utils/formatters';
import { sortEventsByStatus, getEventStatus } from '@utils/eventUtils';
import CalendarNavigation from './CalendarNavigation';

interface DateRangePickerProps {
  startDate: Date | null;
  endDate: Date | null;
  onStartDateChange: (date: Date | null) => void;
  onEndDateChange: (date: Date | null) => void;
  onClose: () => void;
}

/** A day named the way a calendar names it, with `month` 0-indexed as the Date constructors use. */
interface CalendarDay {
  year: number;
  month: number;
  day: number;
}

const DateRangePicker: React.FC<DateRangePickerProps> = ({
  startDate,
  endDate,
  onStartDateChange,
  onEndDateChange,
  onClose
}) => {
  const { t } = useTranslation();
  const { events } = useEvents();
  // Subscribing here is what keeps this picker in step with the header: nothing else in the
  // component re-renders when that switch is thrown.
  const eventClock = useReaderClock();
  const readerZone = useMemo(
    () => getEffectiveTimezone(eventClock.useLocalTimezone, eventClock.useUtc),
    [eventClock]
  );

  const [currentMonth, setCurrentMonth] = useState(() => {
    if (!startDate) return new Date();
    const { year, month } = getDateInTimezone(startDate, readerZone);
    return new Date(year, month, 1);
  });

  // Every day here is a calendar day on the reader's clock, and the value handed out is the moment
  // that day begins there. The grid, the range labels and the event presets then all name the same
  // day. Reaching for setHours instead reads the browser's own calendar, which within the offset
  // of midnight is a different day from the one on screen.
  const dayStartOf = (date: Date): number => {
    const { year, month, day } = getDateInTimezone(date, readerZone);
    return getDayStartInTimezone(year, month, day, readerZone).getTime();
  };

  const gridDayStart = (day: number): number =>
    getDayStartInTimezone(
      currentMonth.getFullYear(),
      currentMonth.getMonth(),
      day,
      readerZone
    ).getTime();

  // The picked bounds are moments on the reader's clock, so they are read back on it.
  const selectedDayFormat = useMemo<TimestampSettings>(
    () => ({ ...eventClock, forceYear: false, style: 'dateOnly' }),
    [eventClock]
  );

  // The quick presets below name whole calendar days too, so they are built on the same clock the
  // grid draws and the label reads. Day and month numbers outside their normal range are handed
  // straight to getDayStartInTimezone, which normalizes them the way Date.UTC does: day 0 is the
  // last day of the previous month, and a negative day counts back from there.
  const readerToday = (): CalendarDay => getDateInTimezone(new Date(), readerZone);

  const selectDayRange = (from: CalendarDay, to: CalendarDay): void => {
    onStartDateChange(getDayStartInTimezone(from.year, from.month, from.day, readerZone));
    onEndDateChange(getDayStartInTimezone(to.year, to.month, to.day, readerZone));
  };

  // Sort events: active first, then upcoming, then past
  const sortedEvents = useMemo(() => sortEventsByStatus(events), [events]);

  const handleEventPresetClick = (startUtc: string, endUtc: string) => {
    // This button's tooltip names the event's days on the reader's clock, so the range takes its
    // bounds from that same clock.
    const { start } = getDayBoundsInTimezone(parseUtcDate(startUtc), readerZone);
    const { end } = getDayBoundsInTimezone(parseUtcDate(endUtc), readerZone);
    onStartDateChange(start);
    onEndDateChange(end);
    // Navigate calendar to show the event's start month
    const { year, month } = getDateInTimezone(start, readerZone);
    setCurrentMonth(new Date(year, month, 1));
  };
  const [hoveredDate, setHoveredDate] = useState<Date | null>(null);
  const [lastClickTime, setLastClickTime] = useState<number>(0);
  const [lastClickedDate, setLastClickedDate] = useState<Date | null>(null);
  const [clickCount, setClickCount] = useState<number>(0);

  const handleDateClick = (day: number) => {
    const selectedDate = new Date(gridDayStart(day));

    const now = Date.now();
    const timeDiff = now - lastClickTime;
    const isSameDate = lastClickedDate && selectedDate.getTime() === lastClickedDate.getTime();

    if (isSameDate && timeDiff < 500) {
      setClickCount((prev) => prev + 1);
    } else {
      setClickCount(1);
    }

    setLastClickTime(now);
    setLastClickedDate(selectedDate);

    // Triple-click to unselect
    if (isSameDate && clickCount >= 2) {
      onStartDateChange(null);
      onEndDateChange(null);
      setClickCount(0);
      return;
    }

    if (!startDate || endDate) {
      onStartDateChange(selectedDate);
      onEndDateChange(null);
    } else {
      if (selectedDate < startDate) {
        onStartDateChange(selectedDate);
        onEndDateChange(startDate);
      } else if (selectedDate.getTime() === startDate.getTime()) {
        onEndDateChange(selectedDate);
      } else {
        onEndDateChange(selectedDate);
      }
    }
  };

  const handleDateHover = (day: number) => {
    if (startDate && !endDate) {
      setHoveredDate(new Date(gridDayStart(day)));
    } else {
      setHoveredDate(null);
    }
  };

  const handleMouseLeave = () => {
    setHoveredDate(null);
  };

  const currentYear = new Date().getFullYear();
  const startYear = 1999;
  const endYear = currentYear + 2;
  const monthNames = t('common.dateRangePicker.months', { returnObjects: true }) as string[];

  // Both ends are normalized to the day they fall on, so a bound that carries the end of its day -
  // which is what an event preset hands back - still marks that one cell rather than none.
  const isDateInRange = (day: number): boolean => {
    const checkDate = gridDayStart(day);

    if (startDate && endDate) {
      return checkDate >= dayStartOf(startDate) && checkDate <= dayStartOf(endDate);
    }

    if (startDate && !endDate && hoveredDate) {
      const start = dayStartOf(startDate);
      const hover = dayStartOf(hoveredDate);
      return checkDate >= Math.min(start, hover) && checkDate <= Math.max(start, hover);
    }

    return false;
  };

  const isStartDate = (day: number): boolean => {
    if (!startDate) return false;
    return gridDayStart(day) === dayStartOf(startDate);
  };

  const isEndDate = (day: number): boolean => {
    if (!endDate) return false;
    return gridDayStart(day) === dayStartOf(endDate);
  };

  const isHoveredDate = (day: number): boolean => {
    if (!hoveredDate) return false;
    return gridDayStart(day) === dayStartOf(hoveredDate);
  };

  const isToday = (day: number): boolean => {
    return gridDayStart(day) === dayStartOf(new Date());
  };

  const daysInMonth = getDaysInMonth(currentMonth);
  const firstDayOfMonth = getFirstDayOfMonth(currentMonth);
  const weekDays = t('common.dateRangePicker.weekDays', { returnObjects: true }) as string[];
  // Counted between the two days themselves, not their raw instants: an end that carries the last
  // millisecond of its day used to round up to an extra day, and a DST step inside the span makes
  // the raw difference a whole hour short of a multiple of 24.
  const selectedDays =
    startDate && endDate
      ? Math.abs(
          Math.round((dayStartOf(endDate) - dayStartOf(startDate)) / (1000 * 60 * 60 * 24))
        ) + 1
      : 0;

  return (
    <Modal
      opened={true}
      onClose={onClose}
      title={
        <div className="flex items-center gap-2">
          <Calendar className="w-5 h-5 text-[var(--theme-primary)]" />
          <span>{t('common.dateRangePicker.title')}</span>
        </div>
      }
      size="md"
    >
      <div>
        <CalendarNavigation
          currentMonth={currentMonth}
          startYear={startYear}
          endYear={endYear}
          monthNames={monthNames}
          onChange={setCurrentMonth}
        />

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

        <div className="grid grid-cols-7 gap-1" onMouseLeave={handleMouseLeave}>
          {Array.from({ length: firstDayOfMonth }).map((_, index) => (
            <div key={`empty-${index}`} />
          ))}
          {Array.from({ length: daysInMonth }).map((_, index) => {
            const day = index + 1;
            const inRange = isDateInRange(day);
            const isStart = isStartDate(day);
            const isEnd = isEndDate(day);
            const isHovered = isHoveredDate(day);
            const today = isToday(day);

            let className = 'relative p-2 text-sm transition cursor-pointer ';

            if (isStart && isEnd) {
              className += 'rounded-lg ';
            } else if (isStart) {
              className += 'rounded-l-lg ';
            } else if (isEnd) {
              className += 'rounded-r-lg ';
            } else if (inRange) {
              className += '';
            } else {
              className += 'rounded-lg ';
            }

            if (isStart || isEnd) {
              className +=
                'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-semibold z-10 ';
            } else if (inRange) {
              className += 'bg-[var(--theme-primary)]/20 text-[var(--theme-text-primary)] ';
            } else if (isHovered && startDate && !endDate) {
              className += 'bg-[var(--theme-bg-tertiary)]/50 text-[var(--theme-text-primary)] ';
            } else {
              className += 'hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] ';
            }

            if (today && !isStart && !isEnd) {
              className += 'ring-2 ring-[var(--theme-primary)]/50 ';
            }

            return (
              <button
                key={day}
                onClick={() => handleDateClick(day)}
                onMouseEnter={() => handleDateHover(day)}
                className={className}
              >
                {day}
                {today && (
                  <div className="absolute bottom-0.5 left-1/2 transform -translate-x-1/2 w-1 h-1 bg-[var(--theme-primary)] rounded-full" />
                )}
              </button>
            );
          })}
        </div>

        <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
          <div className="flex justify-between text-sm">
            <div>
              <span className="text-[var(--theme-text-secondary)]">
                {t('common.dateRangePicker.startLabel')}
              </span>
              <span className="text-[var(--theme-text-primary)] font-medium">
                {startDate
                  ? formatTimestamp(startDate, selectedDayFormat)
                  : t('common.dateRangePicker.notSelected')}
              </span>
            </div>
            <div>
              <span className="text-[var(--theme-text-secondary)]">
                {t('common.dateRangePicker.endLabel')}
              </span>
              <span className="text-[var(--theme-text-primary)] font-medium">
                {endDate
                  ? formatTimestamp(endDate, selectedDayFormat)
                  : t('common.dateRangePicker.notSelected')}
              </span>
            </div>
          </div>
          <div className="mt-2 text-xs text-[var(--theme-text-secondary)] text-center">
            {!startDate
              ? t('common.dateRangePicker.selectStartDate')
              : !endDate
                ? t('common.dateRangePicker.selectEndDate')
                : t('common.dateRangePicker.daysSelected', { count: selectedDays })}
          </div>
        </div>

        {/* Quick Presets */}
        <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
          <div className="text-xs text-[var(--theme-text-secondary)] mb-2">
            {t('common.dateRangePicker.quickSelect')}
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              onClick={() => {
                const today = readerToday();
                selectDayRange(today, today);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.today')}
            </button>
            <button
              onClick={() => {
                const end = readerToday();
                selectDayRange({ ...end, day: end.day - 6 }, end);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.last7Days')}
            </button>
            <button
              onClick={() => {
                const end = readerToday();
                selectDayRange({ ...end, day: end.day - 29 }, end);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.last30Days')}
            </button>
            <button
              onClick={() => {
                const { year, month } = readerToday();
                selectDayRange({ year, month, day: 1 }, { year, month: month + 1, day: 0 });
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.thisMonth')}
            </button>
            <button
              onClick={() => {
                const { year, month } = readerToday();
                selectDayRange({ year, month: month - 1, day: 1 }, { year, month, day: 0 });
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.lastMonth')}
            </button>
            <button
              onClick={() => {
                const { year } = readerToday();
                selectDayRange({ year, month: 0, day: 1 }, { year, month: 11, day: 31 });
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.thisYear')}
            </button>
          </div>
        </div>

        {/* Event Presets - Only show if there are events */}
        {sortedEvents.length > 0 && (
          <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
            <div className="text-xs text-[var(--theme-text-secondary)] mb-2">
              {t('common.dateRangePicker.eventDateRanges')}
            </div>
            <div className="flex flex-wrap gap-2">
              {sortedEvents.slice(0, 6).map((event) => {
                const status = getEventStatus(event.startTimeUtc, event.endTimeUtc);
                const colorVar = event.colorIndex
                  ? getEventColorVar(event.colorIndex)
                  : 'var(--theme-primary)';

                return (
                  <Tooltip
                    key={event.id}
                    content={`${event.name}: ${formatEventDateRange(event.startTimeUtc, event.endTimeUtc, eventClock)}`}
                    position="top"
                  >
                    <button
                      onClick={() => handleEventPresetClick(event.startTimeUtc, event.endTimeUtc)}
                      className="flex items-center gap-2 px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
                    >
                      <div
                        className="w-2 h-2 rounded-full flex-shrink-0"
                        style={{ backgroundColor: colorVar }}
                      />
                      <span className="truncate max-w-[100px]">{event.name}</span>
                      {status === 'active' && (
                        <Badge variant="success" className="live-badge">
                          {t('common.dateRangePicker.liveBadge')}
                        </Badge>
                      )}
                    </button>
                  </Tooltip>
                );
              })}
            </div>
            <div className="mt-1 text-[10px] text-[var(--theme-text-muted)]">
              {t('common.dateRangePicker.eventRangeHint')}
            </div>
          </div>
        )}

        {/* Action Buttons */}
        <div className="flex gap-2 mt-4">
          <button
            onClick={() => {
              onStartDateChange(null);
              onEndDateChange(null);
              setHoveredDate(null);
              setClickCount(0);
              setLastClickedDate(null);
              setCurrentMonth(new Date());
            }}
            className="flex-1 px-3 py-2 text-sm bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
          >
            {t('common.clear')}
          </button>
          <button
            onClick={onClose}
            className="flex-1 px-3 py-2 text-sm bg-[var(--theme-primary)] text-[var(--theme-button-text)] rounded-lg hover:bg-[var(--theme-primary)]/90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            disabled={!startDate || !endDate}
          >
            {t('common.apply')}
          </button>
        </div>
      </div>
    </Modal>
  );
};

export default DateRangePicker;
