import type {
  PersistentIntegrationLoginAvailability,
  PersistentPrefillContainerDto
} from '@components/features/prefill/persistentPrefillTypes';
import type { ScheduledPrefillServiceKey } from './types';

export type ScheduledPrefillPersistentAction =
  | 'start'
  | 'stop'
  | 'logout'
  | 'login'
  | 'download'
  | 'cancel'
  | null;

export interface ScheduledPrefillPersistentActionState {
  serviceKey: ScheduledPrefillServiceKey;
  action: NonNullable<ScheduledPrefillPersistentAction>;
}

export interface ScheduledPrefillPersistentCardProps {
  onStop: () => void;
  onLogout: () => void;
  serviceKey: ScheduledPrefillServiceKey;
  container?: PersistentPrefillContainerDto;
  disabled?: boolean;
  statusLoading?: boolean;
  authenticating?: boolean;
  integrationLoginAvailability?: PersistentIntegrationLoginAvailability;
  integrationLoginAvailabilityLoading?: boolean;
  action?: ScheduledPrefillPersistentAction;
  onStart: () => void;
  onLogin: (reuseIntegration: boolean) => void;
}
