import type { TFunction } from 'i18next';
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
import type { ScheduledPrefillPersistentAction } from './scheduledPrefillPersistentTypes';
import type {
  ScheduledPrefillAccountStatus,
  ScheduledPrefillContainerStatus,
  ScheduledPrefillServiceKey,
  ScheduledPrefillServiceStatus
} from './types';

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

/**
 * The one reading of a service's container and account state, shared by the Services row, the
 * schedule meta line and the service dialog.
 */
export const getScheduledPrefillServiceStatus = (
  serviceKey: ScheduledPrefillServiceKey,
  input: {
    container: PersistentPrefillContainerDto | undefined;
    listLoaded: boolean;
    listFailed: boolean;
    action: ScheduledPrefillPersistentAction;
    authenticating: boolean;
    loginError: string | null;
  }
): ScheduledPrefillServiceStatus => {
  const { container, action } = input;
  const containerStatus: ScheduledPrefillContainerStatus = (() => {
    // Before the first answer a missing container is not known to be stopped.
    if (container === undefined && !input.listLoaded)
      return input.listFailed ? 'unknown' : 'checking';
    if (action === 'start') return 'starting';
    if (action === 'stop') return 'stopping';
    if (!container?.isRunning) return 'stopped';
    if (container.isPrefilling || (container.activeRunCount ?? 0) > 0) return 'downloading';
    return 'running';
  })();
  const account: ScheduledPrefillAccountStatus = (() => {
    if (isScheduledPrefillAnonymousService(serviceKey)) return 'notNeeded';
    if (containerStatus === 'checking' || containerStatus === 'unknown') return containerStatus;
    if (!container?.isRunning || containerStatus === 'starting') return 'checkedAfterStart';
    if (action === 'logout') return 'loggingOut';
    if (input.authenticating || action === 'login') return 'loggingIn';
    if (input.loginError !== null) return 'loginFailed';
    // A daemon still logged in past its Login duration keeps running schedules, so it reads
    // logged in; the dialog shows the re-login fact.
    if (!container.isAuthenticated)
      return container.needsRelogin ? 'loginExpired' : 'loginRequired';
    return 'loggedIn';
  })();
  const next: ScheduledPrefillServiceStatus['next'] =
    containerStatus === 'stopped' || containerStatus === 'starting'
      ? 'start'
      : account === 'loginRequired' || account === 'loginExpired' || account === 'loginFailed'
        ? 'logIn'
        : 'manage';
  return { container: containerStatus, account, next };
};

/** A status reading as shown on screen: a dot (or none), or the one spinner, beside its words. */
interface ScheduledPrefillStatusFact {
  tone: 'idle' | 'running' | 'warning' | 'error' | 'info' | null;
  busy: boolean;
  label: string;
}

/** Words and dot for one container or account status, shared by the Services row and the dialog. */
export const getScheduledPrefillStatusFact = (
  status: ScheduledPrefillContainerStatus | ScheduledPrefillAccountStatus,
  container: PersistentPrefillContainerDto | undefined,
  t: TFunction
): ScheduledPrefillStatusFact => {
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  switch (status) {
    case 'checking':
      return { tone: null, busy: false, label: t(`${baseKey}.serviceStatus.checking`) };
    case 'unknown':
      return { tone: 'idle', busy: false, label: t('common.unknown') };
    case 'stopped':
      return { tone: 'idle', busy: false, label: t('prefill.persistent.states.stopped') };
    case 'starting':
      return { tone: null, busy: true, label: t(`${baseKey}.serviceStatus.starting`) };
    case 'stopping':
      return { tone: null, busy: true, label: t(`${baseKey}.serviceStatus.stopping`) };
    case 'running':
      return { tone: 'running', busy: false, label: t('prefill.persistent.states.running') };
    case 'downloading': {
      const count = Math.max(container?.activeRunCount ?? 0, container?.isPrefilling ? 1 : 0);
      return {
        tone: 'info',
        busy: false,
        label: `${t(`${baseKey}.persistentContainers.steps.downloading`)}, ${t(
          `${baseKey}.serviceStatus.runCount`,
          { count }
        )}`
      };
    }
    case 'notNeeded':
      return {
        tone: null,
        busy: false,
        label: t(`${baseKey}.persistentContainers.anonymous.badge`)
      };
    case 'checkedAfterStart':
      return { tone: null, busy: false, label: t(`${baseKey}.serviceStatus.checkedAfterStart`) };
    case 'loggedIn':
      return { tone: 'running', busy: false, label: t(`${baseKey}.platforms.status.loggedIn`) };
    case 'loginRequired':
      return {
        tone: 'warning',
        busy: false,
        label: t(`${baseKey}.platforms.status.loginRequired`)
      };
    case 'loginExpired':
      return { tone: 'warning', busy: false, label: t(`${baseKey}.serviceStatus.loginExpired`) };
    case 'loggingIn':
      return { tone: null, busy: true, label: t(`${baseKey}.serviceStatus.loggingIn`) };
    case 'loggingOut':
      return { tone: null, busy: true, label: t(`${baseKey}.serviceStatus.loggingOut`) };
    case 'loginFailed':
      return { tone: 'error', busy: false, label: t(`${baseKey}.serviceStatus.loginFailed`) };
  }
};
