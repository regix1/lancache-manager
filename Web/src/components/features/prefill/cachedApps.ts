export function resolveCachedAppIds(
  previous: readonly string[],
  verified: readonly string[],
  unknown: readonly string[]
): string[] {
  const unresolved = new Set(unknown);
  return [...new Set([...verified, ...previous.filter((id) => unresolved.has(id))])];
}
