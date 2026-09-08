import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import type {
  PersistentIntegrationLoginAvailability,
  PersistentPrefillContainerDto
} from '@components/features/prefill/persistentPrefillTypes';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import {
  ScheduledPrefillDownloadFields,
  ScheduledPrefillNotificationFields,
  ScheduledPrefillScheduleFields
} from './ScheduledPrefillPlatformFields';
import { SCHEDULED_PREFILL_PLATFORM_UI } from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type { ScheduledPrefillSchedule, ScheduledPrefillServiceKey } from './types';

interface ScheduledPrefillPlatformSectionProps {
  serviceKey: ScheduledPrefillServiceKey;
  scheduleControls?: ReactNode;
  containerSettings?: (disabled: boolean) => ReactNode;
  gameSelectionLoading?: boolean;
  onSelectGames: () => void;
  onClearGames: () => void;
  onStop: () => void;
  onLogout: () => void;
  config: ScheduledPrefillSchedule;
  disabled?: boolean;
  statusLoading?: boolean;
  container?: PersistentPrefillContainerDto;
  selectedGamesCount: number;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticating: boolean;
  integrationLoginAvailability?: PersistentIntegrationLoginAvailability;
  integrationLoginAvailabilityLoading?: boolean;
  onChange: (config: ScheduledPrefillSchedule) => void;
  onStart: () => void;
  onLogin: (reuseIntegration: boolean) => void;
  onDownload: () => void;
  onCancelDownload: () => void;
}

export function ScheduledPrefillPlatformSection({
  serviceKey,
  scheduleControls,
  containerSettings,
  gameSelectionLoading,
  onSelectGames,
  onClearGames,
  onStop,
  onLogout,
  config,
  disabled = false,
  statusLoading = false,
  container,
  selectedGamesCount,
  persistentAction,
  authenticating,
  integrationLoginAvailability,
  integrationLoginAvailabilityLoading = false,
  onChange,
  onStart,
  onLogin,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPlatformSectionProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const platformMeta = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];

  const fieldsDisabled = disabled || !config.enabled;

  return (
    <section
      className={`scheduled-prefill-platform-section ${platformMeta.rowClassName}${
        config.enabled ? '' : ' scheduled-prefill-platform-section--off'
      }`}
      aria-label={t(`${baseKey}.services.${serviceKey}`)}
    >
      <div className="scheduled-prefill-platform-section__blocks">
        <ScheduledPrefillPersistentCard
          scheduleControls={scheduleControls}
          containerSettings={containerSettings}
          gameSelectionLoading={gameSelectionLoading}
          onSelectGames={onSelectGames}
          onClearGames={onClearGames}
          onStop={onStop}
          onLogout={onLogout}
          serviceKey={serviceKey}
          container={container}
          selectedGamesCount={selectedGamesCount}
          disabled={fieldsDisabled}
          scheduleEnabled={config.enabled}
          statusLoading={statusLoading}
          authenticating={authenticating}
          integrationLoginAvailability={integrationLoginAvailability}
          integrationLoginAvailabilityLoading={integrationLoginAvailabilityLoading}
          action={persistentAction?.serviceKey === serviceKey ? persistentAction.action : null}
          onStart={onStart}
          onLogin={onLogin}
          onDownload={onDownload}
          onCancelDownload={onCancelDownload}
        />

        <Card
          padding="md"
          className="scheduled-prefill-platform-block scheduled-prefill-platform-block--schedule"
        >
          <h4 className="scheduled-prefill-platform-block__title">
            {t(`${baseKey}.platforms.sections.schedule`)}
          </h4>
          <div className="scheduled-prefill-config-modal__settings-list">
            <ScheduledPrefillScheduleFields
              serviceKey={serviceKey}
              config={config}
              disabled={fieldsDisabled}
              onChange={onChange}
            />
          </div>
        </Card>

        <Card padding="md" className="scheduled-prefill-platform-block">
          <h4 className="scheduled-prefill-platform-block__title">
            {t(`${baseKey}.platforms.sections.download`)}
          </h4>
          <div className="scheduled-prefill-config-modal__settings-list">
            <ScheduledPrefillDownloadFields
              serviceKey={serviceKey}
              config={config}
              disabled={fieldsDisabled}
              onChange={onChange}
            />
          </div>
        </Card>

        <Card
          padding="md"
          className="scheduled-prefill-platform-block scheduled-prefill-platform-block--notifications"
        >
          <h4 className="scheduled-prefill-platform-block__title">
            {t(`${baseKey}.platforms.sections.notifications`)}
          </h4>
          <div className="scheduled-prefill-config-modal__settings-list">
            <ScheduledPrefillNotificationFields
              serviceKey={serviceKey}
              config={config}
              disabled={fieldsDisabled}
              onChange={onChange}
            />
          </div>
        </Card>
      </div>
    </section>
  );
}
