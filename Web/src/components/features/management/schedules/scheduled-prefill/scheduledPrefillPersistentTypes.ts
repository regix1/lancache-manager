import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
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
  statusLoading?: boolean;
  authenticating?: boolean;
  action?: ScheduledPrefillPersistentAction;
  gameSelectionLoading?: boolean;
  onStart: () => void;
  onStop: () => void;
  onLogin: () => void;
  onLogout: () => void;
  onSelectGames: () => void;
  onDownload: () => void;
  onCancelDownload: () => void;
}
