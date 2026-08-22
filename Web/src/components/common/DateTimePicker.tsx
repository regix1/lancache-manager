import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Calendar, Clock } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { useTimezone } from '@contexts/useTimezone';
import { getDaysInMonth, getFirstDayOfMonth } from '@utils/calendar';
import { formatTimestamp } from '@utils/dateTimeFormat';
import CalendarNavigation from './CalendarNavigation';

interface DateTimePickerProps {
  value: Date | null;
  onChange: (date: Date) => void;
  onClose: () => void;
  title?: string;
  minDate?: Date; // Minimum selectable date/time
}

const DateTimePicker: React.FC<DateTimePickerProps> = ({
  value,
  onChange,
  onClose,
  title,
  minDate
}) => {
  const { t } = useTranslation();
  const { use24HourFormat } = useTimezone();
  const resolvedTitle = title || t('common.dateTimePicker.title');

  const [currentMonth, setCurrentMonth] = useState(() => {
    return value ? new Date(value.getFullYear(), value.getMonth(), 1) : new Date();
  });
  const [selectedDate, setSelectedDate] = useState<Date | null>(value);
  const [hours, setHours] = useState(() => (value ? value.getHours() : new Date().getHours()));
  const [minutes, setMinutes] = useState(() => (value ? value.getMinutes() : 0));
  const [amPm, setAmPm] = useState<'AM' | 'PM'>(() => {
    const h = value ? value.getHours() : new Date().getHours();
    return h >= 12 ? 'PM' : 'AM';
  });

  const handleDateClick = (day: number) => {
    const newDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    newDate.setHours(hours, minutes, 0, 0);
    setSelectedDate(newDate);
  };

  const handleHourChange = (hour: number) => {
    if (use24HourFormat) {
      setHours(hour);
    } else {
      // Convert 12h to 24h
      if (amPm === 'PM' && hour !== 12) {
        setHours(hour + 12);
      } else if (amPm === 'AM' && hour === 12) {
        setHours(0);
      } else {
        setHours(hour);
      }
    }
  };

  const handleMinuteChange = (minute: number) => {
    setMinutes(minute);
  };

  const handleAmPmChange = (value: 'AM' | 'PM') => {
    setAmPm(value);
    // Adjust hours based on AM/PM
    if (value === 'PM' && hours < 12) {
      setHours(hours + 12);
    } else if (value === 'AM' && hours >= 12) {
      setHours(hours - 12);
    }
  };

  // The dropdowns and the segmented control speak strings; these keep the numeric
  // handlers above typed instead of widening them to accept a raw option value.
  const handleHourSelect = (option: string): void => handleHourChange(Number(option));
  const handleMinuteSelect = (option: string): void => handleMinuteChange(Number(option));
  const handleAmPmSelect = (option: string): void =>
    handleAmPmChange(option === 'PM' ? 'PM' : 'AM');

  const clampToMinDate = (date: Date): Date => {
    if (!minDate) return date;
    return date < minDate ? new Date(minDate) : date;
  };

  const handleApply = () => {
    if (selectedDate) {
      const finalDate = new Date(selectedDate);
      finalDate.setHours(hours, minutes, 0, 0);
      onChange(clampToMinDate(finalDate));
    }
    onClose();
  };

  const currentYear = new Date().getFullYear();
  const startYear = currentYear - 5;
  const endYear = currentYear + 5;
  const monthNames = t('common.dateTimePicker.months', { returnObjects: true }) as string[];

  const weekDays = t('common.dateTimePicker.weekDays', { returnObjects: true }) as string[];
  const amLabel = t('common.dateTimePicker.am');
  const pmLabel = t('common.dateTimePicker.pm');

  const isSelectedDate = (day: number): boolean => {
    if (!selectedDate) return false;
    return (
      selectedDate.getFullYear() === currentMonth.getFullYear() &&
      selectedDate.getMonth() === currentMonth.getMonth() &&
      selectedDate.getDate() === day
    );
  };

  // This picker collects a wall-clock date and time: handleDateClick builds the value from the
  // cell's own (year, month, day) plus the hour and minute below it, and isSelectedDate and
  // isBeforeMinDate read it back the same way. Today is taken from the same calendar, so a
  // display setting that renders other timestamps on another clock cannot move the ring.
  const isToday = (day: number): boolean => {
    const now = new Date();
    return (
      currentMonth.getFullYear() === now.getFullYear() &&
      currentMonth.getMonth() === now.getMonth() &&
      day === now.getDate()
    );
  };

  // Check if a day is before the minimum date
  const isBeforeMinDate = (day: number): boolean => {
    if (!minDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    const minDateOnly = new Date(minDate.getFullYear(), minDate.getMonth(), minDate.getDate());
    return checkDate < minDateOnly;
  };

  // Check if the selected date is the same as minDate (for time restrictions)
  const isMinDateDay = (): boolean => {
    if (!minDate || !selectedDate) return false;
    return (
      selectedDate.getFullYear() === minDate.getFullYear() &&
      selectedDate.getMonth() === minDate.getMonth() &&
      selectedDate.getDate() === minDate.getDate()
    );
  };

  // Get minimum hour allowed (only applies on minDate day)
  const getMinHour = (): number => {
    if (!isMinDateDay() || !minDate) return 0;
    return minDate.getHours();
  };

  // Get minimum minute allowed (only applies on minDate day and same hour)
  const getMinMinute = (): number => {
    if (!isMinDateDay() || !minDate) return 0;
    if (hours > minDate.getHours()) return 0;
    if (hours === minDate.getHours()) return minDate.getMinutes();
    return 0;
  };

  const daysInMonth = getDaysInMonth(currentMonth);
  const firstDayOfMonth = getFirstDayOfMonth(currentMonth);

  // Display hours for dropdown
  const displayHour = use24HourFormat ? hours : hours === 0 ? 12 : hours > 12 ? hours - 12 : hours;

  const hourOptions: DropdownOption[] = (
    use24HourFormat
      ? Array.from({ length: 24 }, (_, i) => i)
      : Array.from({ length: 12 }, (_, i) => (i === 0 ? 12 : i))
  ).map((hour) => {
    // Labels are in the displayed format but the floor is a 24h value, so compare in 24h.
    let hour24 = hour;
    if (!use24HourFormat) {
      if (amPm === 'PM' && hour !== 12) hour24 = hour + 12;
      else if (amPm === 'AM' && hour === 12) hour24 = 0;
    }

    return {
      value: String(hour),
      label: hour.toString().padStart(2, '0'),
      disabled: hour24 < getMinHour()
    };
  });

  const minuteOptions: DropdownOption[] = Array.from({ length: 60 }, (_, i) => ({
    value: String(i),
    label: i.toString().padStart(2, '0'),
    disabled: i < getMinMinute()
  }));

  const formatTime = (): string => {
    const h = use24HourFormat ? hours : displayHour;
    const suffix = use24HourFormat ? '' : ` ${amPm === 'AM' ? amLabel : pmLabel}`;
    return `${h.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}${suffix}`;
  };

  // The time half comes from the pickers above, so only the date half is formatted here.
  // It has to name the day whose cell is highlighted, and that cell was built in the browser's
  // own calendar, so both clock preferences are opted out of here: rendering the same instant on
  // the UTC or the server clock can print the day before the one the user just clicked.
  const formatDate = (): string =>
    formatTimestamp(selectedDate, {
      useLocalTimezone: true,
      useUtc: false,
      use24Hour: use24HourFormat,
      forceYear: false,
      style: 'dateOnly'
    });

  return (
    <Modal
      opened={true}
      onClose={onClose}
      title={
        <div className="flex items-center gap-2">
          <Calendar className="w-5 h-5 text-[var(--theme-primary)]" />
          <span>{resolvedTitle}</span>
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
          {Array.from({ length: firstDayOfMonth }).map((_, index) => (
            <div key={`empty-${index}`} />
          ))}
          {Array.from({ length: daysInMonth }).map((_, index) => {
            const day = index + 1;
            const selected = isSelectedDate(day);
            const today = isToday(day);
            const disabled = isBeforeMinDate(day);

            let className = 'relative p-2 text-sm transition rounded-lg ';

            if (disabled) {
              className += 'text-[var(--theme-text-muted)] cursor-not-allowed opacity-40 ';
            } else if (selected) {
              className +=
                'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-semibold cursor-pointer ';
            } else if (today) {
              className +=
                'ring-2 ring-[var(--theme-primary)]/50 text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)] cursor-pointer ';
            } else {
              className +=
                'hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] cursor-pointer ';
            }

            return (
              <button
                key={day}
                onClick={() => !disabled && handleDateClick(day)}
                disabled={disabled}
                className={className}
              >
                {day}
                {today && !selected && !disabled && (
                  <div className="absolute bottom-0.5 left-1/2 transform -translate-x-1/2 w-1 h-1 bg-[var(--theme-primary)] rounded-full" />
                )}
              </button>
            );
          })}
        </div>

        {/* Time Selection */}
        <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
          <div className="flex flex-wrap items-center justify-center gap-2">
            <Clock className="w-4 h-4 text-[var(--theme-text-secondary)]" />
            <span className="text-sm text-[var(--theme-text-secondary)]">
              {t('common.dateTimePicker.timeLabel')}
            </span>

            <EnhancedDropdown
              options={hourOptions}
              value={String(displayHour)}
              onChange={handleHourSelect}
              variant="button"
              size="md"
              maxHeight="200px"
              dropdownWidth="w-24"
              className="w-[74px]"
            />

            <span className="text-[var(--theme-text-primary)] font-medium">:</span>

            <EnhancedDropdown
              options={minuteOptions}
              value={String(minutes)}
              onChange={handleMinuteSelect}
              variant="button"
              size="md"
              maxHeight="200px"
              dropdownWidth="w-24"
              className="w-[74px]"
            />

            {/* AM/PM Toggle (only for 12h format). h-11 rather than min-h-11 because the
                container's height is what the segments stretch to fill. */}
            {!use24HourFormat && (
              <SegmentedControl
                options={[
                  { value: 'AM', label: amLabel },
                  { value: 'PM', label: pmLabel }
                ]}
                value={amPm}
                onChange={handleAmPmSelect}
                size="md"
                className="max-sm:h-11"
              />
            )}
          </div>
        </div>

        {/* Selected Value Display */}
        <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
          <div className="text-center">
            <span className="text-sm text-[var(--theme-text-secondary)]">
              {t('common.dateTimePicker.selectedLabel')}
            </span>
            <span className="text-[var(--theme-text-primary)] font-medium">
              {selectedDate ? `${formatDate()} ${formatTime()}` : t('common.dateTimePicker.none')}
            </span>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="flex gap-2 mt-4">
          <Button
            variant="filled"
            color="secondary"
            size="md"
            className="max-sm:min-h-11"
            onClick={() => {
              const now = new Date();
              now.setSeconds(0, 0);
              const clampedNow = clampToMinDate(now);
              setSelectedDate(clampedNow);
              setHours(clampedNow.getHours());
              setMinutes(clampedNow.getMinutes());
              setAmPm(clampedNow.getHours() >= 12 ? 'PM' : 'AM');
              setCurrentMonth(new Date(clampedNow.getFullYear(), clampedNow.getMonth(), 1));
            }}
            fullWidth
          >
            {t('common.dateTimePicker.now')}
          </Button>
          <Button
            variant="filled"
            color="primary"
            size="md"
            className="max-sm:min-h-11"
            onClick={handleApply}
            disabled={!selectedDate}
            fullWidth
          >
            {t('common.apply')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default DateTimePicker;
