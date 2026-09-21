export function resolveCachedAppIds(
  previous: readonly string[],
  verified: readonly string[],
  unknown: readonly string[]
): string[] {
  const unresolved = new Set(unknown);
  return [...new Set([...verified, ...previous.filter((id) => unresolved.has(id))])];
}

type ResolvedCacheStatus = 'cached' | 'outdated' | 'unknown' | 'notCached';

export function resolveCacheStatus(
  appId: string,
  cached: ReadonlySet<string>,
  outdated: ReadonlySet<string>,
  unknown: ReadonlySet<string>
): ResolvedCacheStatus {
  const key = appId.toLowerCase();
  if (unknown.has(key)) return 'unknown';
  if (outdated.has(key)) return 'outdated';
  if (cached.has(key)) return 'cached';
  return 'notCached';
}
