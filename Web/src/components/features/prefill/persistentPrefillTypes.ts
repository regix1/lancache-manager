import type { PrefillRun } from './hooks/prefillTypes';
import type { IntegrationReason } from '../../../types';

export type PersistentPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type PersistentPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export interface PersistentPrefillContainerDto {
  runs?: PrefillRun[];
  daemonInstanceId?: string | null;
  features?: string[];
  maxConcurrentRuns?: number;
  activeRunCount?: number;
  recovering?: boolean;
  sessionId: string;
  service: PersistentPrefillServiceId;
  isRunning: boolean;
  isAuthenticated: boolean;
  daemonAuthExpiresAtUtc: string | null;
  authExpiresAtUtc: string;
  createdAtUtc: string;
  needsRelogin: boolean;
  isPrefilling?: boolean;
  runId?: string | null;
  totalBytesTransferred?: number;
  currentAppName?: string | null;
}

export type PersistentIntegrationLoginAvailability = {
  account?: string | null;
} & (
  | { available: true; reason?: IntegrationReason | null }
  | { available: false; reason: IntegrationReason }
);

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
