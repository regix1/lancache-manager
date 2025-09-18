import React, { useState, useRef, useEffect } from 'react';
import { ChevronLeft, ChevronRight, Calendar, X, ChevronDown } from 'lucide-react';

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
  const [currentMonth, setCurrentMonth] = useState(new Date());
  const [selectingStart, setSelectingStart] = useState(true);
  const [showYearDropdown, setShowYearDropdown] = useState(false);
  const [showMonthDropdown, setShowMonthDropdown] = useState(false);
  const modalRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (modalRef.current && !modalRef.current.contains(event.target as Node)) {
        onClose();
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [onClose]);

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
    closeDropdowns(); // Close any open dropdowns when selecting a date

    const selectedDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    selectedDate.setHours(0, 0, 0, 0);

    // Check if clicking the same date to deselect
    if (selectingStart || !startDate) {
      // If clicking the same start date, deselect it
      if (startDate && selectedDate.getTime() === startDate.getTime()) {
        onStartDateChange(null);
        onEndDateChange(null);
        setSelectingStart(true);
        return;
      }
      onStartDateChange(selectedDate);
      onEndDateChange(null);
      setSelectingStart(false);
    } else {
      // If clicking the same end date, deselect it
      if (endDate && selectedDate.getTime() === endDate.getTime()) {
        onEndDateChange(null);
        setSelectingStart(false);
        return;
      }
      // If clicking the same start date while selecting end, deselect both
      if (selectedDate.getTime() === startDate.getTime()) {
        onStartDateChange(null);
        onEndDateChange(null);
        setSelectingStart(true);
        return;
      }

      if (selectedDate < startDate) {
        onStartDateChange(selectedDate);
        onEndDateChange(startDate);
      } else {
        onEndDateChange(selectedDate);
      }
      setSelectingStart(true);
    }
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
    'January', 'February', 'March', 'April', 'May', 'June',
    'July', 'August', 'September', 'October', 'November', 'December'
  ];

  const isDateInRange = (day: number): boolean => {
    if (!startDate || !endDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);
    return checkDate >= startDate && checkDate <= endDate;
  };

  const isStartDate = (day: number): boolean => {
    if (!startDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);
    return checkDate.getTime() === startDate.getTime();
  };

  const isEndDate = (day: number): boolean => {
    if (!endDate) return false;
    const checkDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    checkDate.setHours(0, 0, 0, 0);
    return checkDate.getTime() === endDate.getTime();
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
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-[100000] p-4">
      <div
        ref={modalRef}
        className="bg-[var(--theme-bg-secondary)] rounded-lg shadow-xl max-w-md w-full"
        style={{ border: '1px solid var(--theme-border-primary)' }}
      >
        <div className="flex items-center justify-between p-4 border-b" style={{ borderColor: 'var(--theme-border-primary)' }}>
          <div className="flex items-center gap-2">
            <Calendar className="w-5 h-5 text-[var(--theme-primary)]" />
            <h3 className="text-lg font-semibold text-[var(--theme-text-primary)]">
              Select Date Range
            </h3>
          </div>
          <button
            onClick={onClose}
            className="p-1 hover:bg-[var(--theme-bg-tertiary)] rounded transition-colors"
          >
            <X className="w-5 h-5 text-[var(--theme-text-secondary)]" />
          </button>
        </div>

        <div className="p-4">
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
                  <div className="absolute top-full left-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg max-h-48 overflow-y-auto" style={{ zIndex: 100003 }}>
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
                  <div className="absolute top-full right-0 mt-1 bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] rounded-lg shadow-lg max-h-48 overflow-y-auto" style={{ zIndex: 100003 }}>
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

          <div className="grid grid-cols-7 gap-1">
            {Array.from({ length: firstDayOfMonth }).map((_, index) => (
              <div key={`empty-${index}`} />
            ))}
            {Array.from({ length: daysInMonth }).map((_, index) => {
              const day = index + 1;
              const inRange = isDateInRange(day);
              const isStart = isStartDate(day);
              const isEnd = isEndDate(day);
              const today = isToday(day);

              return (
                <button
                  key={day}
                  onClick={() => handleDateClick(day)}
                  className={`
                    relative p-2 text-sm rounded transition-all
                    ${
                      isStart || isEnd
                        ? 'bg-[var(--theme-primary)] text-white font-semibold'
                        : inRange
                        ? 'bg-[var(--theme-primary)]/20 text-[var(--theme-text-primary)]'
                        : 'hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]'
                    }
                    ${today ? 'ring-2 ring-[var(--theme-primary)]/50' : ''}
                  `}
                >
                  {day}
                  {today && !showYearDropdown && !showMonthDropdown && (
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
              {selectingStart || !startDate
                ? 'Select start date'
                : 'Select end date'}
            </div>
          </div>

          <div className="mt-4 flex gap-2">
            <button
              onClick={() => {
                onStartDateChange(null);
                onEndDateChange(null);
                setSelectingStart(true);
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
              className="flex-1 px-3 py-2 text-sm bg-[var(--theme-primary)] text-white rounded hover:bg-[var(--theme-primary)]/90 transition-colors"
            >
              Apply
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};

export default DateRangePicker;