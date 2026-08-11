import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Clock } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import {
  getTimeFormatOptions,
  timezoneSelectorKeys
} from '@components/features/user/timeFormatOptions';
import preferencesService from '@services/preferences.service';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { useTimezone } from '@contexts/useTimezone';
import { useAuth } from '@contexts/useAuth';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { getEffectiveTimezone, getTimeInTimezone } from '@utils/timezone';
import { clockFromTimeSetting, timeSettingFromClock } from '@utils/pendingPreferences';
import type { TimeSettingValue } from '@contexts/TimezoneContext.types';

interface TimezoneSelectorProps {
  iconOnly?: boolean;
}

const TimezoneSelector: React.FC<TimezoneSelectorProps> = ({ iconOnly = false }) => {
  const { t } = useTranslation();
  const { currentPreferences } = useSessionPreferences();
  const {
    useLocalTimezone,
    useUtcTimezone,
    use24HourFormat,
    setPendingTimeSetting,
    dropPendingTimeSetting
  } = useTimezone();
  const { authMode } = useAuth();
  const { prefs: guestDefaults, loading: loadingDefaults } = useDefaultGuestPreferences();
  const { notifyError } = useErrorHandler();
  const [tick, setTick] = useState(0);
  const hasAutoSwitched = useRef(false);

  const isGuest = authMode === 'guest';

  // Get allowed time formats from SessionPreferencesContext
  const userAllowedFormats = currentPreferences?.allowedTimeFormats || null;

  const getAdminDefault = (): TimeSettingValue => timeSettingFromClock(guestDefaults);

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

    const currentValue = getCurrentValue();
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

  const computeTime = () => {
    const timezone = getEffectiveTimezone(useLocalTimezone, useUtcTimezone);
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

  const getCurrentValue = (): TimeSettingValue =>
    timeSettingFromClock({ useUtcTimezone, useLocalTimezone, use24HourFormat });

  const handleTimeSettingChange = async (value: string) => {
    const typedValue = value as TimeSettingValue;
    setPendingTimeSetting(typedValue);

    // Takes back only this click's own optimistic values. A save can fail while the click after it is
    // already on the wire, and clearing the three keys outright would drop the newer click's values
    // too.
    const reportFailure = (error?: unknown): void => {
      dropPendingTimeSetting(typedValue);
      notifyError(t('common.timezoneSelector.errors.updateFailed'), error, {
        logLabel: 'Failed to update time settings'
      });
    };

    try {
      const saved = await preferencesService.setClockPreferences(clockFromTimeSetting(typedValue));

      // A rejected promise is not what a rejected save looks like here: the service reports failure
      // by resolving false. Left unread, the optimistic value simply expires and the control slides
      // back to the old choice with nothing said.
      if (!saved) {
        reportFailure();
      }
    } catch (error) {
      reportFailure(error);
    }
  };

  const options = getTimeFormatOptions(t, timezoneSelectorKeys);

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
