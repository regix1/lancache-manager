import { useMemo } from 'react';
import { useConfig } from '@contexts/useConfig';

/**
 * Fleet-wide availability of disk-level logical-object operations: per-game and per-service
 * cache removal, corruption detection/removal, and eviction removal. These need the monolithic
 * cache-key recipe, so a fleet whose every enabled datasource is bare-metal cannot map objects
 * on disk. A legacy single-path setup (no datasource list) and any datasource that still maps
 * objects keep the actions available; a mixed fleet stays enabled and the backend fails closed
 * per datasource. Whole-cache clear never depends on this and stays available everywhere.
 */
export function useDiskObjectCapability(): boolean {
  const { config } = useConfig();
  return useMemo(() => {
    const enabled = (config.dataSources ?? []).filter((ds) => ds.enabled);
    return enabled.length === 0 || enabled.some((ds) => ds.canMapLogicalObjects !== false);
  }, [config.dataSources]);
}
