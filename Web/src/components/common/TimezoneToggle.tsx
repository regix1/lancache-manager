import React, { useState, useEffect, useRef } from 'react';
import { Globe, MapPin, Loader2 } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';
import preferencesService from '@services/preferences.service';
import { storage } from '@utils/storage';

// Storage key for persistence
const STORAGE_KEY = 'lancache_timezone_preference';

const TimezoneToggle: React.FC = () => {
  // Default to server timezone (false), but check localStorage first
  const [useLocalTimezone, setUseLocalTimezone] = useState(() => {
    const saved = storage.getItem(STORAGE_KEY);
    return saved === 'true'; // Defaults to false (server timezone) if not set
  });
  const [isLoading, setIsLoading] = useState(false);
  const isTogglingRef = useRef(false); // Ref-based lock for extra protection

  // Load initial preference from server
  useEffect(() => {
    const loadPreference = async () => {
      const prefs = await preferencesService.getPreferences();
      setUseLocalTimezone(prefs.useLocalTimezone);
      // Save to localStorage for faster subsequent loads
      storage.setItem(STORAGE_KEY, prefs.useLocalTimezone.toString());
    };
    loadPreference();
  }, []);

  // Listen for preference changes from other tabs/devices
  useEffect(() => {
    const handlePreferenceChange = (event: any) => {
      const { key, value } = event.detail;
      if (key === 'useLocalTimezone') {
        setUseLocalTimezone(value);
        // Also update localStorage when changes come from other sources
        storage.setItem(STORAGE_KEY, value.toString());
      }
    };

    window.addEventListener('preference-changed', handlePreferenceChange);
    return () => window.removeEventListener('preference-changed', handlePreferenceChange);
  }, []);

  const handleToggle = async () => {
    // Double protection against spam clicking
    if (isLoading || isTogglingRef.current) return;

    isTogglingRef.current = true;
    setIsLoading(true);
    const newValue = !useLocalTimezone;

    try {
      // Save to localStorage immediately for instant persistence
      storage.setItem(STORAGE_KEY, newValue.toString());

      // Update state
      setUseLocalTimezone(newValue);

      // Save to server and wait for completion
      await preferencesService.setPreference('useLocalTimezone', newValue);

      // Ensure minimum loading time for visual feedback (300ms)
      // This prevents rapid toggling and ensures updates propagate
      await new Promise(resolve => setTimeout(resolve, 300));
    } catch (error) {
      console.error('Failed to update timezone preference:', error);
      // Revert on error
      setUseLocalTimezone(!newValue);
      storage.setItem(STORAGE_KEY, (!newValue).toString());
    } finally {
      setIsLoading(false);
      isTogglingRef.current = false;
    }
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
