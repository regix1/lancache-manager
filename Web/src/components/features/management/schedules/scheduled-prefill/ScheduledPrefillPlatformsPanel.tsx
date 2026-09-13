import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { ActionMenu, ActionMenuDangerItem, ActionMenuItem } from '@components/ui/ActionMenu';
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
import { SCHEDULED_PREFILL_PLATFORM_UI } from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillSchedule,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillPlatformsPanelProps {
  cancellingRunIds?: string[];
  runErrors?: Record<string, string>;
  config: ScheduledPrefillConfigDto;
  containerSettings?: (disabled: boolean) => ReactNode;
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
  onCancelDownload: (serviceKey: ScheduledPrefillServiceKey, runId?: string) => void;
}

export function ScheduledPrefillPlatformsPanel({
  config,
  containerSettings,
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
  onCancelDownload,
  cancellingRunIds,
  runErrors
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

  return (
    <section className="scheduled-prefill-platforms-panel">
      <div className="scheduled-prefill-platforms">
        <nav
          className="scheduled-prefill-platforms__nav"
          aria-label={t(`${baseKey}.platforms.navLabel`)}
        >
          {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
            const platformMeta = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
            const PlatformIcon = platformMeta.icon;
            const isActive = activeServiceKey === serviceKey;

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
                onClick={() => {
                  setActionsOpen(false);
                  setActiveServiceKey(serviceKey);
                }}
              >
                <span className="scheduled-prefill-platforms__nav-icon" aria-hidden="true">
                  <PlatformIcon size={18} />
                </span>
                <span className="scheduled-prefill-platforms__nav-text">
                  <span className="scheduled-prefill-platforms__nav-label">
                    {t(`${baseKey}.services.${serviceKey}`)}
                  </span>
                </span>
              </Button>
            );
          })}
        </nav>

        <div className="scheduled-prefill-platforms__content" ref={contentRef}>
          {activeSchedule && (
            <ScheduledPrefillPlatformSection
              scheduleControls={
                <div
                  className={`scheduled-prefill-platforms__records ${activePlatform.rowClassName}`}
                >
                  <div className="scheduled-prefill-platforms__record-field">
                    <span className="form-field-label">{t(`${baseKey}.records.label`)}</span>
                    <EnhancedDropdown
                      options={activeService.schedules.map((schedule) => ({
                        value: schedule.id,
                        label: schedule.name
                      }))}
                      value={activeSchedule?.id ?? ''}
                      onChange={(scheduleId) => {
                        setActionsOpen(false);
                        setSelectedScheduleId(scheduleId);
                      }}
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
                      width="w-40"
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
              }
              containerSettings={containerSettings}
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
              onChange={(schedule) => onScheduleChange(activeServiceKey, schedule)}
              gameSelectionLoading={gameSelectionLoadingServiceKey === activeServiceKey}
              onSelectGames={() => onSelectGames(activeServiceKey, activeSchedule.id)}
              onClearGames={() => onClearGames(activeServiceKey, activeSchedule.id)}
              onStop={() => onStop(activeServiceKey)}
              onLogout={() => onLogout(activeServiceKey)}
              onStart={() => onStart(activeServiceKey)}
              onLogin={(reuseIntegration) => onLogin(activeServiceKey, reuseIntegration)}
              onDownload={() => onDownload(activeServiceKey, activeSchedule.id)}
              onCancelDownload={(runId) => onCancelDownload(activeServiceKey, runId)}
              cancellingRunIds={cancellingRunIds}
              runErrors={runErrors}
            />
          )}
        </div>
      </div>
    </section>
  );
}
