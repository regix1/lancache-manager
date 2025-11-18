import React, { useState, useEffect } from 'react';
import { Globe, MapPin } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import preferencesService from '@services/preferences.service';
import { storage } from '@utils/storage';

// Storage key for persistence
const STORAGE_KEY = 'lancache_timezone_preference';

const TimezoneSelector: React.FC = () => {
  const [useLocalTimezone, setUseLocalTimezone] = useState(() => {
    const saved = storage.getItem(STORAGE_KEY);
    return saved === 'true'; // Defaults to false (server timezone)
  });

  // Load initial preference from server ONCE
  useEffect(() => {
    const loadPreference = async () => {
      const prefs = await preferencesService.getPreferences();
      setUseLocalTimezone(prefs.useLocalTimezone);
      storage.setItem(STORAGE_KEY, prefs.useLocalTimezone.toString());
    };
    loadPreference();
  }, []);

  const handleTimezoneChange = async (value: string) => {
    const newValue = value === 'local';

    // Optimistic update
    setUseLocalTimezone(newValue);
    storage.setItem(STORAGE_KEY, newValue.toString());

    // Save to server (will trigger SignalR broadcast to other components/tabs)
    try {
      await preferencesService.setPreference('useLocalTimezone', newValue);
    } catch (error) {
      console.error('Failed to update timezone preference:', error);
      // Revert on error
      setUseLocalTimezone(!newValue);
      storage.setItem(STORAGE_KEY, (!newValue).toString());
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
