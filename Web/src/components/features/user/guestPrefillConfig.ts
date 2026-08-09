import type { PrefillServiceConfig } from '@components/features/prefill/hooks/prefillServiceConfig';

export interface GuestPrefillConfig {
  enabledByDefault: boolean;
  durationHours: number;
  maxThreadCount: number | null;
}

export interface GuestPrefillConfigResponse {
  enabledByDefault: boolean;
  durationHours: number;
  maxThreadCount?: number | null;
}

export const toGuestPrefillConfig = (
  service: PrefillServiceConfig,
  response: GuestPrefillConfigResponse
): GuestPrefillConfig => ({
  enabledByDefault: response.enabledByDefault,
  durationHours: response.durationHours,
  maxThreadCount: service.supportsMaxThreads ? (response.maxThreadCount ?? null) : null
});
