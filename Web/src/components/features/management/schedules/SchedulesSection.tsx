import React, { useState, useEffect, useCallback, memo } from 'react';
import './SchedulesSection.css';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
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

interface ScheduleCardProps {
  service: ServiceScheduleInfo;
  isAdmin: boolean;
  onIntervalChange: (key: string, intervalHours: number) => Promise<void>;
  onRunOnStartupChange: (key: string, runOnStartup: boolean) => Promise<void>;
  onRunNow: (key: string) => Promise<void>;
  runningKey: string | null;
  savingKey: string | null;
  justCompleted: boolean;
}

const ScheduleCard = memo(function ScheduleCard({
  service,
  isAdmin,
  onIntervalChange,
  onRunOnStartupChange,
  onRunNow,
  runningKey,
  savingKey,
  justCompleted
}: ScheduleCardProps) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const formattedNextRun = useFormattedDateTime(service.nextRunUtc);

  const isRunningThis = runningKey === service.key;
  const isSavingThis = savingKey === service.key;

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
    service.intervalHours === 0
      ? '0'
      : service.intervalHours === -1
        ? '-1'
        : String(service.intervalHours);
  const hasCurrentOption = standardOptions.some((opt) => opt.value === currentVal);
  const allOptions = hasCurrentOption
    ? standardOptions
    : [
        { value: currentVal, label: formatIntervalLabel(service.intervalHours) },
        ...standardOptions
      ];

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

  const currentIntervalValue =
    service.intervalHours === 0
      ? '0'
      : service.intervalHours === -1
        ? '-1'
        : String(service.intervalHours);

  const handleIntervalChange = useCallback(
    (value: string) => {
      const hours = parseFloat(value);
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

  const isDisabled = !isAdmin || isRunningThis || isSavingThis;

  const hasExpandableContent = true;

  return (
    <Card
      className={`schedule-card${service.intervalHours === 0 ? ' schedule-card-disabled' : ''}${justCompleted ? ' schedule-card-completed' : ''}`}
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
          <EnhancedDropdown
            options={allOptions}
            value={currentIntervalValue}
            onChange={handleIntervalChange}
            disabled={isDisabled}
            variant="button"
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

      {/* Run-on-startup toggle */}
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
  );
});

const SchedulesSection: React.FC<SchedulesSectionProps> = ({ isAdmin }) => {
  const { t } = useTranslation();
  const [schedules, setSchedules] = useState<ServiceScheduleInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [runningKey, setRunningKey] = useState<string | null>(null);
  const [savingKey, setSavingKey] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  const [completedKeys, setCompletedKeys] = useState<Set<string>>(new Set());
  const { on, off, connectionState } = useSignalR();
  const { addNotification } = useNotifications();

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

  const handleIntervalChange = useCallback(
    async (key: string, intervalHours: number) => {
      setSavingKey(key);
      try {
        await ApiService.updateSchedule(key, intervalHours);
        await fetchSchedules();
      } catch {
        // Revert silently — polling will correct state
      } finally {
        setSavingKey(null);
      }
    },
    [fetchSchedules]
  );

  const handleRunOnStartupChange = useCallback(
    async (key: string, runOnStartup: boolean) => {
      const displayName = t(`management.schedules.services.${key}.displayName`);
      setSavingKey(key);
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
      } finally {
        setSavingKey(null);
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

      // Flash all cards to confirm reset
      const allKeys = new Set(schedules.map((s) => s.key));
      setCompletedKeys(allKeys);
      setTimeout(() => setCompletedKeys(new Set()), 3000);
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
      setCompletedKeys((prev) => new Set([...prev, key]));
      setTimeout(
        () =>
          setCompletedKeys((prev) => {
            const next = new Set(prev);
            next.delete(key);
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
          <ScheduleCard
            key={service.key}
            service={service}
            isAdmin={isAdmin}
            onIntervalChange={handleIntervalChange}
            onRunOnStartupChange={handleRunOnStartupChange}
            onRunNow={handleRunNow}
            runningKey={runningKey}
            savingKey={savingKey}
            justCompleted={completedKeys.has(service.key)}
          />
        ))}
      </div>
    </div>
  );
};

export default SchedulesSection;
