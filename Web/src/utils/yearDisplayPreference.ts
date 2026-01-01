/**
 * Global year display preference state
 * This allows formatDateTime to access the preference without circular dependencies
 */

let alwaysShowYearGlobal = false;

export function setGlobalAlwaysShowYearPreference(alwaysShow: boolean): void {
  alwaysShowYearGlobal = alwaysShow;
}

export function getGlobalAlwaysShowYearPreference(): boolean {
  return alwaysShowYearGlobal;
}
