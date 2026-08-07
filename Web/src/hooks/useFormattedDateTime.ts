import { useMemo } from 'react';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import { useTimezone } from '@contexts/useTimezone';

/**
 * Hook that formats a date/time and automatically re-renders when timezone or time format preference changes
 * Use this instead of formatDateTime() directly in components to get live preference updates
 */
export const useFormattedDateTime = (
  dateString: string | Date | null | undefined,
  forceYear = false
): string => {
  const { useLocalTimezone, use24HourFormat, refreshKey } = useTimezone();

  return useMemo(() => {
    const settings: TimestampSettings = {
      useLocalTimezone,
      use24Hour: use24HourFormat,
      forceYear
    };
    return formatTimestamp(dateString, settings);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- refreshKey intentionally forces recomputation
  }, [dateString, forceYear, useLocalTimezone, use24HourFormat, refreshKey]);
};
