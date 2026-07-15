import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import { ScheduledPrefillScheduleFields } from './ScheduledPrefillScheduleFields';
import { SCHEDULED_PREFILL_PLATFORM_UI } from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type { ScheduledPrefillServiceConfigDto, ScheduledPrefillServiceKey } from './types';

/**
 * Sentinel + the 3 real modes, in dropdown order. `'useGlobal'` maps to a `null` DTO override
 * at the onChange boundary - the DTO itself never carries the sentinel string.
 */
const PERSISTENCE_MODE_OVERRIDE_VALUES = [
  'useGlobal',
  'killOnRestart',
  'keepAcrossRestart',
  'fullPersistence'
] as const;

type PersistenceModeOverrideValue = (typeof PERSISTENCE_MODE_OVERRIDE_VALUES)[number];

const isPersistenceModeOverrideValue = (value: string): value is PersistenceModeOverrideValue =>
  (PERSISTENCE_MODE_OVERRIDE_VALUES as readonly string[]).includes(value);

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

  const handleNotificationChange = (value: string) => {
    onChange({ ...config, showNotification: value === 'visible' });
  };

  const handlePersistenceModeOverrideChange = (value: string) => {
    if (!isPersistenceModeOverrideValue(value)) return;
    onChange({ ...config, persistenceMode: value === 'useGlobal' ? null : value });
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
          <div className="scheduled-prefill-notification-setting">
            <div className="scheduled-prefill-notification-setting__copy">
              <span className="scheduled-prefill-notification-setting__label">
                {t(`${baseKey}.fields.notifications`)}
              </span>
              <span className="scheduled-prefill-notification-setting__help">
                {t(`${baseKey}.fields.notificationsHelp`)}
              </span>
            </div>
            <ToggleSwitch
              options={[
                {
                  value: 'silent',
                  label: t(`${baseKey}.fields.silent`),
                  activeColor: 'default'
                },
                {
                  value: 'visible',
                  label: t(`${baseKey}.fields.visible`),
                  activeColor: 'waiting'
                }
              ]}
              value={config.showNotification !== false ? 'visible' : 'silent'}
              onChange={handleNotificationChange}
              disabled={disabled}
              title={t(`${baseKey}.fields.notifications`)}
            />
          </div>

          <div className="scheduled-prefill-notification-setting">
            <div className="scheduled-prefill-notification-setting__copy">
              <span className="scheduled-prefill-notification-setting__label">
                {t(`${baseKey}.fields.persistenceModeOverride`)}
              </span>
              <span className="scheduled-prefill-notification-setting__help">
                {t(`${baseKey}.fields.persistenceModeOverrideHelp`)}
              </span>
            </div>
            <EnhancedDropdown
              options={PERSISTENCE_MODE_OVERRIDE_VALUES.map((value) => ({
                value,
                label: t(`${baseKey}.settings.persistenceMode.${value}`)
              }))}
              value={config.persistenceMode ?? 'useGlobal'}
              onChange={handlePersistenceModeOverrideChange}
              disabled={disabled}
              variant="button"
              size="lg"
              triggerAriaLabel={t(`${baseKey}.fields.persistenceModeOverride`)}
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
