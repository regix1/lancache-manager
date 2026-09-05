import type { ScheduledPrefillSchedule } from '../management/schedules/scheduled-prefill/types';

export type PersistentPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type PersistentPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export interface PersistentPrefillContainerDto {
  sessionId: string;
  service: PersistentPrefillServiceId;
  isRunning: boolean;
  isAuthenticated: boolean;
  daemonAuthExpiresAtUtc: string | null;
  authExpiresAtUtc: string;
  createdAtUtc: string;
  authTimeRemainingSeconds: number;
  needsRelogin: boolean;
  isPrefilling?: boolean;
  runId?: string | null;
  totalBytesTransferred?: number;
  currentAppName?: string | null;
}

export interface PersistentIntegrationLoginAvailability {
  available: boolean;
  account: string | null;
  reason: string | null;
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

/** Maps a saved schedule's download choice to the persistent daemon's immediate-run contract. */
export function getPersistentPrefillRunOptions(schedule: ScheduledPrefillSchedule) {
  const hasSelectedIds = schedule.selectedAppIds.length > 0;
  return {
    appIds: schedule.selectedAppIds,
    all: !hasSelectedIds && schedule.preset === 'All',
    recent: !hasSelectedIds && schedule.preset === 'Recent',
    recentlyPurchased: false,
    top: !hasSelectedIds && schedule.preset === 'Top' ? (schedule.topCount ?? 50) : null,
    force: schedule.force,
    operatingSystems: schedule.operatingSystems.map((operatingSystem) =>
      operatingSystem.toLowerCase()
    ),
    maxConcurrency: schedule.maxConcurrency.mode === 'Fixed' ? schedule.maxConcurrency.value : null
  };
}
