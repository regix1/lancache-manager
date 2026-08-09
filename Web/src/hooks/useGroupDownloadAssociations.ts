import { useEffect } from 'react';
import type { Download } from '../types';

export function useGroupDownloadAssociations(
  downloads: Download[],
  fetchAssociations: (downloadIds: number[]) => Promise<void>,
  refreshVersion: number
): void {
  useEffect(() => {
    const downloadIds = downloads.map((download) => download.id);
    fetchAssociations(downloadIds);
  }, [downloads, fetchAssociations, refreshVersion]);
}
