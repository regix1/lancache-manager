import React, { useState, useEffect } from 'react';
import { Link, Copy, CheckCircle, Lock, Unlock, Lightbulb, RefreshCw, Clock } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';

const dataRefreshOptions = [
  { value: '5', label: '5 seconds', shortLabel: '5s', description: 'Very frequent updates', rightLabel: '5s', icon: RefreshCw },
  { value: '10', label: '10 seconds', shortLabel: '10s', description: 'Recommended for real-time', rightLabel: '10s', icon: RefreshCw },
  { value: '15', label: '15 seconds', shortLabel: '15s', description: 'Balanced performance', rightLabel: '15s', icon: RefreshCw },
  { value: '30', label: '30 seconds', shortLabel: '30s', description: 'Lower resource usage', rightLabel: '30s', icon: RefreshCw },
  { value: '60', label: '60 seconds', shortLabel: '60s', description: 'Minimal overhead', rightLabel: '60s', icon: RefreshCw },
];

const scrapeIntervalOptions = [
  { value: '5', label: '5 seconds', shortLabel: '5s', description: 'High frequency polling', rightLabel: '5s', icon: Clock },
  { value: '10', label: '10 seconds', shortLabel: '10s', description: 'Recommended', rightLabel: '10s', icon: Clock },
  { value: '15', label: '15 seconds', shortLabel: '15s', description: 'Standard polling', rightLabel: '15s', icon: Clock },
  { value: '30', label: '30 seconds', shortLabel: '30s', description: 'Low frequency', rightLabel: '30s', icon: Clock },
  { value: '60', label: '60 seconds', shortLabel: '60s', description: 'Minimal polling', rightLabel: '60s', icon: Clock },
];

const GrafanaEndpoints: React.FC = () => {
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
          fetch('/api/metrics/security'),
          fetch('/api/metrics/interval')
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
      await fetch('/api/metrics/interval', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ interval: parseInt(value, 10) })
      });
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
      const response = await fetch('/api/metrics/security', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: newValue })
      });
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
            Live API Endpoints for Grafana
          </h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title="Metrics">
              <div className="space-y-1.5">
                <HelpDefinition term="Cache" termColor="blue">
                  Capacity, usage, hit/miss bytes, and hit ratio
                </HelpDefinition>
                <HelpDefinition term="Activity" termColor="green">
                  Active downloads, connected clients, per-service counters
                </HelpDefinition>
              </div>
            </HelpSection>

            <HelpSection title="Integration" variant="subtle">
              Compatible with Prometheus and Grafana JSON datasource plugins.
              Poll every 10-30 seconds for real-time monitoring.
            </HelpSection>

            <HelpNote type="info">
              Toggle authentication below or via docker-compose environment.
            </HelpNote>
          </HelpPopover>
        </div>
        {/* Connected toggle switch */}
        <ToggleSwitch
          options={[
            { value: 'public', label: 'Public', icon: <Unlock />, activeColor: 'default' },
            { value: 'secured', label: 'Secured', icon: <Lock />, activeColor: 'success' }
          ]}
          value={metricsSecured ? 'secured' : 'public'}
          onChange={handleToggleAuth}
          disabled={isToggling}
          loading={isToggling}
          title={metricsSecured
            ? 'Endpoints require API key authentication'
            : 'Endpoints are publicly accessible'}
        />
      </div>

      <p className="text-themed-muted text-sm mb-4">
        {metricsSecured
          ? 'These endpoints require API key authentication. Configure your API key in Grafana or Prometheus.'
          : 'These endpoints are publicly accessible. Use them directly in Grafana or Prometheus.'}
      </p>

      <div className="p-4 rounded-lg bg-themed-tertiary">
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium text-themed-primary">Prometheus Metrics</span>
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
            {copiedEndpoint === 'prometheus' ? 'Copied!' : 'Copy'}
          </Button>
        </div>
        <code className="text-xs block break-all px-3 py-2 rounded-md mb-2 font-mono bg-themed-secondary text-themed-secondary">
          {apiBaseUrl}/metrics
        </code>
        <p className="text-xs text-themed-muted">
          OpenMetrics/Prometheus format for scraping
        </p>
      </div>

      {/* Data Refresh Rate - Controls how often the app updates metrics */}
      <div className="mt-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <RefreshCw className="w-4 h-4 text-themed-muted" />
            <div>
              <span className="text-sm font-medium text-themed-primary">Data Refresh Rate</span>
              <p className="text-xs text-themed-muted">How often metrics are recalculated</p>
            </div>
          </div>
          <EnhancedDropdown
            options={dataRefreshOptions}
            value={dataRefreshRate}
            onChange={handleDataRefreshChange}
            placeholder="Select rate"
            compactMode={true}
            dropdownWidth="w-56"
            alignRight={true}
            dropdownTitle="Data Refresh Rate"
            footerNote="Controls internal metrics update frequency"
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
              <span className="text-sm font-medium text-themed-primary">Prometheus Configuration</span>
              <p className="text-xs text-themed-muted">
                {metricsSecured ? 'Authentication required - include authorization header' : 'Public access - no authentication needed'}
              </p>
            </div>
          </div>
          <EnhancedDropdown
            options={scrapeIntervalOptions}
            value={scrapeInterval}
            onChange={handleScrapeIntervalChange}
            placeholder="Select interval"
            compactMode={true}
            dropdownWidth="w-56"
            alignRight={true}
            dropdownTitle="Scrape Interval"
            footerNote="Adjust scrape_interval in config"
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
              Replace 'your-api-key-here' with the API key from your lancache-manager instance
            </p>
          )}
          <p className="text-xs text-themed-muted flex items-center gap-1.5">
            <Lightbulb className="w-3 h-3 icon-info" />
            Port 80 is the internal container port. External access uses your mapped port (e.g., 8080:80)
          </p>
        </div>

        {parseInt(scrapeInterval) < parseInt(dataRefreshRate) && (
          <p className="text-xs mt-3 flex items-center gap-1.5 text-themed-warning">
            <Lightbulb className="w-3 h-3 icon-warning" />
            Scrape interval is faster than data refresh - you may see stale data
          </p>
        )}
      </div>

      {/* Grafana Query Examples */}
      <div className="mt-4 p-3 rounded-lg border bg-themed-tertiary border-themed-secondary">
        <p className="text-sm font-medium text-themed-primary mb-2">Grafana Query Examples</p>
        <div className="space-y-2">
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Cache hit rate percentage</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_hit_ratio * 100</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Bandwidth saved (24h)</p>
            <code className="text-[10px] font-mono text-themed-secondary">increase(lancache_cache_hit_bytes_total[24h])</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Cache usage in GB</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_used_bytes / 1024 / 1024 / 1024</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Peak usage hour (0-23)</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_peak_hour</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Downloads by hour heatmap</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_hourly_downloads</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Cache growth rate (GB/day)</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_growth_daily_bytes / 1024 / 1024 / 1024</code>
          </div>
          <div className="bg-themed-secondary p-2 rounded">
            <p className="text-[10px] text-themed-muted mb-1"># Days until cache full</p>
            <code className="text-[10px] font-mono text-themed-secondary">lancache_cache_days_until_full</code>
          </div>
        </div>
      </div>
    </Card>
  );
};

export default GrafanaEndpoints;
