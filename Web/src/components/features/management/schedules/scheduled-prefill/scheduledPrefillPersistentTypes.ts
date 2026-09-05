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
  gameSelectionLoading?: boolean;
  onStart: () => void;
  onStop: () => void;
  onLogin: (reuseIntegration: boolean) => void;
  onLogout: () => void;
  onSelectGames: () => void;
  onClearGames: () => void;
  onDownload: () => void;
  onCancelDownload: () => void;
}
