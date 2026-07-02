import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { ScheduledPrefillAnonymousServiceCard } from './ScheduledPrefillAnonymousServiceCard';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import { ScheduledPrefillScheduleFields } from './ScheduledPrefillScheduleFields';
import {
  SCHEDULED_PREFILL_PLATFORM_UI,
  isScheduledPrefillAccountService,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type { ScheduledPrefillServiceConfigDto, ScheduledPrefillServiceKey } from './types';

interface ScheduledPrefillPlatformSectionProps {
  serviceKey: ScheduledPrefillServiceKey;
  config: ScheduledPrefillServiceConfigDto;
  disabled?: boolean;
  statusLoading?: boolean;
  container?: PersistentPrefillContainerDto;
  selectedGamesCount: number;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticating: boolean;
  gameSelectionLoading: boolean;
  onChange: (config: ScheduledPrefillServiceConfigDto) => void;
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
  disabled = false,
  statusLoading = false,
  container,
  selectedGamesCount,
  persistentAction,
  authenticating,
  gameSelectionLoading,
  onChange,
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
  const isAccount = isScheduledPrefillAccountService(serviceKey);

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

      <div className="scheduled-prefill-platform-section__blocks">
        <Card padding="md" className="scheduled-prefill-platform-block">
          <h4 className="scheduled-prefill-platform-block__title">
            {t(`${baseKey}.platforms.sections.schedule`)}
          </h4>
          <ScheduledPrefillScheduleFields
            serviceKey={serviceKey}
            config={config}
            disabled={disabled || !config.enabled}
            onChange={onChange}
          />
        </Card>

        {isScheduledPrefillAnonymousService(serviceKey) && (
          <ScheduledPrefillAnonymousServiceCard serviceKey={serviceKey} />
        )}

        {isAccount && (
          <ScheduledPrefillPersistentCard
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
        )}
      </div>
    </section>
  );
}
