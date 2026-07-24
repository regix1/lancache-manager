import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import StatusDot from '@components/common/StatusDot';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import ScheduleIntervalPicker from '../ScheduleIntervalPicker';
import { formatLastRun } from '../scheduleFormatting';
import type { ServiceScheduleInfo } from '../types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import {
  getPersistentServiceId,
  isScheduledPrefillAccountService,
  needsPersistentLogin,
  SCHEDULED_PREFILL_PLATFORM_UI
} from './scheduledPrefillPlatformUi';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillRowLoginState,
  ScheduledPrefillServiceKey,
  ScheduledPrefillServiceScheduleDto
} from './types';
import { getErrorMessage, isAbortError } from '@utils/error';
import { usePersistentPrefillContainerSignalR } from './usePersistentPrefillContainerSignalR';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';

interface ScheduledPrefillScheduleDetailProps {
  disabled?: boolean;
  /** True when the card is showing the "no services enabled" disabled tint. Only the
   * summary content dims in that state - the zero-enabled warning and Configure button
   * stay at full opacity since they're the way out of that state. */
  dimmed?: boolean;
  /** Card-level Run Now. It lives here (next to Configure, above the per-service table)
   * rather than in the card header because both actions operate on the service list this
   * component renders; the trigger + running state stay owned by SchedulesSection. */
  onRunNow: () => void;
  runNowLoading: boolean;
  runNowDisabled: boolean;
}

interface ScheduledPrefillServiceScheduleRowProps {
  serviceKey: ScheduledPrefillServiceKey;
  label: string;
  enabled: boolean;
  containerRunning: boolean;
  /**
   * Account readiness for Steam/Epic/Xbox (null for anonymous Battle.net/Riot). A running
   * container can still be logged out (e.g. after a cancelled interactive login), and the
   * scheduler gates on the daemon's live login state - so the row must surface it too instead
   * of presenting "Container: Running" as the only prerequisite.
   */
  loginState: ScheduledPrefillRowLoginState | null;
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
 * One row of the per-service schedule table: service identity (brand icon tile, name,
 * enablement + container dots), the Next/Last run readout, and the interval picker. On
 * desktop the cells sit on the table's shared column grid under a single header row; below
 * that width each row folds into its own tile and the per-cell labels take over. Extracted
 * into its own component because it calls hooks (useTranslation, useFormattedDateTime)
 * that cannot run inside a .map() loop in the parent.
 */
function ScheduledPrefillServiceScheduleRow({
  serviceKey,
  label,
  enabled,
  containerRunning,
  loginState,
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
  const platformUi = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
  const ServiceIcon = platformUi.icon;

  return (
    <div role="row" className={`scheduled-prefill-schedule-table__row ${platformUi.rowClassName}`}>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--service"
      >
        <span
          className="icon-box scheduled-prefill-schedule-table__service-icon"
          aria-hidden="true"
        >
          <ServiceIcon size={18} />
        </span>
        <span className="scheduled-prefill-schedule-table__service-name">{label}</span>
        <span className="scheduled-prefill-schedule-table__service-status">
          <span className="scheduled-prefill-schedule-table__status-item">
            <StatusDot
              tone={enabled ? 'running' : 'error'}
              label={
                enabled
                  ? t(`${baseKey}.platforms.status.enabled`)
                  : t(`${baseKey}.platforms.status.disabled`)
              }
            />
            {enabled
              ? t(`${baseKey}.platforms.status.enabled`)
              : t(`${baseKey}.platforms.status.disabled`)}
          </span>
          <span className="scheduled-prefill-schedule-table__status-item">
            <StatusDot
              tone={containerRunning ? 'running' : 'error'}
              label={
                containerRunning
                  ? t('prefill.persistent.states.running')
                  : t('prefill.persistent.states.stopped')
              }
            />
            {t(`${baseKey}.platforms.status.containerShort`)}:{' '}
            {containerRunning
              ? t('prefill.persistent.states.running')
              : t('prefill.persistent.states.stopped')}
          </span>
          {loginState !== null && (
            <span className="scheduled-prefill-schedule-table__status-item">
              <StatusDot
                tone={loginState === 'loggedIn' ? 'running' : 'warning'}
                label={
                  loginState === 'loggedIn'
                    ? t(`${baseKey}.platforms.status.loggedIn`)
                    : t(`${baseKey}.platforms.status.loginRequired`)
                }
              />
              {loginState === 'loggedIn'
                ? t(`${baseKey}.platforms.status.loggedIn`)
                : t(`${baseKey}.platforms.status.loginRequired`)}
            </span>
          )}
        </span>
      </div>
      <div role="cell" className="scheduled-prefill-schedule-table__cell">
        <span
          className="caps-label schedule-timing-label scheduled-prefill-schedule-table__cell-label"
          aria-hidden="true"
        >
          {t('management.schedules.nextRun')}
        </span>
        <span className="scheduled-prefill-schedule-table__value tabular-nums">{nextTiming}</span>
        {nextRunUtc && (
          <span className="scheduled-prefill-schedule-table__date tabular-nums">{nextRunDate}</span>
        )}
      </div>
      <div role="cell" className="scheduled-prefill-schedule-table__cell">
        <span
          className="caps-label schedule-timing-label scheduled-prefill-schedule-table__cell-label"
          aria-hidden="true"
        >
          {t('management.schedules.lastRun')}
        </span>
        <span className="scheduled-prefill-schedule-table__value tabular-nums">
          {formatLastRun(lastRunUtc, t)}
        </span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--interval"
      >
        <span
          className="caps-label schedule-timing-label scheduled-prefill-schedule-table__cell-label"
          aria-hidden="true"
        >
          {t('management.schedules.runEvery')}
        </span>
        <ScheduleIntervalPicker
          intervalHours={intervalHours}
          isDisabled={disabled || !enabled}
          onChange={(hours) => onIntervalChange(serviceKey, hours)}
        />
      </div>
    </div>
  );
}

export function ScheduledPrefillScheduleDetail({
  disabled = false,
  dimmed = false,
  onRunNow,
  runNowLoading,
  runNowDisabled
}: ScheduledPrefillScheduleDetailProps) {
  const { t } = useTranslation();
  const { on, off } = useSignalR();
  // Persistent-container run/login state now flows through the unified activity registry; the
  // fetched container list stays the pre-seed fallback (activity.isActive(...) || existing).
  const activity = useActivityStatus();
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const [persistentContainers, setPersistentContainers] = useState<PersistentPrefillContainerDto[]>(
    []
  );
  const [schedule, setSchedule] = useState<ScheduledPrefillServiceScheduleDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [modalOpened, setModalOpened] = useState(false);
  // Relative labels ("in 2h", "Just now") are computed at render time, so without a clock they
  // freeze at whatever the last fetch produced. A minute tick matches their coarsest granularity
  // and re-derives every timing cell without refetching anything.
  const [now, setNow] = useState(() => Date.now());
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

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

  useEffect(() => {
    const interval = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(interval);
  }, []);

  useEffect(() => {
    // Run lifecycle/progress lines are deliberately NOT rendered on this card - the universal
    // notification already shows them. The completion event only matters here because it stamps
    // fresh last/next-run times that the table should pick up promptly.
    const handleCompleted = () => {
      void refreshSchedule();
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

    on('ScheduledPrefillCompleted', handleCompleted);
    on('SchedulesUpdated', handleSchedulesUpdated);

    return () => {
      off('ScheduledPrefillCompleted', handleCompleted);
      off('SchedulesUpdated', handleSchedulesUpdated);
    };
  }, [off, on, refreshSchedule]);

  const enabledCount = useMemo(
    () =>
      config
        ? SCHEDULED_PREFILL_SERVICE_RUN_ORDER.filter((serviceKey) => config[serviceKey].enabled)
            .length
        : 0,
    [config]
  );

  const totalCount = SCHEDULED_PREFILL_SERVICE_RUN_ORDER.length;

  // Names of the enabled account services whose persistent container needs login. Named
  // explicitly in the warning so a user whose Steam row reads "Logged in" doesn't misread the
  // generic "one or more services" phrasing as Steam not being detected.
  const servicesNeedingLogin = useMemo(() => {
    if (!config) {
      return [];
    }

    const containerByService = new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
      persistentContainers.map((container) => [container.service, container])
    );

    return SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.filter((serviceId) => {
      if (!config[serviceId].enabled) {
        return false;
      }

      const container = containerByService.get(getPersistentServiceId(serviceId));
      return needsPersistentLogin(container);
    }).map((serviceId) => t(`${baseKey}.services.${serviceId}`));
  }, [config, persistentContainers, baseKey, t]);

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

      const diffMs = new Date(item.nextRunUtc).getTime() - now;
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
    [baseKey, now, t]
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
      loginState: ScheduledPrefillRowLoginState | null;
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
        new Date(item.nextRunUtc).getTime() > now
          ? item.nextRunUtc
          : null;
      // The activity registry keys the persistent container by the lowercase platform token
      // (battleNet -> battlenet); the fetched container list is the pre-seed fallback.
      const activityPlatformKey = serviceKey.toLowerCase();
      rows.push({
        key: serviceKey,
        label: t(`${baseKey}.services.${serviceKey}`),
        enabled,
        containerRunning:
          activity.isActive('persistentContainer', activityPlatformKey, 'running') ||
          (container?.isRunning ?? false),
        // Account services gate on the daemon's live login, so mirror that readiness here;
        // anonymous services (Battle.net/Riot) have no login dimension to show.
        loginState: isScheduledPrefillAccountService(serviceKey)
          ? activity.isActive('persistentContainer', activityPlatformKey, 'authenticated') ||
            !needsPersistentLogin(container)
            ? 'loggedIn'
            : 'loginRequired'
          : null,
        intervalHours,
        // A disabled platform keeps its chosen interval for the picker but has no active next run,
        // so feed formatTiming a paused interval instead of suggesting that it will run "soon".
        nextTiming: formatTiming({ ...item, intervalHours: enabled ? intervalHours : 0 }),
        nextRunUtc: upcomingNextRunUtc,
        lastRunUtc: item.lastRunUtc
      });
    }
    return rows;
  }, [schedule, config, persistentContainers, baseKey, formatTiming, now, t, activity]);

  const handleModalSaved = async () => {
    await loadSummary();
  };

  const isInitialLoading = loading && !config;

  return (
    <>
      <div className="scheduled-prefill-card-summary">
        {/* Command strip: enabled-count summary on the left, Run Now + Configure on the
            right. Both buttons stay at full opacity while the summary below dims - the
            Configure button is the way out of the zero-enabled state, and Run Now
            communicates its state through its own disabled styling. */}
        <div className="scheduled-prefill-card-summary__toolbar">
          {isInitialLoading ? (
            <div className="scheduled-prefill-card-summary__loading">
              <LoadingSpinner inline size="sm" />
              <span>{t(`${baseKey}.loading`)}</span>
            </div>
          ) : (
            <p
              className={`scheduled-prefill-card-summary__count${dimmed ? ' schedule-card-disabled' : ''}`}
            >
              {t(`${baseKey}.summary`, { enabled: enabledCount, total: totalCount })}
            </p>
          )}
          <div className="scheduled-prefill-card-summary__actions">
            <Button
              type="button"
              variant="filled"
              color="green"
              size="sm"
              className="control-h-md"
              onClick={onRunNow}
              disabled={runNowDisabled}
              loading={runNowLoading}
              stableWidth
            >
              {t('management.schedules.runNow')}
            </Button>
            <Button
              type="button"
              variant="filled"
              color="blue"
              size="sm"
              className="control-h-md"
              onClick={() => setModalOpened(true)}
              disabled={disabled}
            >
              {t(`${baseKey}.actions.configure`)}
            </Button>
          </div>
        </div>

        {!isInitialLoading && (
          <>
            <div
              className={`scheduled-prefill-card-summary__dimmable${dimmed ? ' schedule-card-disabled' : ''}`}
            >
              {scheduleRows.length > 0 && (
                <div
                  role="table"
                  aria-label={t(`${baseKey}.servicesTitle`)}
                  className="scheduled-prefill-schedule-table"
                >
                  <div role="row" className="scheduled-prefill-schedule-table__head caps-label">
                    <span role="columnheader">{t(`${baseKey}.service`)}</span>
                    <span role="columnheader">{t('management.schedules.nextRun')}</span>
                    <span role="columnheader">{t('management.schedules.lastRun')}</span>
                    <span role="columnheader">{t('management.schedules.runEvery')}</span>
                  </div>
                  {scheduleRows.map((row) => (
                    <ScheduledPrefillServiceScheduleRow
                      key={row.key}
                      serviceKey={row.key}
                      label={row.label}
                      enabled={row.enabled}
                      containerRunning={row.containerRunning}
                      loginState={row.loginState}
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
              {servicesNeedingLogin.length > 0 && (
                <p className="scheduled-prefill-card-summary__warning">
                  {t(`${baseKey}.authWarning`, {
                    services: servicesNeedingLogin.join(', '),
                    count: servicesNeedingLogin.length
                  })}
                </p>
              )}
              {error && (
                <p className="scheduled-prefill-card-summary__error">
                  {t(`${baseKey}.summaryError`, { error })}
                </p>
              )}
            </div>
            {/* Deliberately outside the dimmable wrapper: this warning is the way out of
                the zero-enabled state, so it stays at full opacity while the rest dims. */}
            {config && enabledCount === 0 && (
              <p className="scheduled-prefill-card-summary__warning">
                {t(`${baseKey}.zeroEnabledWarning`)}
              </p>
            )}
          </>
        )}
      </div>

      <ScheduledPrefillConfigModal
        opened={modalOpened}
        onClose={() => setModalOpened(false)}
        onSaved={handleModalSaved}
      />
    </>
  );
}
