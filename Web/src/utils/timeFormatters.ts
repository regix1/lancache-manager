/**
 * Formats a next crawl time value (which can be a number, string, or object) into a human-readable string
 * @param nextCrawlIn The next crawl time value from the API (can be seconds, TimeSpan string, or TimeSpan object)
 * @param isRunning Whether a crawl is currently running
 * @param fullScanRequired Whether a full scan is required (for incremental schedules)
 * @param crawlIncrementalMode Whether the schedule is in incremental mode
 * @returns A formatted time string like "2h 30m", "Due now", "Running now", etc.
 */
export function formatNextCrawlTime(
  nextCrawlIn: any,
  isRunning: boolean = false,
  fullScanRequired: boolean = false,
  crawlIncrementalMode: boolean = true
): string {
  if (isRunning) {
    return 'Running now';
  }

  if (nextCrawlIn === undefined || nextCrawlIn === null) {
    return 'Loading...';
  }

  let totalSeconds: number;

  // Handle different formats from the API
  if (typeof nextCrawlIn === 'object' && nextCrawlIn.totalSeconds !== undefined) {
    // Object with totalSeconds property
    totalSeconds = nextCrawlIn.totalSeconds;
  } else if (typeof nextCrawlIn === 'object' && nextCrawlIn.totalHours !== undefined) {
    // Object with totalHours property
    totalSeconds = nextCrawlIn.totalHours * 3600;
  } else if (typeof nextCrawlIn === 'object' && nextCrawlIn.hours !== undefined) {
    // Object with {hours, minutes, seconds} properties
    totalSeconds = (nextCrawlIn.hours * 3600) + (nextCrawlIn.minutes * 60) + nextCrawlIn.seconds;
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
      totalSeconds = (days * 86400) + (hours * 3600) + (minutes * 60) + seconds;
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
    if (fullScanRequired && crawlIncrementalMode) {
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
 * Converts a time value to total hours
 * @param timeValue The time value (can be seconds, string, or object)
 * @returns Total hours as a number
 */
export function toTotalHours(timeValue: any): number {
  if (typeof timeValue === 'object' && timeValue?.totalHours !== undefined) {
    return timeValue.totalHours;
  } else if (typeof timeValue === 'string') {
    const parts = timeValue.split(':');
    if (parts.length >= 2) {
      const hours = parseInt(parts[0]) || 0;
      const minutes = parseInt(parts[1]) || 0;
      return hours + (minutes / 60);
    }
  } else if (typeof timeValue === 'number') {
    // Assume seconds
    return timeValue / 3600;
  }
  return 0;
}

/**
 * Converts a time value to total seconds
 * @param timeValue The time value (can be seconds, string, or object)
 * @returns Total seconds as a number
 */
export function toTotalSeconds(timeValue: any): number {
  if (typeof timeValue === 'object' && timeValue?.totalSeconds !== undefined) {
    return timeValue.totalSeconds;
  } else if (typeof timeValue === 'object' && timeValue?.totalHours !== undefined) {
    return timeValue.totalHours * 3600;
  } else if (typeof timeValue === 'object' && timeValue?.hours !== undefined) {
    // Object with {hours, minutes, seconds} properties
    return (timeValue.hours * 3600) + (timeValue.minutes * 60) + timeValue.seconds;
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
      return (days * 86400) + (hours * 3600) + (minutes * 60) + seconds;
    }
  } else if (typeof timeValue === 'number') {
    return timeValue;
  }
  return 0;
}
