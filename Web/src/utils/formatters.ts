import i18n from '@/i18n';
import { FILE_SIZE_UNITS } from './constants';
import { formatTimestamp, type ReaderClock } from './dateTimeFormat';

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
  if (!bytes || bytes < 0) return i18n.t('common.notAvailable');

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
  if (!bytesPerSecond || bytesPerSecond <= 0) return i18n.t('common.notAvailable');

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
 *
 * An event's ends are real instants, so they follow whichever clock the reader is on. That clock is
 * an argument because this runs inside a `.map` over events, where a hook cannot go. See
 * {@link ReaderClock} for why reading it from module state instead leaves the label stale.
 */
export function formatEventDateRange(startUtc: string, endUtc: string, clock: ReaderClock): string {
  const settings = { ...clock, forceYear: false, style: 'dateOnly' as const };
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
  if (!dateString) return i18n.t('common.notAvailable');

  try {
    const date = typeof dateString === 'string' ? new Date(dateString) : dateString;
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffSecs = Math.floor(diffMs / 1000);
    const diffMins = Math.floor(diffSecs / 60);
    const diffHours = Math.floor(diffMins / 60);
    const diffDays = Math.floor(diffHours / 24);

    if (diffDays > 0) return i18n.t('common.time.daysAgo', { count: diffDays });
    if (diffHours > 0) return i18n.t('common.time.hoursAgo', { count: diffHours });
    if (diffMins > 0) return i18n.t('common.time.minutesAgo', { count: diffMins });
    return i18n.t('common.time.justNow');
  } catch (_error) {
    return i18n.t('common.time.invalidDate');
  }
}
