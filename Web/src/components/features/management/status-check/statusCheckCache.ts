import type { StatusCheckDomainGroup, StatusCheckStatusResponse } from '@services/api.service';

// Module-level stale-while-revalidate seed cache for the Status Check tab.
//
// ManagementTab renders each section through a `switch`, so StatusCheckSection fully
// unmounts and remounts on every tab switch. Persisting the last server response in this
// module singleton lets a reopen paint the previous result IMMEDIATELY instead of blocking
// on a full-page spinner while the background GET revalidates. It lives outside React state
// on purpose so it survives unmount; a full browser reload starts empty and falls back to
// the GET (server state is the source of truth, this is only a first-paint seed).

let cachedStatus: StatusCheckStatusResponse | null = null;
let cachedDomainGroups: StatusCheckDomainGroup[] | null = null;

export function getCachedStatus(): StatusCheckStatusResponse | null {
  return cachedStatus;
}

export function setCachedStatus(status: StatusCheckStatusResponse): void {
  cachedStatus = status;
}

export function getCachedDomainGroups(): StatusCheckDomainGroup[] | null {
  return cachedDomainGroups;
}

export function setCachedDomainGroups(groups: StatusCheckDomainGroup[]): void {
  cachedDomainGroups = groups;
}
