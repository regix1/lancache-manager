import React, { useState, useEffect, useCallback, useRef, memo } from 'react';
import './SchedulesSection.css';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import HighlightGlow from '@components/ui/HighlightGlow';
import { Checkbox } from '@components/ui/Checkbox';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications';
import { getScheduleIntervalOptions } from './constants';
import { useCountdownTimer } from '@hooks/useCountdownTimer';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import type { ServiceScheduleInfo } from './types';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';

interface SchedulesSectionProps {
  isAdmin: boolean;
  highlightScheduleKey?: string | null;
}

// Isolated countdown component — ticks every second without re-rendering the parent card
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

// Isolated, memoized dropdown — only takes primitive props so it short-circuits re-renders
// when unrelated fields on the parent `service` object change (e.g. a run-on-startup toggle
// causing an optimistic state update). Without this, the dropdown's EnhancedDropdown would
// receive a new options array + onChange callback on every parent re-render and flicker.
interface ScheduleIntervalDropdownProps {
  intervalHours: number;
  isDisabled: boolean;
  onChange: (hours: number) => void;
}

const ScheduleIntervalDropdown = memo(function ScheduleIntervalDropdown({
  intervalHours,
  isDisabled,
  onChange
}: ScheduleIntervalDropdownProps) {
  const { t } = useTranslation();

  const formatIntervalLabel = (hours: number): string => {
    if (hours <= 0) return '';
    if (hours < 1) {
      const minutes = Math.round(hours * 60);
      return t('management.schedules.everyNMinutes', { count: minutes });
    }
    return t('management.schedules.everyNHours', { count: hours });
  };

  const standardOptions = getScheduleIntervalOptions(t);
  const currentVal =
    intervalHours === 0 ? '0' : intervalHours === -1 ? '-1' : String(intervalHours);
  const hasCurrentOption = standardOptions.some((opt) => opt.value === currentVal);
  const allOptions = hasCurrentOption
    ? standardOptions
    : [{ value: currentVal, label: formatIntervalLabel(intervalHours) }, ...standardOptions];

  const handleChange = useCallback(
    (value: string) => {
      onChange(parseFloat(value));
    },
    [onChange]
  );

  return (
    <EnhancedDropdown
      options={allOptions}
      value={currentVal}
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
  onRunNow: (key: string) => Promise<void>;
  runningKey: string | null;
  justCompleted: boolean;
  completedVariant: 'navigate' | 'subtle';
}

const ScheduleCard = memo(function ScheduleCard({
  service,
  isAdmin,
  onIntervalChange,
  onRunOnStartupChange,
  onRunNow,
  runningKey,
  justCompleted,
  completedVariant
}: ScheduleCardProps) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const formattedNextRun = useFormattedDateTime(service.nextRunUtc);

  const isRunningThis = runningKey === service.key;

  const formatLastRun = (lastRunUtc: string | null): string => {
    if (!lastRunUtc) {
      return t('management.schedules.neverRun');
    }
    const date = new Date(lastRunUtc);
    const nowMs = Date.now();
    const diffMs = nowMs - date.getTime();
    const diffSeconds = Math.floor(diffMs / 1000);
    if (diffSeconds < 60) {
      return t('management.schedules.justNow');
    }
    const diffMinutes = Math.floor(diffSeconds / 60);
    if (diffMinutes < 60) {
      return t('management.schedules.minutesAgo', { count: diffMinutes });
    }
    const diffHours = Math.floor(diffMinutes / 60);
    if (diffHours < 24) {
      return t('management.schedules.hoursAgo', { count: diffHours });
    }
    const diffDays = Math.floor(diffHours / 24);
    return t('management.schedules.daysAgo', { count: diffDays });
  };

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
    (e: React.ChangeEvent<HTMLInputElement>) => {
      onRunOnStartupChange(service.key, e.target.checked);
    },
    [service.key, onRunOnStartupChange]
  );

  const handleToggleExpand = useCallback(() => {
    setExpanded((prev) => !prev);
  }, []);

  // NOTE: do NOT include a "saving" flag here. Toggling isDisabled on and off for the
  // ~50ms an API save is in flight causes every control on the card to briefly flash to
  // disabled styling and back — that's the source of the flicker the user reported on the
  // interval dropdown and Run Now button. Optimistic updates already make the UI feel
  // instant; there's no UX benefit to disabling siblings mid-save.
  const isDisabled = !isAdmin || isRunningThis;

  const hasExpandableContent = true;

  return (
    <HighlightGlow enabled={justCompleted} variant={completedVariant}>
      <Card
        className={`schedule-card${service.intervalHours === 0 ? ' schedule-card-disabled' : ''}`}
      >
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
            </h3>
            <p className="schedule-card-description">
              {t(`management.schedules.services.${service.key}.description`)}
            </p>
          </div>
        </div>

        {/* Timing Info */}
        <div className="schedule-timing-row">
          <div className="schedule-timing-item">
            <span className="schedule-timing-label">{t('management.schedules.lastRun')}</span>
            <span className="schedule-timing-value">{formatLastRun(service.lastRunUtc)}</span>
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
        </div>

        {/* Controls */}
        <div className="schedule-controls-row">
          <div className="schedule-dropdown-wrapper">
            <ScheduleIntervalDropdown
              intervalHours={service.intervalHours}
              isDisabled={isDisabled}
              onChange={handleIntervalChange}
            />
          </div>
          <Button
            variant="outline"
            size="sm"
            onClick={handleRunNow}
            disabled={isDisabled}
            loading={isRunningThis}
            className="schedule-run-button"
          >
            {t('management.schedules.runNow')}
          </Button>
        </div>

        {/* Run-on-startup toggle — hidden when interval is "Startup only" (-1) since the
          entire point of that schedule IS to run at startup, making the toggle redundant. */}
        {service.intervalHours !== -1 && (
          <div className="schedule-startup-row">
            <Checkbox
              id={`run-on-startup-${service.key}`}
              checked={service.runOnStartup}
              disabled={isDisabled}
              onChange={handleRunOnStartupChange}
              title={t('management.schedules.runOnStartupTooltip')}
              label={t('management.schedules.runOnStartup')}
            />
          </div>
        )}

        {/* Expandable Gain/Loss */}
        {hasExpandableContent && (
          <div>
            <button
              className="schedule-expand-toggle"
              onClick={handleToggleExpand}
              aria-expanded={expanded}
            >
              <span className={`schedule-expand-chevron${expanded ? ' open' : ''}`}>▼</span>
              {expanded
                ? t('management.schedules.hideDetails')
                : t('management.schedules.showDetails')}
            </button>
            <div className={`schedule-expandable${expanded ? ' open' : ''}`}>
              <div className="schedule-expandable-inner">
                <div className="schedule-gain-loss-item">
                  <span className="schedule-gain-loss-label gain">
                    {t('management.schedules.gain')}
                  </span>
                  <p className="schedule-gain-loss-text">
                    {t(`management.schedules.services.${service.key}.gain`)}
                  </p>
                </div>
                <div className="schedule-gain-loss-item">
                  <span className="schedule-gain-loss-label loss">
                    {t('management.schedules.loss')}
                  </span>
                  <p className="schedule-gain-loss-text">
                    {t(`management.schedules.services.${service.key}.loss`)}
                  </p>
                </div>
              </div>
            </div>
          </div>
        )}
      </Card>
    </HighlightGlow>
  );
});

const SchedulesSection: React.FC<SchedulesSectionProps> = ({ isAdmin, highlightScheduleKey }) => {
  const { t } = useTranslation();
  const [schedules, setSchedules] = useState<ServiceScheduleInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [runningKey, setRunningKey] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  // Map of schedule key -> glow variant. `navigate` is the default (2-pulse attention
  // grab) used by Run Now and external View Schedule navigation. `subtle` is used by
  // Reset to Defaults where every card flashes at once and needs to feel like an
  // acknowledgement rather than an attention-grab.
  const [completedKeys, setCompletedKeys] = useState<Record<string, 'navigate' | 'subtle'>>({});
  const { on, off, connectionState } = useSignalR();
  const { addNotification } = useNotifications();

  // Keep a ref in sync with schedules so callbacks can read the latest value without
  // needing `schedules` in their useCallback deps — that would cause every callback to
  // re-create on every optimistic update and propagate new references through memoized
  // children, defeating memo and causing visible dropdown flicker on unrelated toggles.
  const schedulesRef = useRef(schedules);
  useEffect(() => {
    schedulesRef.current = schedules;
  }, [schedules]);

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
    fetchSchedules().finally(() => setLoading(false));
  }, [fetchSchedules]);

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

  // React to external navigation (e.g. from Data section cards): flash the target card
  // border and scroll it into view. Uses the same completed-flash animation as Run Now.
  //
  // Why the retry loop: when the user clicks View Schedule, both `activeSection` and
  // `highlightScheduleKey` flip in the same tick. The SchedulesSection mounts fresh and
  // `schedules` starts empty while the initial fetch is in flight — so the target card
  // does not exist in the DOM yet when this effect first runs. We retry the querySelector
  // for up to ~2s so the scroll fires as soon as the card renders.
  useEffect(() => {
    if (!highlightScheduleKey) return;
    const key = highlightScheduleKey;

    setCompletedKeys((prev) => ({ ...prev, [key]: 'navigate' }));

    let cancelled = false;
    let attempts = 0;
    const maxAttempts = 40; // 40 * 50ms = 2s
    const tryScroll = () => {
      if (cancelled) return;
      const el = document.querySelector<HTMLElement>(`[data-schedule-key="${key}"]`);
      if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        return;
      }
      if (++attempts < maxAttempts) {
        setTimeout(tryScroll, 50);
      }
    };
    tryScroll();

    const flashTimeoutId = setTimeout(() => {
      setCompletedKeys((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
    }, 3000);

    return () => {
      cancelled = true;
      clearTimeout(flashTimeoutId);
    };
  }, [highlightScheduleKey]);

  const handleIntervalChange = useCallback(
    async (key: string, intervalHours: number) => {
      try {
        await ApiService.updateSchedule(key, intervalHours);

        // If the user selects "Startup only" (-1), force runOnStartup=true on the backend.
        // Otherwise the service would never run at all: interval=-1 means "no scheduled
        // runs" in the base class loop, so the ONLY way work can happen is via the
        // startup pass — which requires runOnStartup=true.
        if (intervalHours === -1) {
          const current = schedulesRef.current.find((s) => s.key === key);
          if (current && !current.runOnStartup) {
            await ApiService.setScheduleRunOnStartup(key, true);
          }
        }

        await fetchSchedules();
      } catch {
        // Revert silently — polling will correct state
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

      // Flash all cards to confirm reset — subtle variant since every card glows at
      // once. Duration matches HighlightGlow's SUBTLE_DEFAULT_DURATION so the
      // enabled/class flip and the animation end happen on the same timeline.
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
          details: { notificationType: 'success' }
        });
      } catch {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.schedules.runNowFailed', { service: displayName }),
          details: { notificationType: 'error' }
        });
      } finally {
        setRunningKey(null);
      }
    },
    [addNotification, t]
  );

  if (loading) {
    return (
      <div className="schedules-loading">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return <div className="schedules-error">{error}</div>;
  }

  return (
    <div className="schedules-section">
      <div className="schedules-section-header">
        <div>
          <h2 className="schedules-section-title">{t('management.schedules.title')}</h2>
          <p className="schedules-section-subtitle">{t('management.schedules.subtitle')}</p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={handleResetDefaults}
          disabled={!isAdmin || resetting}
          loading={resetting}
        >
          {t('management.schedules.resetToDefaults')}
        </Button>
      </div>

      <div className="schedules-grid">
        {schedules.map((service) => (
          <div key={service.key} data-schedule-key={service.key} className="schedules-grid-item">
            <ScheduleCard
              service={service}
              isAdmin={isAdmin}
              onIntervalChange={handleIntervalChange}
              onRunOnStartupChange={handleRunOnStartupChange}
              onRunNow={handleRunNow}
              runningKey={runningKey}
              justCompleted={!!completedKeys[service.key]}
              completedVariant={completedKeys[service.key] ?? 'navigate'}
            />
          </div>
        ))}
      </div>
    </div>
  );
};

export default SchedulesSection;
