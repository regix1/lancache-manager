// Server timezone storage
let serverTimezone: string | null = null;

export function setServerTimezone(tz: string) {
  serverTimezone = tz;
}

export function getServerTimezone(): string {
  return serverTimezone || Intl.DateTimeFormat().resolvedOptions().timeZone;
}
