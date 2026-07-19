import type { DatasourceInfo } from '../types';

type NginxReopenMessageKey =
  | 'management.nginxReopen.dockerUnavailable'
  | 'management.nginxReopen.bareMetalUnavailable';

interface NginxReopenGate {
  available: boolean;
  messageKey: NginxReopenMessageKey | null;
}

const isBareMetalLayout = (layout: DatasourceInfo['layout']): boolean =>
  layout === 'bare_metal' || layout === 'mixed';

export function getNginxReopenGate(
  datasources: readonly DatasourceInfo[],
  datasourceNames?: readonly string[] | null
): NginxReopenGate {
  const configuredByName = new Map(datasources.map((datasource) => [datasource.name, datasource]));
  const names = datasourceNames?.filter(Boolean) ?? [];
  const relevant =
    names.length > 0
      ? [...new Set(names)].map((name) => configuredByName.get(name) ?? null)
      : datasources.filter((datasource) => datasource.enabled);
  const unavailable = relevant.filter(
    (datasource) =>
      datasource === null || !datasource.enabled || datasource.nginxReopenAvailable !== true
  );

  if (relevant.length > 0 && unavailable.length === 0) {
    return { available: true, messageKey: null };
  }

  const hasBareMetalDatasource = unavailable.some(
    (datasource) => datasource !== null && isBareMetalLayout(datasource.layout)
  );
  return {
    available: false,
    messageKey: hasBareMetalDatasource
      ? 'management.nginxReopen.bareMetalUnavailable'
      : 'management.nginxReopen.dockerUnavailable'
  };
}

export function getNginxReopenGateForEntities(
  datasources: readonly DatasourceInfo[],
  entities: readonly { datasources?: readonly string[] }[]
): NginxReopenGate {
  const hasUnscopedEntity = entities.some((entity) => !entity.datasources?.length);
  const names = hasUnscopedEntity
    ? undefined
    : entities.flatMap((entity) => entity.datasources ?? []);
  return getNginxReopenGate(datasources, names);
}
