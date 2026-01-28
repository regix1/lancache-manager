import { useMemo } from 'react';
import { getServerTimezone } from '@utils/timezone';
import { useTimezone } from '@contexts/TimezoneContext';
import { getGlobalAlwaysShowYearPreference } from '@utils/yearDisplayPreference';

// Track logging to avoid console spam
let lastLoggedRefreshKey = -1;

/**
 * Hook that formats a date/time and automatically re-renders when timezone or time format preference changes
 * Use this instead of formatDateTime() directly in components to get live preference updates
 */
export const useFormattedDateTime = (dateString: string | Date | null | undefined, forceYear = false): string => {
  const { useLocalTimezone, use24HourFormat, refreshKey } = useTimezone();

  return useMemo(() => {
    // Log only once per refreshKey change
    if (refreshKey !== lastLoggedRefreshKey) {
      console.log('[useFormattedDateTime] useMemo recomputing, refreshKey:', refreshKey, 'alwaysShowYear:', getGlobalAlwaysShowYearPreference());
      lastLoggedRefreshKey = refreshKey;
    }

    if (!dateString) return 'N/A';

    try {
      const date = typeof dateString === 'string' ? new Date(dateString) : dateString;

      if (isNaN(date.getTime())) return 'Invalid Date';

      // Determine which timezone to use based on preference
      let targetTimezone: string | undefined;

      if (useLocalTimezone) {
        // Use browser's local timezone (undefined = automatic)
        targetTimezone = undefined;
      } else {
        // Use server timezone from config
        targetTimezone = getServerTimezone();
      }

      // Check if year should be displayed
      // Include year if: forceYear is true, OR user preference is to always show year, OR date is from different year
      const now = new Date();
      const alwaysShowYear = getGlobalAlwaysShowYearPreference();
      const includeYear = forceYear || alwaysShowYear || date.getFullYear() !== now.getFullYear();

      // Build format options
      const formatOptions: Intl.DateTimeFormatOptions = {
        timeZone: targetTimezone,
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hour12: !use24HourFormat
      };

      // Add year if needed
      if (includeYear) {
        formatOptions.year = 'numeric';
      }

      // Convert to target timezone for display
      try {
        return date.toLocaleString(undefined, formatOptions);
      } catch (tzError) {
        // Timezone invalid, fall back to UTC
        console.warn(`Invalid timezone "${targetTimezone}", falling back to UTC`);
        return date.toLocaleString(undefined, {
          ...formatOptions,
          timeZone: 'UTC'
        });
      }
    } catch (error) {
      return 'Invalid Date';
    }
  }, [dateString, forceYear, useLocalTimezone, use24HourFormat, refreshKey]); // Re-compute when date or preferences change
};
