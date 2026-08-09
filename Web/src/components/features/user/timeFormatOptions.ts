import type { TFunction } from 'i18next';
import { Globe, MapPin } from 'lucide-react';
import type { MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import { TIME_SETTING_VALUES, type TimeSettingValue } from '@contexts/TimezoneContext.types';

/**
 * The icon each clock face is listed with. Server and UTC times both read off a fixed clock, the
 * local ones read off the reader's own, which is what the two icons say.
 */
const clockIcons: Record<TimeSettingValue, MultiSelectOption['icon']> = {
  'server-24h': Globe,
  'server-12h': Globe,
  'local-24h': MapPin,
  'local-12h': MapPin,
  // One entry where the other two clocks are two each: UTC has no 12-hour face worth offering.
  utc: Globe
};

/** Translation key base per clock face on the guest defaults and appearance screens. */
export const guestTimeFormatKeys: Record<TimeSettingValue, string> = {
  'server-24h': 'user.guest.timeFormats.server24h',
  'server-12h': 'user.guest.timeFormats.server12h',
  'local-24h': 'user.guest.timeFormats.local24h',
  'local-12h': 'user.guest.timeFormats.local12h',
  utc: 'user.guest.timeFormats.utc'
};

/** Translation key base per clock face in the header clock picker. */
export const timezoneSelectorKeys: Record<TimeSettingValue, string> = {
  'server-24h': 'common.timezoneSelector.options.server24',
  'server-12h': 'common.timezoneSelector.options.server12',
  'local-24h': 'common.timezoneSelector.options.local24',
  'local-12h': 'common.timezoneSelector.options.local12',
  utc: 'common.timezoneSelector.options.utc'
};

/**
 * The five clock faces as dropdown options, in the one order `TIME_SETTING_VALUES` fixes. Screens
 * word the same five choices differently, so each passes its own key bases; the values, order and
 * icons stay shared, which is what keeps a sixth clock from reaching one screen and not the other.
 */
export const getTimeFormatOptions = (
  t: TFunction,
  keys: Record<TimeSettingValue, string>
): MultiSelectOption[] =>
  TIME_SETTING_VALUES.map((value) => ({
    value,
    label: t(`${keys[value]}.label`),
    description: t(`${keys[value]}.description`),
    icon: clockIcons[value]
  }));
