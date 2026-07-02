import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
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
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
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
        return;
      }

      setRunPhase('failed');
      setRunError(payload.error ?? t(`${eventsKey}.failed`));
    };

    on('ScheduledPrefillStarted', handleStarted);
    on('ScheduledPrefillProgress', handleProgress);
    on('ScheduledPrefillCompleted', handleCompleted);

    return () => {
      off('ScheduledPrefillStarted', handleStarted);
      off('ScheduledPrefillProgress', handleProgress);
      off('ScheduledPrefillCompleted', handleCompleted);
    };
  }, [eventsKey, off, on, refreshSchedule, t]);

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

  const scheduleLines = useMemo(() => {
    const byServiceKey = new Map<ScheduledPrefillServiceKey, ScheduledPrefillServiceScheduleDto>();
    for (const item of schedule) {
      const serviceKey = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[item.serviceId];
      if (serviceKey) {
        byServiceKey.set(serviceKey, item);
      }
    }

    const lines: { key: ScheduledPrefillServiceKey; text: string }[] = [];
    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const item = byServiceKey.get(serviceKey);
      if (!item || !item.enabled) {
        continue;
      }
      lines.push({
        key: serviceKey,
        text: t(`${baseKey}.nextRunSummary.item`, {
          service: t(`${baseKey}.services.${serviceKey}`),
          timing: formatTiming(item)
        })
      });
    }
    return lines;
  }, [schedule, baseKey, formatTiming, t]);

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
              {scheduleLines.length > 0 && (
                <p className="scheduled-prefill-card-summary__schedule">
                  {scheduleLines.map((line, index) => (
                    <Fragment key={line.key}>
                      {index > 0 && (
                        <span
                          className="scheduled-prefill-card-summary__schedule-sep"
                          aria-hidden="true"
                        >
                          {' · '}
                        </span>
                      )}
                      <span className="scheduled-prefill-card-summary__schedule-item">
                        {line.text}
                      </span>
                    </Fragment>
                  ))}
                </p>
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
