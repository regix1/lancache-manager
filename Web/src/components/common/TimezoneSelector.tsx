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
import {
  clockFromTimeSetting,
  confirmPendingTimezone,
  timeSettingFromClock
} from '@utils/pendingPreferences';
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
    if (!isGuest || loadingDefaults || !currentPreferences || hasAutoSwitched.current) return;

    // The clock the guest has stored, not the one on screen: useTimezone still reads its own
    // defaults on the commit this load lands in. [75]
    const storedValue = timeSettingFromClock(currentPreferences);
    const allowedFormats = getEffectiveAllowedFormats();

    if (allowedFormats.length > 0 && !allowedFormats.includes(storedValue)) {
      const adminDefault = getAdminDefault();
      const targetFormat = allowedFormats.includes(adminDefault)
        ? adminDefault
        : (allowedFormats[0] as TimeSettingValue);

      hasAutoSwitched.current = true;
      handleTimeSettingChange(targetFormat);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    isGuest,
    loadingDefaults,
    currentPreferences,
    guestDefaults.allowedTimeFormats,
    userAllowedFormats
  ]);

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
    const click = setPendingTimeSetting(typedValue);

    // Takes back only this click's own optimistic values. A save can fail while the click after it is
    // already on the wire, and clearing the three keys outright would drop the newer click's values
    // too.
    const reportFailure = (message: string, error?: unknown): void => {
      dropPendingTimeSetting(click);
      notifyError(message, error, { logLabel: 'Failed to update time settings' });
    };

    try {
      const answer = await preferencesService.setClockPreferences(clockFromTimeSetting(typedValue));

      // A rejected promise is not what a refused save looks like here: the service answers by
      // resolving. A request that ran out of time is not one that was refused, and calling it a
      // failure tells the reader the clock went nowhere moments before the broadcast moves it.
      if (answer === 'saved') {
        confirmPendingTimezone(click);
      } else if (answer === 'noAnswer') {
        reportFailure(t('common.timezoneSelector.errors.updateNoAnswer'));
      } else {
        reportFailure(t('common.timezoneSelector.errors.updateFailed'));
      }
    } catch (error) {
      reportFailure(t('common.timezoneSelector.errors.updateFailed'), error);
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
          // A greyed row that still describes its clock face reads as broken. The row the reader
          // cannot pick says why instead. [73]
          description: isAllowed ? opt.description : t('common.timezoneSelector.notAllowed'),
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
