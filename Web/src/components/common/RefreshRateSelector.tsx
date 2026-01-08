import React from 'react';
import { Lightbulb, Gauge, Zap, Lock } from 'lucide-react';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import { type RefreshRate } from '@utils/constants';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Tooltip } from '@components/ui/Tooltip';

interface RefreshRateSelectorProps {
  disabled?: boolean;
}

const RefreshRateSelector: React.FC<RefreshRateSelectorProps> = ({ disabled = false }) => {
  const { refreshRate, setRefreshRate, isControlledByAdmin } = useRefreshRate();

  const refreshOptions = [
    {
      value: 'LIVE',
      label: 'Live',
      shortLabel: 'Live',
      description: 'Real-time updates (min 500ms)',
      rightLabel: 'Live',
      icon: Zap
    },
    {
      value: 'ULTRA',
      label: 'Ultra-fast',
      shortLabel: '1s',
      description: 'Max 1 update per second',
      rightLabel: '1s',
      icon: Gauge
    },
    {
      value: 'REALTIME',
      label: 'Real-time',
      shortLabel: '5s',
      description: 'Max 1 update every 5 seconds',
      rightLabel: '5s',
      icon: Gauge
    },
    {
      value: 'STANDARD',
      label: 'Standard',
      shortLabel: '10s',
      description: 'Max 1 update every 10 seconds (recommended)',
      rightLabel: '10s',
      icon: Gauge
    },
    {
      value: 'RELAXED',
      label: 'Relaxed',
      shortLabel: '30s',
      description: 'Max 1 update every 30 seconds',
      rightLabel: '30s',
      icon: Gauge
    },
    {
      value: 'SLOW',
      label: 'Slow',
      shortLabel: '60s',
      description: 'Max 1 update every 60 seconds',
      rightLabel: '60s',
      icon: Gauge
    }
  ];

  const handleRefreshRateChange = (value: string) => {
    setRefreshRate(value as RefreshRate);
  };

  const isDisabled = disabled || isControlledByAdmin;

  // If controlled by admin (guest user), show a locked indicator with tooltip
  if (isControlledByAdmin) {
    const currentOption = refreshOptions.find((opt) => opt.value === refreshRate);
    const displayLabel = currentOption?.shortLabel || refreshRate;

    return (
      <Tooltip content="Refresh rate is controlled by administrator">
        <div className="flex items-center gap-1.5 px-2 py-1 rounded text-sm cursor-not-allowed opacity-75">
          <Lock className="w-3.5 h-3.5 text-themed-muted" />
          <span className="text-themed-secondary">{displayLabel}</span>
        </div>
      </Tooltip>
    );
  }

  return (
    <EnhancedDropdown
      options={refreshOptions}
      value={refreshRate}
      onChange={handleRefreshRateChange}
      disabled={isDisabled}
      placeholder="Select refresh rate"
      compactMode={true}
      dropdownWidth="w-64"
      alignRight={true}
      dropdownTitle="Refresh Rate"
      footerNote="Controls how often the UI updates with new data"
      footerIcon={Lightbulb}
      cleanStyle={true}
    />
  );
};

export default RefreshRateSelector;
