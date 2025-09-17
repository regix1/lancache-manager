import React, { useState, useRef, useEffect } from 'react';
import { ChevronLeft, ChevronRight, Calendar, X } from 'lucide-react';

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

  const getDaysInMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
  };

  const getFirstDayOfMonth = (date: Date): number => {
    return new Date(date.getFullYear(), date.getMonth(), 1).getDay();
  };

  const handleDateClick = (day: number) => {
    const selectedDate = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), day);
    selectedDate.setHours(0, 0, 0, 0);

    if (selectingStart || !startDate) {
      onStartDateChange(selectedDate);
      onEndDateChange(null);
      setSelectingStart(false);
    } else {
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
    setCurrentMonth(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + increment, 1));
  };

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

  const monthYearString = currentMonth.toLocaleDateString('en-US', {
    month: 'long',
    year: 'numeric'
  });

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
            <h4 className="text-[var(--theme-text-primary)] font-medium">{monthYearString}</h4>
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