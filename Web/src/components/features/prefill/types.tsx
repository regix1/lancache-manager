import i18n from '../../../i18n';
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
