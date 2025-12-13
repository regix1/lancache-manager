import React, { useState } from 'react';
import { Clock, Calendar, Radio, Info } from 'lucide-react';
import { useTimeFilter, type TimeRange } from '@contexts/TimeFilterContext';
import DateRangePicker from './DateRangePicker';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';

interface TimeFilterProps {
  disabled?: boolean;
}

const TimeFilter: React.FC<TimeFilterProps> = ({ disabled = false }) => {
  const {
    timeRange,
    setTimeRange,
    customStartDate,
    customEndDate,
    setCustomStartDate,
    setCustomEndDate
  } = useTimeFilter();

  const [showDatePicker, setShowDatePicker] = useState(false);

  const timeOptions = [
    { value: 'live', label: 'Live', shortLabel: 'Live', description: 'Show real-time data updates', icon: Radio, rightLabel: 'Now' },
    { value: '1h', label: 'Last Hour', shortLabel: '1H', description: 'Show data from the last 1 hour', icon: Clock, rightLabel: '1h' },
    { value: '6h', label: 'Last 6 Hours', shortLabel: '6H', description: 'Show data from the last 6 hours', icon: Clock, rightLabel: '6h' },
    { value: '12h', label: 'Last 12 Hours', shortLabel: '12H', description: 'Show data from the last 12 hours', icon: Clock, rightLabel: '12h' },
    { value: '24h', label: 'Last 24 Hours', shortLabel: '24H', description: 'Show data from the last 24 hours', icon: Clock, rightLabel: '24h' },
    { value: '7d', label: 'Last 7 Days', shortLabel: '7D', description: 'Show data from the last 7 days', icon: Calendar, rightLabel: '7d' },
    { value: '30d', label: 'Last 30 Days', shortLabel: '30D', description: 'Show data from the last 30 days', icon: Calendar, rightLabel: '30d' },
    { value: 'custom', label: 'Custom Range', shortLabel: 'Custom', description: 'Select a custom date range', icon: Calendar, rightLabel: '...' }
  ];

  const handleTimeRangeChange = (value: string) => {
    const timeValue = value as TimeRange;
    setTimeRange(timeValue);
    if (timeValue === 'custom') {
      setShowDatePicker(true);
    }
  };

  // Generate custom label for date ranges
  const getCustomTriggerLabel = () => {
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
    return undefined;
  };

  return (
    <>
      <div className="flex items-center gap-2">
        <EnhancedDropdown
          options={timeOptions}
          value={timeRange}
          onChange={handleTimeRangeChange}
          disabled={disabled}
          placeholder="Select time range"
          compactMode={true}
          customTriggerLabel={getCustomTriggerLabel()}
          dropdownWidth="w-64"
          alignRight={true}
          dropdownTitle="Time Range"
          footerNote="Historical data helps identify trends and patterns over time"
          footerIcon={Info}
          cleanStyle={true}
        />
      </div>

      {showDatePicker && (
        <DateRangePicker
          startDate={customStartDate}
          endDate={customEndDate}
          onStartDateChange={setCustomStartDate}
          onEndDateChange={setCustomEndDate}
          onClose={() => {
            setShowDatePicker(false);
            // If dates were cleared, switch back to live mode
            if (!customStartDate || !customEndDate) {
              setTimeRange('live');
            }
          }}
        />
      )}
    </>
  );
};

export default TimeFilter;
