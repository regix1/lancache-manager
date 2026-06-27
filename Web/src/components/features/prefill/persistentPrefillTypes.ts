export type PersistentPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type PersistentPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export interface PersistentPrefillContainerDto {
  sessionId: string;
  service: PersistentPrefillServiceId;
  isRunning: boolean;
  daemonAuthExpiresAtUtc: string | null;
  authExpiresAtUtc: string;
  authTimeRemainingSeconds: number;
  needsRelogin: boolean;
}

export interface PersistentPrefillValiditySettings {
  days: number;
}

export interface PersistentPrefillGuestLifetimeSettings {
  hours: number;
}
