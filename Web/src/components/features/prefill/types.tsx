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

export interface LogEntry {
  id: string;
  timestamp: Date;
  type: 'info' | 'error' | 'warning' | 'download' | 'success';
  message: string;
}

// Grouped command buttons for better organization
// Note: ALL commands require login - nothing works without Steam auth
export const SELECTION_COMMANDS: CommandButton[] = [
  {
    id: 'select-apps',
    label: 'Select Apps',
    description: 'Choose games to prefill',
    icon: <List className="h-4 w-4" />,
    variant: 'filled',
    color: 'blue'
  }
];

export const PREFILL_COMMANDS: CommandButton[] = [
  {
    id: 'prefill',
    label: 'Prefill Selected',
    description: 'Download selected games',
    icon: <Download className="h-4 w-4" />,
    variant: 'filled',
    color: 'green'
  },
  {
    id: 'prefill-all',
    label: 'Prefill All',
    description: 'All owned games',
    icon: <Gamepad2 className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-recent',
    label: 'Recent Played',
    description: 'Last 2 weeks',
    icon: <Clock className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-recent-purchased',
    label: 'Recent Bought',
    description: 'Last 2 weeks',
    icon: <ShoppingCart className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-top',
    label: 'Top 50',
    description: 'Popular games',
    icon: <TrendingUp className="h-4 w-4" />,
    variant: 'outline'
  }
];

export const UTILITY_COMMANDS: CommandButton[] = [
  {
    id: 'prefill-force',
    label: 'Force Download',
    description: 'Re-download all',
    icon: <RefreshCw className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'clear-temp',
    label: 'Clear Temp',
    description: 'Free disk space',
    icon: <Trash2 className="h-4 w-4" />,
    variant: 'outline',
    color: 'red'
  },
  {
    id: 'clear-cache-data',
    label: 'Clear Database',
    description: 'Remove cache records',
    icon: <Database className="h-4 w-4" />,
    variant: 'outline',
    color: 'red',
    authOnly: true
  }
];

// Operating system options for prefill (multi-select)
export const OS_OPTIONS: MultiSelectOption[] = [
  { value: 'windows', label: 'Windows', description: 'Windows game depots' },
  { value: 'linux', label: 'Linux', description: 'Native Linux depots' },
  { value: 'macos', label: 'macOS', description: 'macOS depots' }
];

// Max concurrency/thread options
export const THREAD_OPTIONS: DropdownOption[] = [
  { value: 'default', label: 'Auto', description: 'Let daemon decide (recommended)' },
  { value: '1', label: '1 Thread', description: 'Minimal bandwidth usage' },
  { value: '2', label: '2 Threads', description: 'Low bandwidth usage' },
  { value: '4', label: '4 Threads', description: 'Moderate bandwidth' },
  { value: '8', label: '8 Threads', description: 'High bandwidth' },
  { value: '16', label: '16 Threads', description: 'Very high bandwidth' },
  { value: '32', label: '32 Threads', description: 'Maximum performance' }
];

// Utility functions
export function formatTimeRemaining(seconds: number): string {
  if (seconds <= 0) return 'Expiring...';
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  }
  return `${secs}s`;
}

export function formatDuration(seconds: number): string {
  if (seconds <= 0) return '0s';
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) {
    return `${hours}h ${minutes}m ${secs}s`;
  }
  if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  }
  return `${secs}s`;
}

export function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}
