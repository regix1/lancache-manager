import React from 'react';
import { Globe, MapPin } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import preferencesService from '@services/preferences.service';
import { useTimezone } from '@contexts/TimezoneContext';

const TimezoneSelector: React.FC = () => {
  const { useLocalTimezone } = useTimezone();

  const handleTimezoneChange = async (value: string) => {
    const newValue = value === 'local';

    // Save to server (will trigger SignalR broadcast which updates TimezoneContext)
    try {
      await preferencesService.setPreference('useLocalTimezone', newValue);
    } catch (error) {
      console.error('Failed to update timezone preference:', error);
    }
  };

  const options = [
    {
      value: 'server',
      label: 'Server Timezone',
      shortLabel: 'Server',
      description: 'Show times in server timezone (Docker/UTC)',
      icon: Globe
    },
    {
      value: 'local',
      label: 'Local Timezone',
      shortLabel: 'Local',
      description: 'Show times in your local timezone',
      icon: MapPin
    }
  ];

  return (
    <EnhancedDropdown
      options={options}
      value={useLocalTimezone ? 'local' : 'server'}
      onChange={handleTimezoneChange}
      compactMode={true}
      dropdownWidth="w-64"
      alignRight={true}
    />
  );
};

export default TimezoneSelector;
