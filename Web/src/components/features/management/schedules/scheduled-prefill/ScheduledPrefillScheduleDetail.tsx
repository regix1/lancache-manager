import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import type {
  ScheduledPrefillAccountServiceId,
  ScheduledPrefillAuthStatusItem,
  ScheduledPrefillConfigDto
} from './types';

interface ScheduledPrefillScheduleDetailProps {
  disabled?: boolean;
}

const getErrorMessage = (error: unknown): string =>
  error instanceof Error ? error.message : String(error);

const isAbortError = (error: unknown): boolean =>
  error instanceof DOMException
    ? error.name === 'AbortError'
    : error instanceof Error && error.name === 'AbortError';

export function ScheduledPrefillScheduleDetail({
  disabled = false
}: ScheduledPrefillScheduleDetailProps) {
  const { t } = useTranslation();
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const [authStatuses, setAuthStatuses] = useState<ScheduledPrefillAuthStatusItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [modalOpened, setModalOpened] = useState(false);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

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
              {hasAccountWarning && (
                <p className="scheduled-prefill-card-summary__warning">
                  {t(`${baseKey}.authWarning`)}
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
