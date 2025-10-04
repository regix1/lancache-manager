import { FILE_SIZE_UNITS } from './constants';
import { getServerTimezone } from './timezone';

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
 * Format date/time to locale string using server timezone
 * Database stores UTC, displays in server's configured timezone (from docker-compose TZ)
 */
export function formatDateTime(dateString: string | Date | null | undefined): string {
  if (!dateString) return 'N/A';

  try {
    const date = typeof dateString === 'string' ? new Date(dateString) : dateString;

    if (isNaN(date.getTime())) return 'Invalid Date';

    // Get server timezone from config (set on app startup)
    const serverTimezone = getServerTimezone();

    // Use 24-hour format for UTC, otherwise let locale decide
    const isUTC = serverTimezone === 'UTC';

    // Display in server's timezone
    return date.toLocaleString(undefined, {
      timeZone: serverTimezone,
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: isUTC ? false : undefined // undefined = use locale default
    });
  } catch (error) {
    return 'Invalid Date';
  }
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

/**
 * Format duration from milliseconds
 */
export function formatDuration(ms: number): string {
  if (!ms || ms < 0) return 'N/A';

  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);

  if (days > 0) {
    return `${days}d ${hours % 24}h`;
  } else if (hours > 0) {
    return `${hours}h ${minutes % 60}m`;
  } else if (minutes > 0) {
    return `${minutes}m ${seconds % 60}s`;
  } else {
    return `${seconds}s`;
  }
}

/**
 * Format number with commas
 */
export function formatNumber(num: number): string {
  if (num === null || num === undefined || isNaN(num)) return '0';
  return num.toLocaleString('en-US');
}

/**
 * Format IP address for display
 */
export function formatIpAddress(ip: string): string {
  if (!ip) return 'Unknown';
  if (ip === '127.0.0.1' || ip === 'localhost') return 'Local';
  return ip;
}

/**
 * Get cache hit color based on percentage
 */
export function getCacheHitColor(percent: number): string {
  if (percent >= 75) return 'green';
  if (percent >= 50) return 'blue';
  if (percent >= 25) return 'yellow';
  return 'orange';
}

/**
 * Truncate string with ellipsis
 */
export function truncateString(str: string, maxLength = 50): string {
  if (!str) return '';
  if (str.length <= maxLength) return str;
  return str.substring(0, maxLength - 3) + '...';
}
