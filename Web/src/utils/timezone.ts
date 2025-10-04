// Server timezone storage
let serverTimezone: string | null = null;

export function setServerTimezone(tz: string) {
  serverTimezone = tz;
}

export function getServerTimezone(): string {
  return serverTimezone || Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * Convert UTC date to server timezone for display
 */
export function convertUTCToServerTimezone(utcDate: Date): Date {
  if (!serverTimezone) {
    return utcDate;
  }

  // Get the time in server timezone
  const serverTimeString = utcDate.toLocaleString('en-US', {
    timeZone: serverTimezone
  });

  return new Date(serverTimeString);
}
