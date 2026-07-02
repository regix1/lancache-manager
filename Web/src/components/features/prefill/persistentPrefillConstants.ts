import type {
  PersistentPrefillServiceId,
  PersistentPrefillServiceKey
} from './persistentPrefillTypes';

export const PERSISTENT_PREFILL_SERVICES = [
  {
    key: 'steam',
    service: 'Steam',
    labelKey: 'prefill.persistent.services.steam',
    rowClassName: 'persistent-prefill-service--steam'
  },
  {
    key: 'epic',
    service: 'Epic',
    labelKey: 'prefill.persistent.services.epic',
    rowClassName: 'persistent-prefill-service--epic'
  },
  {
    key: 'xbox',
    service: 'Xbox',
    labelKey: 'prefill.persistent.services.xbox',
    rowClassName: 'persistent-prefill-service--xbox'
  },
  {
    key: 'battleNet',
    service: 'BattleNet',
    labelKey: 'prefill.persistent.services.battleNet',
    rowClassName: 'persistent-prefill-service--battle-net'
  },
  {
    key: 'riot',
    service: 'Riot',
    labelKey: 'prefill.persistent.services.riot',
    rowClassName: 'persistent-prefill-service--riot'
  }
] as const satisfies readonly {
  key: PersistentPrefillServiceKey;
  service: PersistentPrefillServiceId;
  labelKey: string;
  rowClassName: string;
}[];

export const PERSISTENT_PREFILL_VALIDITY_BOUNDS = {
  min: 1,
  max: 365
} as const;
