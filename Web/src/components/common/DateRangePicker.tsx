import React, { useState, useEffect, useRef } from 'react';
import { ChevronLeft, ChevronRight, Calendar, ChevronDown } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';

interface DateRangePickerProps {
  startDate: Date | null;
  endDate: Date | null;
  onStartDateChange: (date: Date | null) => void;
  onEndDateChange: (date: Date | null) => void;
  onClose: () => void;
}

const DateRangePicker: React.FC<DateRangePickerProps> = ({
  startDate,
  endDate,
  onStartDateChange,
  onEndDateChange,
  onClose
}) => {
  const [currentMonth, setCurrentMonth] = useState(() => {
    return startDate ? new Date(startDate.getFullYear(), startDate.getMonth(), 1) : new Date();
  });
  const [showYearDropdown, setShowYearDropdown] = useState(false);
  const [showMonthDropdown, setShowMonthDropdown] = useState(false);
  const [hoveredDate, setHoveredDate] = useState<Date | null>(null);
  const [lastClickTime, setLastClickTime] = useState<number>(0);
  const [lastClickedDate, setLastClickedDate] = useState<Date | null>(null);
  const [clickCount, setClickCount] = useState<number>(0);

  const monthDropdownRef = useRef<HTMLDivElement>(null);
  const yearDropdownRef = useRef<HTMLDivElement>(null);
  const monthButtonRef = useRef<HTMLButtonElement>(null);
  const yearButtonRef = useRef<HTMLButtonElement>(null);

  // Click outside handler for dropdowns
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;

      // Check month dropdown
      if (showMonthDropdown &&
          monthDropdownRef.current &&
          !monthDropdownRef.current.contains(target) &&
          monthButtonRef.current &&
          !monthButtonRef.current.contains(target)) {
        setShowMonthDropdown(false);
      }

      // Check year dropdown
      if (showYearDropdown &&
          yearDropdownRef.current &&
          !yearDropdownRef.current.contains(target) &&
          yearButtonRef.current &&
          !yearButtonRef.current.contains(target)) {
        setShowYearDropdown(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [showMonthDropdown, showYearDropdown]);

  const closeDropdowns = () => {
    setShowYearDropdown(false);
    setShowMonthDropdown(false);
  };

  const getDaysInMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
  };

  const getFirstDayOfMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth(), 1).getDay();
  };

  const handleDateClick = (day: number) => {
    closeDropdowns();

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
    closeDropdowns();
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

  const currentYear = new Date().getFullYear();
  const startYear = 1999;
  const endYear = currentYear + 2;
  const yearOptions = Array.from({ length: endYear - startYear + 1 }, (_, i) => startYear + i);

  const monthNames = [
    'January', 'February', 'March', 'April', 'May', 'June',
    'July', 'August', 'September', 'October', 'November', 'December'
  ];

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
  const weekDays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

  return (
    <Modal
      opened={true}
      onClose={onClose}
      title={
        <div className="flex items-center gap-2">
          <Calendar className="w-5 h-5 text-[var(--theme-primary)]" />
          <span>Select Date Range</span>
        </div>
      }
      size="md"
    >
      <div>
        <div className="mb-4 flex items-center justify-between">
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
                ref={monthButtonRef}
                onClick={() => {
                  setShowMonthDropdown(!showMonthDropdown);
                  setShowYearDropdown(false);
                }}
                className="flex items-center gap-1 px-3 py-1.5 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors border border-transparent hover:border-[var(--theme-border-primary)]"
              >
                {monthNames[currentMonth.getMonth()]}
                <ChevronDown className={`w-4 h-4 transition-transform ${showMonthDropdown ? 'rotate-180' : ''}`} />
              </button>

              {showMonthDropdown && (
                <div
                  ref={monthDropdownRef}
                  className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden"
                  style={{ zIndex: 100003 }}
                >
                  <CustomScrollbar maxHeight="200px" paddingMode="none">
                    <div className="py-1">
                      {monthNames.map((month, index) => (
                        <button
                          key={month}
                          onClick={() => changeToMonth(index)}
                          className={`w-full text-left px-3 py-2 text-sm transition-colors whitespace-nowrap ${
                            index === currentMonth.getMonth()
                              ? 'bg-[var(--theme-primary)] text-white font-medium'
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
                }}
                className="flex items-center gap-1 px-3 py-1.5 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded-lg transition-colors border border-transparent hover:border-[var(--theme-border-primary)]"
              >
                {currentMonth.getFullYear()}
                <ChevronDown className={`w-4 h-4 transition-transform ${showYearDropdown ? 'rotate-180' : ''}`} />
              </button>

              {showYearDropdown && (
                <div
                  ref={yearDropdownRef}
                  className="absolute top-full right-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg overflow-hidden"
                  style={{ zIndex: 100003 }}
                >
                  <CustomScrollbar maxHeight="200px" paddingMode="none">
                    <div className="py-1">
                      {yearOptions.map((year) => (
                        <button
                          key={year}
                          onClick={() => changeYear(year)}
                          className={`w-full text-left px-3 py-2 text-sm transition-colors whitespace-nowrap ${
                            year === currentMonth.getFullYear()
                              ? 'bg-[var(--theme-primary)] text-white font-medium'
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

            let className = 'relative p-2 text-sm transition-all cursor-pointer ';

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
              className += 'bg-[var(--theme-primary)] text-white font-semibold z-10 ';
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
              <span className="text-[var(--theme-text-secondary)]">Start: </span>
              <span className="text-[var(--theme-text-primary)] font-medium">
                {startDate ? startDate.toLocaleDateString() : 'Not selected'}
              </span>
            </div>
            <div>
              <span className="text-[var(--theme-text-secondary)]">End: </span>
              <span className="text-[var(--theme-text-primary)] font-medium">
                {endDate ? endDate.toLocaleDateString() : 'Not selected'}
              </span>
            </div>
          </div>
          <div className="mt-2 text-xs text-[var(--theme-text-secondary)] text-center">
            {!startDate
              ? 'Click to select start date'
              : !endDate
                ? 'Click to select end date'
                : `${Math.abs(Math.ceil((endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24))) + 1} day(s) selected`}
          </div>
        </div>

        {/* Quick Presets */}
        <div className="mt-4 pt-4 border-t border-[var(--theme-border-primary)]">
          <div className="text-xs text-[var(--theme-text-secondary)] mb-2">Quick Select:</div>
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
              Today
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
              Last 7 Days
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
              Last 30 Days
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
              This Month
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
              Last Month
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
              This Year
            </button>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="flex gap-2 mt-4">
          <button
            onClick={() => {
              onStartDateChange(null);
              onEndDateChange(null);
              setHoveredDate(null);
              setClickCount(0);
              setLastClickedDate(null);
              closeDropdowns();
              setCurrentMonth(new Date());
            }}
            className="flex-1 px-3 py-2 text-sm bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded-lg hover:bg-[var(--theme-bg-primary)] transition-colors border border-[var(--theme-border-primary)]"
          >
            Clear
          </button>
          <button
            onClick={onClose}
            className="flex-1 px-3 py-2 text-sm bg-[var(--theme-primary)] text-white rounded-lg hover:bg-[var(--theme-primary)]/90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            disabled={!startDate || !endDate}
          >
            Apply
          </button>
        </div>
      </div>
    </Modal>
  );
};

export default DateRangePicker;
