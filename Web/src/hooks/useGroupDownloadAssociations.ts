import { useEffect } from 'react';

/**
 * Loads the event tags for a group's sessions. Takes the ids because a collapsed group carries
 * only its newest session, and the badges it draws cover the whole membership.
 */
export function useGroupDownloadAssociations(
  downloadIds: number[],
  fetchAssociations: (downloadIds: number[]) => Promise<void>,
  refreshVersion: number
): void {
  useEffect(() => {
    fetchAssociations(downloadIds);
  }, [downloadIds, fetchAssociations, refreshVersion]);
}
