import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight, Calendar, ChevronDown, Clock } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { useTimezone } from '@contexts/TimezoneContext';
import { getEffectiveTimezone, getDateInTimezone } from '@utils/timezone';

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
  const { use24HourFormat, useLocalTimezone } = useTimezone();
  const resolvedTitle = title || t('common.dateTimePicker.title');

  const [currentMonth, setCurrentMonth] = useState(() => {
    return value ? new Date(value.getFullYear(), value.getMonth(), 1) : new Date();
  });
  const [selectedDate, setSelectedDate] = useState<Date | null>(value);
  const [hours, setHours] = useState(() => (value ? value.getHours() : new Date().getHours()));
  const [minutes, setMinutes] = useState(() => (value ? value.getMinutes() : 0));
  const [showYearDropdown, setShowYearDropdown] = useState(false);
  const [showMonthDropdown, setShowMonthDropdown] = useState(false);
  const [showHourDropdown, setShowHourDropdown] = useState(false);
  const [showMinuteDropdown, setShowMinuteDropdown] = useState(false);
  const [amPm, setAmPm] = useState<'AM' | 'PM'>(() => {
    const h = value ? value.getHours() : new Date().getHours();
    return h >= 12 ? 'PM' : 'AM';
  });

  const monthDropdownRef = useRef<HTMLDivElement>(null);
  const yearDropdownRef = useRef<HTMLDivElement>(null);
  const hourDropdownRef = useRef<HTMLDivElement>(null);
  const minuteDropdownRef = useRef<HTMLDivElement>(null);
  const monthButtonRef = useRef<HTMLButtonElement>(null);
  const yearButtonRef = useRef<HTMLButtonElement>(null);
  const hourButtonRef = useRef<HTMLButtonElement>(null);
  const minuteButtonRef = useRef<HTMLButtonElement>(null);

  // Click outside handler for dropdowns
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;

      if (
        showMonthDropdown &&
        monthDropdownRef.current &&
        !monthDropdownRef.current.contains(target) &&
        monthButtonRef.current &&
        !monthButtonRef.current.contains(target)
      ) {
        setShowMonthDropdown(false);
      }
      if (
        showYearDropdown &&
        yearDropdownRef.current &&
        !yearDropdownRef.current.contains(target) &&
        yearButtonRef.current &&
        !yearButtonRef.current.contains(target)
      ) {
        setShowYearDropdown(false);
      }
      if (
        showHourDropdown &&
        hourDropdownRef.current &&
        !hourDropdownRef.current.contains(target) &&
        hourButtonRef.current &&
        !hourButtonRef.current.contains(target)
      ) {
        setShowHourDropdown(false);
      }
      if (
        showMinuteDropdown &&
        minuteDropdownRef.current &&
        !minuteDropdownRef.current.contains(target) &&
        minuteButtonRef.current &&
        !minuteButtonRef.current.contains(target)
      ) {
        setShowMinuteDropdown(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [showMonthDropdown, showYearDropdown, showHourDropdown, showMinuteDropdown]);

  const closeAllDropdowns = () => {
    setShowYearDropdown(false);
    setShowMonthDropdown(false);
    setShowHourDropdown(false);
    setShowMinuteDropdown(false);
  };

  const getDaysInMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
  };

  const getFirstDayOfMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth(), 1).getDay();
  };

  const handleDateClick = (day: number) => {
    closeAllDropdowns();
    const newDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    newDate.setHours(hours, minutes, 0, 0);
    setSelectedDate(newDate);
  };

  const changeMonth = (increment: number) => {
    closeAllDropdowns();
    setCurrentMonth(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + increment, 1));
  };

  const changeYear = (year: number) => {
    setCurrentMonth(new Date(year, currentMonth.getMonth(), 1));
    setShowYearDropdown(false);
  };

  const changeToMonth = (month: number) => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), month, 1));
    setShowMonthDropdown(false);
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
    setShowHourDropdown(false);
  };

  const handleMinuteChange = (minute: number) => {
    setMinutes(minute);
    setShowMinuteDropdown(false);
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
  const yearOptions = Array.from({ length: endYear - startYear + 1 }, (_, i) => startYear + i);

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

  const isToday = (day: number): boolean => {
    const timezone = getEffectiveTimezone(useLocalTimezone);
    const todayParts = getDateInTimezone(new Date(), timezone);
    return (
      currentMonth.getFullYear() === todayParts.year &&
      currentMonth.getMonth() === todayParts.month &&
      day === todayParts.day
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

  const hourOptions = use24HourFormat
    ? Array.from({ length: 24 }, (_, i) => i)
    : Array.from({ length: 12 }, (_, i) => (i === 0 ? 12 : i));

  const minuteOptions = Array.from({ length: 60 }, (_, i) => i);

  const formatTime = (): string => {
    const h = use24HourFormat ? hours : displayHour;
    const suffix = use24HourFormat ? '' : ` ${amPm === 'AM' ? amLabel : pmLabel}`;
    return `${h.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}${suffix}`;
  };

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
        {/* Month/Year Navigation */}
        <div className="mb-4 flex items-center justify-between">
          <Button variant="subtle" size="sm" onClick={() => changeMonth(-1)}>
            <ChevronLeft className="w-5 h-5" />
          </Button>

          <div className="flex items-center gap-2">
            {/* Month Dropdown */}
            <div className="relative">
              <button
                ref={monthButtonRef}
                onClick={() => {
                  setShowMonthDropdown(!showMonthDropdown);
                  setShowYearDropdown(false);
                  setShowHourDropdown(false);
                  setShowMinuteDropdown(false);
                }}
                className="flex items-center gap-1 px-3 py-1.5 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors border border-transparent hover:border-[var(--theme-border-primary)]"
              >
                {monthNames[currentMonth.getMonth()]}
                <ChevronDown
                  className={`w-4 h-4 transition-transform ${showMonthDropdown ? 'rotate-180' : ''}`}
                />
              </button>

              {showMonthDropdown && (
                <div
                  ref={monthDropdownRef}
                  className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-[85]"
                >
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
                ref={yearButtonRef}
                onClick={() => {
                  setShowYearDropdown(!showYearDropdown);
                  setShowMonthDropdown(false);
                  setShowHourDropdown(false);
                  setShowMinuteDropdown(false);
                }}
                className="flex items-center gap-1 px-3 py-1.5 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors border border-transparent hover:border-[var(--theme-border-primary)]"
              >
                {currentMonth.getFullYear()}
                <ChevronDown
                  className={`w-4 h-4 transition-transform ${showYearDropdown ? 'rotate-180' : ''}`}
                />
              </button>

              {showYearDropdown && (
                <div
                  ref={yearDropdownRef}
                  className="absolute top-full right-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-[85]"
                >
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

          <Button variant="subtle" size="sm" onClick={() => changeMonth(1)}>
            <ChevronRight className="w-5 h-5" />
          </Button>
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
          {Array.from({ length: firstDayOfMonth }).map((_, index) => (
            <div key={`empty-${index}`} />
          ))}
          {Array.from({ length: daysInMonth }).map((_, index) => {
            const day = index + 1;
            const selected = isSelectedDate(day);
            const today = isToday(day);
            const disabled = isBeforeMinDate(day);

            let className = 'relative p-2 text-sm transition-all rounded-lg ';

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
          <div className="flex items-center justify-center gap-2">
            <Clock className="w-4 h-4 text-[var(--theme-text-secondary)]" />
            <span className="text-sm text-[var(--theme-text-secondary)]">
              {t('common.dateTimePicker.timeLabel')}
            </span>

            {/* Hour Dropdown */}
            <div className="relative">
              <button
                ref={hourButtonRef}
                onClick={() => {
                  setShowHourDropdown(!showHourDropdown);
                  setShowMinuteDropdown(false);
                  setShowMonthDropdown(false);
                  setShowYearDropdown(false);
                }}
                className="px-3 py-1.5 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg border border-[var(--theme-border-primary)] hover:border-[var(--theme-primary)] transition-colors min-w-[60px] text-center"
              >
                {displayHour.toString().padStart(2, '0')}
              </button>

              {showHourDropdown && (
                <div
                  ref={hourDropdownRef}
                  className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-[85]"
                >
                  <CustomScrollbar maxHeight="200px" paddingMode="none">
                    <div className="py-1">
                      {hourOptions.map((hour) => {
                        // Convert display hour to 24h for comparison
                        let hour24 = hour;
                        if (!use24HourFormat) {
                          if (amPm === 'PM' && hour !== 12) hour24 = hour + 12;
                          else if (amPm === 'AM' && hour === 12) hour24 = 0;
                        }
                        const isDisabled = hour24 < getMinHour();

                        return (
                          <button
                            key={hour}
                            onClick={() => !isDisabled && handleHourChange(hour)}
                            disabled={isDisabled}
                            className={`w-full text-center px-4 py-2 text-sm transition-colors ${
                              isDisabled
                                ? 'text-[var(--theme-text-muted)] opacity-40 cursor-not-allowed'
                                : (use24HourFormat ? hours === hour : displayHour === hour)
                                  ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-medium'
                                  : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                            }`}
                          >
                            {hour.toString().padStart(2, '0')}
                          </button>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              )}
            </div>

            <span className="text-[var(--theme-text-primary)] font-medium">:</span>

            {/* Minute Dropdown */}
            <div className="relative">
              <button
                ref={minuteButtonRef}
                onClick={() => {
                  setShowMinuteDropdown(!showMinuteDropdown);
                  setShowHourDropdown(false);
                  setShowMonthDropdown(false);
                  setShowYearDropdown(false);
                }}
                className="px-3 py-1.5 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg border border-[var(--theme-border-primary)] hover:border-[var(--theme-primary)] transition-colors min-w-[60px] text-center"
              >
                {minutes.toString().padStart(2, '0')}
              </button>

              {showMinuteDropdown && (
                <div
                  ref={minuteDropdownRef}
                  className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden z-[85]"
                >
                  <CustomScrollbar maxHeight="200px" paddingMode="none">
                    <div className="py-1">
                      {minuteOptions.map((minute) => {
                        const isDisabled = minute < getMinMinute();

                        return (
                          <button
                            key={minute}
                            onClick={() => !isDisabled && handleMinuteChange(minute)}
                            disabled={isDisabled}
                            className={`w-full text-center px-4 py-2 text-sm transition-colors ${
                              isDisabled
                                ? 'text-[var(--theme-text-muted)] opacity-40 cursor-not-allowed'
                                : minutes === minute
                                  ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-medium'
                                  : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                            }`}
                          >
                            {minute.toString().padStart(2, '0')}
                          </button>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              )}
            </div>

            {/* AM/PM Toggle (only for 12h format) */}
            {!use24HourFormat && (
              <div className="flex rounded-lg overflow-hidden border border-[var(--theme-border-primary)]">
                <button
                  onClick={() => handleAmPmChange('AM')}
                  className={`px-3 py-1.5 text-sm transition-colors ${
                    amPm === 'AM'
                      ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-medium'
                      : 'bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-primary)]'
                  }`}
                >
                  {amLabel}
                </button>
                <button
                  onClick={() => handleAmPmChange('PM')}
                  className={`px-3 py-1.5 text-sm transition-colors ${
                    amPm === 'PM'
                      ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)] font-medium'
                      : 'bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-primary)]'
                  }`}
                >
                  {pmLabel}
                </button>
              </div>
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
              {selectedDate
                ? `${selectedDate.toLocaleDateString(undefined, { timeZone: getEffectiveTimezone(useLocalTimezone) })} ${formatTime()}`
                : t('common.dateTimePicker.none')}
            </span>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="flex gap-2 mt-4">
          <Button
            variant="outline"
            size="sm"
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
            color="blue"
            size="sm"
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
