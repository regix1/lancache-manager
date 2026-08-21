import React, { useState, useEffect, useCallback } from 'react';
import {
  Link,
  Lock,
  Unlock,
  Lightbulb,
  RefreshCw,
  Clock,
  Settings,
  ListOrdered
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { useCopyFeedback } from '@/hooks/useCopyFeedback';
import { getErrorMessage, isAbortError } from '@utils/error';
import { copyText } from '@utils/clipboard';
import type { MetricsSecurityResponse } from './GrafanaEndpoints.types';

/**
 * One choice in the exported-game-count dropdown. The cap drives every label on the
 * row, so only the sentence explaining the trade-off is written out per choice.
 */
interface GameCountChoice {
  count: number;
  description: string;
}

/**
 * One choice in a polling-rate dropdown. The seconds value drives every label on the
 * row, so only the sentence explaining the trade-off is written out per choice.
 */
interface IntervalChoice {
  seconds: number;
  description: string;
}

/**
 * Shared trigger width for the three dropdowns in the polling-rates section. Their labels are
 * different lengths ("5 seconds" through "60 seconds", "20 games" through "200 games"), so without
 * a floor each control sizes to its own text and the stacked column comes out ragged. Wide enough
 * for the longest label plus the leading icon and the chevron.
 */
const POLLING_DROPDOWN_WIDTH = 'min-w-[150px]';

const GrafanaEndpoints: React.FC = () => {
  const { t } = useTranslation();
  const { isAdmin } = useAuth();
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { on, off, connectionState } = useSignalR();

  const dataRefreshOptions = [
    { seconds: 5, description: t('management.grafana.dataRefresh.5secDesc') },
    { seconds: 10, description: t('management.grafana.dataRefresh.10secDesc') },
    { seconds: 15, description: t('management.grafana.dataRefresh.15secDesc') },
    { seconds: 30, description: t('management.grafana.dataRefresh.30secDesc') },
    { seconds: 60, description: t('management.grafana.dataRefresh.60secDesc') }
  ].map(
    ({ seconds, description }: IntervalChoice): DropdownOption => ({
      value: String(seconds),
      label: t('management.grafana.dataRefresh.label', { seconds }),
      shortLabel: `${seconds}s`,
      description,
      rightLabel: `${seconds}s`,
      icon: RefreshCw
    })
  );

  const scrapeIntervalOptions = [
    { seconds: 5, description: t('management.grafana.scrapeInterval.5secDesc') },
    { seconds: 10, description: t('management.grafana.scrapeInterval.10secDesc') },
    { seconds: 15, description: t('management.grafana.scrapeInterval.15secDesc') },
    { seconds: 30, description: t('management.grafana.scrapeInterval.30secDesc') },
    { seconds: 60, description: t('management.grafana.scrapeInterval.60secDesc') }
  ].map(
    ({ seconds, description }: IntervalChoice): DropdownOption => ({
      value: String(seconds),
      label: t('management.grafana.scrapeInterval.label', { seconds }),
      shortLabel: `${seconds}s`,
      description,
      rightLabel: `${seconds}s`,
      icon: Clock
    })
  );

  const topGameOptions = [
    { count: 20, description: t('management.grafana.gameCount.20gamesDesc') },
    { count: 50, description: t('management.grafana.gameCount.50gamesDesc') },
    { count: 100, description: t('management.grafana.gameCount.100gamesDesc') },
    { count: 200, description: t('management.grafana.gameCount.200gamesDesc') }
  ].map(
    ({ count, description }: GameCountChoice): DropdownOption => ({
      value: String(count),
      label: t('management.grafana.gameCount.label', { games: count }),
      shortLabel: String(count),
      description,
      rightLabel: String(count),
      icon: ListOrdered
    })
  );
  const [copiedEndpoint, markCopied] = useCopyFeedback<string | null>(null);
  const [metricsSecurity, setMetricsSecurity] = useState<MetricsSecurityResponse | null>(null);
  const [dataRefreshRate, setDataRefreshRate] = useState<string>('15');
  const [scrapeInterval, setScrapeInterval] = useState<string>('15');
  const [topGames, setTopGames] = useState<string>('50');
  const [isToggling, setIsToggling] = useState(false);
  const [isConfigExpanded, setIsConfigExpanded] = useState(false);
  const [areRatesExpanded, setAreRatesExpanded] = useState(false);
  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('integrations-grafana', expanded, () => setExpanded((prev) => !prev));
  useAccordionGroupItem('integrations-grafana-polling-rates', areRatesExpanded, () =>
    setAreRatesExpanded((prev) => !prev)
  );
  useAccordionGroupItem('integrations-grafana-prometheus-config', isConfigExpanded, () =>
    setIsConfigExpanded((prev) => !prev)
  );

  const fetchMetricsSecurity = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const data = await ApiService.getMetricsSecurity(signal);
        setMetricsSecurity(data);
      } catch (error: unknown) {
        if (isAbortError(error)) return;
        notifyError(t('management.grafana.errors.loadSecurityStatus'), error, {
          logLabel: 'Failed to load metrics security status'
        });
      }
    },
    [notifyError, t]
  );

  // Load initial state on mount
  useEffect(() => {
    const controller = new AbortController();
    const loadStatus = async () => {
      try {
        const [, intervalRes, gameLimitRes] = await Promise.all([
          fetchMetricsSecurity(controller.signal),
          fetch('/api/metrics/interval', ApiService.getFetchOptions({ signal: controller.signal })),
          fetch(
            '/api/metrics/game-limit',
            ApiService.getFetchOptions({ signal: controller.signal })
          )
        ]);
        if (intervalRes.ok) {
          const intervalData = await intervalRes.json();
          setDataRefreshRate(String(intervalData.interval));
        }
        if (gameLimitRes.ok) {
          const gameLimitData = (await gameLimitRes.json()) as { gameLimit: number };
          setTopGames(String(gameLimitData.gameLimit));
        }
      } catch (error: unknown) {
        if (isAbortError(error)) return;
        // Interval load has a workable default (dataRefreshRate stays '15'); background noise.
        notifyError(t('management.grafana.errors.loadMetricsStatus'), error, {
          silent: true,
          logLabel: 'Failed to load metrics status'
        });
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
      notifyError(t('management.grafana.errors.updateRefreshRate'), error, {
        logLabel: 'Failed to update data refresh rate'
      });
    }
  };

  const handleTopGamesChange = async (value: string) => {
    const previous = topGames;
    setTopGames(value);
    try {
      const response = await fetch(
        '/api/metrics/game-limit',
        ApiService.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ gameLimit: parseInt(value, 10) })
        })
      );
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
    } catch (error) {
      // The server kept its old cap, so the control has to show it again.
      setTopGames(previous);
      notifyError(t('management.grafana.errors.updateTopGames'), error, {
        logLabel: 'Failed to update exported game count'
      });
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
    if (await copyText(text)) {
      markCopied(endpoint);
      return;
    }

    // Low-stakes UI action with no other visible cue either way, so this stays silent rather than
    // becoming an accidental console.error.
    notifyError(t('management.grafana.errors.copyFailed'), undefined, {
      silent: true,
      logLabel: 'Failed to copy text'
    });
  };

  const apiBaseUrl = window.location.origin;

  const accessBadge =
    metricsSecurity != null ? (
      <SectionHeaderChip variant={metricsSecurity.requiresAuthentication ? 'success' : 'neutral'}>
        {metricsSecurity.requiresAuthentication
          ? t('management.grafana.securedOption')
          : t('management.grafana.publicOption')}
      </SectionHeaderChip>
    ) : undefined;

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.grafana.help.aboutTitle')}>
        {t('management.grafana.summary')}
      </HelpSection>

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
            },
            {
              term: t('management.grafana.help.metrics.events.term'),
              description: t('management.grafana.help.metrics.events.description')
            }
          ]}
        />
      </HelpSection>

      <HelpSection title={t('management.grafana.help.integration.title')} variant="subtle">
        {t('management.grafana.help.integration.description')}
      </HelpSection>

      <HelpNote type="info">{t('management.grafana.help.note')}</HelpNote>
    </HelpPopover>
  );

  return (
    <AccordionSection
      title={t('management.grafana.title')}
      shortTitle={t('management.grafana.titleShort')}
      titleAccessory={helpAccessory}
      icon={Link}
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
      badge={accessBadge}
    >
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
            <div className="metrics-toggle-row cluster">
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
                  color="secondary"
                  size="sm"
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
          >
            {copiedEndpoint === 'prometheus' ? t('management.grafana.copied') : t('common.copy')}
          </Button>
        </div>
        <code className="text-xs block break-all px-3 py-2 rounded-md mb-2 font-mono bg-themed-secondary text-themed-secondary">
          {apiBaseUrl}/metrics
        </code>
        <p className="text-xs text-themed-muted">{t('management.grafana.prometheusFormat')}</p>
      </div>

      {/* Refresh and scrape rates - secondary tuning, collapsed by default so the
          endpoint access toggle and metrics URL read as the primary controls. */}
      <div className="mt-4">
        <AccordionSection
          title={t('management.grafana.pollingRates')}
          icon={RefreshCw}
          isExpanded={areRatesExpanded}
          onToggle={() => setAreRatesExpanded((prev) => !prev)}
          surface="well"
        >
          {/* Data Refresh Rate - Controls how often the app updates metrics */}
          <div className="p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
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
                className={POLLING_DROPDOWN_WIDTH}
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
                className={POLLING_DROPDOWN_WIDTH}
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

          {/* Top games exported - caps how many per-game series /metrics emits */}
          <div className="mt-2 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
              <div className="flex items-center gap-2">
                <ListOrdered className="w-4 h-4 text-themed-muted" />
                <div>
                  <span className="text-sm font-medium text-themed-primary">
                    {t('management.grafana.topGames')}
                  </span>
                  <p className="text-xs text-themed-muted">
                    {t('management.grafana.topGamesDesc')}
                  </p>
                </div>
              </div>
              <EnhancedDropdown
                variant="button"
                className={POLLING_DROPDOWN_WIDTH}
                options={topGameOptions}
                value={topGames}
                onChange={handleTopGamesChange}
                placeholder={t('management.grafana.placeholders.selectGameCount')}
                dropdownWidth="w-56"
                alignRight={true}
                dropdownTitle={t('management.grafana.topGames')}
                footerNote={t('management.grafana.topGamesFooter')}
                footerIcon={Lightbulb}
                cleanStyle={true}
              />
            </div>
          </div>
        </AccordionSection>
      </div>

      {/* Stale data warning - shown when scrape is faster than refresh */}
      {parseInt(scrapeInterval) < parseInt(dataRefreshRate) && (
        <p className="text-xs mt-2 px-3 flex items-center gap-1.5 text-themed-warning">
          <Lightbulb className="w-3 h-3 icon-warning" />
          {t('management.grafana.staleDataWarning')}
        </p>
      )}

      {/* Prometheus Config & Query Examples - collapsible */}
      <div className="mt-4">
        <AccordionSection
          title={t('management.grafana.prometheusConfig')}
          icon={Settings}
          isExpanded={isConfigExpanded}
          onToggle={() => setIsConfigExpanded((prev) => !prev)}
          surface="well"
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
              <div className="bg-themed-secondary p-2 rounded">
                <p className="text-[10px] text-themed-muted mb-1">
                  # {t('management.grafana.queries.eventBytes')}
                </p>
                <code className="text-[10px] font-mono text-themed-secondary">
                  topk(8, lancache_event_bytes)
                </code>
              </div>
            </div>
          </div>
        </AccordionSection>
      </div>
    </AccordionSection>
  );
};

export default GrafanaEndpoints;
