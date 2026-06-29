import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { ScheduledPrefillAnonymousServiceCard } from './ScheduledPrefillAnonymousServiceCard';
import { ScheduledPrefillPlatformAuthPanel } from './ScheduledPrefillPlatformAuthPanel';
import { ScheduledPrefillPlatformSubsection } from './ScheduledPrefillPlatformSubsection';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import { ScheduledPrefillScheduleFields } from './ScheduledPrefillScheduleFields';
import {
  SCHEDULED_PREFILL_PLATFORM_UI,
  isScheduledPrefillAccountService,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type {
  ScheduledPrefillAuthStatusItem,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillPlatformSectionProps {
  serviceKey: ScheduledPrefillServiceKey;
  config: ScheduledPrefillServiceConfigDto;
  authStatuses: ScheduledPrefillAuthStatusItem[];
  authLoading?: boolean;
  disabled?: boolean;
  statusLoading?: boolean;
  container?: PersistentPrefillContainerDto;
  selectedGamesCount: number;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticating: boolean;
  gameSelectionLoading: boolean;
  onChange: (config: ScheduledPrefillServiceConfigDto) => void;
  onRefreshAuth?: () => void | Promise<void>;
  onAuthError?: (message: string) => void;
  onStart: () => void;
  onStop: () => void;
  onLogin: () => void;
  onSelectGames: () => void;
  onDownload: () => void;
  onCancelDownload: () => void;
}

export function ScheduledPrefillPlatformSection({
  serviceKey,
  config,
  authStatuses,
  authLoading = false,
  disabled = false,
  statusLoading = false,
  container,
  selectedGamesCount,
  persistentAction,
  authenticating,
  gameSelectionLoading,
  onChange,
  onRefreshAuth,
  onAuthError,
  onStart,
  onStop,
  onLogin,
  onSelectGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPlatformSectionProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const platformMeta = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
  const PlatformIcon = platformMeta.icon;
  const isRunning = container?.isRunning ?? false;
  const isAuthenticated = container?.isAuthenticated ?? false;
  const accountAuthStatus = isScheduledPrefillAccountService(serviceKey)
    ? authStatuses.find((status) => status.serviceId === serviceKey)
    : null;
  const scheduledAuthReady = accountAuthStatus?.loginState === 'ready';
  const needsScheduledAuth = accountAuthStatus?.loginState === 'loginRequired';
  const containerNeedsAttention =
    isRunning && (!isAuthenticated || container?.needsRelogin || container?.isPrefilling);

  const handleEnabledChange = (value: string) => {
    onChange({ ...config, enabled: value === 'enabled' });
  };

  const authBadge = needsScheduledAuth ? (
    <Badge variant="warning">{t(`${baseKey}.platforms.status.scheduledAuthRequired`)}</Badge>
  ) : scheduledAuthReady ? (
    <Badge variant="success">{t(`${baseKey}.platforms.status.scheduledAuthReady`)}</Badge>
  ) : null;

  const containerBadge = isRunning ? (
    <Badge variant={isAuthenticated ? 'success' : 'warning'}>
      {isAuthenticated
        ? t(`${baseKey}.platforms.status.containerReady`)
        : t(`${baseKey}.platforms.status.containerRunning`)}
    </Badge>
  ) : (
    <Badge variant="neutral">{t('prefill.persistent.states.stopped')}</Badge>
  );

  return (
    <section
      className={`scheduled-prefill-platform-section ${platformMeta.rowClassName}`}
      aria-label={t(`${baseKey}.services.${serviceKey}`)}
    >
      <header className="scheduled-prefill-platform-section__header">
        <div className="scheduled-prefill-platform-section__identity">
          <span className="scheduled-prefill-platform-section__icon" aria-hidden="true">
            <PlatformIcon size={22} />
          </span>
          <div className="scheduled-prefill-platform-section__title-block">
            <h3 className="scheduled-prefill-platform-section__title">
              {t(`${baseKey}.services.${serviceKey}`)}
            </h3>
            <div className="scheduled-prefill-platform-section__chips">
              <Badge variant={config.enabled ? 'success' : 'neutral'}>
                {config.enabled
                  ? t(`${baseKey}.platforms.status.enabled`)
                  : t(`${baseKey}.platforms.status.disabled`)}
              </Badge>
            </div>
          </div>
        </div>
        <ToggleSwitch
          options={[
            {
              value: 'disabled',
              label: t(`${baseKey}.fields.toggleOff`),
              activeColor: 'default'
            },
            {
              value: 'enabled',
              label: t(`${baseKey}.fields.toggleOn`),
              activeColor: 'success'
            }
          ]}
          value={config.enabled ? 'enabled' : 'disabled'}
          onChange={handleEnabledChange}
          disabled={disabled}
          title={t(`${baseKey}.fields.enabled`)}
        />
      </header>

      <div className="scheduled-prefill-platform-section__subsections">
        <div className="scheduled-prefill-platform-section__schedule-panel">
          <h4 className="scheduled-prefill-platform-section__subsection-title">
            {t(`${baseKey}.platforms.sections.schedule`)}
          </h4>
          <ScheduledPrefillScheduleFields
            serviceKey={serviceKey}
            config={config}
            disabled={disabled || !config.enabled}
            onChange={onChange}
          />
        </div>

        {isScheduledPrefillAccountService(serviceKey) && (
          <ScheduledPrefillPlatformSubsection
            title={t(`${baseKey}.platforms.sections.scheduledAuth`)}
            defaultExpanded={needsScheduledAuth}
            resetKey={serviceKey}
            badge={authBadge}
          >
            <ScheduledPrefillPlatformAuthPanel
              serviceKey={serviceKey}
              statuses={authStatuses}
              loading={authLoading}
              disabled={disabled}
              onRefresh={onRefreshAuth}
              onError={onAuthError}
            />
          </ScheduledPrefillPlatformSubsection>
        )}

        {isScheduledPrefillAccountService(serviceKey) && (
          <ScheduledPrefillPlatformSubsection
            title={t(`${baseKey}.platforms.sections.persistentContainer`)}
            defaultExpanded={
              isRunning || selectedGamesCount > 0 || Boolean(containerNeedsAttention)
            }
            resetKey={serviceKey}
            badge={containerBadge}
          >
            <p className="scheduled-prefill-platform-section__subsection-help">
              {t(`${baseKey}.persistentContainer.help`)}
            </p>
            <ScheduledPrefillPersistentCard
              embedded
              serviceKey={serviceKey}
              container={container}
              selectedGamesCount={selectedGamesCount}
              disabled={disabled}
              statusLoading={statusLoading}
              authenticating={authenticating}
              action={persistentAction?.serviceKey === serviceKey ? persistentAction.action : null}
              gameSelectionLoading={gameSelectionLoading}
              onStart={onStart}
              onStop={onStop}
              onLogin={onLogin}
              onSelectGames={onSelectGames}
              onDownload={onDownload}
              onCancelDownload={onCancelDownload}
            />
          </ScheduledPrefillPlatformSubsection>
        )}

        {isScheduledPrefillAnonymousService(serviceKey) && (
          <ScheduledPrefillPlatformSubsection
            title={t(`${baseKey}.platforms.sections.anonymous`)}
            defaultExpanded
            resetKey={serviceKey}
            badge={
              <Badge variant="success">
                {t(`${baseKey}.persistentContainers.anonymous.badge`)}
              </Badge>
            }
          >
            <ScheduledPrefillAnonymousServiceCard embedded serviceKey={serviceKey} />
          </ScheduledPrefillPlatformSubsection>
        )}
      </div>
    </section>
  );
}
