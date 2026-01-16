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

export interface PrefillProgress {
  state: string;
  percentComplete: number;
  currentAppId: number;
  currentAppName: string;
  totalApps: number;
  currentApp: number;
  totalBytes: number;
  downloadedBytes: number;
  currentSpeed: number;
  elapsedSeconds: number;
  remainingSeconds?: number;
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

// Max concurrency/thread options
export const THREAD_OPTIONS: DropdownOption[] = [
  { value: 'default', label: '', description: '' },
  { value: '1', label: '', description: '' },
  { value: '2', label: '', description: '' },
  { value: '4', label: '', description: '' },
  { value: '8', label: '', description: '' },
  { value: '16', label: '', description: '' },
  { value: '32', label: '', description: '' }
];

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

export function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}
