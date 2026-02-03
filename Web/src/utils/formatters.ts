import { FILE_SIZE_UNITS } from './constants';
import { getServerTimezone } from './timezone';
import { getGlobalTimezonePreference } from './timezonePreference';
import { getGlobal24HourPreference } from './timeFormatPreference';
import { getGlobalAlwaysShowYearPreference } from './yearDisplayPreference';

/**
 * Format date/time to localized string
 * NOTE: This is for non-React contexts (CSV exports, etc.)
 * For React components, use the useFormattedDateTime hook instead
 *
 * @param dateString - The date to format
 * @param forceYear - If true, always include the year in the output
 */
export function formatDateTime(dateString: string | Date | null | undefined, forceYear = false): string {
  if (!dateString) return 'N/A';

  try {
    const date = typeof dateString === 'string' ? new Date(dateString) : dateString;

    if (isNaN(date.getTime())) return 'Invalid Date';

    // Determine which timezone to use based on preference
    let targetTimezone: string | undefined;

    if (getGlobalTimezonePreference()) {
      // Use browser's local timezone (undefined = automatic)
      targetTimezone = undefined;
    } else {
      // Use server timezone from config
      targetTimezone = getServerTimezone();
    }

    // Get 24-hour format preference
    const use24Hour = getGlobal24HourPreference();

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
      hour12: !use24Hour
    };

    // Add year if date is from a different year or forced
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
}

/**
 * Check if a date is from a different year than the current year
 */
export function isFromDifferentYear(dateString: string | Date | null | undefined): boolean {
  if (!dateString) return false;
  try {
    const date = typeof dateString === 'string' ? new Date(dateString) : dateString;
    if (isNaN(date.getTime())) return false;
    return date.getFullYear() !== new Date().getFullYear();
  } catch {
    return false;
  }
}

/**
 * Format bytes to human-readable string
 * @param bytes - The number of bytes to format
 * @param decimals - Number of decimal places (default: 2)
 * @param zeroLabel - What to return when bytes is 0 (default: '0 B', use '-' for tables)
 */
export function formatBytes(bytes: number, decimals = 2, zeroLabel = '0 B'): string {
  if (bytes === 0) return zeroLabel;
  if (!bytes || bytes < 0) return 'N/A';

  const k = 1024;
  const dm = decimals < 0 ? 0 : decimals;

  const i = Math.floor(Math.log(bytes) / Math.log(k));
  const unit = FILE_SIZE_UNITS[i] || 'B';

  return (bytes / Math.pow(k, i)).toFixed(dm) + ' ' + unit;
}

/**
 * Format speed (bytes per second) to human-readable string in bits
 * Network speeds are traditionally measured in bits (Mb/s), not bytes (MB/s)
 */
export function formatSpeed(bytesPerSecond: number | undefined | null, decimals = 1): string {
  if (!bytesPerSecond || bytesPerSecond <= 0) return 'N/A';

  // Convert bytes to bits
  const bitsPerSecond = bytesPerSecond * 8;

  const k = 1024;
  const dm = decimals < 0 ? 0 : decimals;
  const sizes = ['b', 'Kb', 'Mb', 'Gb', 'Tb', 'Pb'];

  const i = Math.floor(Math.log(bitsPerSecond) / Math.log(k));
  const unit = sizes[i] || 'b';

  return parseFloat((bitsPerSecond / Math.pow(k, i)).toFixed(dm)) + ' ' + unit + '/s';
}

/**
 * Format speed with separated value and unit (for split display)
 * @returns Object with {value: string, unit: string}
 */
export function formatSpeedWithSeparatedUnit(
  bytesPerSecond: number | undefined | null,
  decimals = 1
): { value: string; unit: string } {
  if (!bytesPerSecond || bytesPerSecond <= 0) return { value: '0', unit: 'b/s' };

  const bitsPerSecond = bytesPerSecond * 8;
  const k = 1024;
  const dm = decimals < 0 ? 0 : decimals;
  const sizes = ['b', 'Kb', 'Mb', 'Gb', 'Tb', 'Pb'];

  const i = Math.floor(Math.log(bitsPerSecond) / Math.log(k));
  const unit = sizes[i] || 'b';
  const value = parseFloat((bitsPerSecond / Math.pow(k, i)).toFixed(dm));

  return { value: value.toString(), unit: `${unit}/s` };
}

/**
 * Format percentage
 */
export function formatPercent(value: number, decimals = 1): string {
  if (value === null || value === undefined || isNaN(value)) return '0%';
  return `${value.toFixed(decimals)}%`;
}

/**
 * Format relative time (e.g., "2 hours ago")
 */
export function formatRelativeTime(dateString: string | Date | null | undefined): string {
  if (!dateString) return 'N/A';

  try {
    const date = typeof dateString === 'string' ? new Date(dateString) : dateString;
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffSecs = Math.floor(diffMs / 1000);
    const diffMins = Math.floor(diffSecs / 60);
    const diffHours = Math.floor(diffMins / 60);
    const diffDays = Math.floor(diffHours / 24);

    if (diffDays > 0) return `${diffDays}d ago`;
    if (diffHours > 0) return `${diffHours}h ago`;
    if (diffMins > 0) return `${diffMins}m ago`;
    return 'Just now';
  } catch (error) {
    return 'Invalid Date';
  }
}
