export type CacheOutcome = 'Current' | 'Outdated' | 'Unknown';

export type CacheReason =
  | 'UnsupportedDaemon'
  | 'Disconnected'
  | 'ConnectionChanged'
  | 'StatusUnavailable'
  | 'InvalidResult'
  | 'InvalidAppId'
  | 'MissingApp'
  | 'LinkedDepotUnavailable'
  | 'ManifestUnavailable'
  | 'UnsupportedOs'
  | 'NoContent'
  | 'NoCacheEvidence'
  | 'InspectionFailed'
  | 'DeadlineReached'
  | 'AuthenticationRequired';

export interface AppCacheStatus {
  appId: string;
  name: string;
  isUpToDate: boolean | null;
  outcome: CacheOutcome | null;
  reason: CacheReason | null;
  downloadSize: number;
}

export type CacheAppResponse = Omit<AppCacheStatus, 'appId'> & {
  appId: string | number;
};

export const CACHE_REASON_KEYS: Record<CacheReason, string> = {
  UnsupportedDaemon: 'prefill.gameSelection.cacheReasons.unsupportedDaemon',
  Disconnected: 'prefill.gameSelection.cacheReasons.disconnected',
  ConnectionChanged: 'prefill.gameSelection.cacheReasons.connectionChanged',
  StatusUnavailable: 'prefill.gameSelection.cacheReasons.statusUnavailable',
  InvalidResult: 'prefill.gameSelection.cacheReasons.invalidResult',
  InvalidAppId: 'prefill.gameSelection.cacheReasons.invalidAppId',
  MissingApp: 'prefill.gameSelection.cacheReasons.missingApp',
  LinkedDepotUnavailable: 'prefill.gameSelection.cacheReasons.linkedDepotUnavailable',
  ManifestUnavailable: 'prefill.gameSelection.cacheReasons.manifestUnavailable',
  UnsupportedOs: 'prefill.gameSelection.cacheReasons.unsupportedOs',
  NoContent: 'prefill.gameSelection.cacheReasons.noContent',
  NoCacheEvidence: 'prefill.gameSelection.cacheReasons.noCacheEvidence',
  InspectionFailed: 'prefill.gameSelection.cacheReasons.inspectionFailed',
  DeadlineReached: 'prefill.gameSelection.cacheReasons.deadlineReached',
  AuthenticationRequired: 'prefill.gameSelection.cacheReasons.authenticationRequired'
};

export function completeCacheApps(
  cachedAppIds: readonly string[],
  apps: readonly CacheAppResponse[],
  games: readonly { appId: string; name: string }[]
): AppCacheStatus[] {
  const appById = new Map(apps.map((app) => [String(app.appId).toLowerCase(), app]));
  const gameById = new Map(games.map((game) => [game.appId.toLowerCase(), game]));

  return cachedAppIds.map((appId) => {
    const key = appId.toLowerCase();
    const app = appById.get(key);
    if (!app) {
      return {
        appId,
        name: gameById.get(key)?.name ?? '',
        isUpToDate: null,
        outcome: 'Unknown',
        reason: 'InvalidResult',
        downloadSize: 0
      };
    }

    const outcome = app.outcome === null ? 'Unknown' : app.outcome;
    let reason = app.reason;
    if (outcome === 'Unknown' && reason === null) reason = 'InvalidResult';
    return { ...app, appId, outcome, reason };
  });
}

export function groupCacheApps(apps: readonly AppCacheStatus[]): {
  current: string[];
  outdated: string[];
  unknown: string[];
} {
  const current: string[] = [];
  const outdated: string[] = [];
  const unknown: string[] = [];
  for (const app of apps) {
    if (app.outcome === 'Current') current.push(app.appId);
    else if (app.outcome === 'Outdated') outdated.push(app.appId);
    else unknown.push(app.appId);
  }
  return { current, outdated, unknown };
}

export function markCacheAppsUnknown(
  cachedAppIds: readonly string[],
  apps: readonly AppCacheStatus[],
  games: readonly { appId: string; name: string }[],
  reason: CacheReason
): AppCacheStatus[] {
  return completeCacheApps(cachedAppIds, apps, games).map((app) => ({
    ...app,
    isUpToDate: null,
    outcome: 'Unknown',
    reason
  }));
}
