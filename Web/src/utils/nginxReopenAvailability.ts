import type { DatasourceInfo, NginxReopenHint } from '../types';

type NginxReopenMessageKey =
  | 'management.nginxReopen.windowsDockerUnsupported'
  | 'management.nginxReopen.grantSignalPrivilege'
  | 'management.nginxReopen.enablePidHost'
  | 'management.nginxReopen.dockerUnavailable'
  | 'management.nginxReopen.writerUnknown'
  | 'management.nginxReopen.checkOnAction';

export interface NginxReopenGate {
  available: boolean;
  messageKey: NginxReopenMessageKey | null;
}

const hintPrecedence: readonly NginxReopenHint[] = [
  'useLinuxManager',
  'grantSignalPrivilege',
  'enablePidHost',
  'mountDockerSocket'
];

const messageKeyByHint: Record<NginxReopenHint, NginxReopenMessageKey> = {
  useLinuxManager: 'management.nginxReopen.windowsDockerUnsupported',
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
  const unavailable = relevant.filter((datasource) => {
    if (datasource === null || !datasource.enabled) return true;
    return !(
      datasource.nginxReopenRequirement === 'notRequired' ||
      (datasource.nginxReopenRequirement === 'required' &&
        datasource.nginxReopenAvailable === true) ||
      (datasource.nginxReopenRequirement === 'unknown' && datasource.nginxReopenCheckOnAction)
    );
  });

  if (relevant.length > 0 && unavailable.length === 0) {
    const checkOnAction = relevant.some(
      (datasource) => datasource?.nginxReopenRequirement === 'unknown'
    );
    return {
      available: true,
      messageKey: checkOnAction ? 'management.nginxReopen.checkOnAction' : null
    };
  }

  const hint =
    hintPrecedence.find((candidate) =>
      unavailable.some((datasource) => datasource?.nginxReopenHint === candidate)
    ) ?? null;

  return {
    available: false,
    messageKey: hint ? messageKeyByHint[hint] : 'management.nginxReopen.writerUnknown'
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
