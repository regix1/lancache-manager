/**
 * Global timezone preference state
 * This allows formatDateTime to access the preference without circular dependencies
 */

let useLocalTimezoneGlobal = false;

export function setGlobalTimezonePreference(useLocal: boolean): void {
  useLocalTimezoneGlobal = useLocal;
}

export function getGlobalTimezonePreference(): boolean {
  return useLocalTimezoneGlobal;
}
