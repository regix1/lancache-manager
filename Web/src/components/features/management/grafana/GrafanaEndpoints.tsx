import React, { useState, useEffect } from 'react';
import { Link, Copy, CheckCircle, Lock, Unlock, Lightbulb, RefreshCw, Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import ApiService from '@services/api.service';

const GrafanaEndpoints: React.FC = () => {
  const { t } = useTranslation();

  const dataRefreshOptions = [
    { value: '5', label: t('management.grafana.dataRefresh.5sec'), shortLabel: '5s', description: t('management.grafana.dataRefresh.5secDesc'), rightLabel: '5s', icon: RefreshCw },
    { value: '10', label: t('management.grafana.dataRefresh.10sec'), shortLabel: '10s', description: t('management.grafana.dataRefresh.10secDesc'), rightLabel: '10s', icon: RefreshCw },
    { value: '15', label: t('management.grafana.dataRefresh.15sec'), shortLabel: '15s', description: t('management.grafana.dataRefresh.15secDesc'), rightLabel: '15s', icon: RefreshCw },
    { value: '30', label: t('management.grafana.dataRefresh.30sec'), shortLabel: '30s', description: t('management.grafana.dataRefresh.30secDesc'), rightLabel: '30s', icon: RefreshCw },
    { value: '60', label: t('management.grafana.dataRefresh.60sec'), shortLabel: '60s', description: t('management.grafana.dataRefresh.60secDesc'), rightLabel: '60s', icon: RefreshCw },
  ];

  const scrapeIntervalOptions = [
    { value: '5', label: t('management.grafana.scrapeInterval.5sec'), shortLabel: '5s', description: t('management.grafana.scrapeInterval.5secDesc'), rightLabel: '5s', icon: Clock },
    { value: '10', label: t('management.grafana.scrapeInterval.10sec'), shortLabel: '10s', description: t('management.grafana.scrapeInterval.10secDesc'), rightLabel: '10s', icon: Clock },
    { value: '15', label: t('management.grafana.scrapeInterval.15sec'), shortLabel: '15s', description: t('management.grafana.scrapeInterval.15secDesc'), rightLabel: '15s', icon: Clock },
    { value: '30', label: t('management.grafana.scrapeInterval.30sec'), shortLabel: '30s', description: t('management.grafana.scrapeInterval.30secDesc'), rightLabel: '30s', icon: Clock },
    { value: '60', label: t('management.grafana.scrapeInterval.60sec'), shortLabel: '60s', description: t('management.grafana.scrapeInterval.60secDesc'), rightLabel: '60s', icon: Clock },
  ];
  const [copiedEndpoint, setCopiedEndpoint] = useState<string | null>(null);
  const [metricsSecured, setMetricsSecured] = useState<boolean>(false);
  const [dataRefreshRate, setDataRefreshRate] = useState<string>('15');
  const [scrapeInterval, setScrapeInterval] = useState<string>('15');
  const [isToggling, setIsToggling] = useState(false);

  // Load initial state on mount
  useEffect(() => {
    const loadStatus = async () => {
      try {
        const [securityRes, intervalRes] = await Promise.all([
          fetch('/api/metrics/security', ApiService.getFetchOptions()),
          fetch('/api/metrics/interval', ApiService.getFetchOptions())
        ]);
        if (securityRes.ok) {
          const securityData = await securityRes.json();
          setMetricsSecured(securityData.requiresAuthentication);
        }
        if (intervalRes.ok) {
          const intervalData = await intervalRes.json();
          setDataRefreshRate(String(intervalData.interval));
        }
      } catch (error) {
        console.error('Failed to load metrics status:', error);
      }
    };
    loadStatus();
  }, []);

  const handleDataRefreshChange = async (value: string) => {
    setDataRefreshRate(value);
    try {
      await fetch('/api/metrics/interval', ApiService.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ interval: parseInt(value, 10) })
      }));
    } catch (error) {
      console.error('Failed to update data refresh rate:', error);
    }
  };

  const handleScrapeIntervalChange = (value: string) => {
    setScrapeInterval(value);
  };

  const handleToggleAuth = async (value?: string) => {
    if (isToggling) return;
    setIsToggling(true);
    const newValue = value ? value === 'secured' : !metricsSecured;
    setMetricsSecured(newValue); // Optimistic update
    try {
      const response = await fetch('/api/metrics/security', ApiService.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: newValue })
      }));
      if (response.ok) {
        const data = await response.json();
        setMetricsSecured(data.requiresAuthentication);
      } else {
        setMetricsSecured(!newValue); // Revert on failure
      }
    } catch (error) {
      console.error('Failed to toggle metrics auth:', error);
      setMetricsSecured(!newValue); // Revert on error
    } finally {
      setIsToggling(false);
    }
  };

  const copyToClipboard = async (text: string, endpoint: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopiedEndpoint(endpoint);
      setTimeout(() => setCopiedEndpoint(null), 2000);
    } catch (err) {
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
        console.error('Failed to copy text: ', copyErr);
      } finally {
        document.body.removeChild(textArea);
      }
    }
  };

  const apiBaseUrl = window.location.origin;

  return (
    <Card>
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-indigo">
            <Link className="w-5 h-5 icon-indigo" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">
            {t('management.grafana.title')}
          </h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title={t('management.grafana.help.metrics.title')} variant="subtle">
              <div className="divide-y divide-[var(--theme-text-muted)]">
                <div className="py-1.5 first:pt-0 last:pb-0">
                  <div className="font-medium text-themed-primary">{t('management.grafana.help.metrics.cache.term')}</div>
                  <div className="mt-0.5">{t('management.grafana.help.metrics.cache.description')}</div>
                </div>
                <div className="py-1.5 first:pt-0 last:pb-0">
                  <div className="font-medium text-themed-primary">{t('management.grafana.help.metrics.activity.term')}</div>
                  <div className="mt-0.5">{t('management.grafana.help.metrics.activity.description')}</div>
                </div>
              </div>
            </HelpSection>

            <HelpSection title={t('management.grafana.help.integration.title')} variant="subtle">
              {t('management.grafana.help.integration.description')}
            </HelpSection>

            <HelpNote type="info">
              {t('management.grafana.help.note')}
            </HelpNote>
          </HelpPopover>
        </div>
        {/* Connected toggle switch */}
        <ToggleSwitch
          options={[
            { value: 'public', label: t('management.grafana.publicOption'), icon: <Unlock />, activeColor: 'default' },
            { value: 'secured', label: t('management.grafana.securedOption'), icon: <Lock />, activeColor: 'success' }
          ]}
          value={metricsSecured ? 'secured' : 'public'}
          onChange={handleToggleAuth}
          disabled={isToggling}
          loading={isToggling}
          title={metricsSecured
            ? t('management.grafana.securedTooltip')
            : t('management.grafana.publicTooltip')}
        />
      </div>

      <p className="text-themed-muted text-sm mb-4">
        {metricsSecured
          ? t('management.grafana.securedDescription')
          : t('management.grafana.publicDescription')}
      </p>

      <div className="p-4 rounded-lg bg-themed-tertiary">
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium text-themed-primary">{t('management.grafana.prometheusMetrics')}</span>
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
        <p className="text-xs text-themed-muted">
          {t('management.grafana.prometheusFormat')}
        </p>
      </div>

      {/* Data Refresh Rate - Controls how often the app updates metrics */}
      <div className="mt-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <RefreshCw className="w-4 h-4 text-themed-muted" />
            <div>
              <span className="text-sm font-medium text-themed-primary">{t('management.grafana.dataRefreshRate')}</span>
              <p className="text-xs text-themed-muted">{t('management.grafana.dataRefreshRateDesc')}</p>
            </div>
          </div>
          <EnhancedDropdown
            options={dataRefreshOptions}
            value={dataRefreshRate}
            onChange={handleDataRefreshChange}
            placeholder={t('management.grafana.placeholders.selectRate')}
            compactMode={true}
            dropdownWidth="w-56"
            alignRight={true}
            dropdownTitle={t('management.grafana.dataRefreshRate')}
            footerNote={t('management.grafana.dataRefreshFooter')}
            footerIcon={Lightbulb}
            cleanStyle={true}
          />
        </div>
      </div>

      {/* Prometheus Configuration - shows config based on current auth state */}
      <div className="mt-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <Clock className="w-4 h-4 text-themed-muted" />
            <div>
              <span className="text-sm font-medium text-themed-primary">{t('management.grafana.prometheusConfig')}</span>
              <p className="text-xs text-themed-muted">
                {metricsSecured ? t('management.grafana.prometheusConfigSecured') : t('management.grafana.prometheusConfigPublic')}
              </p>
            </div>
          </div>
          <EnhancedDropdown
            options={scrapeIntervalOptions}
            value={scrapeInterval}
            onChange={handleScrapeIntervalChange}
            placeholder={t('management.grafana.placeholders.selectInterval')}
            compactMode={true}
            dropdownWidth="w-56"
            alignRight={true}
            dropdownTitle={t('management.grafana.scrapeIntervalTitle')}
            footerNote={t('management.grafana.scrapeIntervalFooter')}
            footerIcon={Lightbulb}
            cleanStyle={true}
          />
        </div>

        {/* Config content based on current auth state */}
        <div className="space-y-3">
          <div>
            <p className="text-xs font-medium text-themed-secondary mb-1.5">prometheus.yml</p>
            <div className="bg-themed-secondary p-2 rounded font-mono text-[10px] text-themed-muted">
              <div>scrape_configs:</div>
              <div className="ml-2">- job_name: 'lancache-manager'</div>
              <div className="ml-4">static_configs:</div>
              <div className="ml-6">- targets: ['lancache-manager:80']</div>
              <div className="ml-4">scrape_interval: {scrapeInterval}s</div>
              <div className="ml-4">metrics_path: '/metrics'</div>
              {metricsSecured && (
                <>
                  <div className="ml-4 text-themed-success">authorization:</div>
                  <div className="ml-6 text-themed-success">type: Bearer</div>
                  <div className="ml-6 text-themed-success">credentials: 'your-api-key-here'</div>
                </>
              )}
            </div>
          </div>
          {metricsSecured && (
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

        {parseInt(scrapeInterval) < parseInt(dataRefreshRate) && (
          <p className="text-xs mt-3 flex items-center gap-1.5 text-themed-warning">
            <Lightbulb className="w-3 h-3 icon-warning" />
            {t('management.grafana.staleDataWarning')}
          </p>
        )}
      </div>

      {/* Grafana Query Examples */}
      <div className="mt-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <p className="text-sm font-medium text-themed-primary mb-2">{t('management.grafana.queryExamples')}</p>
        <div className="space-y-2">
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.hitRate')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_hit_ratio * 100</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.bandwidthSaved')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">increase(lancache_cache_hit_bytes_total[24h])</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.cacheUsage')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_used_bytes / 1024 / 1024 / 1024</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.peakHour')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_peak_hour</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.hourlyDownloads')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_hourly_downloads</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.growthRate')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_growth_daily_bytes / 1024 / 1024 / 1024</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># {t('management.grafana.queries.daysUntilFull')}</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_days_until_full</code>
          </div>
        </div>
      </div>
    </Card>
  );
};

export default GrafanaEndpoints;
