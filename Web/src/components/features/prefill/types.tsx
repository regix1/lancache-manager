import React from 'react';
import {
  Clock,
  Download,
  List,
  Trash2,
  RefreshCw,
  Gamepad2,
  TrendingUp,
  ShoppingCart,
  Database
} from 'lucide-react';
import i18n from '../../../i18n';
import type { DropdownOption } from '@components/ui/EnhancedDropdown';
import type { MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import type { NetworkDiagnostics } from '@services/api.service';

// Auth states from backend - matches SteamAuthState enum
export type SteamAuthState =
  | 'NotAuthenticated'
  | 'CredentialsRequired'
  | 'TwoFactorRequired'
  | 'EmailCodeRequired'
  | 'Authenticated';

export interface PrefillSessionDto {
  id: string;
  userId: string;
  containerId: string;
  containerName: string;
  status: string;
  createdAt: string;
  expiresAt: string;
  endedAt: string | null;
  timeRemainingSeconds: number;
  authState: SteamAuthState;
  networkDiagnostics?: NetworkDiagnostics;
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
  label: string;
  description: string;
  icon: React.ReactNode;
  variant?: 'default' | 'outline' | 'filled' | 'subtle';
  requiresLogin?: boolean;
  authOnly?: boolean; // Only show for authenticated users (not guests)
  color?: 'blue' | 'green' | 'red' | 'yellow' | 'purple' | 'gray' | 'orange' | 'default';
}

// Grouped command buttons for better organization
// Note: ALL commands require login - nothing works without Steam auth
export const SELECTION_COMMANDS: CommandButton[] = [
  {
    id: 'select-apps',
    label: '',
    description: '',
    icon: <List className="h-4 w-4" />,
    variant: 'filled',
    color: 'blue'
  }
];

export const PREFILL_COMMANDS: CommandButton[] = [
  {
    id: 'prefill',
    label: '',
    description: '',
    icon: <Download className="h-4 w-4" />,
    variant: 'filled',
    color: 'green'
  },
  {
    id: 'prefill-all',
    label: '',
    description: '',
    icon: <Gamepad2 className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-recent',
    label: '',
    description: '',
    icon: <Clock className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-recent-purchased',
    label: '',
    description: '',
    icon: <ShoppingCart className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-top',
    label: '',
    description: '',
    icon: <TrendingUp className="h-4 w-4" />,
    variant: 'outline'
  }
];

export const UTILITY_COMMANDS: CommandButton[] = [
  {
    id: 'prefill-force',
    label: '',
    description: '',
    icon: <RefreshCw className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'clear-temp',
    label: '',
    description: '',
    icon: <Trash2 className="h-4 w-4" />,
    variant: 'outline',
    color: 'red'
  },
  {
    id: 'clear-cache-data',
    label: '',
    description: '',
    icon: <Database className="h-4 w-4" />,
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
    { value: 'auto', label: '', description: '', shortLabel: `Auto (${Math.min(DAEMON_DEFAULT_THREADS, effectiveCap)})` },
    ...STATIC_THREAD_VALUES.map((n: number): DropdownOption => ({
      value: String(n),
      label: '',
      description: '',
      disabled: n > effectiveCap
    }))
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

export function formatDuration(seconds: number): string {
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
