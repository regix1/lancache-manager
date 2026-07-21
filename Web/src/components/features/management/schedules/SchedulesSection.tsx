import React, { useState, useEffect, useCallback, useRef, memo } from 'react';
import './SchedulesSection.css';
import { useTranslation } from 'react-i18next';
import { ChevronRight } from 'lucide-react';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import HighlightGlow from '@components/ui/HighlightGlow';
import type { HighlightGlowVariant } from '@utils/highlightGlow';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { HelpPopover, HelpNote } from '@components/ui/HelpPopover';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { usePicsProgress } from '@contexts/usePicsProgress';
import ScheduleIntervalPicker from './ScheduleIntervalPicker';
import { useCountdownTimer } from '@hooks/useCountdownTimer';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useManagerLoading } from '@hooks/useManagerLoading';
import {
  isNotificationMode,
  isNotificationDisplayMode,
  type NotificationMode,
  type NotificationDisplayMode,
  type ServiceScheduleInfo
} from './types';
import { formatLastRun } from './scheduleFormatting';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { ScheduledPrefillScheduleDetail } from './scheduled-prefill/ScheduledPrefillScheduleDetail';

interface SchedulesSectionProps {
  isAdmin: boolean;
  onNavigateToEvictionSettings?: () => void;
  onNavigateToSteamApi?: () => void;
}

// Isolated countdown component - ticks every second without re-rendering the parent card
const CountdownDisplay = memo(function CountdownDisplay({
  nextRunUtc,
  intervalHours,
  isRunning
}: {
  nextRunUtc: string | null;
  intervalHours: number;
  isRunning: boolean;
}) {
  const { t } = useTranslation();
  const secondsRemaining = useCountdownTimer(nextRunUtc, isRunning);

  if (intervalHours === 0) {
    return (
      <span className="schedule-countdown disabled">{t('management.schedules.disabled')}</span>
    );
  }
  if (intervalHours === -1) {
    return (
      <span className="schedule-countdown disabled">
        {t('management.schedules.intervals.startupOnly')}
      </span>
    );
  }
  if (isRunning) {
    return <span className="schedule-timing-value">{t('management.schedules.statusRunning')}</span>;
  }

  const h = Math.floor(secondsRemaining / 3600);
  const m = Math.floor((secondsRemaining % 3600) / 60);
  const s = secondsRemaining % 60;
  let display: string;
  if (secondsRemaining <= 0) {
    display = t('management.schedules.soon');
  } else if (h > 0) {
    display = `${h}h ${m}m ${s}s`;
  } else if (m > 0) {
    display = `${m}m ${s}s`;
  } else {
    display = `${s}s`;
  }

  return <span className="schedule-countdown">{display}</span>;
});

type DepotScheduledScanMode = 'incremental' | 'full' | 'github';

const getDepotScheduledScanMode = (mode: boolean | string | undefined): DepotScheduledScanMode => {
  if (mode === 'github') {
    return 'github';
  }
  if (mode === false) {
    return 'full';
  }
  return 'incremental';
};

const toDepotScheduledScanModePayload = (mode: DepotScheduledScanMode): boolean | 'github' => {
  if (mode === 'github') {
    return 'github';
  }
  return mode === 'incremental';
};

interface DepotScheduleModeDropdownProps {
  mode: DepotScheduledScanMode;
  isDisabled: boolean;
  isSteamWebApiAvailable: boolean;
  onChange: (mode: DepotScheduledScanMode) => void;
}

const DepotScheduleModeDropdown = memo(function DepotScheduleModeDropdown({
  mode,
  isDisabled,
  isSteamWebApiAvailable,
  onChange
}: DepotScheduleModeDropdownProps) {
  const { t } = useTranslation();
  const options = [
    {
      value: 'incremental',
      label: isSteamWebApiAvailable
        ? t('management.depotMapping.modes.incremental')
        : t('management.depotMapping.modes.incrementalWebApiRequired'),
      disabled: !isSteamWebApiAvailable
    },
    {
      value: 'full',
      label: isSteamWebApiAvailable
        ? t('management.depotMapping.modes.full')
        : t('management.depotMapping.modes.fullWebApiRequired'),
      disabled: !isSteamWebApiAvailable
    },
    {
      value: 'github',
      label: t('management.depotMapping.modes.github')
    }
  ];

  const handleChange = useCallback(
    (value: string) => {
      onChange(value as DepotScheduledScanMode);
    },
    [onChange]
  );

  return (
    <EnhancedDropdown
      options={options}
      value={mode}
      onChange={handleChange}
      disabled={isDisabled}
      variant="button"
    />
  );
});

interface ScheduleCardProps {
  service: ServiceScheduleInfo;
  isAdmin: boolean;
  onIntervalChange: (key: string, intervalHours: number) => Promise<void>;
  onRunOnStartupChange: (key: string, runOnStartup: boolean) => Promise<void>;
  depotScheduledMode: DepotScheduledScanMode;
  isSteamWebApiAvailable: boolean;
  onDepotScanModeChange: (mode: DepotScheduledScanMode) => Promise<void>;
  onRunNow: (key: string) => Promise<void>;
  runningKey: string | null;
  justCompleted: boolean;
  completedVariant: HighlightGlowVariant;
  onNavigateToEvictionSettings?: () => void;
  onNotificationModeChange: (key: string, mode: NotificationMode) => Promise<void>;
  onNotificationDisplayModeChange: (key: string, mode: NotificationDisplayMode) => Promise<void>;
  onNavigateToSteamApi?: () => void;
}

const ScheduleCard = memo(function ScheduleCard({
  service,
  isAdmin,
  onIntervalChange,
  onRunOnStartupChange,
  depotScheduledMode,
  isSteamWebApiAvailable,
  onDepotScanModeChange,
  onRunNow,
  runningKey,
  justCompleted,
  completedVariant,
  onNavigateToEvictionSettings,
  onNotificationModeChange,
  onNotificationDisplayModeChange,
  onNavigateToSteamApi
}: ScheduleCardProps) {
  const { t } = useTranslation();
  const formattedNextRun = useFormattedDateTime(service.nextRunUtc);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const isDepotMapping = service.key === 'depotMapping';
  const isScheduledPrefill = service.key === 'scheduledPrefill';
  const isCacheReconciliation = service.key === 'cacheReconciliation';
  const isRunningThis = runningKey === service.key;
  // Zero interval means the schedule effectively won't run (for scheduled prefill this is
  // the HasAnyEnabledService gate reporting "no services enabled"). The dim only lives on
  // wrapper divs around the card body, not the Card itself, so ScheduledPrefillScheduleDetail
  // can keep its own Configure button and warning text at full opacity - opacity on an
  // ancestor cannot be undone by a descendant's own opacity.
  const isDimmed = service.intervalHours === 0;

  // Run-on-startup is hidden when the interval is "Startup only" (-1) - that schedule already
  // runs at startup, so the toggle is redundant - and for scheduled prefill, where startup is
  // set per platform. The settings disclosure only appears when it has at least one control.
  const hasStartupToggle = service.intervalHours !== -1 && !isScheduledPrefill;
  const showSettingsDisclosure =
    !isScheduledPrefill && (hasStartupToggle || service.supportsNotifications);

  const handleIntervalChange = useCallback(
    (hours: number) => {
      onIntervalChange(service.key, hours);
    },
    [service.key, onIntervalChange]
  );

  const handleRunNow = useCallback(() => {
    onRunNow(service.key);
  }, [service.key, onRunNow]);

  const handleRunOnStartupChange = useCallback(
    (value: string) => {
      onRunOnStartupChange(service.key, value === 'true');
    },
    [service.key, onRunOnStartupChange]
  );

  const handleDepotScanModeChange = useCallback(
    (value: DepotScheduledScanMode) => {
      onDepotScanModeChange(value);
    },
    [onDepotScanModeChange]
  );

  const handleNotificationModeChange = useCallback(
    (value: string) => {
      if (!isNotificationMode(value)) return;
      void onNotificationModeChange(service.key, value);
    },
    [service.key, onNotificationModeChange]
  );

  const handleNotificationDisplayModeChange = useCallback(
    (value: string) => {
      if (!isNotificationDisplayMode(value)) return;
      void onNotificationDisplayModeChange(service.key, value);
    },
    [service.key, onNotificationDisplayModeChange]
  );

  const notificationModeOptions: DropdownOption[] = [
    {
      value: 'all',
      label: t('management.schedules.notificationMode.all'),
      description: t('management.schedules.notificationMode.allDescription')
    },
    {
      value: 'manual',
      label: t('management.schedules.notificationMode.manual'),
      description: t('management.schedules.notificationMode.manualDescription')
    },
    {
      value: 'silent',
      label: t('management.schedules.notificationMode.silent'),
      description: t('management.schedules.notificationMode.silentDescription')
    }
  ];

  const notificationStyleOptions: DropdownOption[] = [
    {
      value: 'full',
      label: t('management.schedules.notificationStyle.full'),
      description: t('management.schedules.notificationStyle.fullDescription')
    },
    {
      value: 'condensed',
      label: t('management.schedules.notificationStyle.condensed'),
      description: t('management.schedules.notificationStyle.condensedDescription')
    }
  ];

  // NOTE: do NOT include a "saving" flag here. Toggling isDisabled on and off for the
  // ~50ms an API save is in flight causes every control on the card to briefly flash to
  // disabled styling and back - that's the source of the flicker the user reported on the
  // interval dropdown and Run Now button. Optimistic updates already make the UI feel
  // instant; there's no UX benefit to disabling siblings mid-save.
  const isDisabled = !isAdmin || isRunningThis;

  return (
    <HighlightGlow enabled={justCompleted} variant={completedVariant}>
      <Card className="schedule-card">
        <div className={`schedule-card-body${isDimmed ? ' schedule-card-disabled' : ''}`}>
          {/* Header */}
          <div className="schedule-card-header">
            <div className="schedule-card-title-group">
              <h3 className="schedule-card-name">
                <span
                  className={`schedule-status-dot${service.isRunning && service.intervalHours > 0 ? ' running' : ''}`}
                  aria-label={
                    service.isRunning
                      ? t('management.schedules.statusRunning')
                      : t('management.schedules.statusIdle')
                  }
                />
                {t(`management.schedules.services.${service.key}.displayName`)}
                <HelpPopover position="left" width={320}>
                  <p className="schedule-help-description">
                    {t(`management.schedules.services.${service.key}.description`)}
                  </p>
                  <HelpNote type="success">
                    {t(`management.schedules.services.${service.key}.gain`)}
                  </HelpNote>
                  <HelpNote type="warning">
                    {t(`management.schedules.services.${service.key}.loss`)}
                  </HelpNote>
                </HelpPopover>
              </h3>
              <p className="schedule-card-description">
                {t(`management.schedules.services.${service.key}.description`)}
              </p>
            </div>
            {!isScheduledPrefill && (
              <div className="schedule-card-header-actions">
                <Button
                  variant="filled"
                  color="green"
                  size="sm"
                  onClick={handleRunNow}
                  disabled={isDisabled || isDimmed}
                  loading={isRunningThis}
                  stableWidth
                  className="schedule-control-button"
                >
                  {t('management.schedules.runNow')}
                </Button>
              </div>
            )}
          </div>

          {/* Readout: last/next run and the interval picker as three labelled slots on
          one shared grid. Scheduled prefill replaces this single aggregate row (which
          only surfaced the MIN next-run / MAX last-run across services) with a
          per-service table inside ScheduledPrefillScheduleDetail below, where Run Now
          sits in that card's own command strip next to Configure. */}
          {!isScheduledPrefill && (
            <div className="schedule-readout-row">
              <div className="schedule-timing-item">
                <span className="schedule-timing-label">{t('management.schedules.lastRun')}</span>
                <span className="schedule-timing-value">
                  {formatLastRun(service.lastRunUtc, t)}
                </span>
              </div>
              <div className="schedule-timing-item">
                <span className="schedule-timing-label">{t('management.schedules.nextRun')}</span>
                <CountdownDisplay
                  nextRunUtc={service.nextRunUtc}
                  intervalHours={service.intervalHours}
                  isRunning={service.isRunning}
                />
                {service.nextRunUtc && service.intervalHours > 0 && !service.isRunning && (
                  <span className="schedule-next-run-date">{formattedNextRun}</span>
                )}
              </div>
              <div className="schedule-timing-item schedule-readout-interval">
                <span className="schedule-timing-label">{t('management.schedules.runEvery')}</span>
                <ScheduleIntervalPicker
                  intervalHours={service.intervalHours}
                  isDisabled={isDisabled}
                  onChange={handleIntervalChange}
                />
              </div>
            </div>
          )}

          {isDepotMapping && (
            <div className="schedule-extra-row">
              <div className="schedule-extra-copy">
                <span className="schedule-extra-label">
                  {t('management.schedules.services.depotMapping.scanModeLabel')}
                </span>
                <p className="schedule-extra-help">
                  {t('management.schedules.services.depotMapping.scanModeHelp')}
                </p>
              </div>
              <div className="schedule-extra-control">
                <DepotScheduleModeDropdown
                  mode={depotScheduledMode}
                  isDisabled={isDisabled}
                  isSteamWebApiAvailable={isSteamWebApiAvailable}
                  onChange={handleDepotScanModeChange}
                />
              </div>
            </div>
          )}
        </div>

        {isScheduledPrefill && (
          <ScheduledPrefillScheduleDetail
            disabled={isDisabled}
            dimmed={isDimmed}
            onRunNow={handleRunNow}
            runNowLoading={isRunningThis}
            runNowDisabled={isDisabled || isDimmed}
          />
        )}

        {/* Footer cluster: settings links, startup toggle, notifications control and
        Run Now in one fixed zone order, pinned to the card's bottom edge (margin-top:
        auto on .schedule-card-footer). The header + readout section above is the
        flexible middle region, so every card in a row shows these controls at the
        same position regardless of description length or how many info rows exist. */}
        <div className="schedule-card-footer">
          <div className={`schedule-card-body${isDimmed ? ' schedule-card-disabled' : ''}`}>
            {/* Reverse of the management-side "View Schedule" button: jumps to the Eviction
            Detection and Removal card in the Storage section and glows it into view. */}
            {isCacheReconciliation && onNavigateToEvictionSettings && (
              <div className="schedule-nav-row">
                <Button
                  variant="filled"
                  color="blue"
                  size="sm"
                  onClick={onNavigateToEvictionSettings}
                >
                  {t('management.schedules.services.cacheReconciliation.viewManagement')}
                </Button>
              </div>
            )}

            {isDepotMapping && onNavigateToSteamApi && (
              <div className="schedule-nav-row">
                <Button variant="filled" color="blue" size="sm" onClick={onNavigateToSteamApi}>
                  {t('management.schedules.services.depotMapping.configureSteamApi')}
                </Button>
              </div>
            )}

            {/* Run-on-startup, notifications and notification style are folded behind one
            disclosure so the resting card stays quiet - the primary controls (Run every, Run
            Now) live up top, and these per-run settings only appear when the row is expanded.
            Startup stays a toggle; the two notification settings use the shared dropdown so
            their selected value reads as plain text instead of a wide segmented bar. */}
            {showSettingsDisclosure && (
              <div className="schedule-settings">
                <button
                  type="button"
                  className={`schedule-settings-toggle${settingsOpen ? ' open' : ''}`}
                  onClick={() => setSettingsOpen((open) => !open)}
                  aria-expanded={settingsOpen}
                >
                  <ChevronRight className="schedule-settings-chevron" />
                  {t('management.schedules.settingsDisclosure')}
                </button>
                <CollapsibleRegion open={settingsOpen}>
                  <div className="schedule-settings-well">
                    {hasStartupToggle && (
                      <div className="schedule-settings-row">
                        <span className="schedule-row-label">
                          {t('management.schedules.runOnStartup')}
                        </span>
                        <div className="schedule-settings-control">
                          <ToggleSwitch
                            options={[
                              {
                                value: 'false',
                                label: t('management.schedules.toggleOff'),
                                activeColor: 'default'
                              },
                              {
                                value: 'true',
                                label: t('management.schedules.toggleOn'),
                                activeColor: 'success'
                              }
                            ]}
                            value={service.runOnStartup ? 'true' : 'false'}
                            onChange={handleRunOnStartupChange}
                            disabled={isDisabled}
                            title={t('management.schedules.runOnStartupTooltip')}
                            size="sm"
                          />
                        </div>
                      </div>
                    )}

                    {service.supportsNotifications && (
                      <div className="schedule-settings-row">
                        <Tooltip
                          content={t('management.schedules.notificationsHelp')}
                          className="inline-flex flex-shrink-0"
                        >
                          <span className="schedule-row-label">
                            {t('management.schedules.notificationsLabel')}
                          </span>
                        </Tooltip>
                        <div className="schedule-settings-control">
                          <EnhancedDropdown
                            options={notificationModeOptions}
                            value={service.notificationMode}
                            onChange={handleNotificationModeChange}
                            disabled={isDisabled}
                            variant="button"
                          />
                        </div>
                      </div>
                    )}

                    {service.supportsNotifications && (
                      <div className="schedule-settings-row">
                        <Tooltip
                          content={t('management.schedules.notificationStyleHelp')}
                          className="inline-flex flex-shrink-0"
                        >
                          <span className="schedule-row-label">
                            {t('management.schedules.notificationStyleLabel')}
                          </span>
                        </Tooltip>
                        <div className="schedule-settings-control">
                          <EnhancedDropdown
                            options={notificationStyleOptions}
                            value={service.notificationDisplayMode}
                            onChange={handleNotificationDisplayModeChange}
                            disabled={isDisabled}
                            variant="button"
                          />
                        </div>
                      </div>
                    )}
                  </div>
                </CollapsibleRegion>
              </div>
            )}
          </div>
        </div>
      </Card>
    </HighlightGlow>
  );
});

const SchedulesSection: React.FC<SchedulesSectionProps> = ({
  isAdmin,
  onNavigateToEvictionSettings,
  onNavigateToSteamApi
}) => {
  const { t } = useTranslation();
  const [schedules, setSchedules] = useState<ServiceScheduleInfo[]>([]);
  const { isLoading, setLoading, markLoaded } = useManagerLoading(true);
  const [error, setError] = useState<string | null>(null);
  const [runningKey, setRunningKey] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  const [runningAll, setRunningAll] = useState(false);
  // Map of schedule key -> glow variant. `navigate` is the default (2-pulse attention
  // grab) used by Run Now and external View Schedule navigation. `subtle` is used by
  // Reset to Defaults where every card flashes at once and needs to feel like an
  // acknowledgement rather than an attention-grab.
  const [completedKeys, setCompletedKeys] = useState<Record<string, HighlightGlowVariant>>({});
  const { on, off, connectionState } = useSignalR();
  const { addNotification } = useNotifications();
  const { progress: picsProgress, refreshProgress, updateProgress } = usePicsProgress();
  const { status: webApiStatus } = useSteamWebApiStatus();
  const depotScheduledMode = getDepotScheduledScanMode(picsProgress?.crawlIncrementalMode);
  const isSteamWebApiAvailable =
    picsProgress?.isWebApiAvailable === true ||
    webApiStatus?.isFullyOperational === true ||
    webApiStatus?.hasApiKey === true;

  // Keep a ref in sync with schedules so callbacks can read the latest value without
  // needing `schedules` in their useCallback deps - that would cause every callback to
  // re-create on every optimistic update and propagate new references through memoized
  // children, defeating memo and causing visible dropdown flicker on unrelated toggles.
  const schedulesRef = useRef(schedules);
  useEffect(() => {
    schedulesRef.current = schedules;
  }, [schedules]);

  const crawlIncrementalModeRef = useRef(picsProgress?.crawlIncrementalMode);
  useEffect(() => {
    crawlIncrementalModeRef.current = picsProgress?.crawlIncrementalMode;
  }, [picsProgress?.crawlIncrementalMode]);

  const fetchSchedules = useCallback(async () => {
    try {
      const data = (await ApiService.getSchedules()) as ServiceScheduleInfo[];
      setSchedules(data);
      setError(null);
    } catch {
      setError(t('management.schedules.fetchError'));
    }
  }, [t]);

  // Initial load
  useEffect(() => {
    setLoading(true);
    fetchSchedules().finally(() => markLoaded());
  }, [fetchSchedules, setLoading, markLoaded]);

  // Subscribe to real-time schedule updates via SignalR
  useEffect(() => {
    const handleSchedulesUpdated = (data: ServiceScheduleInfo[]) => {
      setSchedules(data);
      setError(null);
    };
    on('SchedulesUpdated', handleSchedulesUpdated);
    return () => off('SchedulesUpdated', handleSchedulesUpdated);
  }, [on, off]);

  // Refetch when SignalR reconnects to recover any missed updates
  useEffect(() => {
    if (connectionState === 'connected') {
      fetchSchedules();
    }
  }, [connectionState, fetchSchedules]);

  const handleIntervalChange = useCallback(
    async (key: string, intervalHours: number) => {
      try {
        await ApiService.updateSchedule(key, intervalHours);

        // If the user selects "Startup only" (-1), force runOnStartup=true on the backend.
        // Otherwise the service would never run at all: interval=-1 means "no scheduled
        // runs" in the base class loop, so the ONLY way work can happen is via the
        // startup pass - which requires runOnStartup=true.
        if (intervalHours === -1) {
          const current = schedulesRef.current.find((s) => s.key === key);
          if (current && !current.runOnStartup) {
            await ApiService.setScheduleRunOnStartup(key, true);
          }
        }

        await fetchSchedules();
      } catch {
        // Revert silently - SignalR SchedulesUpdated will correct state
      }
    },
    [fetchSchedules]
  );

  const handleRunOnStartupChange = useCallback(
    async (key: string, runOnStartup: boolean) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      // Optimistic update so the checkbox state flips immediately even before the server responds
      setSchedules((prev) => prev.map((s) => (s.key === key ? { ...s, runOnStartup } : s)));
      try {
        await ApiService.setScheduleRunOnStartup(key, runOnStartup);
        await fetchSchedules();
      } catch {
        // Revert optimistic update by refetching authoritative state
        await fetchSchedules();
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.runOnStartupFailed', { service: displayName }),
          details: { notificationType: 'error' }
        });
      }
    },
    [fetchSchedules, addNotification, t]
  );

  const handleNotificationModeChange = useCallback(
    async (key: string, mode: NotificationMode) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      // Optimistic update so the segmented control flips immediately even before the server responds
      setSchedules((prev) =>
        prev.map((s) => (s.key === key ? { ...s, notificationMode: mode } : s))
      );
      try {
        await ApiService.setScheduleNotificationMode(key, mode);
        await fetchSchedules();
      } catch {
        // Revert optimistic update by refetching authoritative state
        await fetchSchedules();
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.notificationModeFailed', { service: displayName }),
          details: { notificationType: 'error' }
        });
      }
    },
    [fetchSchedules, addNotification, t]
  );

  const handleNotificationDisplayModeChange = useCallback(
    async (key: string, mode: NotificationDisplayMode) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      // Optimistic update so the segmented control flips immediately even before the server responds
      setSchedules((prev) =>
        prev.map((s) => (s.key === key ? { ...s, notificationDisplayMode: mode } : s))
      );
      try {
        await ApiService.setScheduleNotificationDisplayMode(key, mode);
        await fetchSchedules();
      } catch {
        // Revert optimistic update by refetching authoritative state
        await fetchSchedules();
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.notificationStyleFailed', { service: displayName }),
          details: { notificationType: 'error' }
        });
      }
    },
    [fetchSchedules, addNotification, t]
  );

  const handleDepotScanModeChange = useCallback(
    async (mode: DepotScheduledScanMode) => {
      const previousMode = crawlIncrementalModeRef.current ?? true;

      updateProgress((prev) =>
        prev
          ? {
              ...prev,
              crawlIncrementalMode: toDepotScheduledScanModePayload(mode)
            }
          : prev
      );

      try {
        await ApiService.setDepotScheduledScanMode(mode);
        await refreshProgress();
      } catch {
        updateProgress((prev) =>
          prev
            ? {
                ...prev,
                crawlIncrementalMode: previousMode
              }
            : prev
        );
        await refreshProgress();
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.services.depotMapping.scanModeFailed', {
            service: t('management.schedules.services.depotMapping.displayName')
          }),
          details: { notificationType: 'error' }
        });
      }
    },
    [updateProgress, refreshProgress, addNotification, t]
  );

  const handleResetDefaults = useCallback(async () => {
    setResetting(true);
    try {
      await ApiService.resetSchedules();
      await fetchSchedules();

      addNotification({
        type: 'generic',
        status: 'completed',
        message: t('management.schedules.resetComplete'),
        details: { notificationType: 'success' }
      });

      // Flash all cards to confirm reset - subtle variant since every card glows at
      // once. The 1400ms reset just re-arms the trigger; the glow itself ends on its
      // own animationend.
      const flashed = Object.fromEntries(schedules.map((s) => [s.key, 'subtle' as const]));
      setCompletedKeys(flashed);
      setTimeout(() => setCompletedKeys({}), 1400);
    } catch {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.schedules.resetFailed'),
        details: { notificationType: 'error' }
      });
    } finally {
      setResetting(false);
    }
  }, [fetchSchedules, schedules, addNotification, t]);

  const handleRunAll = useCallback(async () => {
    setRunningAll(true);
    try {
      const { triggeredCount } = await ApiService.runAllSchedules();
      await fetchSchedules();

      addNotification({
        type: 'generic',
        status: 'completed',
        message: t('management.schedules.runAllTriggered', { count: triggeredCount }),
        details: { notificationType: 'success' }
      });

      // Flash all cards to acknowledge - same subtle variant as reset since the
      // entire grid lights up at once.
      const flashed = Object.fromEntries(schedules.map((s) => [s.key, 'subtle' as const]));
      setCompletedKeys(flashed);
      setTimeout(() => setCompletedKeys({}), 1400);
    } catch {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.schedules.runAllFailed'),
        details: { notificationType: 'error' }
      });
    } finally {
      setRunningAll(false);
    }
  }, [fetchSchedules, schedules, addNotification, t]);

  const handleRunNow = useCallback(
    async (key: string) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      setRunningKey(key);

      // Flash the card border immediately on click
      setCompletedKeys((prev) => ({ ...prev, [key]: 'navigate' }));
      setTimeout(
        () =>
          setCompletedKeys((prev) => {
            const next = { ...prev };
            delete next[key];
            return next;
          }),
        3000
      );

      try {
        await ApiService.triggerSchedule(key);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: t('management.schedules.runNowTriggered', { service: displayName }),
          details: { notificationType: 'success', serviceKey: key }
        });
      } catch {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.runNowFailed', { service: displayName }),
          details: { notificationType: 'error', serviceKey: key }
        });
      } finally {
        setRunningKey(null);
      }
    },
    [addNotification, t]
  );

  if (isLoading) {
    return (
      <div className="management-section animate-fade-in schedules-loading">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return <div className="management-section animate-fade-in schedules-error">{error}</div>;
  }

  return (
    <div className="management-section animate-fade-in schedules-section">
      <div className="schedules-section-header">
        <div>
          <h2 className="schedules-section-title">{t('management.schedules.title')}</h2>
          <p className="schedules-section-subtitle">{t('management.schedules.subtitle')}</p>
        </div>
        <div className="schedules-section-actions">
          <Button
            variant="filled"
            size="md"
            onClick={handleRunAll}
            disabled={!isAdmin || runningAll || resetting}
            loading={runningAll}
          >
            {t('management.schedules.runAll')}
          </Button>
          <Button
            variant="filled"
            color="yellow"
            size="md"
            onClick={handleResetDefaults}
            disabled={!isAdmin || resetting || runningAll}
            loading={resetting}
          >
            {t('management.schedules.resetToDefaults')}
          </Button>
        </div>
      </div>

      <div className="schedules-grid">
        {schedules.map((service) => (
          <div
            key={service.key}
            data-schedule-key={service.key}
            className={`schedules-grid-item${
              service.key === 'scheduledPrefill' ? ' schedules-grid-item--full' : ''
            }`}
          >
            <ScheduleCard
              service={service}
              isAdmin={isAdmin}
              onIntervalChange={handleIntervalChange}
              onRunOnStartupChange={handleRunOnStartupChange}
              depotScheduledMode={
                service.key === 'depotMapping' ? depotScheduledMode : 'incremental'
              }
              isSteamWebApiAvailable={service.key === 'depotMapping' && isSteamWebApiAvailable}
              onDepotScanModeChange={handleDepotScanModeChange}
              onRunNow={handleRunNow}
              runningKey={runningKey}
              justCompleted={!!completedKeys[service.key]}
              completedVariant={completedKeys[service.key] ?? 'navigate'}
              onNavigateToEvictionSettings={
                service.key === 'cacheReconciliation' ? onNavigateToEvictionSettings : undefined
              }
              onNavigateToSteamApi={
                service.key === 'depotMapping' ? onNavigateToSteamApi : undefined
              }
              onNotificationModeChange={handleNotificationModeChange}
              onNotificationDisplayModeChange={handleNotificationDisplayModeChange}
            />
          </div>
        ))}
      </div>
    </div>
  );
};

export default SchedulesSection;
