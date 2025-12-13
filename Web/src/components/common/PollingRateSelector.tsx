import React from 'react';
import { Lightbulb, Gauge, Zap } from 'lucide-react';
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
      value: 'LIVE',
      label: 'Live',
      shortLabel: 'Live',
      description: 'Instant updates via SignalR',
      rightLabel: 'Live',
      icon: Zap
    },
    {
      value: 'ULTRA',
      label: 'Ultra-fast',
      shortLabel: '1s',
      description: 'Updates every 1 second',
      rightLabel: '1s',
      icon: Gauge
    },
    {
      value: 'REALTIME',
      label: 'Real-time',
      shortLabel: '5s',
      description: 'Updates every 5 seconds',
      rightLabel: '5s',
      icon: Gauge
    },
    {
      value: 'STANDARD',
      label: 'Standard',
      shortLabel: '10s',
      description: 'Updates every 10 seconds (recommended)',
      rightLabel: '10s',
      icon: Gauge
    },
    {
      value: 'RELAXED',
      label: 'Relaxed',
      shortLabel: '30s',
      description: 'Updates every 30 seconds',
      rightLabel: '30s',
      icon: Gauge
    },
    {
      value: 'SLOW',
      label: 'Slow',
      shortLabel: '60s',
      description: 'Updates every 60 seconds',
      rightLabel: '60s',
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
      dropdownTitle="Polling Rate"
      footerNote="Lower rates mean less frequent data updates"
      footerIcon={Lightbulb}
      cleanStyle={true}
    />
  );
};

export default PollingRateSelector;
