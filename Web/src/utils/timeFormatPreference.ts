/**
 * Global 24-hour format preference state
 * This allows formatDateTime to access the preference without circular dependencies
 */

let use24HourFormatGlobal = true; // Default to 24-hour format

export function setGlobal24HourPreference(use24Hour: boolean): void {
  use24HourFormatGlobal = use24Hour;
}

export function getGlobal24HourPreference(): boolean {
  return use24HourFormatGlobal;
}
