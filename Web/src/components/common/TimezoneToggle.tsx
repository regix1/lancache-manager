import React, { useState, useEffect } from 'react';
import { Globe, MapPin } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';
import preferencesService from '@services/preferences.service';

const TimezoneToggle: React.FC = () => {
  const [useLocalTimezone, setUseLocalTimezone] = useState(false);

  // Load initial preference
  useEffect(() => {
    const loadPreference = async () => {
      const prefs = await preferencesService.getPreferences();
      setUseLocalTimezone(prefs.useLocalTimezone);
    };
    loadPreference();
  }, []);

  // Listen for preference changes from other tabs/devices
  useEffect(() => {
    const handlePreferenceChange = (event: any) => {
      const { key, value } = event.detail;
      if (key === 'useLocalTimezone') {
        setUseLocalTimezone(value);
      }
    };

    window.addEventListener('preference-changed', handlePreferenceChange);
    return () => window.removeEventListener('preference-changed', handlePreferenceChange);
  }, []);

  const handleToggle = async () => {
    const newValue = !useLocalTimezone;
    setUseLocalTimezone(newValue);
    await preferencesService.setPreference('useLocalTimezone', newValue);
  };

  const tooltipContent = useLocalTimezone
    ? 'Showing times in your local timezone. Click to show server timezone.'
    : 'Showing times in server timezone. Click to show your local timezone.';

  return (
    <Tooltip content={tooltipContent}>
      <button
        onClick={handleToggle}
        className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg transition-all text-sm font-medium"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          border: '1px solid var(--theme-border)',
          color: 'var(--theme-text-primary)'
        }}
        onMouseEnter={(e) => {
          e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
          e.currentTarget.style.borderColor = 'var(--theme-action)';
        }}
        onMouseLeave={(e) => {
          e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)';
          e.currentTarget.style.borderColor = 'var(--theme-border)';
        }}
      >
        {useLocalTimezone ? (
          <>
            <MapPin size={16} />
            <span className="hidden sm:inline">Local</span>
          </>
        ) : (
          <>
            <Globe size={16} />
            <span className="hidden sm:inline">Server</span>
          </>
        )}
      </button>
    </Tooltip>
  );
};

export default TimezoneToggle;
