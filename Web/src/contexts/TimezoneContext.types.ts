import { createContext } from 'react';

/**
 * The clocks the app can be read on, in the order the selector lists them. UTC is one entry where
 * the server and local clocks are two each: it has no 12-hour face worth offering, so choosing it
 * also puts the 24-hour format on.
 *
 * Anything that needs "every clock" reads this list. Spelling the five out again is how a sixth
 * clock reaches the selector but not the allowed-format defaults that decide whether it can be
 * picked. Callers that hand the list to a `string[]` field copy it with a spread.
 */
export const TIME_SETTING_VALUES = [
  'server-24h',
  'server-12h',
  'local-24h',
  'local-12h',
  'utc'
] as const;

export type TimeSettingValue = (typeof TIME_SETTING_VALUES)[number];

interface TimezoneContextType {
  useLocalTimezone: boolean;
  /** True reads every time on the UTC clock, whatever useLocalTimezone says. */
  useUtcTimezone: boolean;
  use24HourFormat: boolean;
  refreshKey: number;
  /** Hands back the number identifying the pick, for dropPendingTimeSetting. */
  setPendingTimeSetting: (value: TimeSettingValue) => number;
  /** Takes back the optimistic values of one pick when its save failed. */
  dropPendingTimeSetting: (click: number) => void;
}

export const TimezoneContext = createContext<TimezoneContextType>({
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: true,
  refreshKey: 0,
  setPendingTimeSetting: () => 0,
  dropPendingTimeSetting: () => {
    /* noop */
  }
});
