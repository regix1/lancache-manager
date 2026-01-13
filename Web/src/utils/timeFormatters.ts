/**
 * Type guard to check if an object has a totalSeconds property
 */
function hasTotalSeconds(obj: object): obj is { totalSeconds: number } {
  return 'totalSeconds' in obj && typeof (obj as { totalSeconds: unknown }).totalSeconds === 'number';
}

/**
 * Type guard to check if an object has a totalHours property
 */
function hasTotalHours(obj: object): obj is { totalHours: number } {
  return 'totalHours' in obj && typeof (obj as { totalHours: unknown }).totalHours === 'number';
}

/**
 * Type guard to check if an object has hours/minutes/seconds properties
 */
function hasHoursMinutesSeconds(obj: object): obj is { hours: number; minutes: number; seconds: number } {
  return 'hours' in obj && 'minutes' in obj && 'seconds' in obj;
}

/**
 * Represents a time value from the API which can be:
 * - A number (seconds)
 * - A TimeSpan string ("HH:MM:SS" or "D.HH:MM:SS")
 * - A TimeSpan object with totalSeconds, totalHours, or hours/minutes/seconds properties
 */
type TimeValue =
  | number
  | string
  | null
  | undefined
  | { totalSeconds: number }
  | { totalHours: number }
  | { hours: number; minutes: number; seconds: number };

/**
 * Formats a next crawl time value (which can be a number, string, or object) into a human-readable string
 * @param nextCrawlIn The next crawl time value from the API (can be seconds, TimeSpan string, or TimeSpan object)
 * @param isRunning Whether a crawl is currently running
 * @param fullScanRequired Whether a full scan is required (for incremental schedules)
 * @param crawlIncrementalMode Whether the schedule is in incremental mode
 * @returns A formatted time string like "2h 30m", "Due now", "Running now", etc.
 */
export function formatNextCrawlTime(
  nextCrawlIn: TimeValue,
  isRunning = false,
  fullScanRequired = false,
  crawlIncrementalMode: boolean | string = true
): string {
  if (isRunning) {
    return 'Running now';
  }

  if (nextCrawlIn === undefined || nextCrawlIn === null) {
    return 'Loading...';
  }

  let totalSeconds: number;

  // Handle different formats from the API
  if (typeof nextCrawlIn === 'object' && nextCrawlIn !== null && hasTotalSeconds(nextCrawlIn)) {
    // Object with totalSeconds property
    totalSeconds = nextCrawlIn.totalSeconds;
  } else if (typeof nextCrawlIn === 'object' && nextCrawlIn !== null && hasTotalHours(nextCrawlIn)) {
    // Object with totalHours property
    totalSeconds = nextCrawlIn.totalHours * 3600;
  } else if (typeof nextCrawlIn === 'object' && nextCrawlIn !== null && hasHoursMinutesSeconds(nextCrawlIn)) {
    // Object with {hours, minutes, seconds} properties
    totalSeconds = nextCrawlIn.hours * 3600 + nextCrawlIn.minutes * 60 + nextCrawlIn.seconds;
  } else if (typeof nextCrawlIn === 'string') {
    // TimeSpan string format: "HH:MM:SS" or "D.HH:MM:SS"
    const parts = nextCrawlIn.split(':');
    if (parts.length >= 3) {
      const dayHourPart = parts[0].split('.');
      let hours = 0;
      let days = 0;

      if (dayHourPart.length === 2) {
        days = parseInt(dayHourPart[0]) || 0;
        hours = parseInt(dayHourPart[1]) || 0;
      } else {
        hours = parseInt(parts[0]) || 0;
      }

      const minutes = parseInt(parts[1]) || 0;
      const seconds = parseInt(parts[2]) || 0;
      totalSeconds = days * 86400 + hours * 3600 + minutes * 60 + seconds;
    } else {
      return 'Loading...';
    }
  } else if (typeof nextCrawlIn === 'number') {
    // Number (assumed to be seconds)
    totalSeconds = nextCrawlIn;
  } else {
    return 'Loading...';
  }

  // Validate the result
  if (!isFinite(totalSeconds) || isNaN(totalSeconds)) {
    return 'Loading...';
  }

  // Handle "due now" cases
  if (totalSeconds <= 0) {
    // Check if it's incremental mode (true or not "github")
    const isIncrementalMode = typeof crawlIncrementalMode === 'boolean' ? crawlIncrementalMode : crawlIncrementalMode !== 'github';
    if (fullScanRequired && isIncrementalMode) {
      return 'Due now (Full scan required)';
    }
    return 'Due now';
  }

  // Format the time based on duration
  const totalHours = totalSeconds / 3600;

  if (totalHours > 24) {
    const days = Math.floor(totalHours / 24);
    const hours = Math.floor(totalHours % 24);
    return hours > 0 ? `${days}d ${hours}h` : `${days} days`;
  }

  const hours = Math.floor(totalHours);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
}

/**
 * Converts a time value to total seconds
 * @param timeValue The time value (can be seconds, string, or object)
 * @returns Total seconds as a number
 */
export function toTotalSeconds(timeValue: TimeValue): number {
  if (typeof timeValue === 'object' && timeValue !== null && hasTotalSeconds(timeValue)) {
    return timeValue.totalSeconds;
  } else if (typeof timeValue === 'object' && timeValue !== null && hasTotalHours(timeValue)) {
    return timeValue.totalHours * 3600;
  } else if (typeof timeValue === 'object' && timeValue !== null && hasHoursMinutesSeconds(timeValue)) {
    // Object with {hours, minutes, seconds} properties
    return timeValue.hours * 3600 + timeValue.minutes * 60 + timeValue.seconds;
  } else if (typeof timeValue === 'string') {
    const parts = timeValue.split(':');
    if (parts.length >= 3) {
      const dayHourPart = parts[0].split('.');
      let hours = 0;
      let days = 0;

      if (dayHourPart.length === 2) {
        days = parseInt(dayHourPart[0]) || 0;
        hours = parseInt(dayHourPart[1]) || 0;
      } else {
        hours = parseInt(parts[0]) || 0;
      }

      const minutes = parseInt(parts[1]) || 0;
      const seconds = parseInt(parts[2]) || 0;
      return days * 86400 + hours * 3600 + minutes * 60 + seconds;
    }
  } else if (typeof timeValue === 'number') {
    return timeValue;
  }
  return 0;
}
