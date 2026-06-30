import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import type {
  ScheduledPrefillAccountServiceId,
  ScheduledPrefillAuthStatusItem,
  ScheduledPrefillCompletedEvent,
  ScheduledPrefillConfigDto,
  ScheduledPrefillProgressEvent,
  ScheduledPrefillRunPhase,
  ScheduledPrefillRunProgressItem,
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
  const [authStatuses, setAuthStatuses] = useState<ScheduledPrefillAuthStatusItem[]>([]);
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
      const [nextConfig, nextStatuses] = await Promise.all([
        ApiService.getScheduledPrefillConfig(signal),
        ApiService.getScheduledPrefillAuthStatus(signal)
      ]);
      setConfig(nextConfig);
      setAuthStatuses(nextStatuses);
      setError(null);
    } catch (loadError: unknown) {
      if (!isAbortError(loadError)) {
        setError(getErrorMessage(loadError));
      }
    } finally {
      setLoading(false);
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
  }, [eventsKey, off, on, t]);

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

    const authStatusByService = new Map<
      ScheduledPrefillAccountServiceId,
      ScheduledPrefillAuthStatusItem
    >(authStatuses.map((status) => [status.serviceId, status]));

    return SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.some((serviceId) => {
      if (!config[serviceId].enabled) {
        return false;
      }

      const loginState = authStatusByService.get(serviceId)?.loginState;
      return loginState === 'loginRequired' || loginState === 'unsupported';
    });
  }, [config, authStatuses]);

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
