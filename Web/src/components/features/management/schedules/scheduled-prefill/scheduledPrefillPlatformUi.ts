import type { LucideIcon } from 'lucide-react';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { SteamIcon } from '@components/ui/SteamIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
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
