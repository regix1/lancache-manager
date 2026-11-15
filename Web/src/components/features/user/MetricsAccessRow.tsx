import React, { useCallback, useEffect, useRef, useState, type SetStateAction } from 'react';
import { useTranslation } from 'react-i18next';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { HelpPopover } from '@components/ui/HelpPopover';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import ApiService from '@services/api.service';
import { getErrorMessage, isAbortError } from '@utils/error';
import type { MetricsSecurityResponse } from '../management/grafana/GrafanaEndpoints.types';
import MetricsAccessSwitch from '../management/grafana/MetricsAccessSwitch';
import { METRICS_SOURCE_KEYS } from '../management/grafana/metricsSourceKeys';
import UserSettingRow from './UserSettingRow';

/**
 * Whether the Prometheus endpoint asks for the API key, shown and changed beside the sign-in method
 * because a public `/metrics` answers anyone, sign-in or not. It draws the Grafana card's own switch.
 */
const MetricsAccessRow: React.FC = () => {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const [security, setSecurity] = useState<MetricsSecurityResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [retrying, setRetrying] = useState(false);
  // Mount, reconnect and Retry can overlap; only the newest read writes the row.
  const requestRef = useRef(0);

  const load = useCallback(async (signal?: AbortSignal) => {
    const request = ++requestRef.current;
    try {
      const answer = await ApiService.getMetricsSecurity(signal);
      if (request !== requestRef.current) return;
      setSecurity(answer);
      setLoadError(null);
    } catch (error: unknown) {
      if (isAbortError(error)) return;
      if (request !== requestRef.current) return;
      setLoadError(getErrorMessage(error));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  useEffect(() => {
    const handleMetricsSecurityUpdated = (updated: MetricsSecurityResponse) => {
      ++requestRef.current;
      setSecurity(updated);
      setLoadError(null);
    };
    on('MetricsSecurityUpdated', handleMetricsSecurityUpdated);
    return () => off('MetricsSecurityUpdated', handleMetricsSecurityUpdated);
  }, [on, off]);

  // A save here is newer than any read already out, as a pushed update is.
  const changeSecurity = useCallback((update: SetStateAction<MetricsSecurityResponse | null>) => {
    ++requestRef.current;
    setSecurity(update);
  }, []);

  // A switch flipped while the socket was down sends no event this page hears.
  useReconnectRefetch(isConnected, () => void load());

  const retry = async () => {
    if (retrying) return;
    setRetrying(true);
    try {
      await load();
    } finally {
      setRetrying(false);
    }
  };

  if (loadError !== null) {
    return (
      <ErrorBlock
        title={t('management.grafana.errors.loadMetricsStatus')}
        message={loadError}
        retryLabel={t('common.retry')}
        onRetry={retry}
        retrying={retrying}
      />
    );
  }

  return (
    <UserSettingRow
      label={t('management.grafana.prometheusMetrics')}
      help={
        <HelpPopover aria-label={t('user.panes.help.metricsAria')}>
          {t('user.panes.help.metrics')}
        </HelpPopover>
      }
    >
      {security === null ? (
        <LoadingSpinner inline size="sm" className="user-settings-inline-spinner" />
      ) : (
        <>
          <span className="text-sm text-themed-muted">
            {t(METRICS_SOURCE_KEYS[security.source])}
          </span>
          <MetricsAccessSwitch security={security} setSecurity={changeSecurity} size="md" />
        </>
      )}
    </UserSettingRow>
  );
};

export default MetricsAccessRow;
