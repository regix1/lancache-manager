import React, { useState } from 'react';
import { ChevronLeft, ChevronRight, Calendar, ChevronDown } from 'lucide-react';
import { Modal } from '@components/ui/Modal';

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
    // Initialize to show the current month or the month of the start date if it exists
    return startDate ? new Date(startDate.getFullYear(), startDate.getMonth(), 1) : new Date();
  });
  const [showYearDropdown, setShowYearDropdown] = useState(false);
  const [showMonthDropdown, setShowMonthDropdown] = useState(false);
  const [hoveredDate, setHoveredDate] = useState<Date | null>(null);
  const [lastClickTime, setLastClickTime] = useState<number>(0);
  const [lastClickedDate, setLastClickedDate] = useState<Date | null>(null);
  const [clickCount, setClickCount] = useState<number>(0);

  // Close dropdowns when clicking elsewhere
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

    // Track clicks for triple-click detection (within 500ms window)
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

    // Simple logic: if no start date or we have both dates, set start date
    // If we have start date but no end date, set end date
    if (!startDate || (startDate && endDate)) {
      // Starting a new selection
      onStartDateChange(selectedDate);
      onEndDateChange(null);
    } else {
      // Setting end date
      if (selectedDate < startDate) {
        // If selected date is before start, swap them
        onStartDateChange(selectedDate);
        onEndDateChange(startDate);
      } else if (selectedDate.getTime() === startDate.getTime()) {
        // If clicking the same date, make it a single day range
        onEndDateChange(selectedDate);
      } else {
        // Normal end date selection
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
    closeDropdowns(); // Close dropdowns when navigating with arrows
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

  // Generate year options (1999 to 2 years forward)
  const currentYear = new Date().getFullYear();
  const startYear = 1999;
  const endYear = currentYear + 2;
  const yearOptions = Array.from({ length: endYear - startYear + 1 }, (_, i) => startYear + i);

  const monthNames = [
    'January',
    'February',
    'March',
    'April',
    'May',
    'June',
    'July',
    'August',
    'September',
    'October',
    'November',
    'December'
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
            className="p-2 hover:bg-[var(--theme-bg-tertiary)] rounded transition-colors"
          >
            <ChevronLeft className="w-5 h-5 text-[var(--theme-text-primary)]" />
          </button>

          <div className="flex items-center gap-2">
            {/* Month Dropdown */}
            <div className="relative" style={{ zIndex: 100002 }}>
              <button
                onClick={() => {
                  setShowMonthDropdown(!showMonthDropdown);
                  setShowYearDropdown(false);
                }}
                className="flex items-center gap-1 px-3 py-1 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded transition-colors"
              >
                {monthNames[currentMonth.getMonth()]}
                <ChevronDown className="w-4 h-4" />
              </button>

              {showMonthDropdown && (
                <div
                  className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg max-h-48 overflow-y-auto"
                  style={{ zIndex: 100003 }}
                >
                  {monthNames.map((month, index) => (
                    <button
                      key={month}
                      onClick={() => changeToMonth(index)}
                      className={`w-full text-left px-3 py-2 text-sm hover:bg-[var(--theme-bg-tertiary)] transition-colors ${
                        index === currentMonth.getMonth()
                          ? 'bg-[var(--theme-primary)] text-white'
                          : 'text-[var(--theme-text-primary)]'
                      }`}
                    >
                      {month}
                    </button>
                  ))}
                </div>
              )}
            </div>

            {/* Year Dropdown */}
            <div className="relative" style={{ zIndex: 100002 }}>
              <button
                onClick={() => {
                  setShowYearDropdown(!showYearDropdown);
                  setShowMonthDropdown(false);
                }}
                className="flex items-center gap-1 px-3 py-1 text-[var(--theme-text-primary)] font-medium hover:bg-[var(--theme-bg-tertiary)] rounded transition-colors"
              >
                {currentMonth.getFullYear()}
                <ChevronDown className="w-4 h-4" />
              </button>

              {showYearDropdown && (
                <div
                  className="absolute top-full right-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg max-h-48 overflow-y-auto"
                  style={{ zIndex: 100003 }}
                >
                  {yearOptions.map((year) => (
                    <button
                      key={year}
                      onClick={() => changeYear(year)}
                      className={`w-full text-left px-3 py-2 text-sm hover:bg-[var(--theme-bg-tertiary)] transition-colors ${
                        year === currentMonth.getFullYear()
                          ? 'bg-[var(--theme-primary)] text-white'
                          : 'text-[var(--theme-text-primary)]'
                      }`}
                    >
                      {year}
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>

          <button
            onClick={() => changeMonth(1)}
            className="p-2 hover:bg-[var(--theme-bg-tertiary)] rounded transition-colors"
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

            let className = 'relative p-2 text-sm transition-all ';

            // Shape classes for better visual feedback
            if (isStart && isEnd) {
              // Single day selection
              className += 'rounded-lg ';
            } else if (isStart) {
              // Start of range
              className += 'rounded-l-lg ';
            } else if (isEnd) {
              // End of range
              className += 'rounded-r-lg ';
            } else if (inRange) {
              // Middle of range
              className += '';
            } else {
              // Not selected
              className += 'rounded ';
            }

            // Color classes
            if (isStart || isEnd) {
              className += 'bg-[var(--theme-primary)] text-white font-semibold z-10 ';
            } else if (inRange) {
              className += 'bg-[var(--theme-primary)]/20 text-[var(--theme-text-primary)] ';
            } else if (isHovered && startDate && !endDate) {
              className += 'bg-[var(--theme-bg-tertiary)]/50 text-[var(--theme-text-primary)] ';
            } else {
              className += 'hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] ';
            }

            if (today) {
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

        <div className="mt-4 pt-4 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
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
              ? 'Select start date (triple-click to unselect)'
              : !endDate
                ? 'Select end date (triple-click to unselect)'
                : `${Math.abs(Math.ceil((endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24))) + 1} day(s) selected`}
          </div>
        </div>

        {/* Quick Presets */}
        <div className="mt-4 pt-4 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
          <div className="text-xs text-[var(--theme-text-secondary)] mb-2">Quick Select:</div>
          <div className="grid grid-cols-3 gap-2 mb-3">
            <button
              onClick={() => {
                const today = new Date();
                today.setHours(0, 0, 0, 0);
                onStartDateChange(today);
                onEndDateChange(today);
              }}
              className="px-2 py-1 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
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
              className="px-2 py-1 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
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
              className="px-2 py-1 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
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
              className="px-2 py-1 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
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
              className="px-2 py-1 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
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
              className="px-2 py-1 text-xs bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
            >
              This Year
            </button>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="flex gap-2">
          <button
            onClick={() => {
              onStartDateChange(null);
              onEndDateChange(null);
              setHoveredDate(null);
              setClickCount(0);
              setLastClickedDate(null);
              closeDropdowns();
              // Reset calendar to current month/year
              setCurrentMonth(new Date());
            }}
            className="flex-1 px-3 py-2 text-sm bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] rounded hover:bg-[var(--theme-bg-primary)] transition-colors"
          >
            Clear
          </button>
          <button
            onClick={onClose}
            className="flex-1 px-3 py-2 text-sm bg-[var(--theme-primary)] text-white rounded hover:bg-[var(--theme-primary)]/90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
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
