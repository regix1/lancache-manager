interface CacheEntry<T> {
  promise: Promise<T>;
  timestamp: number;
  controller: AbortController;
}

const cache = new Map<string, CacheEntry<unknown>>();
const DEFAULT_TTL_MS = 30_000;

/**
 * Returns a cached in-flight promise if its entry is still fresh; otherwise
 * starts a new fetch. All concurrent callers for the same key share one request.
 */
export function getOrFetch<T>(
  cacheKey: string,
  fetcher: (signal: AbortSignal) => Promise<T>,
  ttlMs: number = DEFAULT_TTL_MS
): Promise<T> {
  const existing = cache.get(cacheKey);
  if (existing && Date.now() - existing.timestamp < ttlMs) {
    return existing.promise as Promise<T>;
  }
  if (existing) {
    existing.controller.abort();
    cache.delete(cacheKey);
  }
  const controller = new AbortController();
  const promise = fetcher(controller.signal);
  const entry: CacheEntry<unknown> = {
    promise: promise as Promise<unknown>,
    timestamp: Date.now(),
    controller
  };
  cache.set(cacheKey, entry);
  promise.catch(() => {
    // Prune failed entries so the next call can retry.
    const current = cache.get(cacheKey);
    if (current && current.promise === (promise as Promise<unknown>)) {
      cache.delete(cacheKey);
    }
  });
  return promise;
}

/**
 * Fire-and-forget prefetch. Errors are swallowed because the user has not yet
 * committed to seeing this data.
 */
export function prefetchRange<T>(
  cacheKey: string,
  fetcher: (signal: AbortSignal) => Promise<T>
): void {
  void getOrFetch(cacheKey, fetcher).catch(() => {
    // prefetch errors are non-fatal
  });
}
