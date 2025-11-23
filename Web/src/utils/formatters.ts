import { FILE_SIZE_UNITS } from './constants';
import { getServerTimezone } from './timezone';
import { getGlobalTimezonePreference } from './timezonePreference';

/**
 * Format date/time to localized string
 * NOTE: This is for non-React contexts (CSV exports, etc.)
 * For React components, use the useFormattedDateTime hook instead
 */
export function formatDateTime(dateString: string | Date | null | undefined): string {
  if (!dateString) return 'N/A';

  try {
    const date = typeof dateString === 'string' ? new Date(dateString) : dateString;

    if (isNaN(date.getTime())) return 'Invalid Date';

    // Determine which timezone to use based on preference
    let targetTimezone: string | undefined;
    let isUTC = false;

    if (getGlobalTimezonePreference()) {
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
}

/**
 * Format bytes to human-readable string
 */
export function formatBytes(bytes: number, decimals = 2): string {
  if (bytes === 0) return '0 B';
  if (!bytes || bytes < 0) return 'N/A';

  const k = 1024;
  const dm = decimals < 0 ? 0 : decimals;

  const i = Math.floor(Math.log(bytes) / Math.log(k));
  const unit = FILE_SIZE_UNITS[i] || 'B';

  return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + ' ' + unit;
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
