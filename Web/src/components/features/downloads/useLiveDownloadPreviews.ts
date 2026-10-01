import { useMemo } from 'react';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import type { Download } from '../../../types';
import { reconcileLivePreviews, type LiveDownloadPreview } from './liveDownloadPreviews';

const EMPTY_PREVIEWS: LiveDownloadPreview[] = [];

/**
 * Projects current server sessions that do not yet have a recorded row. The server snapshot owns
 * membership and lifetime; previews have no browser timer or retained ledger.
 */
export function useLiveDownloadPreviews(
  downloads: Download[],
  enabled: boolean
): LiveDownloadPreview[] {
  const { speedSnapshot, isLoading } = useSpeed();

  return useMemo(() => {
    if (!enabled || isLoading || !speedSnapshot) return EMPTY_PREVIEWS;
    const previews = reconcileLivePreviews({
      gameSpeeds: speedSnapshot.gameSpeeds,
      windowSeconds: speedSnapshot.windowSeconds,
      downloads
    });
    return previews.length > 0 ? previews : EMPTY_PREVIEWS;
  }, [downloads, enabled, isLoading, speedSnapshot]);
}
