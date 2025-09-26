import React, { useState, useRef, useEffect } from 'react';
import { Clock, Calendar, ChevronDown, Loader, Radio } from 'lucide-react';
import { useTimeFilter, TimeRange } from '@contexts/TimeFilterContext';
import DateRangePicker from './DateRangePicker';
import Tooltip from '../ui/Tooltip';

const TimeFilter: React.FC = () => {
  const {
    timeRange,
    setTimeRange,
    customStartDate,
    customEndDate,
    setCustomStartDate,
    setCustomEndDate
  } = useTimeFilter();

  const [showDropdown, setShowDropdown] = useState(false);
  const [showDatePicker, setShowDatePicker] = useState(false);
  const [timeFilterLoading, setTimeFilterLoading] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);

  const timeOptions: { value: TimeRange; label: string; shortLabel?: string }[] = [
    { value: '1h', label: 'Last Hour', shortLabel: '1H' },
    { value: '6h', label: 'Last 6 Hours', shortLabel: '6H' },
    { value: '12h', label: 'Last 12 Hours', shortLabel: '12H' },
    { value: '24h', label: 'Last 24 Hours', shortLabel: '24H' },
    { value: '7d', label: 'Last 7 Days', shortLabel: '7D' },
    { value: '30d', label: 'Last 30 Days', shortLabel: '30D' },
    { value: 'live', label: 'Live Data', shortLabel: 'Live' },
    { value: 'custom', label: 'Custom Range', shortLabel: 'Custom' }
  ];

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setShowDropdown(false);
      }
    };

    if (showDropdown) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [showDropdown]);

  const handleTimeRangeChange = (value: TimeRange) => {
    setTimeRange(value);
    if (value === 'custom') {
      setShowDatePicker(true);
      setShowDropdown(false);
    } else {
      setShowDropdown(false);
      // Show loading for non-custom filters
      setTimeFilterLoading(true);
      setTimeout(() => setTimeFilterLoading(false), 1000);
    }
  };

  // Show loading for custom date changes
  useEffect(() => {
    if (timeRange === 'custom' && customStartDate && customEndDate) {
      setTimeFilterLoading(true);
      setTimeout(() => setTimeFilterLoading(false), 1000);
    }
  }, [customStartDate, customEndDate, timeRange]);

  const getCurrentLabel = () => {
    if (timeRange === 'custom' && customStartDate && customEndDate) {
      const start = customStartDate.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric'
      });
      const end = customEndDate.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric'
      });
      return `${start} - ${end}`;
    }
    const option = timeOptions.find(opt => opt.value === timeRange);
    return option?.shortLabel || option?.label || 'Last 24H';
  };

  return (
    <>
      <div className="relative" ref={dropdownRef}>
        <div className="flex items-center gap-2">
          <button
            onClick={() => setShowDropdown(!showDropdown)}
            className="flex items-center gap-1.5 px-2 sm:px-3 py-1.5 rounded-lg transition-all"
            style={{
              backgroundColor: showDropdown ? 'var(--theme-bg-tertiary)' : 'var(--theme-bg-secondary)',
              border: showDropdown ? '1px solid var(--theme-primary)' : '1px solid var(--theme-border-primary)'
            }}
          >
            {timeFilterLoading ? (
              <Loader className="w-4 h-4 text-[var(--theme-primary)] animate-spin" />
            ) : (
              <Clock className="w-4 h-4 text-[var(--theme-primary)]" />
            )}
            <span className="text-xs sm:text-sm font-medium text-[var(--theme-text-primary)]">
              {timeFilterLoading ? 'Loading...' : getCurrentLabel()}
            </span>
            <ChevronDown
              className={`w-3 h-3 text-[var(--theme-text-secondary)] transition-transform ${
                showDropdown ? 'rotate-180' : ''
              }`}
            />
          </button>
          {timeRange === 'live' && !timeFilterLoading && (
            <Tooltip content="Live Mode: Data updates automatically every few seconds">
              <div className="flex items-center">
                <Radio className="w-3 h-3 animate-pulse" style={{ color: 'var(--theme-success)' }} />
              </div>
            </Tooltip>
          )}
        </div>

        {showDropdown && (
          <div
            className="absolute right-0 mt-2 w-48 rounded-lg shadow-xl z-[99999]"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              border: '1px solid var(--theme-border-primary)'
            }}
          >
            <div className="p-1">
              <div className="px-2 py-1.5 text-xs font-semibold text-[var(--theme-text-secondary)]">
                Time Range
              </div>
              {timeOptions.map((option) => (
                <button
                  key={option.value}
                  onClick={() => handleTimeRangeChange(option.value)}
                  className={`
                    w-full flex items-center justify-between px-3 py-2 rounded text-sm transition-colors
                    ${timeRange === option.value
                      ? 'bg-[var(--theme-primary)]/10 text-[var(--theme-primary)]'
                      : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                    }
                  `}
                >
                  <span className="flex items-center gap-1.5">
                    {option.value === 'custom' && <Calendar className="w-3.5 h-3.5" />}
                    {option.label}
                  </span>
                  {timeRange === option.value && (
                    <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
                      <path
                        fillRule="evenodd"
                        d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                        clipRule="evenodd"
                      />
                    </svg>
                  )}
                </button>
              ))}
            </div>
          </div>
        )}
      </div>

      {showDatePicker && (
        <DateRangePicker
          startDate={customStartDate}
          endDate={customEndDate}
          onStartDateChange={setCustomStartDate}
          onEndDateChange={setCustomEndDate}
          onClose={() => setShowDatePicker(false)}
        />
      )}
    </>
  );
};

export default TimeFilter;