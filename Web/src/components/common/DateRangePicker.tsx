import React, { useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight, Calendar } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import { useEvents } from '@contexts/useEvents';
import { getEventColorVar } from '@utils/eventColors';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import { formatEventDateRange } from '@utils/formatters';
import { sortEventsByStatus, getEventStatus } from '@utils/eventUtils';

interface DateRangePickerProps {
  startDate: Date | null;
  endDate: Date | null;
  onStartDateChange: (date: Date | null) => void;
  onEndDateChange: (date: Date | null) => void;
  onClose: () => void;
}

// The grid builds each selection as a local midnight, so these are calendar days rather than
// instants and must be read back in the browser's calendar: reformatting them in the server's
// timezone can move the readout onto the day before the one the user clicked. The date-only
// shape carries no time, so the 24-hour preference has nothing to act on here.
const SELECTED_DAY_FORMAT: TimestampSettings = {
  useLocalTimezone: true,
  use24Hour: true,
  forceYear: false,
  style: 'dateOnly'
};

const DateRangePicker: React.FC<DateRangePickerProps> = ({
  startDate,
  endDate,
  onStartDateChange,
  onEndDateChange,
  onClose
}) => {
  const { t } = useTranslation();
  const { events } = useEvents();

  const [currentMonth, setCurrentMonth] = useState(() => {
    return startDate ? new Date(startDate.getFullYear(), startDate.getMonth(), 1) : new Date();
  });

  // Sort events: active first, then upcoming, then past
  const sortedEvents = useMemo(() => sortEventsByStatus(events), [events]);

  const handleEventPresetClick = (startUtc: string, endUtc: string) => {
    const start = new Date(startUtc);
    start.setHours(0, 0, 0, 0);
    const end = new Date(endUtc);
    end.setHours(23, 59, 59, 999);
    onStartDateChange(start);
    onEndDateChange(end);
    // Navigate calendar to show the event's start month
    setCurrentMonth(new Date(start.getFullYear(), start.getMonth(), 1));
  };
  const [hoveredDate, setHoveredDate] = useState<Date | null>(null);
  const [lastClickTime, setLastClickTime] = useState<number>(0);
  const [lastClickedDate, setLastClickedDate] = useState<Date | null>(null);
  const [clickCount, setClickCount] = useState<number>(0);

  const getDaysInMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
  };

  const getFirstDayOfMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth(), 1).getDay();
  };

  const handleDateClick = (day: number) => {
    const selectedDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    selectedDate.setHours(0, 0, 0, 0);

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

    if (!startDate || (startDate && endDate)) {
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
      const hoverDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
      hoverDate.setHours(0, 0, 0, 0);
      setHoveredDate(hoverDate);
    } else {
      setHoveredDate(null);
    }
  };

  const handleMouseLeave = () => {
    setHoveredDate(null);
  };

  const changeMonth = (increment: number) => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + increment, 1));
  };

  const changeYear = (year: number) => {
    setCurrentMonth(new Date(year, currentMonth.getMonth(), 1));
  };

  const changeToMonth = (month: number) => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), month, 1));
  };

  // The dropdowns speak strings; these keep the numeric handlers above typed instead
  // of widening them to accept a raw option value.
  const handleMonthSelect = (option: string): void => changeToMonth(Number(option));
  const handleYearSelect = (option: string): void => changeYear(Number(option));

  const currentYear = new Date().getFullYear();
  const startYear = 1999;
  const endYear = currentYear + 2;
  const yearOptions: DropdownOption[] = Array.from({ length: endYear - startYear + 1 }, (_, i) => {
    const year = startYear + i;
    return { value: String(year), label: String(year) };
  });

  const monthNames = t('common.dateRangePicker.months', { returnObjects: true }) as string[];
  const monthOptions: DropdownOption[] = monthNames.map((month, index) => ({
    value: String(index),
    label: month
  }));

  const isDateInRange = (day: number): boolean => {
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);

    if (startDate && endDate) {
      const start = new Date(startDate);
      const end = new Date(endDate);
      start.setHours(0, 0, 0, 0);
      end.setHours(0, 0, 0, 0);
      return checkDate >= start && checkDate <= end;
    }

    if (startDate && !endDate && hoveredDate) {
      const start = new Date(startDate);
      start.setHours(0, 0, 0, 0);
      const hover = new Date(hoveredDate);
      hover.setHours(0, 0, 0, 0);

      const minDate = start < hover ? start : hover;
      const maxDate = start > hover ? start : hover;

      return checkDate >= minDate && checkDate <= maxDate;
    }

    return false;
  };

  const isStartDate = (day: number): boolean => {
    if (!startDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);
    const start = new Date(startDate);
    start.setHours(0, 0, 0, 0);
    return checkDate.getTime() === start.getTime();
  };

  const isEndDate = (day: number): boolean => {
    if (!endDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);
    const end = new Date(endDate);
    end.setHours(0, 0, 0, 0);
    return checkDate.getTime() === end.getTime();
  };

  const isHoveredDate = (day: number): boolean => {
    if (!hoveredDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);
    const hover = new Date(hoveredDate);
    hover.setHours(0, 0, 0, 0);
    return checkDate.getTime() === hover.getTime();
  };

  const isToday = (day: number): boolean => {
    const today = new Date();
    return (
      currentMonth.getFullYear() === today.getFullYear() &&
      currentMonth.getMonth() === today.getMonth() &&
      day === today.getDate()
    );
  };

  const daysInMonth = getDaysInMonth(currentMonth);
  const firstDayOfMonth = getFirstDayOfMonth(currentMonth);
  const weekDays = t('common.dateRangePicker.weekDays', { returnObjects: true }) as string[];
  const selectedDays =
    startDate && endDate
      ? Math.abs(Math.ceil((endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24))) + 1
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
        <div className="mb-4 flex items-center justify-between">
          {/* md is the only size where Button and EnhancedDropdown are both 40px: the
              dropdown trigger's sm is 34px against Button's 32px. Below the phone breakpoint
              the trigger takes a 44px touch floor, so the buttons follow it up with min-h-11.
              Same row and same values as DateTimePicker. */}
          <Button
            variant="filled"
            color="gray"
            size="md"
            className="max-sm:min-h-11"
            onClick={() => changeMonth(-1)}
          >
            <ChevronLeft className="w-5 h-5" />
          </Button>

          <div className="flex items-center gap-2">
            <EnhancedDropdown
              options={monthOptions}
              value={String(currentMonth.getMonth())}
              onChange={handleMonthSelect}
              variant="button"
              size="md"
              maxHeight="200px"
              dropdownWidth="w-40"
              className="w-[104px] sm:w-[128px]"
            />

            <EnhancedDropdown
              options={yearOptions}
              value={String(currentMonth.getFullYear())}
              onChange={handleYearSelect}
              variant="button"
              size="md"
              alignRight
              maxHeight="200px"
              dropdownWidth="w-28"
              className="w-[76px] sm:w-[92px]"
            />
          </div>

          <Button
            variant="filled"
            color="gray"
            size="md"
            className="max-sm:min-h-11"
            onClick={() => changeMonth(1)}
          >
            <ChevronRight className="w-5 h-5" />
          </Button>
        </div>

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
                  ? formatTimestamp(startDate, SELECTED_DAY_FORMAT)
                  : t('common.dateRangePicker.notSelected')}
              </span>
            </div>
            <div>
              <span className="text-[var(--theme-text-secondary)]">
                {t('common.dateRangePicker.endLabel')}
              </span>
              <span className="text-[var(--theme-text-primary)] font-medium">
                {endDate
                  ? formatTimestamp(endDate, SELECTED_DAY_FORMAT)
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
                const today = new Date();
                today.setHours(0, 0, 0, 0);
                onStartDateChange(today);
                onEndDateChange(today);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.today')}
            </button>
            <button
              onClick={() => {
                const end = new Date();
                end.setHours(0, 0, 0, 0);
                const start = new Date(end);
                start.setDate(start.getDate() - 6);
                onStartDateChange(start);
                onEndDateChange(end);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.last7Days')}
            </button>
            <button
              onClick={() => {
                const end = new Date();
                end.setHours(0, 0, 0, 0);
                const start = new Date(end);
                start.setDate(start.getDate() - 29);
                onStartDateChange(start);
                onEndDateChange(end);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.last30Days')}
            </button>
            <button
              onClick={() => {
                const now = new Date();
                const start = new Date(now.getFullYear(), now.getMonth(), 1);
                start.setHours(0, 0, 0, 0);
                const end = new Date(now.getFullYear(), now.getMonth() + 1, 0);
                end.setHours(0, 0, 0, 0);
                onStartDateChange(start);
                onEndDateChange(end);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.thisMonth')}
            </button>
            <button
              onClick={() => {
                const now = new Date();
                const start = new Date(now.getFullYear(), now.getMonth() - 1, 1);
                start.setHours(0, 0, 0, 0);
                const end = new Date(now.getFullYear(), now.getMonth(), 0);
                end.setHours(0, 0, 0, 0);
                onStartDateChange(start);
                onEndDateChange(end);
              }}
              className="px-3 py-1.5 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
            >
              {t('common.dateRangePicker.lastMonth')}
            </button>
            <button
              onClick={() => {
                const now = new Date();
                const start = new Date(now.getFullYear(), 0, 1);
                start.setHours(0, 0, 0, 0);
                const end = new Date(now.getFullYear(), 11, 31);
                end.setHours(0, 0, 0, 0);
                onStartDateChange(start);
                onEndDateChange(end);
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
                    content={`${event.name}: ${formatEventDateRange(event.startTimeUtc, event.endTimeUtc)}`}
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
