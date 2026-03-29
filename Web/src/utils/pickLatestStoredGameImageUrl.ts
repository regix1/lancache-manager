import type { Download } from '../types';

/** Latest non-empty GameImageUrl by session start time (matches backend Downloads ordering). */
export function pickLatestStoredGameImageUrl(downloads: Download[]): string | undefined {
  let best: { t: string; url: string } | null = null;
  for (const d of downloads) {
    const u = d.gameImageUrl?.trim();
    if (!u) continue;
    const t = d.startTimeUtc;
    if (!best || t > best.t) best = { t, url: u };
  }
  return best?.url;
}
