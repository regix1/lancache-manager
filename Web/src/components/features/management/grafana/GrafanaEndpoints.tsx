import React, { useState, useEffect, useCallback } from 'react';
import {
  Link,
  Copy,
  CheckCircle,
  Lock,
  Unlock,
  Lightbulb,
  RefreshCw,
  Clock,
  Settings
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { AccordionSection } from '@components/ui/AccordionSection';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { getErrorMessage, isAbortError } from '@utils/error';
import type { MetricsSecurityResponse } from './GrafanaEndpoints.types';
import './GrafanaEndpoints.css';

const GrafanaEndpoints: React.FC = () => {
  const { t } = useTranslation();
  const { isAdmin } = useAuth();
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { on, off, connectionState } = useSignalR();

  const dataRefreshOptions = [
    {
      value: '5',
      label: t('management.grafana.dataRefresh.5sec'),
      shortLabel: '5s',
      description: t('management.grafana.dataRefresh.5secDesc'),
      rightLabel: '5s',
      icon: RefreshCw
    },
    {
      value: '10',
      label: t('management.grafana.dataRefresh.10sec'),
      shortLabel: '10s',
      description: t('management.grafana.dataRefresh.10secDesc'),
      rightLabel: '10s',
      icon: RefreshCw
    },
    {
      value: '15',
      label: t('management.grafana.dataRefresh.15sec'),
      shortLabel: '15s',
      description: t('management.grafana.dataRefresh.15secDesc'),
      rightLabel: '15s',
      icon: RefreshCw
    },
    {
      value: '30',
      label: t('management.grafana.dataRefresh.30sec'),
      shortLabel: '30s',
      description: t('management.grafana.dataRefresh.30secDesc'),
      rightLabel: '30s',
      icon: RefreshCw
    },
    {
      value: '60',
      label: t('management.grafana.dataRefresh.60sec'),
      shortLabel: '60s',
      description: t('management.grafana.dataRefresh.60secDesc'),
      rightLabel: '60s',
      icon: RefreshCw
    }
  ];

  const scrapeIntervalOptions = [
    {
      value: '5',
      label: t('management.grafana.scrapeInterval.5sec'),
      shortLabel: '5s',
      description: t('management.grafana.scrapeInterval.5secDesc'),
      rightLabel: '5s',
      icon: Clock
    },
    {
      value: '10',
      label: t('management.grafana.scrapeInterval.10sec'),
      shortLabel: '10s',
      description: t('management.grafana.scrapeInterval.10secDesc'),
      rightLabel: '10s',
      icon: Clock
    },
    {
      value: '15',
      label: t('management.grafana.scrapeInterval.15sec'),
      shortLabel: '15s',
      description: t('management.grafana.scrapeInterval.15secDesc'),
      rightLabel: '15s',
      icon: Clock
    },
    {
      value: '30',
      label: t('management.grafana.scrapeInterval.30sec'),
      shortLabel: '30s',
      description: t('management.grafana.scrapeInterval.30secDesc'),
      rightLabel: '30s',
      icon: Clock
    },
    {
      value: '60',
      label: t('management.grafana.scrapeInterval.60sec'),
      shortLabel: '60s',
      description: t('management.grafana.scrapeInterval.60secDesc'),
      rightLabel: '60s',
      icon: Clock
    }
  ];
  const [copiedEndpoint, setCopiedEndpoint] = useState<string | null>(null);
  const [metricsSecurity, setMetricsSecurity] = useState<MetricsSecurityResponse | null>(null);
  const [dataRefreshRate, setDataRefreshRate] = useState<string>('15');
  const [scrapeInterval, setScrapeInterval] = useState<string>('15');
  const [isToggling, setIsToggling] = useState(false);
  const [isConfigExpanded, setIsConfigExpanded] = useState(false);

  const fetchMetricsSecurity = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const data = await ApiService.getMetricsSecurity(signal);
        setMetricsSecurity(data);
      } catch (error: unknown) {
        if (isAbortError(error)) return;
        notifyError(
          t('management.grafana.errors.loadSecurityStatus', 'Failed to load metrics access status'),
          error,
          { logLabel: 'Failed to load metrics security status' }
        );
      }
    },
    [notifyError, t]
  );

  // Load initial state on mount
  useEffect(() => {
    const controller = new AbortController();
    const loadStatus = async () => {
      try {
        const [, intervalRes] = await Promise.all([
          fetchMetricsSecurity(controller.signal),
          fetch('/api/metrics/interval', ApiService.getFetchOptions({ signal: controller.signal }))
        ]);
        if (intervalRes.ok) {
          const intervalData = await intervalRes.json();
          setDataRefreshRate(String(intervalData.interval));
        }
      } catch (error: unknown) {
        if (isAbortError(error)) return;
        // Interval load has a workable default (dataRefreshRate stays '15'); background noise.
        notifyError(
          t('management.grafana.errors.loadMetricsStatus', 'Failed to load metrics status'),
          error,
          { silent: true, logLabel: 'Failed to load metrics status' }
        );
      }
    };
    void loadStatus();
    return () => controller.abort();
  }, [fetchMetricsSecurity, notifyError, t]);

  // Subscribe to real-time MetricsSecurityUpdated events via SignalR
  useEffect(() => {
    const handleMetricsSecurityUpdated = (data: MetricsSecurityResponse) => {
      setMetricsSecurity(data);
    };
    on('MetricsSecurityUpdated', handleMetricsSecurityUpdated);
    return () => off('MetricsSecurityUpdated', handleMetricsSecurityUpdated);
  }, [on, off]);

  // Refetch when SignalR reconnects to recover any missed updates
  useEffect(() => {
    if (connectionState === 'connected') {
      void fetchMetricsSecurity();
    }
  }, [connectionState, fetchMetricsSecurity]);

  const handleDataRefreshChange = async (value: string) => {
    setDataRefreshRate(value);
    try {
      await fetch(
        '/api/metrics/interval',
        ApiService.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ interval: parseInt(value, 10) })
        })
      );
    } catch (error) {
      notifyError(
        t('management.grafana.errors.updateRefreshRate', 'Failed to update data refresh rate'),
        error,
        { logLabel: 'Failed to update data refresh rate' }
      );
    }
  };

  const handleScrapeIntervalChange = (value: string) => {
    setScrapeInterval(value);
  };

  const handleToggleAuth = async (value?: string) => {
    if (isToggling || !metricsSecurity) return;
    setIsToggling(true);
    const newValue = value ? value === 'secured' : !metricsSecurity.requiresAuthentication;
    // Optimistic update
    setMetricsSecurity((prev) => (prev ? { ...prev, requiresAuthentication: newValue } : prev));
    try {
      const data = await ApiService.setMetricsSecurity(newValue);
      setMetricsSecurity(data);
    } catch (error: unknown) {
      // Revert optimistic update
      setMetricsSecurity((prev) => (prev ? { ...prev, requiresAuthentication: !newValue } : prev));
      const message = getErrorMessage(error) || 'network';
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.grafana.metricsToggle.error', { status: message }),
        details: { notificationType: 'error' }
      });
    } finally {
      setIsToggling(false);
    }
  };

  const handleResetToDefault = async () => {
    if (isToggling) return;
    setIsToggling(true);
    try {
      const data = await ApiService.setMetricsSecurity(null);
      setMetricsSecurity(data);
    } catch (error: unknown) {
      const message = getErrorMessage(error) || 'network';
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.grafana.metricsToggle.error', { status: message }),
        details: { notificationType: 'error' }
      });
    } finally {
      setIsToggling(false);
    }
  };

  const getSourceLabel = (security: MetricsSecurityResponse): string => {
    if (security.source === 'ui') {
      return t('management.grafana.metricsToggle.source.ui');
    }
    if (security.source === 'config') {
      if (security.envVarValue !== security.requiresAuthentication) {
        return t('management.grafana.metricsToggle.source.config');
      }
      return t('management.grafana.metricsToggle.source.default');
    }
    return t('management.grafana.metricsToggle.source.default');
  };

  const copyToClipboard = async (text: string, endpoint: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopiedEndpoint(endpoint);
      setTimeout(() => setCopiedEndpoint(null), 2000);
    } catch (_err) {
      // Fallback for older browsers or when clipboard API fails
      const textArea = document.createElement('textarea');
      textArea.value = text;
      textArea.style.position = 'fixed';
      textArea.style.left = '-999999px';
      textArea.style.top = '-999999px';
      document.body.appendChild(textArea);
      textArea.focus();
      textArea.select();

      try {
        document.execCommand('copy');
        setCopiedEndpoint(endpoint);
        setTimeout(() => setCopiedEndpoint(null), 2000);
      } catch (copyErr) {
        // Legacy-clipboard-fallback failure: low-stakes UI action, no other visible cue either
        // way. Silence is explicit rather than an accidental console.error.
        notifyError(
          t('management.grafana.errors.copyFailed', 'Failed to copy to clipboard'),
          copyErr,
          { silent: true, logLabel: 'Failed to copy text' }
        );
      } finally {
        document.body.removeChild(textArea);
      }
    }
  };

  const apiBaseUrl = window.location.origin;

  return (
    <Card>
      <div className="flex items-center gap-3 mb-4">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-indigo">
          <Link className="w-5 h-5 icon-indigo" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary">
          {t('management.grafana.title')}
        </h3>
        <HelpPopover position="left" width={320}>
          <HelpSection title={t('management.grafana.help.metrics.title')} variant="subtle">
            <HelpDefinition
              items={[
                {
                  term: t('management.grafana.help.metrics.cache.term'),
                  description: t('management.grafana.help.metrics.cache.description')
                },
                {
                  term: t('management.grafana.help.metrics.activity.term'),
                  description: t('management.grafana.help.metrics.activity.description')
                }
              ]}
            />
          </HelpSection>

          <HelpSection title={t('management.grafana.help.integration.title')} variant="subtle">
            {t('management.grafana.help.integration.description')}
          </HelpSection>

          <HelpNote type="info">{t('management.grafana.help.note')}</HelpNote>
        </HelpPopover>
      </div>

      <p className="text-themed-muted text-sm mb-4">
        {metricsSecurity?.requiresAuthentication
          ? t('management.grafana.securedDescription')
          : t('management.grafana.publicDescription')}
      </p>

      {/* Endpoint access toolbar */}
      <div className="mb-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        {metricsSecurity === null ? (
          <LoadingSpinner inline size="sm" />
        ) : (
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
            <div>
              <span className="text-sm font-medium text-themed-primary">
                {t('management.grafana.accessMode')}
              </span>
              <p className="metrics-source-label">{getSourceLabel(metricsSecurity)}</p>
            </div>
            <div className="metrics-toggle-row">
              <ToggleSwitch
                options={[
                  {
                    value: 'public',
                    label: t('management.grafana.publicOption'),
                    icon: <Unlock />,
                    activeColor: 'default'
                  },
                  {
                    value: 'secured',
                    label: t('management.grafana.securedOption'),
                    icon: <Lock />,
                    activeColor: 'success'
                  }
                ]}
                value={metricsSecurity.requiresAuthentication ? 'secured' : 'public'}
                onChange={handleToggleAuth}
                disabled={isToggling || !isAdmin}
                loading={isToggling}
                title={
                  !isAdmin
                    ? t('management.grafana.metricsToggle.adminRequired')
                    : metricsSecurity.requiresAuthentication
                      ? t('management.grafana.securedTooltip')
                      : t('management.grafana.publicTooltip')
                }
              />
              {isAdmin && (
                <Button
                  variant="filled"
                  color="gray"
                  size="xs"
                  disabled={metricsSecurity.source !== 'ui' || isToggling}
                  onClick={handleResetToDefault}
                  className="metrics-reset-button"
                >
                  {t('management.grafana.metricsToggle.resetToDefault')}
                </Button>
              )}
            </div>
          </div>
        )}
      </div>

      <div className="p-4 rounded-lg bg-themed-tertiary">
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium text-themed-primary">
            {t('management.grafana.prometheusMetrics')}
          </span>
          <Button
            size="xs"
            variant={copiedEndpoint === 'prometheus' ? 'filled' : 'default'}
            color={copiedEndpoint === 'prometheus' ? 'green' : undefined}
            onClick={() => copyToClipboard(`${apiBaseUrl}/metrics`, 'prometheus')}
            leftSection={
              copiedEndpoint === 'prometheus' ? (
                <CheckCircle className="w-3 h-3" />
              ) : (
                <Copy className="w-3 h-3" />
              )
            }
          >
            {copiedEndpoint === 'prometheus' ? t('management.grafana.copied') : t('common.copy')}
          </Button>
        </div>
        <code className="text-xs block break-all px-3 py-2 rounded-md mb-2 font-mono bg-themed-secondary text-themed-secondary">
          {apiBaseUrl}/metrics
        </code>
        <p className="text-xs text-themed-muted">{t('management.grafana.prometheusFormat')}</p>
      </div>

      {/* Data Refresh Rate - Controls how often the app updates metrics */}
      <div className="mt-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
          <div className="flex items-center gap-2">
            <RefreshCw className="w-4 h-4 text-themed-muted" />
            <div>
              <span className="text-sm font-medium text-themed-primary">
                {t('management.grafana.dataRefreshRate')}
              </span>
              <p className="text-xs text-themed-muted">
                {t('management.grafana.dataRefreshRateDesc')}
              </p>
            </div>
          </div>
          <EnhancedDropdown
            variant="button"
            options={dataRefreshOptions}
            value={dataRefreshRate}
            onChange={handleDataRefreshChange}
            placeholder={t('management.grafana.placeholders.selectRate')}
            dropdownWidth="w-56"
            alignRight={true}
            dropdownTitle={t('management.grafana.dataRefreshRate')}
            footerNote={t('management.grafana.dataRefreshFooter')}
            footerIcon={Lightbulb}
            cleanStyle={true}
          />
        </div>
      </div>

      {/* Stale data warning - shown when scrape is faster than refresh */}
      {parseInt(scrapeInterval) < parseInt(dataRefreshRate) && (
        <p className="text-xs mt-2 px-3 flex items-center gap-1.5 text-themed-warning">
          <Lightbulb className="w-3 h-3 icon-warning" />
          {t('management.grafana.staleDataWarning')}
        </p>
      )}

      {/* Prometheus Scrape Interval - Controls how often Prometheus pulls metrics */}
      <div className="mt-2 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
          <div className="flex items-center gap-2">
            <Clock className="w-4 h-4 text-themed-muted" />
            <div>
              <span className="text-sm font-medium text-themed-primary">
                {t('management.grafana.scrapeIntervalRate')}
              </span>
              <p className="text-xs text-themed-muted">
                {t('management.grafana.scrapeIntervalRateDesc')}
              </p>
            </div>
          </div>
          <EnhancedDropdown
            variant="button"
            options={scrapeIntervalOptions}
            value={scrapeInterval}
            onChange={handleScrapeIntervalChange}
            placeholder={t('management.grafana.placeholders.selectInterval')}
            dropdownWidth="w-56"
            alignRight={true}
            dropdownTitle={t('management.grafana.scrapeIntervalTitle')}
            footerNote={t('management.grafana.scrapeIntervalRateFooter')}
            footerIcon={Lightbulb}
            cleanStyle={true}
          />
        </div>
      </div>

      {/* Prometheus Config & Query Examples - collapsible */}
      <div className="mt-4">
        <AccordionSection
          title={t('management.grafana.prometheusConfig')}
          icon={Settings}
          isExpanded={isConfigExpanded}
          onToggle={() => setIsConfigExpanded((prev) => !prev)}
        >
          {/* Prometheus Configuration - shows config based on current auth state */}
          <div className="p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
            <p className="text-xs text-themed-muted mb-3">
              {metricsSecurity?.requiresAuthentication
                ? t('management.grafana.prometheusConfigSecured')
                : t('management.grafana.prometheusConfigPublic')}
            </p>

            {/* Config content based on current auth state */}
            <div className="space-y-3">
              <div>
                <p className="text-xs font-medium text-themed-secondary mb-1.5">prometheus.yml</p>
                <div className="bg-themed-secondary p-2 rounded font-mono text-[10px] text-themed-muted">
                  <div>scrape_configs:</div>
                  <div className="ml-2">- job_name: &apos;lancache-manager&apos;</div>
                  <div className="ml-4">static_configs:</div>
                  <div className="ml-6">- targets: [&apos;lancache-manager:80&apos;]</div>
                  <div className="ml-4">scrape_interval: {scrapeInterval}s</div>
                  <div className="ml-4">metrics_path: &apos;/metrics&apos;</div>
                  {metricsSecurity?.requiresAuthentication && (
                    <>
                      <div className="ml-4 text-themed-success">authorization:</div>
                      <div className="ml-6 text-themed-success">type: Bearer</div>
                      <div className="ml-6 text-themed-success">
                        credentials: &apos;your-api-key-here&apos;
                      </div>
                    </>
                  )}
                </div>
              </div>
              {metricsSecurity?.requiresAuthentication && (
                <p className="text-xs text-themed-muted flex items-center gap-1.5">
                  <Lightbulb className="w-3 h-3 icon-warning" />
                  {t('management.grafana.replaceApiKey')}
                </p>
              )}
              <p className="text-xs text-themed-muted flex items-center gap-1.5">
                <Lightbulb className="w-3 h-3 icon-info" />
                {t('management.grafana.portInfo')}
              </p>
            </div>
          </div>

          {/* Grafana Query Examples */}
          <div className="mt-3 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
            <p className="text-sm font-medium text-themed-primary mb-2">
              {t('management.grafana.queryExamples')}
            </p>
            <div className="space-y-2">
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.hitRate')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  lancache_cache_hit_ratio * 100
                </code>
              </div>
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.bandwidthSaved')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  increase(lancache_cache_hit_bytes_total[24h])
                </code>
              </div>
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.cacheUsage')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  lancache_cache_used_bytes / 1024 / 1024 / 1024
                </code>
              </div>
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.peakHour')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  lancache_peak_hour
                </code>
              </div>
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.hourlyDownloads')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  lancache_hourly_downloads
                </code>
              </div>
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.growthRate')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  lancache_cache_growth_daily_bytes / 1024 / 1024 / 1024
                </code>
              </div>
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.daysUntilFull')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  lancache_cache_days_until_full
                </code>
              </div>
            </div>
          </div>
        </AccordionSection>
      </div>
    </Card>
  );
};

export default GrafanaEndpoints;
