import { useMemo } from 'react';
import { useConfig } from '@contexts/useConfig';
import type { DatasourceInfo } from '../types';

interface DiskObjectCapability {
  available: boolean;
  denialReason: string | null;
}

/**
 * Fleet-wide availability of disk-level logical-object operations: per-game and per-service
 * cache removal, corruption detection/removal, and eviction removal. Every enabled datasource
 * must have one resolved cache-key scheme because cross-datasource mutations must not partially
 * succeed. Whole-cache clear never depends on this capability.
 */
export function useDiskObjectCapability(): DiskObjectCapability {
  const { config } = useConfig();
  return useMemo<DiskObjectCapability>(() => {
    const enabled = (config.dataSources ?? []).filter(
      (datasource: DatasourceInfo) => datasource.enabled
    );
    const available =
      enabled.length > 0 &&
      enabled.every((datasource: DatasourceInfo) => datasource.canMapLogicalObjects === true);
    const blockedDatasource = enabled.find(
      (datasource: DatasourceInfo) => datasource.canMapLogicalObjects !== true
    );

    return {
      available,
      denialReason: blockedDatasource?.capabilityDenialReason ?? null
    };
  }, [config.dataSources]);
}
