import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
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
import type { CustomSchedule } from '../custom-schedule/types';
import type { ServiceScheduleInfo } from '../types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
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
  ScheduledPrefillServiceId,
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
  /** Starts one platform now, from that platform's own table row. */
  onRunService: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => void;
  isRunServicePending: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => boolean;
  /** True while the whole prefill schedule is running server-side, or the reader is not an admin. */
  runServiceDisabled: boolean;
}

interface ScheduledPrefillServiceScheduleRowProps {
  serviceKey: ScheduledPrefillServiceKey;
  /** Backend platform name, which is what the per-service run route is addressed by. */
  serviceId: ScheduledPrefillServiceId;
  scheduleId: string;
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
  /** The saved cron schedule, which runs in preference to the interval, or null. */
  customSchedule: CustomSchedule | null;
  /** Relative next-run hint ("in 2d", "soon", "paused", "on startup") from formatTiming. */
  nextTiming: string;
  /** Raw next-run timestamp; non-null only for a real upcoming run (drives the absolute date). */
  nextRunUtc: string | null;
  lastRunUtc: string | null;
  disabled: boolean;
  runPending: boolean;
  runDisabled: boolean;
  /** True while this service's own run is in flight, which is when Cancel replaces nothing and appears. */
  isRunning: boolean;
  cancelPending: boolean;
  onRun: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => void;
  onCancel: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => void;
  onOpen: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onEnable: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onIntervalChange: (
    serviceKey: ScheduledPrefillServiceKey,
    scheduleId: string,
    hours: number
  ) => void;
  onCustomScheduleChange: (
    serviceKey: ScheduledPrefillServiceKey,
    scheduleId: string,
    schedule: CustomSchedule
  ) => void;
}

/** Fills the Actions dropdown's trigger-icon slot with the shared spinner while a run or
 * cancel request is in flight, since EnhancedDropdown has no loading state of its own. */
function ScheduledPrefillActionsSpinnerIcon({
  className
}: {
  size?: number;
  className?: string;
  style?: CSSProperties;
}) {
  return <LoadingSpinner inline size="xs" className={className} />;
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
  serviceId,
  scheduleId,
  label,
  enabled,
  containerRunning,
  loginState,
  intervalHours,
  customSchedule,
  nextTiming,
  nextRunUtc,
  lastRunUtc,
  disabled,
  runPending,
  runDisabled,
  isRunning,
  cancelPending,
  onRun,
  onCancel,
  onOpen,
  onEnable,
  onIntervalChange,
  onCustomScheduleChange
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
          onChange={(hours) => onIntervalChange(serviceKey, scheduleId, hours)}
          customSchedule={customSchedule}
          onCustomScheduleChange={(schedule) =>
            onCustomScheduleChange(serviceKey, scheduleId, schedule)
          }
        />
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--action"
      >
        <EnhancedDropdown
          options={
            isRunning
              ? [
                  {
                    value: 'cancel',
                    label: t('management.schedules.services.scheduledPrefill.cancelService'),
                    disabled: disabled || cancelPending
                  }
                ]
              : [
                  {
                    value: 'run',
                    label: t('management.schedules.services.scheduledPrefill.runService'),
                    disabled: runDisabled || runPending
                  },
                  {
                    value: 'open',
                    label: t(`${baseKey}.records.open`)
                  },
                  ...(!enabled
                    ? [
                        {
                          value: 'enable',
                          label: t(`${baseKey}.records.enable`)
                        }
                      ]
                    : [])
                ]
          }
          value=""
          onChange={(action) => {
            if (action === 'cancel') {
              onCancel(serviceId, scheduleId);
              return;
            }
            if (action === 'open') {
              onOpen(serviceKey, scheduleId);
              return;
            }
            if (action === 'enable') {
              onEnable(serviceKey, scheduleId);
              return;
            }
            onRun(serviceId, scheduleId);
          }}
          customTriggerLabel={t('management.actions.menuLabel')}
          triggerIcon={runPending || cancelPending ? ScheduledPrefillActionsSpinnerIcon : undefined}
          triggerAriaLabel={t('management.actions.menuLabel')}
          className="w-full"
          disabled={isRunning ? disabled || cancelPending : disabled || runPending}
          size="md"
          variant="button"
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
  runNowDisabled,
  onRunService,
  isRunServicePending,
  runServiceDisabled
}: ScheduledPrefillScheduleDetailProps) {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
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
  /** Services whose cancel request is in flight, so the row can show it without waiting for SignalR. */
  const [cancelingServices, setCancelingServices] = useState<ScheduledPrefillServiceId[]>([]);
  const [modalOpened, setModalOpened] = useState(false);
  const [modalRecord, setModalRecord] = useState<{
    serviceKey: ScheduledPrefillServiceKey;
    scheduleId: string;
  } | null>(null);
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

  // Refresh the schedule when SignalR reconnects (catches events missed during disconnect). The
  // container list is already covered by the reconnect refresh above.
  useReconnectRefetch(isConnected, () => {
    void refreshSchedule();
  });

  // Card-level per-service save. Goes through the same whole-config round-trip the Configure
  // modal uses; optimistic so the control never flashes back to the old value.
  const saveServiceConfig = useCallback(
    async (
      serviceKey: ScheduledPrefillServiceKey,
      scheduleId: string,
      patch: { enabled?: boolean; intervalHours?: number; customSchedule?: CustomSchedule | null }
    ) => {
      if (!config) {
        return;
      }

      const previous = config;
      const updated: ScheduledPrefillConfigDto = {
        ...config,
        [serviceKey]: {
          ...config[serviceKey],
          schedules: config[serviceKey].schedules.map((schedule) =>
            schedule.id === scheduleId ? { ...schedule, ...patch } : schedule
          )
        }
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

  const handleServiceIntervalChange = useCallback(
    async (serviceKey: ScheduledPrefillServiceKey, scheduleId: string, hours: number) => {
      // The schedule clears in the same round-trip. A saved schedule runs in preference to the
      // interval, so one left behind would swallow the interval the user just picked.
      await saveServiceConfig(serviceKey, scheduleId, {
        intervalHours: hours,
        customSchedule: null
      });
    },
    [saveServiceConfig]
  );

  const handleServiceCustomScheduleChange = useCallback(
    async (
      serviceKey: ScheduledPrefillServiceKey,
      scheduleId: string,
      schedule: CustomSchedule
    ) => {
      await saveServiceConfig(serviceKey, scheduleId, { customSchedule: schedule });
    },
    [saveServiceConfig]
  );

  // Cancels one service's run through its tracked operation. The row is the only place a silent
  // run can be stopped from: it raises no notification, and the notification carried the only
  // cancel button. The schedule refreshes over SignalR when the run ends, so nothing is set here
  // beyond clearing the pending flag.
  const handleCancelService = useCallback(
    async (serviceId: ScheduledPrefillServiceId, scheduleId: string) => {
      const operationId = schedule.find(
        (item) => item.serviceId === serviceId && item.scheduleId === scheduleId
      )?.operationId;
      if (!operationId) return;
      setCancelingServices((pending) => [...pending, serviceId]);
      try {
        await ApiService.cancelOperation(operationId);
      } catch (cancelError) {
        setError(getErrorMessage(cancelError));
      } finally {
        setCancelingServices((pending) => pending.filter((id) => id !== serviceId));
        // Re-reads the rows rather than waiting for SignalR, which both drops the button as soon
        // as the run is gone and clears a row still offering Cancel for an id the tracker has
        // already reaped.
        await refreshSchedule();
      }
    },
    [schedule, refreshSchedule]
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

    // A start moves no timing, so the broadcast gate below cannot see it, yet each row's Run button
    // reads a per-service running flag that only the schedule fetch carries. Without this a service
    // started from its own row or by the cadence would keep an enabled button until the run ended.
    // Still no progress lines on this card; this refetches the row state, nothing else. [48]
    const handleStarted = () => {
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

    on('ScheduledPrefillStarted', handleStarted);
    on('ScheduledPrefillCompleted', handleCompleted);
    on('SchedulesUpdated', handleSchedulesUpdated);

    return () => {
      off('ScheduledPrefillStarted', handleStarted);
      off('ScheduledPrefillCompleted', handleCompleted);
      off('SchedulesUpdated', handleSchedulesUpdated);
    };
  }, [off, on, refreshSchedule]);

  const enabledCount = useMemo(
    () =>
      config
        ? SCHEDULED_PREFILL_SERVICE_RUN_ORDER.flatMap(
            (serviceKey) => config[serviceKey].schedules
          ).filter((schedule) => schedule.enabled).length
        : 0,
    [config]
  );

  const totalCount = config
    ? SCHEDULED_PREFILL_SERVICE_RUN_ORDER.reduce(
        (count, serviceKey) => count + config[serviceKey].schedules.length,
        0
      )
    : 0;

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
      if (!config[serviceId].schedules.some((schedule) => schedule.enabled)) {
        return false;
      }

      const container = containerByService.get(getPersistentServiceId(serviceId));
      return needsPersistentLogin(container);
    }).map((serviceId) => t(`${baseKey}.services.${serviceId}`));
  }, [config, persistentContainers, baseKey, t]);

  const formatTiming = useCallback(
    (item: ScheduledPrefillServiceScheduleDto): string => {
      // A saved schedule runs in preference to the interval, so it is decided first. The two
      // interval sentinels below would otherwise claim the row and word it as paused or
      // startup-only while the schedule is what actually drives it.
      const hasCustomSchedule = !!item.customSchedule;
      if (!hasCustomSchedule) {
        if (item.intervalHours === 0) {
          return t(`${baseKey}.nextRunSummary.paused`);
        }
        if (item.intervalHours === -1) {
          return t(`${baseKey}.nextRunSummary.startupOnly`);
        }
      }
      if (!item.nextRunUtc) {
        return hasCustomSchedule
          ? t(`${baseKey}.nextRunSummary.customSchedule`)
          : t(`${baseKey}.nextRunSummary.soon`);
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
    const rows: {
      key: ScheduledPrefillServiceKey;
      rowId: string;
      serviceId: ScheduledPrefillServiceId;
      scheduleId: string;
      label: string;
      enabled: boolean;
      containerRunning: boolean;
      loginState: ScheduledPrefillRowLoginState | null;
      intervalHours: number;
      customSchedule: CustomSchedule | null;
      nextTiming: string;
      nextRunUtc: string | null;
      lastRunUtc: string | null;
      isRunning: boolean;
      operationId: string | null;
    }[] = [];
    const containerByService = new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
      persistentContainers.map((container) => [container.service, container])
    );
    for (const item of schedule) {
      const serviceKey = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[item.serviceId];
      const serviceConfig = serviceKey ? config?.[serviceKey] : undefined;
      const savedSchedule = serviceConfig?.schedules.find(
        (candidate) => candidate.id === item.scheduleId
      );
      if (!serviceKey || !savedSchedule) {
        continue;
      }
      const enabled = savedSchedule.enabled;
      const container = containerByService.get(getPersistentServiceId(serviceKey));
      // Prefer the (optimistically updated) config value so the picker reflects a change
      // immediately; the schedule DTO catches up on the post-save refresh.
      const intervalHours = savedSchedule.intervalHours;
      const customSchedule = savedSchedule.customSchedule ?? null;
      // The absolute date is only meaningful for a real upcoming run. An overdue (past)
      // nextRunUtc that has not been re-stamped yet still reads as "soon" via formatTiming, so
      // suppress the stale elapsed timestamp here rather than render a confusing past date.
      // A custom schedule keeps its next run whatever the interval reads, since it is what the
      // server computes the run from.
      const upcomingNextRunUtc =
        enabled &&
        (intervalHours > 0 || customSchedule !== null) &&
        item.nextRunUtc !== null &&
        new Date(item.nextRunUtc).getTime() > now
          ? item.nextRunUtc
          : null;
      // The activity registry keys the persistent container by the lowercase platform token
      // (battleNet -> battlenet); the fetched container list is the pre-seed fallback.
      const activityPlatformKey = serviceKey.toLowerCase();
      rows.push({
        key: serviceKey,
        rowId: `${serviceKey}:${item.scheduleId}`,
        serviceId: item.serviceId,
        scheduleId: item.scheduleId,
        label: `${t(`${baseKey}.services.${serviceKey}`)} · ${item.name}`,
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
        customSchedule,
        // Disabled records retain their setup for reopening but do not claim a next run.
        nextTiming: enabled
          ? formatTiming({
              ...item,
              intervalHours,
              customSchedule
            })
          : '',
        nextRunUtc: upcomingNextRunUtc,
        lastRunUtc: item.lastRunUtc,
        isRunning: item.isRunning,
        operationId: item.operationId
      });
    }
    return rows;
  }, [schedule, config, persistentContainers, baseKey, formatTiming, now, t, activity]);

  const handleModalSaved = async () => {
    await loadSummary();
  };

  const handleOpenSchedule = (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => {
    setModalRecord({ serviceKey, scheduleId });
    setModalOpened(true);
  };

  const handleEnableSchedule = async (
    serviceKey: ScheduledPrefillServiceKey,
    scheduleId: string
  ) => {
    await saveServiceConfig(serviceKey, scheduleId, { enabled: true });
  };

  const isInitialLoading = loading && !config;
  const backendUpdateRequired = config !== null && config.version < 6;
  const tableDisabled = disabled || backendUpdateRequired;

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
              color="run"
              size="sm"
              className="control-h-md"
              onClick={onRunNow}
              disabled={runNowDisabled || backendUpdateRequired}
              loading={runNowLoading}
              stableWidth
            >
              {t('management.schedules.runNow')}
            </Button>
            <Button
              type="button"
              variant="filled"
              color="secondary"
              size="sm"
              className="control-h-md"
              onClick={() => setModalOpened(true)}
              disabled={tableDisabled}
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
                    <span role="columnheader">{t('management.actions.menuLabel')}</span>
                  </div>
                  {scheduleRows.map((row) => (
                    <ScheduledPrefillServiceScheduleRow
                      key={row.rowId}
                      serviceKey={row.key}
                      serviceId={row.serviceId}
                      scheduleId={row.scheduleId}
                      label={row.label}
                      enabled={row.enabled}
                      containerRunning={row.containerRunning}
                      loginState={row.loginState}
                      intervalHours={row.intervalHours}
                      customSchedule={row.customSchedule}
                      nextTiming={row.nextTiming}
                      nextRunUtc={row.nextRunUtc}
                      lastRunUtc={row.lastRunUtc}
                      disabled={tableDisabled}
                      runPending={isRunServicePending(row.serviceId, row.scheduleId)}
                      runDisabled={runServiceDisabled || backendUpdateRequired || row.isRunning}
                      isRunning={row.isRunning && row.operationId !== null}
                      cancelPending={cancelingServices.includes(row.serviceId)}
                      onRun={onRunService}
                      onCancel={(serviceId, scheduleId) =>
                        void handleCancelService(serviceId, scheduleId)
                      }
                      onOpen={handleOpenSchedule}
                      onEnable={(serviceKey, scheduleId) =>
                        void handleEnableSchedule(serviceKey, scheduleId)
                      }
                      onIntervalChange={(serviceKey, scheduleId, hours) =>
                        void handleServiceIntervalChange(serviceKey, scheduleId, hours)
                      }
                      onCustomScheduleChange={(serviceKey, scheduleId, schedule) =>
                        void handleServiceCustomScheduleChange(serviceKey, scheduleId, schedule)
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
              )}{' '}
              {backendUpdateRequired && (
                <p className="scheduled-prefill-card-summary__error">
                  {t(`${baseKey}.backendUpdateRequired`)}
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
        initialServiceKey={modalRecord?.serviceKey}
        initialScheduleId={modalRecord?.scheduleId}
        onClose={() => {
          setModalOpened(false);
          setModalRecord(null);
        }}
        onSaved={handleModalSaved}
      />
    </>
  );
}
