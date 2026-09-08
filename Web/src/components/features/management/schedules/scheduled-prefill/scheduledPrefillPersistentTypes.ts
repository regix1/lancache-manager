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
  containerSettings?: ReactNode;
  gameSelectionLoading?: boolean;
  onSelectGames: () => void;
  onClearGames: () => void;
  onStop: () => void;
  onLogout: () => void;
  serviceKey: ScheduledPrefillServiceKey;
  container?: PersistentPrefillContainerDto;
  selectedGamesCount: number;
  disabled?: boolean;
  /** The schedule whose game selection this card manages is switched on. Off greys out the
   *  selection and the manual download; the container itself stays under the user's control. */
  scheduleEnabled: boolean;
  statusLoading?: boolean;
  authenticating?: boolean;
  integrationLoginAvailability?: PersistentIntegrationLoginAvailability;
  integrationLoginAvailabilityLoading?: boolean;
  action?: ScheduledPrefillPersistentAction;
  onStart: () => void;
  onLogin: (reuseIntegration: boolean) => void;
  onDownload: () => void;
  onCancelDownload: () => void;
}
