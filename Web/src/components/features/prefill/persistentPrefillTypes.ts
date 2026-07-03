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

/**
 * Discriminator for a 404 from GET .../persistent/challenge (PersistentPrefillController's
 * ResolveRunningPersistentSession; wire shape PersistentSessionNotFoundResponse). Distinguishes a
 * persistent session that flipped to Error (daemon socket dropped) from one that was simply never
 * started, so the UI can show different copy for each.
 */
export type PersistentSessionNotFoundState = 'notStarted' | 'errored';
