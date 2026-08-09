import { useMemo } from 'react';
import { formatTimestamp } from '@utils/dateTimeFormat';
import { useReaderClock } from '@hooks/useReaderClock';

/**
 * Hook that formats a date/time and automatically re-renders when timezone or time format preference changes
 *
 * This is how a component should render an absolute timestamp. Reaching for formatTimestamp
 * directly means assembling the settings by hand, and a component that gets that wrong renders the
 * clock the reader has just switched away from and then stops changing.
 */
export const useFormattedDateTime = (
  dateString: string | Date | null | undefined,
  forceYear = false
): string => {
  const clock = useReaderClock();

  return useMemo(() => {
    return formatTimestamp(dateString, { ...clock, forceYear });
  }, [clock, dateString, forceYear]);
};
