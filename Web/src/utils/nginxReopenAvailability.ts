import type { DatasourceInfo, NginxReopenHint } from '../types';

type NginxReopenMessageKey =
  | 'management.nginxReopen.grantSignalPrivilege'
  | 'management.nginxReopen.enablePidHost'
  | 'management.nginxReopen.dockerUnavailable';

export interface NginxReopenGate {
  available: boolean;
  messageKey: NginxReopenMessageKey | null;
}

const hintPrecedence: readonly NginxReopenHint[] = [
  'grantSignalPrivilege',
  'enablePidHost',
  'mountDockerSocket'
];

const messageKeyByHint: Record<NginxReopenHint, NginxReopenMessageKey> = {
  grantSignalPrivilege: 'management.nginxReopen.grantSignalPrivilege',
  enablePidHost: 'management.nginxReopen.enablePidHost',
  mountDockerSocket: 'management.nginxReopen.dockerUnavailable'
};

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

  const hint =
    hintPrecedence.find((candidate) =>
      unavailable.some((datasource) => datasource?.nginxReopenHint === candidate)
    ) ?? 'mountDockerSocket';

  return {
    available: false,
    messageKey: messageKeyByHint[hint]
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
