import React, { useState, useEffect } from 'react';
import { Globe, MapPin, Loader2 } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';
import preferencesService from '@services/preferences.service';

const TimezoneToggle: React.FC = () => {
  const [useLocalTimezone, setUseLocalTimezone] = useState(false);
  const [isLoading, setIsLoading] = useState(false);

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
    if (isLoading) return; // Prevent spam clicking

    setIsLoading(true);
    const newValue = !useLocalTimezone;
    setUseLocalTimezone(newValue);
    await preferencesService.setPreference('useLocalTimezone', newValue);
    // Context + SignalR will handle the re-render automatically

    // Keep loading state for a brief moment to ensure updates propagate
    setTimeout(() => setIsLoading(false), 500);
  };

  const tooltipContent = useLocalTimezone
    ? 'Showing times in your local timezone. Click to show server timezone.'
    : 'Showing times in server timezone. Click to show your local timezone.';

  return (
    <Tooltip content={tooltipContent}>
      <button
        onClick={handleToggle}
        disabled={isLoading}
        className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg transition-all text-sm font-medium disabled:opacity-50 disabled:cursor-not-allowed"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          border: '1px solid var(--theme-border)',
          color: 'var(--theme-text-primary)'
        }}
        onMouseEnter={(e) => {
          if (!isLoading) {
            e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
            e.currentTarget.style.borderColor = 'var(--theme-action)';
          }
        }}
        onMouseLeave={(e) => {
          if (!isLoading) {
            e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)';
            e.currentTarget.style.borderColor = 'var(--theme-border)';
          }
        }}
      >
        {isLoading ? (
          <>
            <Loader2 size={16} className="animate-spin" />
            <span className="hidden sm:inline">Loading...</span>
          </>
        ) : useLocalTimezone ? (
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
