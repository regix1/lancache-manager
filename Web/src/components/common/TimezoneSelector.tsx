import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Clock, Globe, MapPin } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import preferencesService from '@services/preferences.service';
import { useSessionPreferences } from '@contexts/SessionPreferencesContext';
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
  const { currentPreferences } = useSessionPreferences();
  const { useLocalTimezone, use24HourFormat, setPendingTimeSetting } = useTimezone();
  const { authMode } = useAuth();
  const { prefs: guestDefaults, loading: loadingDefaults } = useDefaultGuestPreferences();
  const [tick, setTick] = useState(0);
  const hasAutoSwitched = useRef(false);

  const isGuest = authMode === 'guest';

  // Get allowed time formats from SessionPreferencesContext
  const userAllowedFormats = currentPreferences?.allowedTimeFormats || null;

  const getAdminDefault = (): TimeSettingValue => {
    const isLocal = guestDefaults.useLocalTimezone;
    const is24h = guestDefaults.use24HourFormat;

    if (isLocal && is24h) return 'local-24h';
    if (isLocal && !is24h) return 'local-12h';
    if (!isLocal && is24h) return 'server-24h';
    return 'server-12h';
  };

  useEffect(() => {
    const interval = setInterval(() => {
      setTick((t) => t + 1);
    }, 1000);
    return () => clearInterval(interval);
  }, []);

  const getEffectiveAllowedFormats = (): string[] => {
    if (userAllowedFormats && userAllowedFormats.length > 0) {
      return userAllowedFormats;
    }
    if (
      isGuest &&
      guestDefaults.allowedTimeFormats &&
      guestDefaults.allowedTimeFormats.length > 0
    ) {
      return guestDefaults.allowedTimeFormats;
    }
    return [];
  };

  useEffect(() => {
    if (!isGuest || loadingDefaults || hasAutoSwitched.current) return;

    const currentValue = getCurrentValueInternal();
    const allowedFormats = getEffectiveAllowedFormats();

    if (allowedFormats.length > 0 && !allowedFormats.includes(currentValue)) {
      const adminDefault = getAdminDefault();
      const targetFormat = allowedFormats.includes(adminDefault)
        ? adminDefault
        : (allowedFormats[0] as TimeSettingValue);

      hasAutoSwitched.current = true;
      handleTimeSettingChange(targetFormat);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isGuest, loadingDefaults, guestDefaults.allowedTimeFormats, userAllowedFormats]);

  const getCurrentValueInternal = (): TimeSettingValue => {
    if (useLocalTimezone) {
      return use24HourFormat ? 'local-24h' : 'local-12h';
    } else {
      return use24HourFormat ? 'server-24h' : 'server-12h';
    }
  };

  const computeTime = () => {
    const timezone = getEffectiveTimezone(useLocalTimezone);
    const { hour: hours, minute: minutes } = getTimeInTimezone(new Date(), timezone);

    if (use24HourFormat) {
      return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}`;
    } else {
      const period =
        hours >= 12 ? t('common.timezoneSelector.pm') : t('common.timezoneSelector.am');
      const displayHour = hours % 12 || 12;
      return `${displayHour}:${minutes.toString().padStart(2, '0')} ${period}`;
    }
  };

  void tick;
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

  const adminDefault = isGuest ? getAdminDefault() : null;
  const effectiveAllowedFormats = getEffectiveAllowedFormats();

  return (
    <EnhancedDropdown
      options={options.map((opt) => {
        const isAllowed =
          effectiveAllowedFormats.length === 0 || effectiveAllowedFormats.includes(opt.value);
        return {
          ...opt,
          label:
            opt.value === adminDefault
              ? `${opt.label} (${t('common.timezoneSelector.defaultLabel')})`
              : opt.label,
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
