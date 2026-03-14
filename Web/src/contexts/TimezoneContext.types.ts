import { createContext } from 'react';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

interface TimezoneContextType {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  refreshKey: number;
  setPendingTimeSetting: (value: TimeSettingValue | null) => void;
  forceRefresh: () => void;
}

export const TimezoneContext = createContext<TimezoneContextType>({
  useLocalTimezone: false,
  use24HourFormat: true,
  refreshKey: 0,
  setPendingTimeSetting: () => {
    /* noop */
  },
  forceRefresh: () => {
    /* noop */
  }
});
