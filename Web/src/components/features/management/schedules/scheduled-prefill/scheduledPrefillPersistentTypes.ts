import type { ReactNode } from 'react';
import type {
  PersistentIntegrationLoginAvailability,
  PersistentPrefillContainerDto
} from '@components/features/prefill/persistentPrefillTypes';
import type { ScheduledPrefillServiceKey } from './types';

export type ScheduledPrefillPersistentAction =
  | 'start'
  | 'stop'
  | 'logout'
  | 'download'
  | 'cancel'
  | null;

export interface ScheduledPrefillPersistentActionState {
  serviceKey: ScheduledPrefillServiceKey;
  action: NonNullable<ScheduledPrefillPersistentAction>;
}

export interface ScheduledPrefillPersistentCardProps {
  scheduleControls?: ReactNode;
  gameSelectionLoading?: boolean;
  onSelectGames: () => void;
  onClearGames: () => void;
  onStop: () => void;
  onLogout: () => void;
  serviceKey: ScheduledPrefillServiceKey;
  container?: PersistentPrefillContainerDto;
  selectedGamesCount: number;
  disabled?: boolean;
  /** The schedule whose game selection this card manages is switched on. */
  scheduleEnabled: boolean;
  statusLoading?: boolean;
  authenticating?: boolean;
  integrationLoginAvailability?: PersistentIntegrationLoginAvailability;
  integrationLoginAvailabilityLoading?: boolean;
  action?: ScheduledPrefillPersistentAction;
  onStart: () => void;
  onLogin: (reuseIntegration: boolean) => void;
  onDownload: () => void;
  onCancelDownload: (runId?: string) => void;
}
