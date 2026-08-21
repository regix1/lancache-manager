import React, { useState, useEffect, useCallback, useMemo, useRef, memo } from 'react';
import './SchedulesSection.css';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Play, X } from 'lucide-react';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import HighlightGlow from '@components/ui/HighlightGlow';
import type { HighlightGlowVariant } from '@utils/highlightGlow';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { HelpPopover, HelpNote, HelpSection } from '@components/ui/HelpPopover';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { LoadingState } from '@components/ui/ManagerCard';
import ApiService, { type IncrementalViabilityCheck } from '@services/api.service';
import { ApiError } from '@services/apiError';
import { useNotifications } from '@contexts/notifications';
import { usePicsProgress } from '@contexts/usePicsProgress';
import { useSetupStatus } from '@contexts/useSetupStatus';
import ScheduleIntervalPicker from './ScheduleIntervalPicker';
import type { CustomSchedule } from './custom-schedule/types';
import { useCountdownTimer } from '@hooks/useCountdownTimer';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useManagerLoading } from '@hooks/useManagerLoading';
import { useOptimisticPending } from '@hooks/useOptimisticPending';
import {
  isNotificationMode,
  isNotificationDisplayMode,
  type NotificationMode,
  type NotificationDisplayMode,
  type PendingFullScan,
  type ServiceScheduleInfo
} from './types';
import { APP_EVENTS } from '@utils/constants';
import { formatCount } from '@utils/formatters';
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
  hasCustomSchedule,
  isRunning
}: {
  nextRunUtc: string | null;
  intervalHours: number;
  hasCustomSchedule: boolean;
  isRunning: boolean;
}) {
  const { t } = useTranslation();
  const secondsRemaining = useCountdownTimer(nextRunUtc, isRunning);

  // A saved schedule runs in preference to the interval, which is left untouched beside it - so
  // the two interval sentinels only describe the schedule when there is no custom one.
  if (!hasCustomSchedule) {
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

interface DepotScanModeAvailability {
  /** False until this page load has read the three facts below. */
  isKnown: boolean;
  isSetupCompleted: boolean;
  hasDepotMappings: boolean;
  isSteamWebApiAvailable: boolean;
}

/** The label shown in place of a scan mode that cannot run, plus the fuller reason under it. */
interface DepotScanModeRequirement {
  labelKey: string;
  helpKey: string;
}

/** Body the mode route answers with when the mode asked for cannot run on this install. */
interface ScanModeRefusal {
  stageKey?: string;
  error?: string;
}

// The refusal names the requirement in the same stage key the dropdown shows for it, so a save the
// server refuses says which requirement is missing rather than only that the save failed. The
// browser's copy of the facts can be a moment behind the server's - a key removed in another tab,
// or a progress blob restored from sessionStorage - and that is exactly when this runs.
const getScanModeRefusalKey = (error: unknown): string | null => {
  if (!(error instanceof ApiError) || error.status !== 400) {
    return null;
  }
  const refusal = error.body as ScanModeRefusal | null;
  return typeof refusal?.stageKey === 'string' && refusal.stageKey.length > 0
    ? refusal.stageKey
    : null;
};

// One place decides what each scan mode needs, so the dropdown and the save path can never
// disagree about which modes can run. Every term here reads a fact that survives at rest: the
// crawl closes its own Steam socket when it finishes, so a connection flag would grey both Steam
// modes out permanently.
const getDepotScanModeRequirement = (
  mode: DepotScheduledScanMode,
  availability: DepotScanModeAvailability
): DepotScanModeRequirement | null => {
  // A GitHub import needs neither Steam nor a key.
  if (mode === 'github') {
    return null;
  }
  // Each fact below arrives from its own request after mount. Until they have all landed nothing
  // counts as a missing requirement: an unread value read as an absent capability would grey both
  // Steam modes out on every page load and pop them back a moment later. This is the one place the
  // loading window is stated, so a fourth requirement cannot spell it a fourth way.
  if (!availability.isKnown) {
    return null;
  }
  if (!availability.isSetupCompleted) {
    return {
      labelKey:
        mode === 'incremental'
          ? 'management.depotMapping.modes.incrementalSetupRequired'
          : 'management.depotMapping.modes.fullSetupRequired',
      helpKey: 'management.depotMapping.modes.setupRequiredHelp'
    };
  }
  // An incremental scan asks Steam what changed since the mappings it already holds, so with none
  // stored there is nothing to compare against and Steam answers with a required full update.
  if (mode === 'incremental' && !availability.hasDepotMappings) {
    return {
      labelKey: 'management.depotMapping.modes.incrementalMappingsRequired',
      helpKey: 'management.depotMapping.modes.mappingsRequiredHelp'
    };
  }
  // Incremental reads the PICS changelist over the Steam client connection and never calls the
  // Steam Web API, so only the full scan, which enumerates every app through it, needs the key.
  if (mode === 'full' && !availability.isSteamWebApiAvailable) {
    return {
      labelKey: 'management.depotMapping.modes.fullWebApiRequired',
      helpKey: 'management.depotMapping.modes.fullWebApiRequiredHelp'
    };
  }
  return null;
};

interface DepotScheduleModeDropdownProps {
  mode: DepotScheduledScanMode;
  isDisabled: boolean;
  availability: DepotScanModeAvailability;
  onChange: (mode: DepotScheduledScanMode) => void;
}

const DepotScheduleModeDropdown = memo(function DepotScheduleModeDropdown({
  mode,
  isDisabled,
  availability,
  onChange
}: DepotScheduleModeDropdownProps) {
  const { t } = useTranslation();
  const buildOption = (scanMode: DepotScheduledScanMode): DropdownOption => {
    const requirement = getDepotScanModeRequirement(scanMode, availability);
    if (!requirement) {
      return {
        value: scanMode,
        label: t(`management.depotMapping.modes.${scanMode}`)
      };
    }
    // The label is the reason in short form, matching how the missing Web API key already reads;
    // the description carries the way out, which is too long for a single truncated line.
    return {
      value: scanMode,
      label: t(requirement.labelKey),
      description: t(requirement.helpKey),
      disabled: true
    };
  };
  const options: DropdownOption[] = [
    buildOption('incremental'),
    buildOption('full'),
    buildOption('github')
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

/** Nothing is requested until the user asks, so idle is the state this starts and stays in. */
type IncrementalCheckState =
  | { phase: 'idle' }
  // Carries the answer already on screen, if there is one. A checking phase that held nothing
  // pulled the result panel out of the open accordion on every press and put it back when the
  // reply landed, which reads as the accordion closing and reopening.
  | { phase: 'checking'; previous: IncrementalViabilityCheck | null }
  | { phase: 'answered'; check: IncrementalViabilityCheck }
  | { phase: 'failed' };

/** Which of the three things Steam can say about an incremental scan the answer amounts to. */
type IncrementalCheckOutcome = 'unreachable' | 'viable' | 'fullRequired';

const INCREMENTAL_CHECK_MESSAGE_KEYS: Record<IncrementalCheckOutcome, string> = {
  unreachable: 'management.schedules.services.depotMapping.incrementalUnreachable',
  viable: 'management.schedules.services.depotMapping.incrementalViable',
  fullRequired: 'management.schedules.services.depotMapping.incrementalFullRequired'
};

// A connection failure fills the figures with placeholders and can leave the full-scan flag set
// either way, so reachability is decided before that flag is read - the other order reports a
// timeout as "Steam wants a full update", which Steam never said. Anything that is not clearly
// viable falls to fullRequired rather than to nothing, so no answer can render an empty well.
const getIncrementalCheckOutcome = (check: IncrementalViabilityCheck): IncrementalCheckOutcome => {
  if (check.error) {
    return 'unreachable';
  }
  if (check.isViable && !check.willTriggerFullScan) {
    return 'viable';
  }
  return 'fullRequired';
};

/** Both figures are the server's own estimates, so both read as approximate once they are non-zero. */
const formatIncrementalEstimate = (value: number): string =>
  value > 0 ? `~${formatCount(value)}` : formatCount(value);

interface DepotIncrementalCheckProps {
  isDisabled: boolean;
  mode: DepotScheduledScanMode;
}

// Asking Steam what an incremental scan would do opens a real Steam connection whenever the
// server's own answer has aged past an hour, so the request hangs off this button and nothing
// else: no mount effect, no dropdown open, no render path reaches it. Inside that hour the server
// replays its cached answer, so looking a second time costs nothing.
const DepotIncrementalCheck = memo(function DepotIncrementalCheck({
  isDisabled,
  mode
}: DepotIncrementalCheckProps) {
  const { t } = useTranslation();
  const [state, setState] = useState<IncrementalCheckState>({ phase: 'idle' });

  // Which request the row is currently willing to accept an answer from. Keeping the last
  // answer visible means the dismiss button is now on screen while a request is out, so a reply
  // that lands after the panel was closed has to be dropped rather than reopening it.
  const currentRequest = useRef(0);

  // Drops a verdict about one scan mode when the row switches to another, the same job a
  // key on this component used to do. It is an effect and not a key because the mode is
  // derived from live progress data: any refresh that momentarily reports a different value
  // remounted this row inside an open accordion, and the panel losing a child and getting it
  // back reads as it closing and reopening. Guarded on the previous value so a refresh
  // carrying the same mode leaves an answer on screen. The request counter moves with the
  // mode, since a reply still on its way was asked about the mode that has just been left and
  // would otherwise land in the cleared panel as though it described the new one.
  const checkedMode = useRef(mode);
  useEffect(() => {
    if (checkedMode.current === mode) {
      return;
    }
    checkedMode.current = mode;
    currentRequest.current += 1;
    setState({ phase: 'idle' });
  }, [mode]);

  const runCheck = useCallback(async (): Promise<void> => {
    const request = currentRequest.current + 1;
    currentRequest.current = request;
    // The answer already on screen stays there until the new one replaces it in place, so a
    // second press never changes the height of the panel this row sits in.
    setState((prev) => ({
      phase: 'checking',
      previous: prev.phase === 'answered' ? prev.check : null
    }));
    try {
      const check = await ApiService.checkIncrementalViability();
      if (currentRequest.current !== request) {
        return;
      }
      setState({ phase: 'answered', check });
    } catch {
      if (currentRequest.current !== request) {
        return;
      }
      // The request itself never reached an answer, which is a different thing from Steam being
      // unreachable, so it gets its own sentence rather than borrowing the Steam one.
      setState({ phase: 'failed' });
    }
  }, []);

  const handleCheck = useCallback((): void => {
    void runCheck();
  }, [runCheck]);

  // Back to idle, which is the state the row starts in, so the Check button is live again and
  // the next press fetches a fresh answer rather than replaying the one that was just closed.
  // Bumping the request counter retires any reply still on its way, which would otherwise put
  // the panel back after it was closed.
  const handleDismiss = useCallback((): void => {
    currentRequest.current += 1;
    setState({ phase: 'idle' });
  }, []);

  const isChecking = state.phase === 'checking';
  // The previous answer counts as the one to show while a new one is on its way, so the panel
  // holds its size across a press instead of emptying and refilling.
  const check =
    state.phase === 'answered' ? state.check : state.phase === 'checking' ? state.previous : null;
  const outcome = check ? getIncrementalCheckOutcome(check) : null;

  // The same control closes the answer and the "check did not finish" note, so it is written
  // once here. It can be pressed while a request is out, since the previous answer stays on
  // screen through a re-check - handleDismiss retires the reply rather than cancelling it.
  const dismissButton = (
    <button
      type="button"
      className="btn-icon-square btn-icon-square--sm themed-border-radius-sm focus-ring incremental-check-dismiss"
      onClick={handleDismiss}
      aria-label={t('management.schedules.services.depotMapping.dismissIncrementalCheck')}
    >
      <X className="w-4 h-4" />
    </button>
  );

  return (
    <>
      <div className="schedule-detail-row">
        {/* The popover, not a standing paragraph, carries what Check does and what its two
            figures actually count. It replaces the label's tooltip rather than joining it, so
            the row keeps one help affordance. */}
        <span className="schedule-detail-label schedule-detail-label-help">
          {t('management.schedules.services.depotMapping.incrementalCheckLabel')}
          <HelpPopover position="left" width={320}>
            <HelpSection
              title={t('management.schedules.services.depotMapping.incrementalHelpTitle')}
            >
              {t('management.schedules.services.depotMapping.incrementalCheckHelp')}
            </HelpSection>
            <HelpSection
              title={t('management.schedules.services.depotMapping.incrementalFiguresTitle')}
            >
              {t('management.schedules.services.depotMapping.incrementalFiguresHelp')}
            </HelpSection>
            <HelpNote type="tip">
              {t('management.schedules.services.depotMapping.incrementalGithubTip')}
            </HelpNote>
          </HelpPopover>
        </span>
        <div className="schedule-detail-control">
          {/* Full control column, like every other control in this panel, so the rows line up
              down one edge. The rest matches Run Now on the scheduled-prefill card: the label
              stays put and stableWidth overlays the spinner on it, so a Steam call that takes
              seconds does not swap the text and resize the button under the pointer. Loading is
              not repeated in disabled - Button already blocks a second press while it spins, and
              hand-toggling disabled for an in-flight request is what flashed every control on
              this card to the greyed-out look (see the note on isDisabled below). */}
          <Button
            type="button"
            variant="subtle"
            size="sm"
            fullWidth
            stableWidth
            onClick={handleCheck}
            disabled={isDisabled}
            loading={isChecking}
          >
            {t('management.schedules.services.depotMapping.checkIncremental')}
          </Button>
        </div>
      </div>

      {check && outcome && (
        <div className="well-surface incremental-check-result divided-list">
          <div className="incremental-check-verdict">
            <div className="incremental-check-verdict-text">
              <p className="incremental-check-line">{t(INCREMENTAL_CHECK_MESSAGE_KEYS[outcome])}</p>
              {/* Steam's own words for why it could not answer. Kept because it names the actual
                  failure (a timeout reads differently from a refused connection) and the line
                  above deliberately does not guess at one. */}
              {outcome === 'unreachable' && check.error && (
                <p className="incremental-check-error">{check.error}</p>
              )}
            </div>
            {dismissButton}
          </div>
          {/* No figures on the unreachable branch: the server sends placeholders there, not
              measurements. On the full-scan branch a zero gap means there is no stored baseline to
              be behind, so the figure is left out rather than shown as the digit 0. */}
          {outcome !== 'unreachable' && (outcome === 'viable' || check.changeGap > 0) && (
            <div className="incremental-check-figure">
              <span className="caps-label">
                {t('management.schedules.services.depotMapping.steamWideChanges')}
              </span>
              <span className="incremental-check-value tabular-nums">
                {formatCount(check.changeGap)}
              </span>
            </div>
          )}
          {outcome !== 'unreachable' && (
            <div className="incremental-check-figure">
              <span className="caps-label">
                {t('management.schedules.services.depotMapping.appsWorthChecking')}
              </span>
              <span className="incremental-check-value tabular-nums">
                {formatIncrementalEstimate(check.estimatedAppsToScan)}
              </span>
            </div>
          )}
        </div>
      )}

      {state.phase === 'failed' && (
        <div className="well-surface incremental-check-result">
          <div className="incremental-check-verdict">
            <div className="incremental-check-verdict-text">
              <p className="incremental-check-line">
                {t('management.schedules.services.depotMapping.incrementalCheckFailed')}
              </p>
            </div>
            {dismissButton}
          </div>
        </div>
      )}
    </>
  );
});

interface ScheduleRowProps {
  service: ServiceScheduleInfo;
  isAdmin: boolean;
  onIntervalChange: (key: string, intervalHours: number) => Promise<void>;
  onCustomScheduleChange: (key: string, schedule: CustomSchedule) => Promise<void>;
  onRunOnStartupChange: (key: string, runOnStartup: boolean) => Promise<void>;
  depotScheduledMode: DepotScheduledScanMode;
  depotScanModeAvailability: DepotScanModeAvailability;
  onDepotScanModeChange: (mode: DepotScheduledScanMode) => Promise<void>;
  onRunNow: (key: string) => Promise<void>;
  /** True while this row's own click is covering the gap between the POST resolving and the
   * SignalR SchedulesUpdated push that flips service.isRunning - see isRunningDot below. */
  isPendingRun: boolean;
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
  onCustomScheduleChange,
  onRunOnStartupChange,
  depotScheduledMode,
  depotScanModeAvailability,
  onDepotScanModeChange,
  onRunNow,
  isPendingRun,
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
  // Running state now flows through the unified activity registry, which holds a finished run visible
  // long enough to be seen; service.isRunning is the fallback for the brief window before the first
  // ActivityUpdated snapshot arrives.
  const activity = useActivityStatus();
  const isRunningDot = activity.isActive('schedule', service.key, 'running') || service.isRunning;

  const isDepotMapping = service.key === 'depotMapping';
  const isCacheReconciliation = service.key === 'cacheReconciliation';
  // The Xbox sign-in registers a tracked XboxMapping operation for the whole device-code wait, which
  // is what turns isRunningOrPending true below and greys out Run Now. Without this the button looks
  // broken for up to 15 minutes, so the row says which one of the two is holding it.
  const isAwaitingSignIn = service.key === 'xboxMapping' && service.awaitingSignIn === true;
  // Server truth (isRunningDot) catches a run started by the scheduler itself, another browser
  // tab, or already in progress before this page loaded; isPendingRun covers the ~1.5s gap
  // between this click's POST resolving and that flag arriving over SignalR. Run Now gates on
  // both so a duplicate click can never slip through either window.
  const isRunningOrPending = isRunningDot || isPendingRun;
  const customSchedule = service.customSchedule ?? null;
  // Zero interval means the schedule effectively won't run; the informational cells dim
  // but the interval picker stays fully legible - it is the way back out of the state.
  // A custom schedule is what runs when one is saved, so the interval value no longer
  // says anything about whether the row is idle.
  const isDimmed = service.intervalHours === 0 && customSchedule === null;

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

  const handleCustomScheduleChange = useCallback(
    (schedule: CustomSchedule) => {
      onCustomScheduleChange(service.key, schedule);
    },
    [service.key, onCustomScheduleChange]
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

  // Cancelling the Full Scan Required prompt hides it for the rest of the browser tab, on purpose.
  // This is the way back to it. App owns the prompt and sits far above this row, so the request
  // travels as a window event, the same route the dashboard widgets use to ask App to change tab.
  // The figures come from the schedules response, so they survive a reload that the SignalR event
  // behind the first showing does not.
  const handleShowFullScanPrompt = useCallback(() => {
    if (!service.pendingFullScan) return;
    window.dispatchEvent(
      new CustomEvent<PendingFullScan>(APP_EVENTS.SHOW_FULL_SCAN_MODAL, {
        detail: service.pendingFullScan
      })
    );
  }, [service.pendingFullScan]);

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
  // feel instant; there's no UX benefit to disabling siblings mid-save. This gates the
  // interval picker, toggles and dropdowns below - it stays scoped to this row's own
  // pending click, not the broader isRunningOrPending, since changing this service's own
  // settings while it happens to be running doesn't conflict with the run in progress.
  const isDisabled = !isAdmin || isPendingRun;
  // Run Now additionally gates on isRunningOrPending (see above) - re-clicking it while the
  // same service is already running would trigger the identical run a second time for no
  // reason, which is the one control on this row where that distinction matters.
  const isRunNowDisabled = !isAdmin || isRunningOrPending;

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
                    <div className="schedule-flag-slot">
                      <Badge
                        variant={service.runOnStartup ? 'success' : 'neutral'}
                        className="schedule-task-flag"
                      >
                        {service.runOnStartup
                          ? t('management.schedules.startupOn')
                          : t('management.schedules.startupOff')}
                      </Badge>
                    </div>
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
            {/* A custom schedule computes its own next run and leaves the interval untouched,
            so gating the tooltip on a positive interval alone would drop it for exactly the
            rows that most need an exact timestamp. */}
            {service.nextRunUtc &&
            (service.intervalHours > 0 || customSchedule !== null) &&
            !service.isRunning ? (
              <Tooltip
                content={`${t('management.schedules.nextRun')}: ${formattedNextRun}`}
                className="schedule-countdown-slot"
              >
                <CountdownDisplay
                  nextRunUtc={service.nextRunUtc}
                  intervalHours={service.intervalHours}
                  hasCustomSchedule={customSchedule !== null}
                  isRunning={service.isRunning}
                />
              </Tooltip>
            ) : (
              <CountdownDisplay
                nextRunUtc={service.nextRunUtc}
                intervalHours={service.intervalHours}
                hasCustomSchedule={customSchedule !== null}
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
              customSchedule={customSchedule}
              onCustomScheduleChange={handleCustomScheduleChange}
              variant="ghost"
            />
          </div>

          <div className="schedule-cell-actions" onClick={stopRowToggle}>
            {/* Sign-in first: a waiting sign-in also reads as running, and "already running" would
            send the user looking for a catalog refresh that is not happening. */}
            <Tooltip
              content={
                isAwaitingSignIn
                  ? t('management.schedules.services.xboxMapping.signInWaitingHelp')
                  : isRunningOrPending
                    ? t('management.schedules.runNowAlreadyRunning', {
                        service: t(`management.schedules.services.${service.key}.displayName`)
                      })
                    : t('management.schedules.runNow')
              }
              className="schedule-action-slot"
            >
              <button
                type="button"
                className="schedule-icon-btn schedule-run-now themed-border-radius-sm"
                onClick={handleRunNow}
                disabled={isRunNowDisabled || isDimmed}
                aria-label={t('management.schedules.runNow')}
              >
                {isRunningOrPending ? (
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

            {/* First of the settings: it answers whether this task runs at all outside its
            interval, which is read before any question about how a run behaves. */}
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

            {isDepotMapping && (
              <div className="schedule-detail-row">
                <Tooltip
                  content={t('management.schedules.services.depotMapping.scanModeHelp')}
                  position="bottom"
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
                    availability={depotScanModeAvailability}
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

            {/* Sits under the mode control because it answers the question that control asks:
            what an incremental run would actually do this time. It is information on request,
            not a gate - the mode dropdown decides what can run from facts that cost nothing.
            The mode is a prop, never a key: it is derived from live progress data, so keying on
            it let an unrelated refresh remount this row inside the open accordion. Dropping a
            stale verdict is handled inside the component instead. */}
            {isDepotMapping && (
              <DepotIncrementalCheck isDisabled={isDisabled} mode={depotScheduledMode} />
            )}

            {/* Only while the server still reports a skipped scan, so the row goes back to its
            usual shape the moment a full scan or a GitHub download clears the condition. */}
            {isDepotMapping && service.pendingFullScan && (
              <div className="schedule-detail-row">
                <Tooltip
                  content={t('management.schedules.services.depotMapping.fullScanRequiredHelp')}
                  position="bottom"
                  className="inline-flex flex-shrink-0"
                >
                  <span className="schedule-detail-label">
                    {t('management.schedules.services.depotMapping.fullScanRequiredLabel')}
                  </span>
                </Tooltip>
                <div className="schedule-detail-control">
                  <Button variant="subtle" size="sm" fullWidth onClick={handleShowFullScanPrompt}>
                    {t('management.schedules.services.depotMapping.showFullScanPrompt')}
                  </Button>
                </div>
              </div>
            )}

            {/* Only while the sign-in is still waiting, so the row goes back to its usual shape as
            soon as the code is approved, cancelled or expires. Nothing to click: the approval
            happens on Microsoft's page, so this row carries the sentence itself instead of the
            label-plus-control pair the settings rows use. */}
            {isAwaitingSignIn && (
              <div className="schedule-detail-row">
                <span className="schedule-detail-label">
                  {t('management.schedules.services.xboxMapping.signInWaitingLabel')}
                </span>
                <p className="schedule-detail-summary">
                  {t('management.schedules.services.xboxMapping.signInWaitingHelp')}
                </p>
              </div>
            )}

            {service.supportsNotifications && (
              <div className="schedule-detail-row">
                <Tooltip
                  content={t('management.schedules.notificationsHelp')}
                  position="bottom"
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
                  position="bottom"
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
  isPendingRun: boolean;
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
  isPendingRun,
  justCompleted,
  completedVariant
}: ScheduledPrefillCardProps) {
  const { t } = useTranslation();
  // Running state flows through the unified activity registry, which holds a finished run visible long
  // enough to be seen; service.isRunning is the pre-seed fallback.
  const activity = useActivityStatus();
  const isRunningDot = activity.isActive('schedule', service.key, 'running') || service.isRunning;
  // Server truth (isRunningDot) catches a run started by the scheduler itself, another browser
  // tab, or already in progress before this page loaded; isPendingRun covers the ~1.5s gap
  // before that flag arrives over SignalR.
  const isRunningOrPending = isRunningDot || isPendingRun;
  // The HasAnyEnabledService gate reports "no services enabled" as interval 0. The dim
  // only wraps the header, not the detail, so its Configure button and warning text (the
  // way out of the disabled state) stay at full opacity - opacity on an ancestor cannot
  // be undone by a descendant's own opacity.
  const isDimmed = service.intervalHours === 0;
  // Scoped to this card's own pending click, same as the table rows - Configure and the
  // per-service interval pickers don't need to block on a genuine run in progress.
  const isDisabled = !isAdmin || isPendingRun;
  const isRunNowDisabled = !isAdmin || isRunningOrPending;

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
          runNowLoading={isRunningOrPending}
          runNowDisabled={isRunNowDisabled || isDimmed}
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
  // Per-key optimistic pending, not a single shared flag: covers the ~1.5s gap between a Run
  // Now POST resolving and the SignalR SchedulesUpdated push that confirms it server-side, for
  // however many of the 12 rows get clicked, not just the most recent one.
  const { isPending, markStarting, clearPending } = useOptimisticPending<string>();
  const [resetting, setResetting] = useState(false);
  const [runningAll, setRunningAll] = useState(false);
  // Map of schedule key -> glow variant. `navigate` is the default (2-pulse attention
  // grab) used by Run Now and external View Schedule navigation. `subtle` is used by
  // Reset to Defaults where every row flashes at once and needs to feel like an
  // acknowledgement rather than an attention-grab.
  const [completedKeys, setCompletedKeys] = useState<Record<string, HighlightGlowVariant>>({});
  const { on, off, connectionState } = useSignalR();
  const { addNotification } = useNotifications();
  const {
    progress: picsProgress,
    isLoading: picsLoading,
    refreshProgress,
    updateProgress
  } = usePicsProgress();
  const { status: webApiStatus } = useSteamWebApiStatus();
  const { setupStatus, isSetupStatusKnown } = useSetupStatus();
  const depotScheduledMode = getDepotScheduledScanMode(picsProgress?.crawlIncrementalMode);
  // The progress blob is restored from sessionStorage on a reload, so a non-null picsProgress is
  // not evidence that this page load has an answer - it can predate the API key being added or the
  // mappings being wiped. The fetch flag is the evidence.
  // isSetupStatusKnown, not `setupStatus !== null`: a failed status call falls back to a
  // placeholder that reads as an incomplete setup, so testing for null answered "setup is not
  // finished" whenever the route had simply failed once, and both Steam modes went to
  // "(setup required)" on a fully configured install.
  const isDepotScanModeKnown = !picsLoading && picsProgress !== null && isSetupStatusKnown;
  const isSetupCompleted = setupStatus?.isCompleted === true;
  // A full scan empties the mapping table at the start and refills it as it goes, so a count read
  // while a crawl is running says nothing about whether a baseline exists. Only the count at rest
  // answers that.
  const hasDepotMappings =
    picsProgress !== null && (picsProgress.isProcessing || picsProgress.depotMappingsFound > 0);
  // Both terms are the server's own cached answer arriving by two routes, so the dropdown judges a
  // full scan on the fact the save route judges it on. Either one settles it: the status route
  // stops answering entirely once it takes a 401, and the progress route carries the same fact. A
  // stored key is deliberately not a third term - a key Steam has rejected is still stored, so
  // reading it as availability offered a full scan that the save route then refused.
  const isSteamWebApiAvailable =
    picsProgress?.isWebApiAvailable === true || webApiStatus?.isFullyOperational === true;
  // Each term is reduced to the boolean it contributes before the object is built. picsProgress is
  // replaced on every PICS push during a crawl, and depending on the object itself gave the rows a
  // new prop identity at the push rate, re-rendering all twelve of them for a counter that changes
  // no answer here.
  const depotScanModeAvailability = useMemo<DepotScanModeAvailability>(
    () => ({
      isKnown: isDepotScanModeKnown,
      isSetupCompleted,
      hasDepotMappings,
      isSteamWebApiAvailable
    }),
    [isDepotScanModeKnown, isSetupCompleted, hasDepotMappings, isSteamWebApiAvailable]
  );

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
      // This push is the server truth the optimistic Run Now flag was waiting for, so retire the flag
      // here rather than letting the hook's safety timeout do it. A row is settled once the server
      // reports it running, or once its last-run stamp moves - a run short enough to finish between
      // two pushes never reports isRunning at all, and without that second test its button would keep
      // spinning for seconds after the work was done.
      const previous = schedulesRef.current;
      data.forEach((service) => {
        const before = previous.find((entry) => entry.key === service.key);
        if (
          service.isRunning ||
          (before !== undefined && before.lastRunUtc !== service.lastRunUtc)
        ) {
          clearPending(service.key);
        }
      });
      setSchedules(data);
      setError(null);
    };
    on('SchedulesUpdated', handleSchedulesUpdated);
    return () => off('SchedulesUpdated', handleSchedulesUpdated);
  }, [on, off, clearPending]);

  // Refetch when SignalR reconnects to recover any missed updates
  useEffect(() => {
    if (connectionState === 'connected') {
      fetchSchedules();
    }
  }, [connectionState, fetchSchedules]);

  const handleIntervalChange = useCallback(
    async (key: string, intervalHours: number) => {
      try {
        const current = schedulesRef.current.find((s) => s.key === key);

        // A saved schedule runs in preference to the interval, so it has to be cleared first -
        // otherwise it would swallow the interval the user just picked and nothing would change.
        if (current?.customSchedule) {
          await ApiService.setScheduleCustomSchedule(key, null);
        }

        await ApiService.updateSchedule(key, intervalHours);

        // If the user selects "Startup only" (-1), force runOnStartup=true on the backend.
        // Otherwise the service would never run at all: interval=-1 means "no scheduled
        // runs" in the base class loop, so the ONLY way work can happen is via the
        // startup pass - which requires runOnStartup=true.
        if (intervalHours === -1 && current && !current.runOnStartup) {
          await ApiService.setScheduleRunOnStartup(key, true);
        }

        await fetchSchedules();
      } catch {
        // Revert silently - SignalR SchedulesUpdated will correct state
      }
    },
    [fetchSchedules]
  );

  const handleCustomScheduleChange = useCallback(
    async (key: string, schedule: CustomSchedule) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      // Optimistic so the trigger reads as the custom schedule immediately
      setSchedules((prev) =>
        prev.map((s) => (s.key === key ? { ...s, customSchedule: schedule } : s))
      );
      try {
        await ApiService.setScheduleCustomSchedule(key, schedule);
        await fetchSchedules();
      } catch {
        // Revert optimistic update by refetching authoritative state
        await fetchSchedules();
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.customScheduleFailed', { service: displayName }),
          details: { notificationType: 'error' }
        });
      }
    },
    [fetchSchedules, addNotification, t]
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
      // The mode route accepts any well-formed value without checking whether that mode can run,
      // so this is the only thing standing between a stale or programmatic call and a scheduled
      // scan that can only ever skip itself. A click that lands on a stale render has to say why
      // nothing happened, or the control reads as broken.
      const requirement = getDepotScanModeRequirement(mode, depotScanModeAvailability);
      if (requirement) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t(requirement.helpKey),
          details: { notificationType: 'error' }
        });
        return;
      }

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
      } catch (error: unknown) {
        updateProgress((prev) =>
          prev
            ? {
                ...prev,
                crawlIncrementalMode: previousMode
              }
            : prev
        );
        await refreshProgress();
        const scanModeFailed = t('management.schedules.services.depotMapping.scanModeFailed', {
          service: t('management.schedules.services.depotMapping.displayName')
        });
        const refusalKey = getScanModeRefusalKey(error);
        addNotification({
          type: 'generic',
          status: 'failed',
          // A stage key this build does not carry a translation for falls back to the plain
          // failure rather than printing the key path at the user.
          message: refusalKey ? t(refusalKey, { defaultValue: scanModeFailed }) : scanModeFailed,
          details: { notificationType: 'error' }
        });
      }
    },
    [depotScanModeAvailability, updateProgress, refreshProgress, addNotification, t]
  );

  const flashAll = useCallback(() => {
    const flashed = Object.fromEntries(
      schedules.map((schedule) => [schedule.key, 'subtle' as const])
    );
    setCompletedKeys(flashed);
    setTimeout(() => setCompletedKeys({}), 1400);
  }, [schedules]);

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

      flashAll();
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
  }, [fetchSchedules, flashAll, addNotification, t]);

  const handleRunAll = useCallback(async () => {
    setRunningAll(true);
    try {
      const { triggeredCount, alreadyRunningCount } = await ApiService.runAllSchedules();
      await fetchSchedules();

      // Services that were mid-run are not left out: each had a follow-up run armed and runs
      // again when its current one ends. Report that second number instead of only the started
      // count, which on its own reads as if the rest were ignored.
      const queuedNext = alreadyRunningCount ?? 0;
      addNotification({
        type: 'generic',
        status: 'completed',
        message:
          queuedNext > 0
            ? t('management.schedules.runAllTriggeredWithQueued', {
                count: triggeredCount,
                queued: queuedNext
              })
            : t('management.schedules.runAllTriggered', { count: triggeredCount }),
        details: { notificationType: 'success' }
      });

      flashAll();
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
  }, [fetchSchedules, flashAll, addNotification, t]);

  const handleRunNow = useCallback(
    async (key: string) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      markStarting(key);

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
        const result = await ApiService.triggerSchedule(key);
        if (result.alreadyRunning) {
          // The click still armed the service's pending-run flag, so one more run follows the
          // one in progress - say that rather than only "already running", which reads as a
          // no-op. Nothing is cleared here on purpose: the SchedulesUpdated handler retires the
          // pending flag once server truth actually lands, so clearing it on this response would
          // re-enable the button while the run it queued behind is still going.
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.schedules.runNowQueuedNext', { service: displayName }),
            details: { notificationType: 'info', serviceKey: key }
          });
        } else {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.schedules.runNowTriggered', { service: displayName }),
            details: { notificationType: 'success', serviceKey: key }
          });
        }
      } catch {
        clearPending(key);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.runNowFailed', { service: displayName }),
          details: { notificationType: 'error', serviceKey: key }
        });
      }
    },
    [addNotification, t, markStarting, clearPending]
  );

  if (isLoading) {
    return (
      <div className="management-section animate-fade-in schedules-loading">
        <div className="w-full">
          <LoadingState shape="schedule" rows={5} />
        </div>
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
        <div className="flex items-center gap-2 min-w-0">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-accent)]" />
          <h3 className="management-group-label caps-label">{t('management.schedules.title')}</h3>
        </div>
        <div className="schedules-section-actions">
          <Button
            variant="filled"
            color="run"
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
              onCustomScheduleChange={handleCustomScheduleChange}
              onRunOnStartupChange={handleRunOnStartupChange}
              depotScheduledMode={
                service.key === 'depotMapping' ? depotScheduledMode : 'incremental'
              }
              depotScanModeAvailability={depotScanModeAvailability}
              onDepotScanModeChange={handleDepotScanModeChange}
              onRunNow={handleRunNow}
              isPendingRun={isPending(service.key)}
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
          isPendingRun={isPending(prefillSchedule.key)}
          justCompleted={!!completedKeys[prefillSchedule.key]}
          completedVariant={completedKeys[prefillSchedule.key] ?? 'navigate'}
        />
      )}
    </div>
  );
};

export default SchedulesSection;
