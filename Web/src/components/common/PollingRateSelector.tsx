import React from 'react';
import { Gauge } from 'lucide-react';
import { usePollingRate } from '@contexts/PollingRateContext';
import { type PollingRate } from '@utils/constants';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';

interface PollingRateSelectorProps {
  disabled?: boolean;
}

const PollingRateSelector: React.FC<PollingRateSelectorProps> = ({ disabled = false }) => {
  const { pollingRate, setPollingRate } = usePollingRate();

  const pollingOptions = [
    {
      value: 'STANDARD',
      label: 'Standard (10s)',
      shortLabel: '10s',
      description: 'Updates every 10 seconds (recommended)',
      icon: Gauge
    },
    {
      value: 'REALTIME',
      label: 'Real-time (5s)',
      shortLabel: '5s',
      description: 'Updates every 5 seconds (high server load)',
      icon: Gauge
    },
    {
      value: 'RELAXED',
      label: 'Relaxed (30s)',
      shortLabel: '30s',
      description: 'Updates every 30 seconds (low server load)',
      icon: Gauge
    },
    {
      value: 'SLOW',
      label: 'Slow (60s)',
      shortLabel: '60s',
      description: 'Updates every 60 seconds (minimal server impact)',
      icon: Gauge
    },
    {
      value: 'ULTRA',
      label: 'Ultra-fast (1s)',
      shortLabel: '1s',
      description: 'Updates every 1 second (very high load, unstable)',
      icon: Gauge
    }
  ];

  const handlePollingRateChange = (value: string) => {
    setPollingRate(value as PollingRate);
  };

  return (
    <EnhancedDropdown
      options={pollingOptions}
      value={pollingRate}
      onChange={handlePollingRateChange}
      disabled={disabled}
      placeholder="Select polling rate"
      compactMode={true}
      dropdownWidth="w-64"
      alignRight={true}
    />
  );
};

export default PollingRateSelector;
