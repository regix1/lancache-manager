import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import {
  SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import { ScheduledPrefillAuthStatus } from './ScheduledPrefillAuthStatus';
import { ScheduledPrefillServiceRow } from './ScheduledPrefillServiceRow';
import type {
  ScheduledPrefillAuthStatusItem,
  ScheduledPrefillConfigDto,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillConfigModalProps {
  opened: boolean;
  onClose: () => void;
  onSaved?: () => void | Promise<void>;
}

const getErrorMessage = (error: unknown): string =>
  error instanceof Error ? error.message : String(error);

const isAbortError = (error: unknown): boolean =>
  error instanceof DOMException
    ? error.name === 'AbortError'
    : error instanceof Error && error.name === 'AbortError';

const validateServiceConfig = (
  serviceConfig: ScheduledPrefillServiceConfigDto,
  serviceName: string,
  t: (key: string, values?: Record<string, string | number>) => string
): string | null => {
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  if (!serviceConfig.enabled) {
    return null;
  }

  if (serviceConfig.preset === 'Top' && (!serviceConfig.topCount || serviceConfig.topCount < 1)) {
    return t(`${baseKey}.validation.topCount`, { service: serviceName });
  }

  if (serviceConfig.operatingSystems.length === 0) {
    return t(`${baseKey}.validation.operatingSystems`, { service: serviceName });
  }

  if (
    serviceConfig.maxConcurrency.mode === 'Fixed' &&
    (serviceConfig.maxConcurrency.value < SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min ||
      serviceConfig.maxConcurrency.value > SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max)
  ) {
    return t(`${baseKey}.validation.maxConcurrency`, {
      service: serviceName,
      min: SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min,
      max: SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max
    });
  }

  return null;
};

export function ScheduledPrefillConfigModal({
  opened,
  onClose,
  onSaved
}: ScheduledPrefillConfigModalProps) {
  const { t } = useTranslation();
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const [authStatuses, setAuthStatuses] = useState<ScheduledPrefillAuthStatusItem[]>([]);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [loadingAuthStatus, setLoadingAuthStatus] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  const loadConfig = useCallback(async (signal?: AbortSignal) => {
    setLoadingConfig(true);
    try {
      const nextConfig = await ApiService.getScheduledPrefillConfig(signal);
      setConfig(nextConfig);
      setLoadError(null);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        setLoadError(getErrorMessage(error));
      }
    } finally {
      setLoadingConfig(false);
    }
  }, []);

  const loadAuthStatus = useCallback(async (signal?: AbortSignal) => {
    setLoadingAuthStatus(true);
    try {
      const nextStatuses = await ApiService.getScheduledPrefillAuthStatus(signal);
      setAuthStatuses(nextStatuses);
      setLoadError(null);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        setLoadError(getErrorMessage(error));
      }
    } finally {
      setLoadingAuthStatus(false);
    }
  }, []);

  useEffect(() => {
    if (!opened) {
      return;
    }

    const controller = new AbortController();
    setValidationError(null);
    setSaveError(null);
    void loadConfig(controller.signal);
    void loadAuthStatus(controller.signal);

    return () => {
      controller.abort();
    };
  }, [opened, loadConfig, loadAuthStatus]);

  const isLoading = loadingConfig || loadingAuthStatus;
  const hasInitialData = config !== null;

  const validationMessage = useMemo(() => {
    if (!config) {
      return null;
    }

    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const serviceName = t(`${baseKey}.services.${serviceKey}`);
      const error = validateServiceConfig(config[serviceKey], serviceName, t);
      if (error) {
        return error;
      }
    }

    return null;
  }, [config, t, baseKey]);

  const handleServiceChange = (
    serviceKey: ScheduledPrefillServiceKey,
    serviceConfig: ScheduledPrefillServiceConfigDto
  ) => {
    setConfig((current) => (current ? { ...current, [serviceKey]: serviceConfig } : current));
    setValidationError(null);
    setSaveError(null);
  };

  const handleSave = async () => {
    if (!config) {
      return;
    }

    if (validationMessage) {
      setValidationError(validationMessage);
      return;
    }

    setSaving(true);
    setSaveError(null);
    setValidationError(null);

    try {
      await ApiService.updateScheduledPrefillConfig(config);
      await Promise.resolve(onSaved?.());
      onClose();
    } catch (error: unknown) {
      setSaveError(getErrorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  const handleClose = () => {
    if (!saving) {
      onClose();
    }
  };

  return (
    <Modal opened={opened} onClose={handleClose} title={t(`${baseKey}.title`)} size="xl">
      <div className="scheduled-prefill-config-modal">
        {isLoading && !hasInitialData ? (
          <div className="scheduled-prefill-config-modal__loading">
            <LoadingSpinner size="lg" />
            <span>{t(`${baseKey}.loading`)}</span>
          </div>
        ) : (
          <>
            {loadError && (
              <div className="scheduled-prefill-config-modal__message scheduled-prefill-config-modal__message--error">
                {t(`${baseKey}.loadError`, { error: loadError })}
              </div>
            )}
            {validationError && (
              <div className="scheduled-prefill-config-modal__message scheduled-prefill-config-modal__message--warning">
                {validationError}
              </div>
            )}
            {saveError && (
              <div className="scheduled-prefill-config-modal__message scheduled-prefill-config-modal__message--error">
                {t(`${baseKey}.saveError`, { error: saveError })}
              </div>
            )}

            <ScheduledPrefillAuthStatus
              statuses={authStatuses}
              loading={loadingAuthStatus}
              disabled={saving}
              onRefresh={() => loadAuthStatus()}
              onError={setSaveError}
            />

            {config ? (
              <div className="scheduled-prefill-config-modal__rows">
                {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => (
                  <div key={serviceKey} className="scheduled-prefill-config-modal__row">
                    <ScheduledPrefillServiceRow
                      serviceKey={serviceKey}
                      config={config[serviceKey]}
                      disabled={saving || loadingConfig}
                      onChange={(serviceConfig) => handleServiceChange(serviceKey, serviceConfig)}
                    />
                  </div>
                ))}
              </div>
            ) : (
              <div className="scheduled-prefill-config-modal__empty">{t(`${baseKey}.empty`)}</div>
            )}
          </>
        )}

        <div className="scheduled-prefill-config-modal__actions">
          <Button type="button" variant="default" onClick={handleClose} disabled={saving}>
            {t('common.cancel')}
          </Button>
          <Button
            type="button"
            variant="filled"
            color="green"
            onClick={handleSave}
            disabled={!config || saving || loadingConfig}
          >
            {saving && <LoadingSpinner inline size="sm" />}
            {saving ? t(`${baseKey}.actions.saving`) : t(`${baseKey}.actions.save`)}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
