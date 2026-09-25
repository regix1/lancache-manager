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
  /** True once the container list has answered at least once. */
  listLoaded: boolean;
  /** True while the latest container list read failed. */
  listFailed: boolean;
  /** Set until the dialog closes after a login succeeded, to show the logged-in note. */
  justLoggedIn: boolean;
  /** The last Start, Stop, Log in or Log out error for this service. */
  actionError?: string;
  /** True when `actionError` holds the Log out restart notice rather than a failure. */
  actionNotice: boolean;
  integrationLoginError?: string;
  authenticating?: boolean;
  integrationLoginAvailability?: PersistentIntegrationLoginAvailability;
  integrationLoginAvailabilityLoading?: boolean;
  action?: ScheduledPrefillPersistentAction;
  onStart: () => void;
  onLogin: (reuseIntegration: boolean) => void;
}
