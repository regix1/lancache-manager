import { useEffect, useMemo, useRef, useState } from 'react';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import type { Download } from '../../../types';
import {
  computeStickyTtlMs,
  reconcileLivePreviews,
  type LiveDownloadPreview,
  type LivePreviewLedgerEntry
} from './liveDownloadPreviews';

// Module-level stable empty result: consumers can gate on length without re-rendering, and
// no flash occurs while the speed context is still loading.
const EMPTY_PREVIEWS: LiveDownloadPreview[] = [];

/**
 * Derives the in-progress preview rows for a live view from the current speed snapshot,
 * reconciled against the recorded downloads. The reconciliation ledger is bounded by the
 * currently live identities: a stale pre-existing row does not hide live traffic during an
 * ingestion pause, while a newly inserted or advanced row suppresses its preview. Previews
 * are presentation-only and must never be merged into any Download[] collection.
 */
export function useLiveDownloadPreviews(
  downloads: Download[],
  enabled: boolean
): LiveDownloadPreview[] {
  const { speedSnapshot, isLoading } = useSpeed();
  const ledgerRef = useRef<Map<string, LivePreviewLedgerEntry>>(new Map());
  // Sticky rows must also disappear WITHOUT a newer snapshot arriving, so a timer re-runs
  // the reconciliation once the earliest sticky TTL can have elapsed.
  const [recomputeAt, setRecomputeAt] = useState(0);

  const previews = useMemo(() => {
    if (!enabled || isLoading || !speedSnapshot) {
      ledgerRef.current = new Map();
      return EMPTY_PREVIEWS;
    }

    const result = reconcileLivePreviews({
      gameSpeeds: speedSnapshot.gameSpeeds ?? [],
      windowSeconds: speedSnapshot.windowSeconds,
      downloads,
      ledger: ledgerRef.current,
      now: Math.max(Date.now(), recomputeAt)
    });
    ledgerRef.current = result.ledger;
    return result.previews.length > 0 ? result.previews : EMPTY_PREVIEWS;
  }, [enabled, isLoading, speedSnapshot, downloads, recomputeAt]);

  useEffect(() => {
    if (previews.length === 0) return;
    const stickyMs = computeStickyTtlMs(speedSnapshot?.windowSeconds);
    const earliestExpiry = Math.min(...previews.map((p) => p.lastSeenAt + stickyMs));
    const timer = setTimeout(
      () => setRecomputeAt(Date.now()),
      Math.max(250, earliestExpiry - Date.now())
    );
    return () => clearTimeout(timer);
  }, [previews, speedSnapshot]);

  return previews;
}
