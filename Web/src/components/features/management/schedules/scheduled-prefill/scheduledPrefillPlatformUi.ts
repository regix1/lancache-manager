import type { LucideIcon } from 'lucide-react';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { SteamIcon } from '@components/ui/SteamIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { PERSISTENT_PREFILL_SERVICES } from '@components/features/prefill/persistentPrefillConstants';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_ANONYMOUS_SERVICE_IDS
} from './constants';
import type { ScheduledPrefillServiceKey } from './types';

interface ScheduledPrefillPlatformUiMeta {
  icon: LucideIcon | typeof SteamIcon;
  rowClassName: string;
}

export const SCHEDULED_PREFILL_PLATFORM_UI: Record<
  ScheduledPrefillServiceKey,
  ScheduledPrefillPlatformUiMeta
> = {
  steam: { icon: SteamIcon, rowClassName: 'scheduled-prefill-platform--steam' },
  epic: { icon: EpicIcon, rowClassName: 'scheduled-prefill-platform--epic' },
  xbox: { icon: XboxIcon, rowClassName: 'scheduled-prefill-platform--xbox' },
  battleNet: { icon: BlizzardIcon, rowClassName: 'scheduled-prefill-platform--battlenet' },
  riot: { icon: RiotIcon, rowClassName: 'scheduled-prefill-platform--riot' }
};

export const isScheduledPrefillAccountService = (
  serviceKey: ScheduledPrefillServiceKey
): serviceKey is (typeof SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS)[number] =>
  (SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS as readonly string[]).includes(serviceKey);

export const isScheduledPrefillAnonymousService = (
  serviceKey: ScheduledPrefillServiceKey
): serviceKey is (typeof SCHEDULED_PREFILL_ANONYMOUS_SERVICE_IDS)[number] =>
  (SCHEDULED_PREFILL_ANONYMOUS_SERVICE_IDS as readonly string[]).includes(serviceKey);

export const getPersistentServiceId = (
  serviceKey: ScheduledPrefillServiceKey
): PersistentPrefillServiceId => {
  const service = PERSISTENT_PREFILL_SERVICES.find((item) => item.key === serviceKey);
  if (!service) {
    throw new Error(`Unknown scheduled prefill service: ${serviceKey}`);
  }

  return service.service;
};

export const needsPersistentLogin = (
  container: PersistentPrefillContainerDto | undefined
): boolean =>
  !container || !container.isRunning || !container.isAuthenticated || container.needsRelogin;
