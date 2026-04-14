import { useState, useEffect, useContext } from 'react';
import { ImageCacheContext } from '@components/common/ImageCacheContext';
import ApiService from '@services/api.service';

let availableIds = new Set<string>();
let lastCacheBuster = -1;
let fetchPromise: Promise<void> | null = null;
let initialPreloadPromise: Promise<void> | null = null;

function fetchAvailableIds(cacheBuster: number): Promise<void> {
  if (lastCacheBuster === cacheBuster && availableIds.size > 0) {
    return Promise.resolve();
  }
  if (fetchPromise && lastCacheBuster === cacheBuster) {
    return fetchPromise;
  }
  lastCacheBuster = cacheBuster;
  fetchPromise = ApiService.getAvailableGameImages()
    .then((ids: string[]) => {
      availableIds = new Set(ids);
    })
    .catch(() => {
      availableIds = new Set();
    })
    .finally(() => {
      fetchPromise = null;
    });
  return fetchPromise;
}

/**
 * Preload the available-game-images set BEFORE React first renders so the
 * module-global `availableIds` is populated on first mount. Fixes the
 * cold-cache banner flash: without this the first paint uses an empty Set,
 * `availableImages.has(...)` returns false, and `<GameImage>` is not rendered
 * until the initial fetch resolves and triggers a second commit.
 *
 * Idempotent: multiple callers share the same Promise. Failures are swallowed
 * so boot is never blocked; the hook's own useEffect will retry.
 */
export function preloadAvailableGameImages(): Promise<void> {
  if (initialPreloadPromise) {
    return initialPreloadPromise;
  }
  initialPreloadPromise = fetchAvailableIds(0).catch((err: unknown) => {
    console.warn('[useAvailableGameImages] preload failed:', err);
  });
  return initialPreloadPromise;
}

export function useAvailableGameImages(): Set<string> {
  const cacheBuster = useContext(ImageCacheContext);
  const [ids, setIds] = useState<Set<string>>(availableIds);

  useEffect(() => {
    fetchAvailableIds(cacheBuster).then(() => {
      setIds((prev: Set<string>): Set<string> => {
        if (prev.size === availableIds.size) {
          let identical = true;
          for (const id of availableIds) {
            if (!prev.has(id)) {
              identical = false;
              break;
            }
          }
          if (identical) {
            return prev;
          }
        }
        return new Set(availableIds);
      });
    });
  }, [cacheBuster]);

  return ids;
}
