import { FILE_SIZE_UNITS } from './constants';
import { formatTimestamp, type TimestampSettings, type TimestampStyle } from './dateTimeFormat';
import { getGlobalTimezonePreference } from './timezonePreference';
import { getGlobal24HourPreference } from './timeFormatPreference';

/**
 * Read the display preferences from module state.
 * This path is not reactive: a component that renders through it keeps its old string until
 * something else re-renders it. Components should use the useFormattedDateTime hook instead.
 */
function currentTimestampSettings(forceYear: boolean, style: TimestampStyle): TimestampSettings {
  return {
    useLocalTimezone: getGlobalTimezonePreference(),
    use24Hour: getGlobal24HourPreference(),
    forceYear,
    style
  };
}

/**
 * Format date/time to localized string
 * NOTE: This is for non-React contexts (CSV exports, etc.)
 * For React components, use the useFormattedDateTime hook instead
 *
 * @param dateString - The date to format
 * @param forceYear - Kept for callers that ask for it; every style now carries the year anyway
 * @param style - Defaults to the standard timestamp; pass 'log' where seconds are real resolution
 */
export function formatDateTime(
  dateString: string | Date | null | undefined,
  forceYear = false,
  style: TimestampStyle = 'stamp'
): string {
  return formatTimestamp(dateString, currentTimestampSettings(forceYear, style));
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
 * Format an event date range as a localized string (e.g., "Jan 5, 2026" or "Jan 5, 2026 - Jan 7, 2026")
 */
export function formatEventDateRange(startUtc: string, endUtc: string): string {
  const settings = currentTimestampSettings(false, 'dateOnly');
  const startStr = formatTimestamp(startUtc, settings);
  const endStr = formatTimestamp(endUtc, settings);
  return startStr === endStr ? startStr : `${startStr} - ${endStr}`;
}

/**
 * Format a number with locale-aware thousand separators (e.g., 1234 -> "1,234")
 */
export function formatCount(value: number | null | undefined): string {
  if (value === null || value === undefined || isNaN(value)) return '0';
  return value.toLocaleString();
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
  } catch (_error) {
    return 'Invalid Date';
  }
}
