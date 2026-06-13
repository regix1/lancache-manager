import {
  Clock,
  Download,
  List,
  Trash2,
  RefreshCw,
  Gamepad2,
  TrendingUp,
  ShoppingCart,
  Database,
  type LucideIcon
} from 'lucide-react';
import i18n from '../../../i18n';
import type { DropdownOption } from '@components/ui/EnhancedDropdown';
import type { MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import type { NetworkDiagnostics } from '@services/api.service';
import type { DaemonSessionStatus, DaemonAuthState } from '@/types/operations';

export interface PrefillSessionDto {
  id: string;
  userId: string;
  containerId: string;
  containerName: string;
  /**
   * Lifecycle status of the underlying daemon container (NOT the persisted PrefillSession
   * from the admin API - that one uses `PrefillSessionStatus` with different values).
   * Wire values: `'Active' | 'Terminated' | 'Error'` per backend `DaemonSessionStatus`.
   */
  status: DaemonSessionStatus;
  createdAt: string;
  expiresAt: string;
  endedAt: string | null;
  timeRemainingSeconds: number;
  authState: DaemonAuthState;
  networkDiagnostics?: NetworkDiagnostics;
  /**
   * Server truth: is a prefill currently running on the daemon? Stays true from start-ack
   * through the real download (driven by terminal socket state, not the start-ack).
   * Used to re-hydrate the progress bar on (re)connect/(re)mount/tab-return.
   */
  isPrefilling?: boolean;
  /** App id currently being prefilled (when isPrefilling). */
  currentAppId?: string;
  /** Display name of the app currently being prefilled (when isPrefilling). */
  currentAppName?: string;
  /** Total bytes transferred so far in the running prefill (when isPrefilling). */
  totalBytesTransferred?: number;
}

export interface PrefillPanelProps {
  onSessionEnd?: () => void;
}

export type CommandType =
  | 'select-apps'
  | 'prefill'
  | 'prefill-all'
  | 'prefill-recent'
  | 'prefill-recent-purchased'
  | 'prefill-top'
  | 'prefill-force'
  | 'clear-temp'
  | 'clear-cache-data';

export interface CommandButton {
  id: CommandType;
  /** Icon component reference (rendered by the consumer; storing the component, not JSX,
   *  keeps this a pure .ts module so it satisfies the Fast-Refresh / "no JSX in data" rule). */
  icon: LucideIcon;
  variant?: 'default' | 'outline' | 'filled' | 'subtle';
  requiresLogin?: boolean;
  authOnly?: boolean; // Only show for authenticated users (not guests)
  color?: 'blue' | 'green' | 'red' | 'yellow' | 'purple' | 'gray' | 'orange' | 'default';
}

// Grouped command buttons for better organization.
// Note: ALL commands require login - nothing works without Steam auth.
// Labels/descriptions are resolved via i18n in the consumer (keyed by `id`).
export const SELECTION_COMMANDS: CommandButton[] = [
  {
    id: 'select-apps',
    icon: List,
    variant: 'filled',
    color: 'blue'
  }
];

export const PREFILL_COMMANDS: CommandButton[] = [
  {
    id: 'prefill',
    icon: Download,
    variant: 'filled',
    color: 'green'
  },
  {
    id: 'prefill-all',
    icon: Gamepad2,
    variant: 'outline'
  },
  {
    id: 'prefill-recent',
    icon: Clock,
    variant: 'outline'
  },
  {
    id: 'prefill-recent-purchased',
    icon: ShoppingCart,
    variant: 'outline'
  },
  {
    id: 'prefill-top',
    icon: TrendingUp,
    variant: 'outline'
  }
];

export const UTILITY_COMMANDS: CommandButton[] = [
  {
    id: 'prefill-force',
    icon: RefreshCw,
    variant: 'outline'
  },
  {
    id: 'clear-temp',
    icon: Trash2,
    variant: 'outline',
    color: 'red'
  },
  {
    id: 'clear-cache-data',
    icon: Database,
    variant: 'outline',
    color: 'red',
    authOnly: true
  }
];

// Operating system options for prefill (multi-select)
export const OS_OPTIONS: MultiSelectOption[] = [
  { value: 'windows', label: '', description: '' },
  { value: 'linux', label: '', description: '' },
  { value: 'macos', label: '', description: '' }
];

// Static thread count values
const STATIC_THREAD_VALUES = [1, 2, 4, 8, 16, 32, 64, 128, 256];

// Daemon default when no concurrency value is specified
const DAEMON_DEFAULT_THREADS = 30;
const MAX_THREAD_VALUE = 256;

// Build dynamic thread options based on optional guest thread limit
export function getThreadOptions(maxThreadLimit?: number | null): DropdownOption[] {
  const effectiveCap = maxThreadLimit ?? MAX_THREAD_VALUE;
  return [
    {
      value: 'auto',
      label: '',
      description: '',
      shortLabel: `Auto (${Math.min(DAEMON_DEFAULT_THREADS, effectiveCap)})`
    },
    ...STATIC_THREAD_VALUES.map(
      (n: number): DropdownOption => ({
        value: String(n),
        label: '',
        description: '',
        disabled: n > effectiveCap
      })
    )
  ];
}

// Utility functions
export function formatTimeRemaining(seconds: number): string {
  const t = i18n.t.bind(i18n);
  if (seconds <= 0) return t('prefill.timeRemaining.expiring');
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) {
    return t('prefill.timeRemaining.hoursMinutes', { hours, minutes });
  }
  if (minutes > 0) {
    return t('prefill.timeRemaining.minutesSeconds', { minutes, seconds: secs });
  }
  return t('prefill.timeRemaining.seconds', { seconds: secs });
}

// Short download ETA formatter. Distinct from formatTimeRemaining (which carries SESSION-expiry
// semantics and returns "Expiring..." at <= 0). For a download ETA, <= 0 means "almost done", so
// it returns a "< 1s" string instead of a misleading expiry label.
export function formatEtaShort(seconds: number): string {
  const t = i18n.t.bind(i18n);
  if (seconds <= 0) return t('prefill.progress.etaLessThanSecond');
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) {
    return t('prefill.progress.etaHoursMinutes', { hours, minutes });
  }
  if (minutes > 0) {
    return t('prefill.progress.etaMinutesSeconds', { minutes, seconds: secs });
  }
  return t('prefill.progress.etaSeconds', { seconds: secs });
}

export function formatDurationFromSeconds(seconds: number): string {
  const t = i18n.t.bind(i18n);
  if (seconds <= 0) return t('prefill.duration.zero');
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) {
    return t('prefill.duration.hoursMinutesSeconds', { hours, minutes, seconds: secs });
  }
  if (minutes > 0) {
    return t('prefill.duration.minutesSeconds', { minutes, seconds: secs });
  }
  return t('prefill.duration.seconds', { seconds: secs });
}

// Re-export formatBytes from central location for backward compatibility
export { formatBytes } from '@utils/formatters';
