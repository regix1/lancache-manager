import { useCallback, useEffect, useMemo, useState } from 'react';
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
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import { getPersistentServiceId, needsPersistentLogin } from './scheduledPrefillPlatformUi';
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

interface ScheduledPrefillScheduleDetailProps {
  disabled?: boolean;
}

export function ScheduledPrefillScheduleDetail({
  disabled = false
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
    try {
      setSchedule(await ApiService.getScheduledPrefillSchedule());
    } catch {
      // Keep the prior schedule on failure; SchedulesUpdated / a later load will correct it.
    }
  }, []);

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

    on('ScheduledPrefillStarted', handleStarted);
    on('ScheduledPrefillProgress', handleProgress);
    on('ScheduledPrefillCompleted', handleCompleted);

    return () => {
      off('ScheduledPrefillStarted', handleStarted);
      off('ScheduledPrefillProgress', handleProgress);
      off('ScheduledPrefillCompleted', handleCompleted);
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
      intervalHours: number;
      timing: string | null;
    }[] = [];
    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const item = byServiceKey.get(serviceKey);
      if (!item || !item.enabled) {
        continue;
      }
      // Prefer the (optimistically updated) config value so the picker reflects a change
      // immediately; the schedule DTO catches up on the post-save refresh.
      const intervalHours = config ? config[serviceKey].intervalHours : item.intervalHours;
      rows.push({
        key: serviceKey,
        label: t(`${baseKey}.services.${serviceKey}`),
        intervalHours,
        // Paused (0) / startup-only (-1) are already spelled out by the picker itself;
        // only show a timing hint when there is an actual upcoming run.
        timing: intervalHours > 0 ? formatTiming({ ...item, intervalHours }) : null
      });
    }
    return rows;
  }, [schedule, config, baseKey, formatTiming, t]);

  const handleModalSaved = async () => {
    await loadSummary();
  };

  const latestProgressLine =
    runProgress.length > 0 ? formatProgressLine(runProgress[runProgress.length - 1]) : null;

  return (
    <>
      <div className="schedule-extra-row scheduled-prefill-card-summary">
        <div className="scheduled-prefill-card-summary__content">
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
                    <div key={row.key} className="scheduled-prefill-card-summary__schedule-row">
                      <span className="scheduled-prefill-card-summary__schedule-service">
                        {row.label}
                      </span>
                      <div className="scheduled-prefill-card-summary__schedule-picker">
                        <ScheduleIntervalPicker
                          intervalHours={row.intervalHours}
                          isDisabled={disabled}
                          onChange={(hours) => void handleServiceIntervalChange(row.key, hours)}
                        />
                      </div>
                      {row.timing && (
                        <span className="scheduled-prefill-card-summary__schedule-timing">
                          {row.timing}
                        </span>
                      )}
                    </div>
                  ))}
                </div>
              )}
              {config && enabledCount === 0 && (
                <p className="scheduled-prefill-card-summary__warning">
                  {t(`${baseKey}.zeroEnabledWarning`)}
                </p>
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
