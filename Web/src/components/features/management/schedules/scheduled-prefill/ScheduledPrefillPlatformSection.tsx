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
  config: ScheduledPrefillSchedule;
  disabled?: boolean;
  statusLoading?: boolean;
  container?: PersistentPrefillContainerDto;
  selectedGamesCount: number;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticating: boolean;
  integrationLoginAvailability?: PersistentIntegrationLoginAvailability;
  integrationLoginAvailabilityLoading?: boolean;
  gameSelectionLoading: boolean;
  onChange: (config: ScheduledPrefillSchedule) => void;
  onStart: () => void;
  onStop: () => void;
  onLogin: (reuseIntegration: boolean) => void;
  onLogout: () => void;
  onSelectGames: () => void;
  onClearGames: () => void;
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
  integrationLoginAvailability,
  integrationLoginAvailabilityLoading = false,
  gameSelectionLoading,
  onChange,
  onStart,
  onStop,
  onLogin,
  onLogout,
  onSelectGames,
  onClearGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPlatformSectionProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const platformMeta = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];

  // A schedule that is switched off greys its options out; the toggle, the name and the
  // record menu in the header above stay live so it can be renamed or switched back on.
  const fieldsDisabled = disabled || !config.enabled;

  return (
    <section
      className={`scheduled-prefill-platform-section ${platformMeta.rowClassName}${
        config.enabled ? '' : ' scheduled-prefill-platform-section--off'
      }`}
      aria-label={t(`${baseKey}.services.${serviceKey}`)}
    >
      <div className="scheduled-prefill-platform-section__blocks">
        <Card padding="md" className="scheduled-prefill-platform-block">
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

        <Card padding="md" className="scheduled-prefill-platform-block">
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

        <ScheduledPrefillPersistentCard
          serviceKey={serviceKey}
          container={container}
          selectedGamesCount={selectedGamesCount}
          disabled={disabled}
          scheduleEnabled={config.enabled}
          statusLoading={statusLoading}
          authenticating={authenticating}
          integrationLoginAvailability={integrationLoginAvailability}
          integrationLoginAvailabilityLoading={integrationLoginAvailabilityLoading}
          action={persistentAction?.serviceKey === serviceKey ? persistentAction.action : null}
          gameSelectionLoading={gameSelectionLoading}
          onStart={onStart}
          onStop={onStop}
          onLogin={onLogin}
          onLogout={onLogout}
          onSelectGames={onSelectGames}
          onClearGames={onClearGames}
          onDownload={onDownload}
          onCancelDownload={onCancelDownload}
        />
      </div>
    </section>
  );
}
