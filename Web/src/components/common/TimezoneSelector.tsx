import React, { useState, useEffect, useMemo } from 'react';
import { Globe, MapPin } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import preferencesService from '@services/preferences.service';
import { useTimezone } from '@contexts/TimezoneContext';
import { getServerTimezone } from '@utils/timezone';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

const TimezoneSelector: React.FC = () => {
  const { useLocalTimezone, use24HourFormat, setPendingTimeSetting } = useTimezone();
  const [tick, setTick] = useState(0);
  const serverTimezone = useMemo(() => getServerTimezone(), []);

  // Tick every second to trigger re-render for clock update
  useEffect(() => {
    const interval = setInterval(() => {
      setTick(t => t + 1);
    }, 1000);
    return () => clearInterval(interval);
  }, []);

  // Compute current time based on timezone/format preferences
  const computeTime = () => {
    const now = new Date();
    let hours: number;
    let minutes: number;

    if (useLocalTimezone) {
      hours = now.getHours();
      minutes = now.getMinutes();
    } else {
      const serverTime = new Date(now.toLocaleString('en-US', { timeZone: serverTimezone || 'UTC' }));
      hours = serverTime.getHours();
      minutes = serverTime.getMinutes();
    }

    if (use24HourFormat) {
      return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}`;
    } else {
      const period = hours >= 12 ? 'PM' : 'AM';
      const displayHour = hours % 12 || 12;
      return `${displayHour}:${minutes.toString().padStart(2, '0')} ${period}`;
    }
  };

  void tick; // Force re-render every second for clock
  const currentTime = computeTime();

  const getCurrentValue = (): TimeSettingValue => {
    if (useLocalTimezone) {
      return use24HourFormat ? 'local-24h' : 'local-12h';
    } else {
      return use24HourFormat ? 'server-24h' : 'server-12h';
    }
  };

  const handleTimeSettingChange = async (value: string) => {
    const typedValue = value as TimeSettingValue;
    setPendingTimeSetting(typedValue);

    try {
      const newUseLocal = typedValue.startsWith('local');
      const newUse24Hour = typedValue.endsWith('24h');
      await Promise.all([
        preferencesService.setPreference('useLocalTimezone', newUseLocal),
        preferencesService.setPreference('use24HourFormat', newUse24Hour)
      ]);
    } catch (error) {
      console.error('Failed to update time settings:', error);
      setPendingTimeSetting(null);
    }
  };

  const options = [
    {
      value: 'server-24h',
      label: 'Server (24h)',
      description: 'Server timezone with 24-hour format (15:30)',
      icon: Globe
    },
    {
      value: 'server-12h',
      label: 'Server (12h)',
      description: 'Server timezone with 12-hour format (3:30 PM)',
      icon: Globe
    },
    {
      value: 'local-24h',
      label: 'Local (24h)',
      description: 'Your local timezone with 24-hour format',
      icon: MapPin
    },
    {
      value: 'local-12h',
      label: 'Local (12h)',
      description: 'Your local timezone with 12-hour format',
      icon: MapPin
    }
  ];

  return (
    <EnhancedDropdown
      options={options}
      value={getCurrentValue()}
      onChange={handleTimeSettingChange}
      compactMode={true}
      customTriggerLabel={currentTime}
      dropdownWidth="w-72"
      alignRight={true}
    />
  );
};

export default TimezoneSelector;
