import React, { useState, useEffect, useCallback, useRef, memo } from 'react';
import './SchedulesSection.css';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Play } from 'lucide-react';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
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
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import StatusDot from '@components/common/StatusDot';
import { ScheduledPrefillScheduleDetail } from './scheduled-prefill/ScheduledPrefillScheduleDetail';

interface SchedulesSectionProps {
  isAdmin: boolean;
  onNavigateToEvictionSettings?: () => void;
  onNavigateToSteamApi?: () => void;
}

// Isolated countdown component - ticks every second without re-rendering the parent row
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
    // Minute precision an hour out: a dozen rows each ticking a seconds digit reads
    // as constant motion across the table. Seconds only appear inside the final hour,
    // where imminence is the information.
    display = `${h}h ${m}m`;
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
      className="w-full"
    />
  );
});

interface ScheduleRowProps {
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

// One schedule = one row of the shared table. The whole row toggles a detail well
// (CollapsibleRegion) holding the secondary settings, so opening a row only ever grows
// that row - no other row or column moves.
const ScheduleRow = memo(function ScheduleRow({
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
}: ScheduleRowProps) {
  const { t } = useTranslation();
  const formattedNextRun = useFormattedDateTime(service.nextRunUtc);
  const [detailOpen, setDetailOpen] = useState(false);
  // Running state now flows through the unified activity registry; service.isRunning is the fallback
  // for the brief window before the first ActivityUpdated snapshot arrives.
  const activity = useActivityStatus();
  const isRunningDot = activity.isActive('schedule', service.key, 'running') || service.isRunning;

  const isDepotMapping = service.key === 'depotMapping';
  const isCacheReconciliation = service.key === 'cacheReconciliation';
  const isRunningThis = runningKey === service.key;
  // Zero interval means the schedule effectively won't run; the informational cells dim
  // but the interval picker stays fully legible - it is the way back out of the state.
  const isDimmed = service.intervalHours === 0;

  // Run-on-startup is hidden when the interval is "Startup only" (-1): that schedule
  // already runs at startup, so the toggle is redundant.
  const hasStartupToggle = service.intervalHours !== -1;
  const hasDetail =
    hasStartupToggle ||
    service.supportsNotifications ||
    isDepotMapping ||
    (isCacheReconciliation && !!onNavigateToEvictionSettings);

  const toggleDetail = useCallback(() => {
    setDetailOpen((open) => !open);
  }, []);

  // The interval picker, action buttons and help popover live inside the clickable row;
  // their clicks must not double as a row toggle.
  const stopRowToggle = useCallback((event: React.MouseEvent) => {
    event.stopPropagation();
  }, []);

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
  // ~50ms an API save is in flight causes every control on the row to briefly flash to
  // disabled styling and back - that was the source of the flicker previously reported
  // on the interval dropdown and Run Now button. Optimistic updates already make the UI
  // feel instant; there's no UX benefit to disabling siblings mid-save.
  const isDisabled = !isAdmin || isRunningThis;

  // Settings-at-a-glance under the task name; the detail well below stays the place
  // where they are edited.
  const hasSettingsFlags = hasStartupToggle || service.supportsNotifications || isDepotMapping;

  return (
    <HighlightGlow enabled={justCompleted} variant={completedVariant}>
      <div
        className={`schedule-item${detailOpen ? ' schedule-item--open' : ''}`}
        data-schedule-key={service.key}
      >
        {/* Whole-row click is a pointer convenience only: the row holds nested buttons and
        dropdowns, so it must not be a button itself (nested-interactive). The chevron is
        the accessible toggle - real button, aria-expanded, focus ring. */}
        <div
          className={`schedule-table-cols schedule-row${
            hasDetail ? ' schedule-row--interactive' : ''
          }${isDimmed ? ' schedule-row--dimmed' : ''}`}
          onClick={hasDetail ? toggleDetail : undefined}
        >
          <div className="schedule-cell-task">
            <StatusDot
              state={isRunningDot ? 'active' : 'inactive'}
              label={
                isRunningDot
                  ? t('management.schedules.statusRunning')
                  : t('management.schedules.statusIdle')
              }
            />
            <div className="schedule-task-text">
              <span className="schedule-task-name">
                {t(`management.schedules.services.${service.key}.displayName`)}
                <span className="schedule-task-help" onClick={stopRowToggle}>
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
                </span>
              </span>
              {/* Compact pills, same colour axes as the scheduled-prefill platform
              badges: filled purple = all runs, filled blue = manual only, dotted
              outline = silent. Each tooltip pairs the value with its label. */}
              {hasSettingsFlags && (
                <span className="schedule-task-flags">
                  {hasStartupToggle && (
                    <Tooltip
                      content={`${t('management.schedules.runOnStartup')}: ${service.runOnStartup ? t('management.schedules.toggleOn') : t('management.schedules.toggleOff')}`}
                      className="schedule-flag-slot"
                    >
                      <Badge
                        variant={service.runOnStartup ? 'success' : 'neutral'}
                        className="schedule-task-flag"
                      >
                        {service.runOnStartup
                          ? t('management.schedules.startupOn')
                          : t('management.schedules.startupOff')}
                      </Badge>
                    </Tooltip>
                  )}
                  {service.supportsNotifications && (
                    <>
                      <Tooltip
                        content={`${t('management.schedules.notificationsLabel')}: ${t(`management.schedules.notificationMode.${service.notificationMode}`)}`}
                        className="schedule-flag-slot"
                      >
                        <Badge
                          variant={
                            service.notificationMode === 'silent'
                              ? 'waiting-outline'
                              : service.notificationMode === 'manual'
                                ? 'info'
                                : 'waiting'
                          }
                          className="schedule-task-flag"
                        >
                          {t(`management.schedules.notificationMode.${service.notificationMode}`)}
                        </Badge>
                      </Tooltip>
                      <Tooltip
                        content={`${t('management.schedules.notificationStyleLabel')}: ${t(`management.schedules.notificationStyle.${service.notificationDisplayMode}`)}`}
                        className="schedule-flag-slot"
                      >
                        <Badge variant="neutral" className="schedule-task-flag">
                          {t(
                            `management.schedules.notificationStyle.${service.notificationDisplayMode}`
                          )}
                        </Badge>
                      </Tooltip>
                    </>
                  )}
                  {isDepotMapping && (
                    <Tooltip
                      content={`${t('management.schedules.services.depotMapping.scanModeLabel')}: ${t(`management.depotMapping.modes.${depotScheduledMode}`)}`}
                      className="schedule-flag-slot"
                    >
                      <Badge variant="neutral" className="schedule-task-flag">
                        {t(`management.depotMapping.modes.${depotScheduledMode}`)}
                      </Badge>
                    </Tooltip>
                  )}
                </span>
              )}
            </div>
          </div>

          <div className="schedule-cell schedule-cell-last">
            <span className="caps-label schedule-cell-label">
              {t('management.schedules.lastRun')}
            </span>
            <span className="schedule-timing-value">{formatLastRun(service.lastRunUtc, t)}</span>
          </div>

          <div className="schedule-cell schedule-cell-next">
            <span className="caps-label schedule-cell-label">
              {t('management.schedules.nextRun')}
            </span>
            {/* The absolute date lives in the tooltip rather than a second line under
            every countdown - the relative time is the readout, the exact timestamp is
            the detail. */}
            {service.nextRunUtc && service.intervalHours > 0 && !service.isRunning ? (
              <Tooltip
                content={`${t('management.schedules.nextRun')}: ${formattedNextRun}`}
                className="schedule-countdown-slot"
              >
                <CountdownDisplay
                  nextRunUtc={service.nextRunUtc}
                  intervalHours={service.intervalHours}
                  isRunning={service.isRunning}
                />
              </Tooltip>
            ) : (
              <CountdownDisplay
                nextRunUtc={service.nextRunUtc}
                intervalHours={service.intervalHours}
                isRunning={service.isRunning}
              />
            )}
          </div>

          <div className="schedule-cell schedule-cell-interval" onClick={stopRowToggle}>
            <span className="caps-label schedule-cell-label">
              {t('management.schedules.runEvery')}
            </span>
            <ScheduleIntervalPicker
              intervalHours={service.intervalHours}
              isDisabled={isDisabled}
              onChange={handleIntervalChange}
              variant="ghost"
            />
          </div>

          <div className="schedule-cell-actions" onClick={stopRowToggle}>
            <Tooltip content={t('management.schedules.runNow')} className="schedule-action-slot">
              <button
                type="button"
                className="schedule-icon-btn schedule-run-now themed-border-radius-sm"
                onClick={handleRunNow}
                disabled={isDisabled || isDimmed}
                aria-label={t('management.schedules.runNow')}
              >
                {isRunningThis ? (
                  <LoadingSpinner size="xs" inline />
                ) : (
                  <>
                    {/* Desktop shows the glyph, phones swap it for the label (CSS):
                    icon-only reads fine in a table's action rail but as decoration
                    on a stretched tile button. */}
                    <Play className="w-4 h-4 schedule-run-icon" />
                    <span className="schedule-run-label">{t('management.schedules.runNow')}</span>
                  </>
                )}
              </button>
            </Tooltip>
            {hasDetail && (
              <button
                type="button"
                className="schedule-icon-btn schedule-chevron themed-border-radius-sm"
                onClick={toggleDetail}
                aria-expanded={detailOpen}
                aria-label={
                  detailOpen
                    ? t('management.schedules.hideDetails')
                    : t('management.schedules.showDetails')
                }
              >
                <ChevronDown className="w-4 h-4" />
              </button>
            )}
          </div>
        </div>

        {hasDetail && (
          <CollapsibleRegion open={detailOpen} contentClassName="schedule-row-detail">
            {/* One-line summary anchors the expanded view now that the row itself no
            longer carries the description; the full copy stays in the (?) popover. */}
            <p className="schedule-detail-summary">
              {t(`management.schedules.services.${service.key}.summary`)}
            </p>
            {isDepotMapping && (
              <div className="schedule-detail-row">
                <Tooltip
                  content={t('management.schedules.services.depotMapping.scanModeHelp')}
                  className="inline-flex flex-shrink-0"
                >
                  <span className="schedule-detail-label">
                    {t('management.schedules.services.depotMapping.scanModeLabel')}
                  </span>
                </Tooltip>
                <div className="schedule-detail-control">
                  <DepotScheduleModeDropdown
                    mode={depotScheduledMode}
                    isDisabled={isDisabled}
                    isSteamWebApiAvailable={isSteamWebApiAvailable}
                    onChange={handleDepotScanModeChange}
                  />
                </div>
                {onNavigateToSteamApi && (
                  <Button variant="subtle" size="sm" onClick={onNavigateToSteamApi}>
                    {t('management.schedules.services.depotMapping.configureSteamApi')}
                  </Button>
                )}
              </div>
            )}

            {hasStartupToggle && (
              <div className="schedule-detail-row">
                <span className="schedule-detail-label">
                  {t('management.schedules.runOnStartup')}
                </span>
                <div className="schedule-detail-control">
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
              <div className="schedule-detail-row">
                <Tooltip
                  content={t('management.schedules.notificationsHelp')}
                  className="inline-flex flex-shrink-0"
                >
                  <span className="schedule-detail-label">
                    {t('management.schedules.notificationsLabel')}
                  </span>
                </Tooltip>
                <div className="schedule-detail-control">
                  <EnhancedDropdown
                    options={notificationModeOptions}
                    value={service.notificationMode}
                    onChange={handleNotificationModeChange}
                    disabled={isDisabled}
                    variant="button"
                    className="w-full"
                  />
                </div>
              </div>
            )}

            {service.supportsNotifications && (
              <div className="schedule-detail-row">
                <Tooltip
                  content={t('management.schedules.notificationStyleHelp')}
                  className="inline-flex flex-shrink-0"
                >
                  <span className="schedule-detail-label">
                    {t('management.schedules.notificationStyleLabel')}
                  </span>
                </Tooltip>
                <div className="schedule-detail-control">
                  <EnhancedDropdown
                    options={notificationStyleOptions}
                    value={service.notificationDisplayMode}
                    onChange={handleNotificationDisplayModeChange}
                    disabled={isDisabled}
                    variant="button"
                    className="w-full"
                  />
                </div>
              </div>
            )}

            {/* Reverse of the management-side "View Schedule" button: jumps to the Eviction
            Detection and Removal card in the Storage section and glows it into view. */}
            {isCacheReconciliation && onNavigateToEvictionSettings && (
              <div className="schedule-detail-nav">
                <Button variant="subtle" size="sm" onClick={onNavigateToEvictionSettings}>
                  {t('management.schedules.services.cacheReconciliation.viewManagement')}
                </Button>
              </div>
            )}
          </CollapsibleRegion>
        )}
      </div>
    </HighlightGlow>
  );
});

interface ScheduledPrefillCardProps {
  service: ServiceScheduleInfo;
  isAdmin: boolean;
  onRunNow: (key: string) => Promise<void>;
  runningKey: string | null;
  justCompleted: boolean;
  completedVariant: HighlightGlowVariant;
}

// Scheduled prefill keeps its own full-width card: it carries five independent service
// schedules and container states in ScheduledPrefillScheduleDetail, which doesn't fit a
// single table row.
const ScheduledPrefillCard = memo(function ScheduledPrefillCard({
  service,
  isAdmin,
  onRunNow,
  runningKey,
  justCompleted,
  completedVariant
}: ScheduledPrefillCardProps) {
  const { t } = useTranslation();
  const isRunningThis = runningKey === service.key;
  // Running state flows through the unified activity registry; service.isRunning is the pre-seed fallback.
  const activity = useActivityStatus();
  const isRunningDot = activity.isActive('schedule', service.key, 'running') || service.isRunning;
  // The HasAnyEnabledService gate reports "no services enabled" as interval 0. The dim
  // only wraps the header, not the detail, so its Configure button and warning text (the
  // way out of the disabled state) stay at full opacity - opacity on an ancestor cannot
  // be undone by a descendant's own opacity.
  const isDimmed = service.intervalHours === 0;
  const isDisabled = !isAdmin || isRunningThis;

  const handleRunNow = useCallback(() => {
    onRunNow(service.key);
  }, [service.key, onRunNow]);

  return (
    <HighlightGlow enabled={justCompleted} variant={completedVariant}>
      <Card className="schedule-card">
        <div className={`schedule-card-body${isDimmed ? ' schedule-card-disabled' : ''}`}>
          <div className="schedule-card-header">
            <div className="schedule-card-title-group">
              <h3 className="schedule-card-name">
                <StatusDot
                  state={isRunningDot ? 'active' : 'inactive'}
                  label={
                    isRunningDot
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
              {/* Short summary on the card, like the table rows above - the full
              description lives only in the (?) popover so it isn't shown twice. */}
              <p className="schedule-card-description">
                {t(`management.schedules.services.${service.key}.summary`)}
              </p>
            </div>
          </div>
        </div>

        <ScheduledPrefillScheduleDetail
          disabled={isDisabled}
          dimmed={isDimmed}
          onRunNow={handleRunNow}
          runNowLoading={isRunningThis}
          runNowDisabled={isDisabled || isDimmed}
        />
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
  // Reset to Defaults where every row flashes at once and needs to feel like an
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

  // Bumped on every SignalR SchedulesUpdated. fetchSchedules captures this before its request and
  // discards the response if a newer push landed while the GET was in flight, so a full-list GET
  // snapshot (mount, reconnect, post Run All/Reset) can never roll back a fresher live update - e.g.
  // a run START/END dot change delivered during the fetch.
  const signalrGenerationRef = useRef(0);
  // Only one GET is ever in flight. A second caller (e.g. the reconnect effect firing right after the
  // mount effect) can't race it - it records a trailing refresh instead, which runs once when the
  // current fetch settles. So two GETs never resolve stale-last, a failing fetch never discards a
  // concurrent successful one, and a reconnect recovery is never dropped.
  const isFetchingRef = useRef(false);
  const pendingRefetchRef = useRef(false);

  const crawlIncrementalModeRef = useRef(picsProgress?.crawlIncrementalMode);
  useEffect(() => {
    crawlIncrementalModeRef.current = picsProgress?.crawlIncrementalMode;
  }, [picsProgress?.crawlIncrementalMode]);

  const fetchSchedules = useCallback(async () => {
    if (isFetchingRef.current) {
      // A refresh was requested while one is already in flight (e.g. a reconnect during the mount
      // GET). Record it so exactly one more fetch runs when the current one settles.
      pendingRefetchRef.current = true;
      return;
    }
    isFetchingRef.current = true;
    try {
      do {
        pendingRefetchRef.current = false;
        const generationAtRequest = signalrGenerationRef.current;
        try {
          const data = (await ApiService.getSchedules()) as ServiceScheduleInfo[];
          // A SignalR SchedulesUpdated arrived while this GET was in flight - it is fresher than this
          // snapshot, so drop the GET result rather than roll back the live state.
          if (signalrGenerationRef.current === generationAtRequest) {
            setSchedules(data);
            setError(null);
          }
        } catch {
          // Only surface the fatal error view on an initial-load failure (nothing on screen yet) AND
          // only when no SignalR push landed during this GET - a push may have just populated the list
          // (schedulesRef lags a render), and a transient refetch failure must not blank live data.
          if (
            schedulesRef.current.length === 0 &&
            signalrGenerationRef.current === generationAtRequest
          ) {
            setError(t('management.schedules.fetchError'));
          }
        }
        // If another refresh was requested mid-fetch, run one more pass rather than dropping it.
      } while (pendingRefetchRef.current);
    } finally {
      isFetchingRef.current = false;
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
      signalrGenerationRef.current += 1;
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
      // Optimistic update so the dropdown flips immediately even before the server responds
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
      // Optimistic update so the dropdown flips immediately even before the server responds
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

      // Flash every row to confirm reset - subtle variant since they all glow at
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

      // Flash every row to acknowledge - same subtle variant as reset since the
      // entire list lights up at once.
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

      // Flash the row border immediately on click
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

  const genericSchedules = schedules.filter((service) => service.key !== 'scheduledPrefill');
  const prefillSchedule = schedules.find((service) => service.key === 'scheduledPrefill');

  return (
    <div className="management-section animate-fade-in schedules-section">
      <div className="schedules-section-header">
        <div>
          {/* Keyline bar beside the title so this header matches the other management
              sections' group headers. Subtitle is indented to sit under the title. */}
          <div className="flex items-center gap-2">
            <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
            <h2 className="schedules-section-title">{t('management.schedules.title')}</h2>
          </div>
          <p className="schedules-section-subtitle pl-3">{t('management.schedules.subtitle')}</p>
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
            variant="default"
            size="md"
            onClick={handleResetDefaults}
            disabled={!isAdmin || resetting || runningAll}
            loading={resetting}
          >
            {t('management.schedules.resetToDefaults')}
          </Button>
        </div>
      </div>

      {genericSchedules.length > 0 && (
        <div className="schedule-table divided-list">
          <div className="schedule-table-cols schedule-table-head caps-label">
            <span>{t('management.schedules.taskColumn')}</span>
            <span>{t('management.schedules.lastRun')}</span>
            <span>{t('management.schedules.nextRun')}</span>
            <span>{t('management.schedules.runEvery')}</span>
            <span aria-hidden="true" />
          </div>
          {genericSchedules.map((service) => (
            <ScheduleRow
              key={service.key}
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
          ))}
        </div>
      )}

      {prefillSchedule && (
        <ScheduledPrefillCard
          service={prefillSchedule}
          isAdmin={isAdmin}
          onRunNow={handleRunNow}
          runningKey={runningKey}
          justCompleted={!!completedKeys[prefillSchedule.key]}
          completedVariant={completedKeys[prefillSchedule.key] ?? 'navigate'}
        />
      )}
    </div>
  );
};

export default SchedulesSection;
