import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import type { ScheduledPrefillServiceKey } from './types';

export type ScheduledPrefillPersistentAction = 'start' | 'stop' | 'download' | 'cancel' | null;

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
  onSelectGames: () => void;
  onDownload: () => void;
  onCancelDownload: () => void;
}

export interface ScheduledPrefillContainersSectionProps {
  disabled?: boolean;
  statusLoading?: boolean;
  containersByServiceKey: Map<ScheduledPrefillServiceKey, PersistentPrefillContainerDto>;
  selectedGamesCountByServiceKey: Record<ScheduledPrefillServiceKey, number>;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticatingServiceKeys: ScheduledPrefillServiceKey[];
  gameSelectionLoadingServiceKey: ScheduledPrefillServiceKey | null;
  onStart: (serviceKey: ScheduledPrefillServiceKey) => void;
  onStop: (serviceKey: ScheduledPrefillServiceKey) => void;
  onLogin: (serviceKey: ScheduledPrefillServiceKey) => void;
  onSelectGames: (serviceKey: ScheduledPrefillServiceKey) => void;
  onDownload: (serviceKey: ScheduledPrefillServiceKey) => void;
  onCancelDownload: (serviceKey: ScheduledPrefillServiceKey) => void;
}
