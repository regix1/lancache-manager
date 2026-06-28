export type ScheduledPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export type ScheduledPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type ScheduledPrefillAccountServiceId = 'steam' | 'epic' | 'xbox';

export type ScheduledPrefillPreset = 'All' | 'Recent' | 'Top';

export type ScheduledPrefillOperatingSystem = 'Windows' | 'Linux' | 'Macos';

export type ScheduledPrefillMaxConcurrencyMode = 'Auto' | 'Fixed';

interface ScheduledPrefillAutoMaxConcurrency {
  mode: 'Auto';
  value?: null;
}

interface ScheduledPrefillFixedMaxConcurrency {
  mode: 'Fixed';
  value: number;
}

export type ScheduledPrefillMaxConcurrency =
  | ScheduledPrefillAutoMaxConcurrency
  | ScheduledPrefillFixedMaxConcurrency;

export interface ScheduledPrefillServiceConfigDto {
  serviceId: ScheduledPrefillServiceId;
  enabled: boolean;
  preset: ScheduledPrefillPreset;
  selectedAppIds: string[];
  topCount?: number | null;
  operatingSystems: ScheduledPrefillOperatingSystem[];
  force: boolean;
  maxConcurrency: ScheduledPrefillMaxConcurrency;
}

export interface ScheduledPrefillConfigDto {
  version: number;
  maxServiceRuntime: string;
  stallTimeout: string;
  steam: ScheduledPrefillServiceConfigDto;
  epic: ScheduledPrefillServiceConfigDto;
  xbox: ScheduledPrefillServiceConfigDto;
  battleNet: ScheduledPrefillServiceConfigDto;
  riot: ScheduledPrefillServiceConfigDto;
}

export type ScheduledPrefillAuthLoginState = 'ready' | 'loginRequired' | 'unsupported';

export interface ScheduledPrefillAuthStatusItem {
  serviceId: ScheduledPrefillAccountServiceId;
  isAuthenticated: boolean;
  displayName: string | null;
  expiresAtUtc: string | null;
  loginState: ScheduledPrefillAuthLoginState;
}

export interface ScheduledPrefillStartedEvent {
  operationId: string;
  serviceCount: number;
}

export interface ScheduledPrefillProgressEvent {
  operationId: string;
  serviceId: string;
  stage: string;
  message: string;
  needsLoginReason?: string | null;
}

export interface ScheduledPrefillCompletedEvent {
  operationId: string | null;
  success: boolean;
  error?: string | null;
}

export type ScheduledPrefillRunPhase = 'idle' | 'running' | 'completed' | 'failed';

export interface ScheduledPrefillRunProgressItem {
  serviceId: string;
  stage: string;
  message: string;
}
