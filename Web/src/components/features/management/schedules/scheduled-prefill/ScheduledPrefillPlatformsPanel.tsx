import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import { Button } from '@components/ui/Button';
import {
  ActionMenu,
  ActionMenuDangerItem,
  ActionMenuDivider,
  ActionMenuItem
} from '@components/ui/ActionMenu';
import Badge from '@components/ui/Badge';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import FormField from '@components/ui/FormField';
import { TextInput } from '@components/ui/TextInput';
import { noAutofill } from '@utils/autofill';
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
  onAddSchedule: (serviceKey: ScheduledPrefillServiceKey) => string;
  onDuplicateSchedule: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => string;
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
  const [actionsOpen, setActionsOpen] = useState(false);
  const pendingFocusScheduleId = useRef<string | null>(null);
  const contentRef = useRef<HTMLDivElement | null>(null);
  const activeService = config[activeServiceKey];
  const activePlatform = SCHEDULED_PREFILL_PLATFORM_UI[activeServiceKey];
  const ActivePlatformIcon = activePlatform.icon;
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

  // A record the user just created is only worth creating if they can see it, so it becomes the
  // shown one and its name is where the caret lands, ready to be typed over.
  const showNewSchedule = (scheduleId: string): void => {
    pendingFocusScheduleId.current = scheduleId;
    setSelectedScheduleId(scheduleId);
  };

  useEffect(() => {
    const scheduleId = pendingFocusScheduleId.current;
    // The section renders one schedule at a time, so the name input only exists once the new
    // record is the shown one. A creator that found nothing to copy leaves the selection where it
    // was, and this waits rather than focusing another record's name.
    if (!scheduleId || activeSchedule?.id !== scheduleId) {
      return;
    }
    pendingFocusScheduleId.current = null;
    contentRef.current
      ?.querySelector<HTMLInputElement>('.scheduled-prefill-platforms__record-name')
      ?.focus();
  }, [activeSchedule]);

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

        <div className="scheduled-prefill-platforms__content" ref={contentRef}>
          {/* One header row per platform: who it is, which saved schedule is open, what that
              schedule is called, what else can be done with it, and whether it is on. The two
              captioned fields carry the app's form-label voice; everything else centers on the
              same control line. */}
          <div className={`scheduled-prefill-platforms__records ${activePlatform.rowClassName}`}>
            <div className="scheduled-prefill-platforms__platform">
              <span className="scheduled-prefill-platforms__platform-icon" aria-hidden="true">
                <ActivePlatformIcon size={24} />
              </span>
              <h3 className="scheduled-prefill-platforms__platform-title">
                {t(`${baseKey}.services.${activeServiceKey}`)}
              </h3>
            </div>
            <div className="scheduled-prefill-platforms__record-field">
              <span className="form-field-label">{t(`${baseKey}.records.label`)}</span>
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
                size="md"
              />
            </div>
            {activeSchedule && (
              <div className="scheduled-prefill-platforms__record-field scheduled-prefill-platforms__record-field--name">
                <FormField label={t(`${baseKey}.records.name`)}>
                  {(field) => (
                    <TextInput
                      {...field}
                      {...noAutofill}
                      className="scheduled-prefill-platforms__record-name"
                      size="md"
                      value={activeSchedule.name}
                      onChange={(event) =>
                        onScheduleChange(activeServiceKey, {
                          ...activeSchedule,
                          name: event.target.value
                        })
                      }
                      disabled={disabled}
                    />
                  )}
                </FormField>
              </div>
            )}
            <div className="scheduled-prefill-platforms__record-actions">
              <ActionMenu
                isOpen={actionsOpen}
                onClose={() => setActionsOpen(false)}
                align="right"
                width="w-48"
                trigger={
                  <Button
                    type="button"
                    variant="menu"
                    size="md"
                    open={actionsOpen}
                    className="w-full"
                    disabled={disabled}
                    onClick={() => setActionsOpen((open) => !open)}
                    aria-expanded={actionsOpen}
                    aria-haspopup="menu"
                    rightSection={<ChevronDown size={16} aria-hidden="true" />}
                  >
                    {t('management.actions.menuLabel')}
                  </Button>
                }
              >
                <ActionMenuItem
                  onClick={() => {
                    setActionsOpen(false);
                    showNewSchedule(onAddSchedule(activeServiceKey));
                  }}
                >
                  {t(`${baseKey}.records.new`)}
                </ActionMenuItem>
                <ActionMenuItem
                  onClick={() => {
                    if (!activeSchedule) return;
                    setActionsOpen(false);
                    showNewSchedule(onDuplicateSchedule(activeServiceKey, activeSchedule.id));
                  }}
                  disabled={!activeSchedule}
                >
                  {t(`${baseKey}.records.saveAs`)}
                </ActionMenuItem>
                <ActionMenuItem
                  onClick={() => {
                    if (!activeSchedule) return;
                    setActionsOpen(false);
                    onScheduleChange(activeServiceKey, {
                      ...activeSchedule,
                      enabled: !activeSchedule.enabled
                    });
                  }}
                  disabled={!activeSchedule}
                >
                  {t(
                    `${baseKey}.records.${activeSchedule?.enabled ? 'disableSchedule' : 'enableSchedule'}`
                  )}
                </ActionMenuItem>
                <ActionMenuDivider />
                <ActionMenuDangerItem
                  onClick={() => {
                    if (!activeSchedule) return;
                    setActionsOpen(false);
                    onDeleteSchedule(activeServiceKey, activeSchedule.id);
                  }}
                  disabled={!activeSchedule || activeService.schedules.length === 1}
                >
                  {t(`${baseKey}.records.delete`)}
                </ActionMenuDangerItem>
              </ActionMenu>
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
