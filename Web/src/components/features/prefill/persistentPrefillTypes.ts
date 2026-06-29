export type PersistentPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type PersistentPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export interface PersistentPrefillContainerDto {
  sessionId: string;
  service: PersistentPrefillServiceId;
  isRunning: boolean;
  isAuthenticated: boolean;
  daemonAuthExpiresAtUtc: string | null;
  authExpiresAtUtc: string;
  authTimeRemainingSeconds: number;
  needsRelogin: boolean;
  isPrefilling?: boolean;
  totalBytesTransferred?: number;
  currentAppName?: string | null;
}

export interface PersistentPrefillValiditySettings {
  days: number;
}

export interface PersistentPrefillGuestLifetimeSettings {
  hours: number;
}
