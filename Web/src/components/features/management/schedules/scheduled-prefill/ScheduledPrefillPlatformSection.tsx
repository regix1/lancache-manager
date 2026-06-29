import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { ScheduledPrefillAnonymousServiceCard } from './ScheduledPrefillAnonymousServiceCard';
import { ScheduledPrefillPlatformAuthPanel } from './ScheduledPrefillPlatformAuthPanel';
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

  const handleEnabledChange = (value: string) => {
    onChange({ ...config, enabled: value === 'enabled' });
  };

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
              {isScheduledPrefillAccountService(serviceKey) && (
                <Badge variant={scheduledAuthReady ? 'success' : 'warning'}>
                  {scheduledAuthReady
                    ? t(`${baseKey}.platforms.status.scheduledAuthReady`)
                    : t(`${baseKey}.platforms.status.scheduledAuthRequired`)}
                </Badge>
              )}
              {isScheduledPrefillAccountService(serviceKey) && isRunning && (
                <Badge variant={isAuthenticated ? 'success' : 'warning'}>
                  {isAuthenticated
                    ? t(`${baseKey}.platforms.status.containerReady`)
                    : t(`${baseKey}.platforms.status.containerRunning`)}
                </Badge>
              )}
              {selectedGamesCount > 0 && (
                <Badge variant="info">
                  {t(`${baseKey}.selectedGames.count`, { count: selectedGamesCount })}
                </Badge>
              )}
            </div>
          </div>
        </div>
        <ToggleSwitch
          options={[
            {
              value: 'disabled',
              label: t('management.schedules.disabled'),
              activeColor: 'default'
            },
            {
              value: 'enabled',
              label: t(`${baseKey}.fields.enabled`),
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
        {isScheduledPrefillAccountService(serviceKey) && (
          <div className="scheduled-prefill-platform-section__subsection">
            <h4 className="scheduled-prefill-platform-section__subsection-title">
              {t(`${baseKey}.platforms.sections.scheduledAuth`)}
            </h4>
            <ScheduledPrefillPlatformAuthPanel
              serviceKey={serviceKey}
              statuses={authStatuses}
              loading={authLoading}
              disabled={disabled}
              onRefresh={onRefreshAuth}
              onError={onAuthError}
            />
          </div>
        )}

        {isScheduledPrefillAccountService(serviceKey) && (
          <div className="scheduled-prefill-platform-section__subsection">
            <h4 className="scheduled-prefill-platform-section__subsection-title">
              {t(`${baseKey}.platforms.sections.persistentContainer`)}
            </h4>
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
          </div>
        )}

        {isScheduledPrefillAnonymousService(serviceKey) && (
          <div className="scheduled-prefill-platform-section__subsection">
            <h4 className="scheduled-prefill-platform-section__subsection-title">
              {t(`${baseKey}.platforms.sections.anonymous`)}
            </h4>
            <ScheduledPrefillAnonymousServiceCard embedded serviceKey={serviceKey} />
          </div>
        )}

        <div className="scheduled-prefill-platform-section__subsection">
          <h4 className="scheduled-prefill-platform-section__subsection-title">
            {t(`${baseKey}.platforms.sections.schedule`)}
          </h4>
          {isScheduledPrefillAccountService(serviceKey) && selectedGamesCount > 0 && (
            <p className="scheduled-prefill-platform-section__override">
              {t(`${baseKey}.selectedGames.count`, { count: selectedGamesCount })}
              {'. '}
              {t(`${baseKey}.selectedGames.overridePreset`)}
            </p>
          )}
          <ScheduledPrefillScheduleFields
            serviceKey={serviceKey}
            config={config}
            disabled={disabled || !config.enabled}
            onChange={onChange}
          />
        </div>
      </div>
    </section>
  );
}
