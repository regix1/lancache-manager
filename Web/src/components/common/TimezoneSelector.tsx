import React from 'react';
import { Globe, MapPin } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import preferencesService from '@services/preferences.service';
import { useTimezone } from '@contexts/TimezoneContext';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

const TimezoneSelector: React.FC = () => {
  const { useLocalTimezone, use24HourFormat } = useTimezone();

  // Derive current value from both preferences
  const getCurrentValue = (): TimeSettingValue => {
    if (useLocalTimezone) {
      return use24HourFormat ? 'local-24h' : 'local-12h';
    } else {
      return use24HourFormat ? 'server-24h' : 'server-12h';
    }
  };

  const handleTimeSettingChange = async (value: string) => {
    const newUseLocal = value.startsWith('local');
    const newUse24Hour = value.endsWith('24h');

    // Save both preferences (will trigger SignalR broadcasts which update TimezoneContext)
    try {
      // Update both preferences
      await Promise.all([
        preferencesService.setPreference('useLocalTimezone', newUseLocal),
        preferencesService.setPreference('use24HourFormat', newUse24Hour)
      ]);
    } catch (error) {
      console.error('Failed to update time settings:', error);
    }
  };

  const options = [
    {
      value: 'server-24h',
      label: 'Server (24h)',
      shortLabel: 'Server 24h',
      description: 'Server timezone with 24-hour format (15:30)',
      icon: Globe
    },
    {
      value: 'server-12h',
      label: 'Server (12h)',
      shortLabel: 'Server 12h',
      description: 'Server timezone with 12-hour format (3:30 PM)',
      icon: Globe
    },
    {
      value: 'local-24h',
      label: 'Local (24h)',
      shortLabel: 'Local 24h',
      description: 'Your local timezone with 24-hour format',
      icon: MapPin
    },
    {
      value: 'local-12h',
      label: 'Local (12h)',
      shortLabel: 'Local 12h',
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
      dropdownWidth="w-72"
      alignRight={true}
    />
  );
};

export default TimezoneSelector;
