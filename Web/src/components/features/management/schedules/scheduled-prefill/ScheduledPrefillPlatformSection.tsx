import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { noAutofill } from '@utils/autofill';
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
  const PlatformIcon = platformMeta.icon;

  const handleEnabledChange = (value: string) => {
    onChange({ ...config, enabled: value === 'enabled' });
  };

  // A record stays editable while its automation is off: a saved setup is the same record with
  // enabled false, and Save as creates one in exactly that state.
  const fieldsDisabled = disabled;

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
            <label className="sr-only" htmlFor={`scheduled-prefill-schedule-name-${config.id}`}>
              {t(`${baseKey}.records.name`)}
            </label>
            <input
              {...noAutofill}
              id={`scheduled-prefill-schedule-name-${config.id}`}
              className="scheduled-prefill-platform-section__schedule-name"
              value={config.name}
              onChange={(event) => onChange({ ...config, name: event.target.value })}
              disabled={disabled}
            />
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
        />
      </header>

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
