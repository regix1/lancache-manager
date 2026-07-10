import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
  SCHEDULED_PREFILL_RUN_COMPLETED_DISMISS_MS,
  SCHEDULED_PREFILL_RUN_FAILED_DISMISS_MS,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import ScheduleIntervalPicker from '../ScheduleIntervalPicker';
import { formatLastRun } from '../scheduleFormatting';
import type { ServiceScheduleInfo } from '../types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import {
  getPersistentServiceId,
  needsPersistentLogin,
  SCHEDULED_PREFILL_PLATFORM_UI
} from './scheduledPrefillPlatformUi';
import type {
  ScheduledPrefillCompletedEvent,
  ScheduledPrefillConfigDto,
  ScheduledPrefillProgressEvent,
  ScheduledPrefillRunPhase,
  ScheduledPrefillRunProgressItem,
  ScheduledPrefillServiceKey,
  ScheduledPrefillServiceScheduleDto,
  ScheduledPrefillStartedEvent
} from './types';
import { getErrorMessage, isAbortError } from '@utils/error';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import { usePersistentPrefillContainerSignalR } from './usePersistentPrefillContainerSignalR';

interface ScheduledPrefillScheduleDetailProps {
  disabled?: boolean;
  /** True when the card is showing the "no services enabled" disabled tint. Only the
   * summary content dims in that state - the zero-enabled warning and Configure button
   * stay at full opacity since they're the way out of that state. */
  dimmed?: boolean;
}

interface ScheduledPrefillServiceScheduleRowProps {
  serviceKey: ScheduledPrefillServiceKey;
  label: string;
  enabled: boolean;
  containerRunning: boolean;
  intervalHours: number;
  /** Relative next-run hint ("in 2d", "soon", "paused", "on startup") from formatTiming. */
  nextTiming: string;
  /** Raw next-run timestamp; non-null only for a real upcoming run (drives the absolute date). */
  nextRunUtc: string | null;
  lastRunUtc: string | null;
  disabled: boolean;
  onIntervalChange: (serviceKey: ScheduledPrefillServiceKey, hours: number) => void;
}

/**
 * One service row: service identity, enablement and live container state, and its interval picker
 * on the first line, then a two-column "Next run" / "Last run" readout below. Extracted into its own component
 * because it calls hooks (useTranslation, useFormattedDateTime) that cannot run inside a .map()
 * loop in the parent.
 */
function ScheduledPrefillServiceScheduleRow({
  serviceKey,
  label,
  enabled,
  containerRunning,
  intervalHours,
  nextTiming,
  nextRunUtc,
  lastRunUtc,
  disabled,
  onIntervalChange
}: ScheduledPrefillServiceScheduleRowProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const nextRunDate = useFormattedDateTime(nextRunUtc);
  const ServiceIcon = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey].icon;

  return (
    <div className="scheduled-prefill-card-summary__schedule-row">
      <div className="scheduled-prefill-card-summary__schedule-head">
        <span className="scheduled-prefill-card-summary__schedule-icon" aria-hidden="true">
          <ServiceIcon size={16} />
        </span>
        <span className="scheduled-prefill-card-summary__schedule-service">{label}</span>
        <div className="scheduled-prefill-card-summary__schedule-status">
          <span className="scheduled-prefill-card-summary__schedule-status-item">
            <span
              className={`scheduled-prefill-card-summary__schedule-status-dot scheduled-prefill-card-summary__schedule-status-dot--${enabled ? 'success' : 'error'}`}
              aria-hidden="true"
            />
            {enabled
              ? t(`${baseKey}.platforms.status.enabled`)
              : t(`${baseKey}.platforms.status.disabled`)}
          </span>
          <span className="scheduled-prefill-card-summary__schedule-status-item">
            <span
              className={`scheduled-prefill-card-summary__schedule-status-dot scheduled-prefill-card-summary__schedule-status-dot--${containerRunning ? 'success' : 'error'}`}
              aria-hidden="true"
            />
            {t(`${baseKey}.platforms.status.containerShort`)}:{' '}
            {containerRunning
              ? t('prefill.persistent.states.running')
              : t('prefill.persistent.states.stopped')}
          </span>
        </div>
        <div className="scheduled-prefill-card-summary__schedule-picker">
          <ScheduleIntervalPicker
            intervalHours={intervalHours}
            isDisabled={disabled || !enabled}
            onChange={(hours) => onIntervalChange(serviceKey, hours)}
          />
        </div>
      </div>
      <div className="scheduled-prefill-card-summary__schedule-readout">
        <div className="scheduled-prefill-card-summary__schedule-slot">
          <span className="schedule-timing-label">{t('management.schedules.nextRun')}</span>
          <span className="scheduled-prefill-card-summary__schedule-value">{nextTiming}</span>
          {nextRunUtc && (
            <span className="scheduled-prefill-card-summary__schedule-date">{nextRunDate}</span>
          )}
        </div>
        <div className="scheduled-prefill-card-summary__schedule-slot">
          <span className="schedule-timing-label">{t('management.schedules.lastRun')}</span>
          <span className="scheduled-prefill-card-summary__schedule-value">
            {formatLastRun(lastRunUtc, t)}
          </span>
        </div>
      </div>
    </div>
  );
}

export function ScheduledPrefillScheduleDetail({
  disabled = false,
  dimmed = false
}: ScheduledPrefillScheduleDetailProps) {
  const { t } = useTranslation();
  const { on, off } = useSignalR();
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const [persistentContainers, setPersistentContainers] = useState<PersistentPrefillContainerDto[]>(
    []
  );
  const [schedule, setSchedule] = useState<ScheduledPrefillServiceScheduleDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [modalOpened, setModalOpened] = useState(false);
  const [runPhase, setRunPhase] = useState<ScheduledPrefillRunPhase>('idle');
  const [runProgress, setRunProgress] = useState<ScheduledPrefillRunProgressItem[]>([]);
  const [runError, setRunError] = useState<string | null>(null);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const eventsKey = 'management.schedules.services.scheduledPrefill.events';

  // Aborts the in-flight refreshSchedule() fetch so an unmounted or superseded refresh never
  // setStates (last writer wins).
  const refreshScheduleControllerRef = useRef<AbortController | null>(null);
  const refreshContainersControllerRef = useRef<AbortController | null>(null);

  // Last-seen prefill entry from the SchedulesUpdated broadcast. That event fires on every
  // tracked service's work-tick, so we only refetch when the prefill aggregate actually
  // changed (a real run or a config save), never on idle ticks. null until the first payload,
  // which only seeds the snapshot since mount's loadSummary already fetched.
  const lastPrefillAggregateRef = useRef<{
    lastRunUtc: string | null;
    nextRunUtc: string | null;
    intervalHours: number;
  } | null>(null);

  // Auto-dismiss the terminal run-result line (e.g. "Riot: Prefill completed (130.05 MB
  // downloaded)") back to idle so it does not linger on the card forever; in-progress runs
  // never schedule a dismiss. Each hook instance cancels its own prior pending timer, so a
  // new terminal event for the same kind always restarts the clock.
  const scheduleRunCompletedDismiss = useTimeoutCallback(
    SCHEDULED_PREFILL_RUN_COMPLETED_DISMISS_MS
  );
  const scheduleRunFailedDismiss = useTimeoutCallback(SCHEDULED_PREFILL_RUN_FAILED_DISMISS_MS);

  const loadSummary = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    try {
      const [nextConfig, nextContainers, nextSchedule] = await Promise.all([
        ApiService.getScheduledPrefillConfig(signal),
        ApiService.getPersistentPrefillContainers(signal),
        ApiService.getScheduledPrefillSchedule(signal)
      ]);
      setConfig(nextConfig);
      setPersistentContainers(nextContainers);
      setSchedule(nextSchedule);
      setError(null);
    } catch (loadError: unknown) {
      if (!isAbortError(loadError)) {
        setError(getErrorMessage(loadError));
      }
    } finally {
      setLoading(false);
    }
  }, []);

  // Best-effort refresh of just the per-service schedule (no spinner) after a run
  // completes, so the next-run summary reflects the freshly stamped last-run times.
  const refreshSchedule = useCallback(async () => {
    // Supersede any in-flight refresh so only the newest one wins, and give this fetch its
    // own signal so an unmount (see the load effect cleanup) cancels it.
    refreshScheduleControllerRef.current?.abort();
    const controller = new AbortController();
    refreshScheduleControllerRef.current = controller;
    try {
      const nextSchedule = await ApiService.getScheduledPrefillSchedule(controller.signal);
      if (!controller.signal.aborted) {
        setSchedule(nextSchedule);
      }
    } catch {
      // Aborted (unmount/superseded) or failed: keep the prior schedule; SchedulesUpdated /
      // a later load will correct it.
    }
  }, []);

  // Keep the at-a-glance container badges current while this card remains mounted. Container
  // start/stop actions happen inside Configure, but their state is useful on the schedule page
  // even after that modal closes. Refresh only the lightweight container list for these events;
  // the config and per-service schedule do not need to be fetched again.
  const refreshPersistentContainers = useCallback(async () => {
    refreshContainersControllerRef.current?.abort();
    const controller = new AbortController();
    refreshContainersControllerRef.current = controller;
    try {
      const nextContainers = await ApiService.getPersistentPrefillContainers(controller.signal);
      if (!controller.signal.aborted) {
        setPersistentContainers(nextContainers);
      }
    } catch {
      // Preserve the last known status when a refresh is aborted or temporarily unavailable.
    }
  }, []);

  usePersistentPrefillContainerSignalR({
    enabled: true,
    onRefresh: () => {
      void refreshPersistentContainers();
    }
  });

  // Card-level per-service interval change. Saves via the same whole-config round-trip the
  // Configure modal uses; optimistic so the picker never flashes back to the old value.
  const handleServiceIntervalChange = useCallback(
    async (serviceKey: ScheduledPrefillServiceKey, hours: number) => {
      if (!config) {
        return;
      }

      const previous = config;
      const updated: ScheduledPrefillConfigDto = {
        ...config,
        [serviceKey]: { ...config[serviceKey], intervalHours: hours }
      };
      setConfig(updated);

      try {
        await ApiService.updateScheduledPrefillConfig(updated);
        await refreshSchedule();
      } catch (saveError: unknown) {
        setConfig(previous);
        setError(getErrorMessage(saveError));
      }
    },
    [config, refreshSchedule]
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadSummary(controller.signal);

    return () => {
      controller.abort();
      refreshScheduleControllerRef.current?.abort();
      refreshContainersControllerRef.current?.abort();
    };
  }, [loadSummary]);

  const formatProgressLine = useCallback(
    (item: ScheduledPrefillRunProgressItem): string => {
      const serviceKey = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[item.serviceId];
      const serviceLabel = serviceKey ? t(`${baseKey}.services.${serviceKey}`) : item.serviceId;

      if (item.stage === 'skipped') {
        return t(`${eventsKey}.skipped`, { service: serviceLabel, reason: item.message });
      }

      if (item.stage === 'needs-login') {
        return t(`${eventsKey}.needsLogin`, { service: serviceLabel });
      }

      return t(`${eventsKey}.serviceProgress`, {
        service: serviceLabel,
        message: item.message
      });
    },
    [baseKey, eventsKey, t]
  );

  useEffect(() => {
    const handleStarted = (_payload: ScheduledPrefillStartedEvent) => {
      setRunPhase('running');
      setRunProgress([]);
      setRunError(null);
    };

    const handleProgress = (payload: ScheduledPrefillProgressEvent) => {
      setRunPhase('running');
      setRunProgress((previous) => {
        const next = previous.filter((item) => item.serviceId !== payload.serviceId);
        next.push({
          serviceId: payload.serviceId,
          stage: payload.stage,
          message: payload.message,
          bytesDownloaded: payload.bytesDownloaded,
          downloadSessionId: payload.downloadSessionId
        });
        return next;
      });
    };

    const handleCompleted = (payload: ScheduledPrefillCompletedEvent) => {
      void refreshSchedule();

      if (payload.success) {
        setRunPhase('completed');
        setRunError(null);
        // Only reset to idle if a newer event (e.g. the next scheduled run starting) hasn't
        // already moved the phase on; otherwise this stale timer would be a no-op by design.
        scheduleRunCompletedDismiss(() => {
          setRunPhase((current) => (current === 'completed' ? 'idle' : current));
        });
        return;
      }

      setRunPhase('failed');
      setRunError(payload.error ?? t(`${eventsKey}.failed`));
      scheduleRunFailedDismiss(() => {
        setRunPhase((current) => (current === 'failed' ? 'idle' : current));
      });
    };

    // The generic schedule broadcast carries the full ServiceScheduleInfo[] and fires on every
    // tracked service's work-tick (roughly once a minute) plus schedule mutations. Refetching
    // the per-service block on every one of those would hammer a constrained server, so gate
    // on the pushed prefill entry: only refresh when its last/next-run or interval actually
    // moved, which happens only on a real run or a config save (from any surface). The first
    // payload just seeds the snapshot - mount's loadSummary already fetched the schedule.
    const handleSchedulesUpdated = (schedules: ServiceScheduleInfo[]) => {
      const prefill = schedules.find((entry) => entry.key === 'scheduledPrefill');
      if (!prefill) {
        return;
      }

      const previous = lastPrefillAggregateRef.current;
      const next = {
        lastRunUtc: prefill.lastRunUtc,
        nextRunUtc: prefill.nextRunUtc,
        intervalHours: prefill.intervalHours
      };
      lastPrefillAggregateRef.current = next;

      if (!previous) {
        return;
      }

      if (
        previous.lastRunUtc !== next.lastRunUtc ||
        previous.nextRunUtc !== next.nextRunUtc ||
        previous.intervalHours !== next.intervalHours
      ) {
        void refreshSchedule();
      }
    };

    on('ScheduledPrefillStarted', handleStarted);
    on('ScheduledPrefillProgress', handleProgress);
    on('ScheduledPrefillCompleted', handleCompleted);
    on('SchedulesUpdated', handleSchedulesUpdated);

    return () => {
      off('ScheduledPrefillStarted', handleStarted);
      off('ScheduledPrefillProgress', handleProgress);
      off('ScheduledPrefillCompleted', handleCompleted);
      off('SchedulesUpdated', handleSchedulesUpdated);
    };
  }, [
    eventsKey,
    off,
    on,
    refreshSchedule,
    scheduleRunCompletedDismiss,
    scheduleRunFailedDismiss,
    t
  ]);

  const enabledCount = useMemo(
    () =>
      config
        ? SCHEDULED_PREFILL_SERVICE_RUN_ORDER.filter((serviceKey) => config[serviceKey].enabled)
            .length
        : 0,
    [config]
  );

  const totalCount = SCHEDULED_PREFILL_SERVICE_RUN_ORDER.length;

  const hasAccountWarning = useMemo(() => {
    if (!config) {
      return false;
    }

    const containerByService = new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
      persistentContainers.map((container) => [container.service, container])
    );

    return SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.some((serviceId) => {
      if (!config[serviceId].enabled) {
        return false;
      }

      const container = containerByService.get(getPersistentServiceId(serviceId));
      return needsPersistentLogin(container);
    });
  }, [config, persistentContainers]);

  const formatTiming = useCallback(
    (item: ScheduledPrefillServiceScheduleDto): string => {
      if (item.intervalHours === 0) {
        return t(`${baseKey}.nextRunSummary.paused`);
      }
      if (item.intervalHours === -1) {
        return t(`${baseKey}.nextRunSummary.startupOnly`);
      }
      if (!item.nextRunUtc) {
        return t(`${baseKey}.nextRunSummary.soon`);
      }

      const diffMs = new Date(item.nextRunUtc).getTime() - Date.now();
      if (diffMs <= 0) {
        return t(`${baseKey}.nextRunSummary.soon`);
      }

      const diffMinutes = Math.floor(diffMs / 60000);
      if (diffMinutes < 60) {
        return t(`${baseKey}.nextRunSummary.inMinutes`, { count: Math.max(1, diffMinutes) });
      }
      const diffHours = Math.floor(diffMinutes / 60);
      if (diffHours < 24) {
        return t(`${baseKey}.nextRunSummary.inHours`, { count: diffHours });
      }
      const diffDays = Math.floor(diffHours / 24);
      return t(`${baseKey}.nextRunSummary.inDays`, { count: diffDays });
    },
    [baseKey, t]
  );

  const scheduleRows = useMemo(() => {
    const byServiceKey = new Map<ScheduledPrefillServiceKey, ScheduledPrefillServiceScheduleDto>();
    for (const item of schedule) {
      const serviceKey = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[item.serviceId];
      if (serviceKey) {
        byServiceKey.set(serviceKey, item);
      }
    }

    const rows: {
      key: ScheduledPrefillServiceKey;
      label: string;
      enabled: boolean;
      containerRunning: boolean;
      intervalHours: number;
      nextTiming: string;
      nextRunUtc: string | null;
      lastRunUtc: string | null;
    }[] = [];
    const containerByService = new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
      persistentContainers.map((container) => [container.service, container])
    );
    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const item = byServiceKey.get(serviceKey);
      if (!item) {
        continue;
      }
      const enabled = config ? config[serviceKey].enabled : item.enabled;
      const container = containerByService.get(getPersistentServiceId(serviceKey));
      // Prefer the (optimistically updated) config value so the picker reflects a change
      // immediately; the schedule DTO catches up on the post-save refresh.
      const intervalHours = config ? config[serviceKey].intervalHours : item.intervalHours;
      // The absolute date is only meaningful for a real upcoming run. An overdue (past)
      // nextRunUtc that has not been re-stamped yet still reads as "soon" via formatTiming, so
      // suppress the stale elapsed timestamp here rather than render a confusing past date.
      const upcomingNextRunUtc =
        enabled &&
        intervalHours > 0 &&
        item.nextRunUtc !== null &&
        new Date(item.nextRunUtc).getTime() > Date.now()
          ? item.nextRunUtc
          : null;
      rows.push({
        key: serviceKey,
        label: t(`${baseKey}.services.${serviceKey}`),
        enabled,
        containerRunning: container?.isRunning ?? false,
        intervalHours,
        // A disabled platform keeps its chosen interval for the picker but has no active next run,
        // so feed formatTiming a paused interval instead of suggesting that it will run "soon".
        nextTiming: formatTiming({ ...item, intervalHours: enabled ? intervalHours : 0 }),
        nextRunUtc: upcomingNextRunUtc,
        lastRunUtc: item.lastRunUtc
      });
    }
    return rows;
  }, [schedule, config, persistentContainers, baseKey, formatTiming, t]);

  const handleModalSaved = async () => {
    await loadSummary();
  };

  const latestProgressLine =
    runProgress.length > 0 ? formatProgressLine(runProgress[runProgress.length - 1]) : null;

  return (
    <>
      <div className="schedule-extra-row scheduled-prefill-card-summary">
        <div className="scheduled-prefill-card-summary__content">
          {/* The zero-enabled warning below is deliberately outside this wrapper: it's the
              way out of the disabled state, so it (and the Configure button further down)
              stay at full opacity while the rest of the summary dims. */}
          <div
            className={`scheduled-prefill-card-summary__dimmable${dimmed ? ' schedule-card-disabled' : ''}`}
          >
            {loading && !config ? (
              <div className="scheduled-prefill-card-summary__loading">
                <LoadingSpinner inline size="sm" />
                <span>{t(`${baseKey}.loading`)}</span>
              </div>
            ) : (
              <>
                <span className="schedule-extra-label">{t(`${baseKey}.label`)}</span>
                <p className="schedule-extra-help scheduled-prefill-card-summary__text">
                  {t(`${baseKey}.summary`, { enabled: enabledCount, total: totalCount })}
                </p>
                {scheduleRows.length > 0 && (
                  <div className="scheduled-prefill-card-summary__schedule">
                    {scheduleRows.map((row) => (
                      <ScheduledPrefillServiceScheduleRow
                        key={row.key}
                        serviceKey={row.key}
                        label={row.label}
                        enabled={row.enabled}
                        containerRunning={row.containerRunning}
                        intervalHours={row.intervalHours}
                        nextTiming={row.nextTiming}
                        nextRunUtc={row.nextRunUtc}
                        lastRunUtc={row.lastRunUtc}
                        disabled={disabled}
                        onIntervalChange={(serviceKey, hours) =>
                          void handleServiceIntervalChange(serviceKey, hours)
                        }
                      />
                    ))}
                  </div>
                )}
                {hasAccountWarning && (
                  <p className="scheduled-prefill-card-summary__warning">
                    {t(`${baseKey}.authWarning`)}
                  </p>
                )}
                {runPhase === 'running' && (
                  <p className="scheduled-prefill-card-summary__progress">
                    {latestProgressLine ?? t(`${eventsKey}.started`)}
                  </p>
                )}
                {runPhase === 'completed' && (
                  <p className="scheduled-prefill-card-summary__progress scheduled-prefill-card-summary__progress--success">
                    {latestProgressLine ?? t(`${eventsKey}.completed`)}
                  </p>
                )}
                {runPhase === 'failed' && (
                  <p className="scheduled-prefill-card-summary__error">
                    {runError ?? latestProgressLine ?? t(`${eventsKey}.failed`)}
                  </p>
                )}
                {error && (
                  <p className="scheduled-prefill-card-summary__error">
                    {t(`${baseKey}.summaryError`, { error })}
                  </p>
                )}
              </>
            )}
          </div>
          {config && enabledCount === 0 && (
            <p className="scheduled-prefill-card-summary__warning">
              {t(`${baseKey}.zeroEnabledWarning`)}
            </p>
          )}
        </div>
        <div className="schedule-extra-control scheduled-prefill-card-summary__actions">
          <Button
            type="button"
            variant="filled"
            color="blue"
            size="sm"
            className="schedule-control-button"
            onClick={() => setModalOpened(true)}
            disabled={disabled}
          >
            {t(`${baseKey}.actions.configure`)}
          </Button>
        </div>
      </div>

      <ScheduledPrefillConfigModal
        opened={modalOpened}
        onClose={() => setModalOpened(false)}
        onSaved={handleModalSaved}
      />
    </>
  );
}
