import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import type {
  PersistentIntegrationLoginAvailability,
  PersistentPrefillContainerDto
} from '@components/features/prefill/persistentPrefillTypes';
import { SCHEDULED_PREFILL_SERVICE_RUN_ORDER } from './constants';
import { ScheduledPrefillPlatformSection } from './ScheduledPrefillPlatformSection';
import {
  SCHEDULED_PREFILL_PLATFORM_UI,
  isScheduledPrefillAccountService,
  needsPersistentLogin
} from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillSchedule,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillPlatformsPanelProps {
  config: ScheduledPrefillConfigDto;
  initialServiceKey?: ScheduledPrefillServiceKey;
  initialScheduleId?: string;
  disabled?: boolean;
  statusLoading?: boolean;
  containersByServiceKey: Map<ScheduledPrefillServiceKey, PersistentPrefillContainerDto>;
  selectedGamesCountByScheduleId: Record<string, number>;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticatingServiceKeys: ScheduledPrefillServiceKey[];
  integrationLoginAvailabilityByService: Map<
    ScheduledPrefillServiceKey,
    PersistentIntegrationLoginAvailability
  >;
  integrationLoginAvailabilityLoading: boolean;
  gameSelectionLoadingServiceKey: ScheduledPrefillServiceKey | null;
  onScheduleChange: (
    serviceKey: ScheduledPrefillServiceKey,
    schedule: ScheduledPrefillSchedule
  ) => void;
  onAddSchedule: (serviceKey: ScheduledPrefillServiceKey) => void;
  onDuplicateSchedule: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onDeleteSchedule: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onStart: (serviceKey: ScheduledPrefillServiceKey) => void;
  onStop: (serviceKey: ScheduledPrefillServiceKey) => void;
  onLogin: (serviceKey: ScheduledPrefillServiceKey, reuseIntegration: boolean) => void;
  onLogout: (serviceKey: ScheduledPrefillServiceKey) => void;
  onSelectGames: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onClearGames: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onDownload: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onCancelDownload: (serviceKey: ScheduledPrefillServiceKey) => void;
}

export function ScheduledPrefillPlatformsPanel({
  config,
  initialServiceKey = 'steam',
  initialScheduleId,
  disabled = false,
  statusLoading = false,
  containersByServiceKey,
  selectedGamesCountByScheduleId,
  persistentAction,
  authenticatingServiceKeys,
  integrationLoginAvailabilityByService,
  integrationLoginAvailabilityLoading,
  gameSelectionLoadingServiceKey,
  onScheduleChange,
  onAddSchedule,
  onDuplicateSchedule,
  onDeleteSchedule,
  onStart,
  onStop,
  onLogin,
  onLogout,
  onSelectGames,
  onClearGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPlatformsPanelProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const [activeServiceKey, setActiveServiceKey] =
    useState<ScheduledPrefillServiceKey>(initialServiceKey);
  const [selectedScheduleId, setSelectedScheduleId] = useState(initialScheduleId ?? '');
  const activeService = config[activeServiceKey];
  const activeSchedule = useMemo(
    () =>
      activeService.schedules.find((schedule) => schedule.id === selectedScheduleId) ??
      activeService.schedules[0] ??
      null,
    [activeService.schedules, selectedScheduleId]
  );

  useEffect(() => {
    if (activeSchedule && activeSchedule.id !== selectedScheduleId) {
      setSelectedScheduleId(activeSchedule.id);
    }
  }, [activeSchedule, selectedScheduleId]);

  useEffect(() => {
    if (initialScheduleId) {
      setActiveServiceKey(initialServiceKey);
      setSelectedScheduleId(initialScheduleId);
    }
  }, [initialServiceKey, initialScheduleId]);

  const getNavHint = (serviceKey: ScheduledPrefillServiceKey): string | null => {
    const serviceConfig = config[serviceKey];
    // The container list loads independently of config, so don't flag a false "needs login" hint
    // while it's still loading (or hasn't loaded) - we simply don't know its state yet.
    if (!serviceConfig.schedules.some((schedule) => schedule.enabled) || statusLoading) {
      return null;
    }

    if (isScheduledPrefillAccountService(serviceKey)) {
      const container = containersByServiceKey.get(serviceKey);
      if (needsPersistentLogin(container)) {
        return t(`${baseKey}.platforms.nav.loginRequired`);
      }
    }

    return null;
  };

  return (
    <section className="scheduled-prefill-platforms-panel">
      <div className="scheduled-prefill-platforms">
        <nav
          className="scheduled-prefill-platforms__nav"
          aria-label={t(`${baseKey}.platforms.navLabel`)}
        >
          {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
            const serviceConfig = config[serviceKey];
            const platformMeta = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
            const PlatformIcon = platformMeta.icon;
            const isActive = activeServiceKey === serviceKey;
            const navHint = getNavHint(serviceKey);
            const container = containersByServiceKey.get(serviceKey);

            return (
              <Button
                key={serviceKey}
                type="button"
                variant="transparent"
                fullWidth
                className={`scheduled-prefill-platforms__nav-item focus-ring${
                  isActive ? ' scheduled-prefill-platforms__nav-item--active' : ''
                } ${platformMeta.rowClassName}`}
                aria-current={isActive ? 'page' : undefined}
                onClick={() => setActiveServiceKey(serviceKey)}
              >
                <span className="scheduled-prefill-platforms__nav-icon" aria-hidden="true">
                  <PlatformIcon size={18} />
                </span>
                <span className="scheduled-prefill-platforms__nav-text">
                  <span className="scheduled-prefill-platforms__nav-label">
                    {t(`${baseKey}.services.${serviceKey}`)}
                  </span>
                  {navHint && (
                    <span className="scheduled-prefill-platforms__nav-hint">{navHint}</span>
                  )}
                </span>
                <span className="scheduled-prefill-platforms__nav-badges">
                  {/* Operational state stays color-coded (green = active, red = inactive), while
                      notification mode uses its own axis: filled purple = all runs, filled blue =
                      manual runs only, dotted outline = silent. */}
                  <Badge
                    variant={
                      serviceConfig.schedules.some((schedule) => schedule.enabled)
                        ? 'success'
                        : 'error'
                    }
                    className="scheduled-prefill-platforms__nav-badge"
                  >
                    {serviceConfig.schedules.some((schedule) => schedule.enabled)
                      ? t(`${baseKey}.platforms.status.enabled`)
                      : t(`${baseKey}.platforms.status.disabled`)}
                  </Badge>
                  {/* Neutral while the container list is still loading so it does not flash red
                      before its real running state is known. */}
                  <Badge
                    variant={statusLoading ? 'neutral' : container?.isRunning ? 'success' : 'error'}
                    className="scheduled-prefill-platforms__nav-badge"
                  >
                    <span className="sr-only">
                      {t(`${baseKey}.platforms.status.containerShort`)}:{' '}
                    </span>
                    {statusLoading
                      ? t('common.loading')
                      : container?.isRunning
                        ? t('prefill.persistent.states.running')
                        : t('prefill.persistent.states.stopped')}
                  </Badge>
                  <Badge
                    variant={
                      serviceConfig.schedules[0]?.notificationMode === 'silent'
                        ? 'waiting-outline'
                        : serviceConfig.schedules[0]?.notificationMode === 'manual'
                          ? 'info'
                          : 'waiting'
                    }
                    className="scheduled-prefill-platforms__nav-badge"
                  >
                    {t(
                      `management.schedules.notificationMode.${serviceConfig.schedules[0]?.notificationMode ?? 'all'}`
                    )}
                  </Badge>
                </span>
              </Button>
            );
          })}
        </nav>

        <div className="scheduled-prefill-platforms__content">
          <div className="scheduled-prefill-platforms__records">
            <EnhancedDropdown
              options={activeService.schedules.map((schedule) => ({
                value: schedule.id,
                label: schedule.name
              }))}
              value={activeSchedule?.id ?? ''}
              onChange={setSelectedScheduleId}
              disabled={disabled || activeService.schedules.length === 0}
              variant="button"
              triggerAriaLabel={t(`${baseKey}.records.label`)}
            />
            <div className="scheduled-prefill-platforms__record-actions">
              <Button
                type="button"
                variant="default"
                size="sm"
                onClick={() => onAddSchedule(activeServiceKey)}
                disabled={disabled}
              >
                {t(`${baseKey}.records.new`)}
              </Button>
              <Button
                type="button"
                variant="default"
                size="sm"
                onClick={() =>
                  activeSchedule && onDuplicateSchedule(activeServiceKey, activeSchedule.id)
                }
                disabled={disabled || !activeSchedule}
              >
                {t(`${baseKey}.records.saveAs`)}
              </Button>
              <Button
                type="button"
                variant="default"
                size="sm"
                onClick={() =>
                  activeSchedule && onDeleteSchedule(activeServiceKey, activeSchedule.id)
                }
                disabled={disabled || !activeSchedule || activeService.schedules.length === 1}
              >
                {t('common.delete')}
              </Button>
            </div>
          </div>
          {activeSchedule && (
            <ScheduledPrefillPlatformSection
              key={activeSchedule.id}
              serviceKey={activeServiceKey}
              config={activeSchedule}
              disabled={disabled}
              statusLoading={statusLoading}
              container={containersByServiceKey.get(activeServiceKey)}
              selectedGamesCount={selectedGamesCountByScheduleId[activeSchedule.id] ?? 0}
              persistentAction={persistentAction}
              authenticating={authenticatingServiceKeys.includes(activeServiceKey)}
              integrationLoginAvailability={integrationLoginAvailabilityByService.get(
                activeServiceKey
              )}
              integrationLoginAvailabilityLoading={integrationLoginAvailabilityLoading}
              gameSelectionLoading={gameSelectionLoadingServiceKey === activeServiceKey}
              onChange={(schedule) => onScheduleChange(activeServiceKey, schedule)}
              onStart={() => onStart(activeServiceKey)}
              onStop={() => onStop(activeServiceKey)}
              onLogin={(reuseIntegration) => onLogin(activeServiceKey, reuseIntegration)}
              onLogout={() => onLogout(activeServiceKey)}
              onSelectGames={() => onSelectGames(activeServiceKey, activeSchedule.id)}
              onClearGames={() => onClearGames(activeServiceKey, activeSchedule.id)}
              onDownload={() => onDownload(activeServiceKey, activeSchedule.id)}
              onCancelDownload={() => onCancelDownload(activeServiceKey)}
            />
          )}
        </div>
      </div>
    </section>
  );
}
