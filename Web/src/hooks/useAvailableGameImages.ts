import { useState, useEffect, useContext } from 'react';
import { ImageCacheContext } from '@components/common/ImageCacheContext';
import ApiService from '@services/api.service';

/**
 * How long a loaded id set is treated as current for a component that mounts at the cache buster it
 * was loaded at. A new cache buster is a change already known to have happened, so it takes the
 * forced path and never waits on this window.
 */
const STALE_AFTER_MS = 30_000;

/**
 * The id set every row checks with `.has()` before it renders a banner, carrying the version each
 * image's bytes were stored at. A banner's URL is built from its own version, so replacing one
 * game's artwork changes one URL and leaves every other banner in the browser's cache.
 */
class AvailableGameImages extends Set<string> {
  /** Keyed by lower-cased id. See the case note on `has`. */
  private readonly versions: Record<string, number>;

  constructor(versions: Record<string, number>) {
    super(Object.keys(versions));
    this.versions = {};
    for (const [id, version] of Object.entries(versions)) {
      this.versions[id.toLowerCase()] = version;
    }
  }

  /**
   * Matched without regard to case. An Epic id keeps whatever case Epic gave it, so a download row
   * asks for "Fortnite" while the same game can be advertised as "fortnite", and a case-sensitive
   * check answered no and hid a banner whose bytes the server was serving perfectly well. Games with
   * a hex-string id were unaffected, which is what made this look like it only struck some titles.
   * Ids are unique without case in every namespace here: Steam's are digits, Epic's never differ by
   * case alone, and name-keyed slugs are already lower-cased.
   */
  override has(id: string): boolean {
    return super.has(id) || this.versions[id.toLowerCase()] !== undefined;
  }

  /**
   * 0 for an id the server no longer offers, which happens for the render between the cache being
   * cleared and the next response landing. No image is ever stored at second 0, so the URL it builds
   * is answered with the current bytes and is never kept.
   */
  versionOf(id: string): number {
    return this.versions[id.toLowerCase()] ?? 0;
  }
}

let available = new AvailableGameImages({});
let lastCacheBuster = -1;
let loadedAt = 0;
let fetchPromise: Promise<void> | null = null;

/**
 * Drops the shared id set and its freshness stamp so the next mount always fetches.
 * @public exercised by the guard script, which cannot be seen from the bundle's entry points.
 */
export function resetAvailableGameImages(): void {
  available = new AvailableGameImages({});
  lastCacheBuster = -1;
  loadedAt = 0;
  fetchPromise = null;
}

function fetchAvailableIds(cacheBuster: number): Promise<void> {
  const forced = lastCacheBuster !== cacheBuster;
  if (!forced && available.size > 0 && Date.now() - loadedAt < STALE_AFTER_MS) {
    return Promise.resolve();
  }
  if (fetchPromise && !forced) {
    return fetchPromise;
  }
  lastCacheBuster = cacheBuster;
  const issuedFor = cacheBuster;
  fetchPromise = ApiService.getAvailableGameImages()
    .then((versions: Record<string, number>) => new AvailableGameImages(versions))
    .catch((err: unknown) => {
      // Module-scope helper (no hook context available) - background image-availability check,
      // only affects whether game icons show a fallback. Log for debugging and degrade to "no
      // images available" rather than crash the consuming component.
      console.error('Failed to load available game images, falling back to empty set:', err);
      return new AvailableGameImages({});
    })
    .then((images: AvailableGameImages) => {
      // Two bumps can land inside one round trip, because a depot mapping triggers a fetch pass
      // that can arrive alongside the scheduled one. If this response is no longer the newest one
      // asked for, it is already out of date: publishing it would put the older set back and stamp
      // it fresh, and clearing the slot would drop the newer request still in flight.
      if (lastCacheBuster !== issuedFor) {
        return;
      }
      available = images;
      loadedAt = Date.now();
      fetchPromise = null;
    });
  return fetchPromise;
}

export function useAvailableGameImages(): AvailableGameImages {
  const cacheBuster = useContext(ImageCacheContext);
  const [images, setImages] = useState<AvailableGameImages>(available);

  useEffect(() => {
    fetchAvailableIds(cacheBuster).then(() => {
      setImages((prev: AvailableGameImages): AvailableGameImages => {
        // A refetch that found the same artwork must not publish a new object, or every row on the
        // page re-renders each time the set goes stale. A moved version counts as different, or a
        // replaced banner would keep the URL it was already showing.
        if (prev.size === available.size) {
          let identical = true;
          for (const id of available) {
            if (!prev.has(id) || prev.versionOf(id) !== available.versionOf(id)) {
              identical = false;
              break;
            }
          }
          if (identical) {
            return prev;
          }
        }
        return available;
      });
    });
  }, [cacheBuster]);

  return images;
}
