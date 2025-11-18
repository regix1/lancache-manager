import { useMemo } from 'react';
import { getServerTimezone } from '@utils/timezone';
import { useTimezone } from '@contexts/TimezoneContext';

/**
 * Hook that formats a date/time and automatically re-renders when timezone preference changes
 * Use this instead of formatDateTime() directly in components to get live timezone updates
 */
export const useFormattedDateTime = (dateString: string | Date | null | undefined): string => {
  const { useLocalTimezone, refreshKey } = useTimezone();

  return useMemo(() => {
    if (!dateString) return 'N/A';

    try {
      const date = typeof dateString === 'string' ? new Date(dateString) : dateString;

      if (isNaN(date.getTime())) return 'Invalid Date';

      // Determine which timezone to use based on preference
      let targetTimezone: string | undefined;
      let isUTC = false;

      if (useLocalTimezone) {
        // Use browser's local timezone (undefined = automatic)
        targetTimezone = undefined;
      } else {
        // Use server timezone from config
        targetTimezone = getServerTimezone();
        isUTC = targetTimezone === 'UTC';
      }

      // Convert to target timezone for display
      try {
        return date.toLocaleString(undefined, {
          timeZone: targetTimezone,
          month: 'short',
          day: 'numeric',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
          hour12: isUTC ? false : undefined
        });
      } catch (tzError) {
        // Timezone invalid, fall back to UTC
        console.warn(`Invalid timezone "${targetTimezone}", falling back to UTC`);
        return date.toLocaleString(undefined, {
          timeZone: 'UTC',
          month: 'short',
          day: 'numeric',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
          hour12: false
        });
      }
    } catch (error) {
      return 'Invalid Date';
    }
  }, [dateString, useLocalTimezone, refreshKey]); // Re-compute when date or timezone changes
};
