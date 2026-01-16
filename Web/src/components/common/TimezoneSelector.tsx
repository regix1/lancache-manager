import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Clock, Globe, MapPin } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import preferencesService from '@services/preferences.service';
import { useTimezone } from '@contexts/TimezoneContext';
import { useAuth } from '@contexts/AuthContext';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { getEffectiveTimezone, getTimeInTimezone } from '@utils/timezone';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

interface TimezoneSelectorProps {
  iconOnly?: boolean;
}

const TimezoneSelector: React.FC<TimezoneSelectorProps> = ({ iconOnly = false }) => {
  const { t } = useTranslation();
  const { useLocalTimezone, use24HourFormat, setPendingTimeSetting } = useTimezone();
  const { authMode } = useAuth();
  const { prefs: guestDefaults, loading: loadingDefaults } = useDefaultGuestPreferences();
  const [tick, setTick] = useState(0);
  const hasAutoSwitched = useRef(false);
  const [userAllowedFormats, setUserAllowedFormats] = useState<string[] | null>(null);

  const isGuest = authMode === 'guest';

  // Load user's own allowed formats from their preferences
  useEffect(() => {
    const loadUserFormats = async () => {
      try {
        const prefs = await preferencesService.getPreferences();
        setUserAllowedFormats(prefs.allowedTimeFormats || null);
      } catch (error) {
        console.error('Failed to load user preferences:', error);
      }
    };
    loadUserFormats();

    // Listen for preference changes
    const handlePrefChange = (e: CustomEvent) => {
      if (e.detail?.key === 'allowedTimeFormats') {
        setUserAllowedFormats(e.detail.value || null);
      }
    };
    window.addEventListener('preference-changed', handlePrefChange as EventListener);
    return () => window.removeEventListener('preference-changed', handlePrefChange as EventListener);
  }, []);

  // Get the admin's default setting for guests
  const getAdminDefault = (): TimeSettingValue => {
    const isLocal = guestDefaults.useLocalTimezone;
    const is24h = guestDefaults.use24HourFormat;

    if (isLocal && is24h) return 'local-24h';
    if (isLocal && !is24h) return 'local-12h';
    if (!isLocal && is24h) return 'server-24h';
    return 'server-12h';
  };

  // Tick every second to trigger re-render for clock update
  useEffect(() => {
    const interval = setInterval(() => {
      setTick(t => t + 1);
    }, 1000);
    return () => clearInterval(interval);
  }, []);

  // Get effective allowed formats: user's own settings take priority over admin defaults
  const getEffectiveAllowedFormats = (): string[] => {
    // User's own allowed formats take priority if set
    if (userAllowedFormats && userAllowedFormats.length > 0) {
      return userAllowedFormats;
    }
    // Fall back to admin defaults for guests
    if (isGuest && guestDefaults.allowedTimeFormats && guestDefaults.allowedTimeFormats.length > 0) {
      return guestDefaults.allowedTimeFormats;
    }
    // No restrictions - all formats allowed
    return [];
  };

  // Auto-switch guest to allowed format if current selection is not allowed
  useEffect(() => {
    if (!isGuest || loadingDefaults || hasAutoSwitched.current) return;

    const currentValue = getCurrentValueInternal();
    const allowedFormats = getEffectiveAllowedFormats();

    if (allowedFormats.length > 0 && !allowedFormats.includes(currentValue)) {
      // Current selection not allowed - switch to admin default or first allowed
      const adminDefault = getAdminDefault();
      const targetFormat = allowedFormats.includes(adminDefault)
        ? adminDefault
        : (allowedFormats[0] as TimeSettingValue);

      hasAutoSwitched.current = true;
      handleTimeSettingChange(targetFormat);
    }
  }, [isGuest, loadingDefaults, guestDefaults.allowedTimeFormats, userAllowedFormats]);

  // Helper to get current value without dependency issues
  const getCurrentValueInternal = (): TimeSettingValue => {
    if (useLocalTimezone) {
      return use24HourFormat ? 'local-24h' : 'local-12h';
    } else {
      return use24HourFormat ? 'server-24h' : 'server-12h';
    }
  };

  // Compute current time based on timezone/format preferences
  const computeTime = () => {
    const timezone = getEffectiveTimezone(useLocalTimezone);
    const { hour: hours, minute: minutes } = getTimeInTimezone(new Date(), timezone);

    if (use24HourFormat) {
      return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}`;
    } else {
      const period = hours >= 12 ? t('common.timezoneSelector.pm') : t('common.timezoneSelector.am');
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
      label: t('common.timezoneSelector.options.server24.label'),
      description: t('common.timezoneSelector.options.server24.description'),
      icon: Globe
    },
    {
      value: 'server-12h',
      label: t('common.timezoneSelector.options.server12.label'),
      description: t('common.timezoneSelector.options.server12.description'),
      icon: Globe
    },
    {
      value: 'local-24h',
      label: t('common.timezoneSelector.options.local24.label'),
      description: t('common.timezoneSelector.options.local24.description'),
      icon: MapPin
    },
    {
      value: 'local-12h',
      label: t('common.timezoneSelector.options.local12.label'),
      description: t('common.timezoneSelector.options.local12.description'),
      icon: MapPin
    }
  ];

  // Mark the admin's default for guests and filter by allowed formats
  const adminDefault = isGuest ? getAdminDefault() : null;
  const effectiveAllowedFormats = getEffectiveAllowedFormats();

  return (
    <EnhancedDropdown
      options={options.map(opt => {
        const isAllowed = effectiveAllowedFormats.length === 0 || effectiveAllowedFormats.includes(opt.value);
        return {
          ...opt,
          label: opt.value === adminDefault ? `${opt.label} (${t('common.timezoneSelector.defaultLabel')})` : opt.label,
          disabled: !isAllowed
        };
      })}
      value={getCurrentValue()}
      onChange={handleTimeSettingChange}
      compactMode={true}
      customTriggerLabel={currentTime}
      iconOnly={iconOnly}
      triggerIcon={Clock}
      triggerAriaLabel={t('common.timezoneSelector.title')}
      dropdownWidth="w-72"
      alignRight={true}
      maxHeight="400px"
    />
  );
};

export default TimezoneSelector;
