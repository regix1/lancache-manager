import React, { useState, use } from 'react';
import { Link, Copy, CheckCircle, Lock, Unlock, Timer, Lightbulb } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';

interface MetricsStatus {
  requiresAuthentication: boolean;
  interval: number;
}

// Fetch metrics status and interval
const fetchMetricsStatus = async (): Promise<MetricsStatus> => {
  try {
    const [statusRes, intervalRes] = await Promise.all([
      fetch('/api/metrics/status'),
      fetch('/api/metrics/interval')
    ]);
    const statusData = await statusRes.json();
    const intervalData = await intervalRes.json();
    return {
      requiresAuthentication: statusData.requiresAuthentication,
      interval: intervalData.interval
    };
  } catch {
    return { requiresAuthentication: false, interval: 15 };
  }
};

// Cache the promise to avoid refetching on every render
let metricsStatusPromise: Promise<MetricsStatus> | null = null;

const getMetricsStatusPromise = () => {
  if (!metricsStatusPromise) {
    metricsStatusPromise = fetchMetricsStatus();
  }
  return metricsStatusPromise;
};

const scrapeIntervalOptions = [
  { value: '5', label: '5 seconds', shortLabel: '5s', description: 'Very frequent updates', rightLabel: '5s', icon: Timer },
  { value: '10', label: '10 seconds', shortLabel: '10s', description: 'Recommended for real-time', rightLabel: '10s', icon: Timer },
  { value: '15', label: '15 seconds', shortLabel: '15s', description: 'Balanced performance', rightLabel: '15s', icon: Timer },
  { value: '30', label: '30 seconds', shortLabel: '30s', description: 'Lower resource usage', rightLabel: '30s', icon: Timer },
  { value: '60', label: '60 seconds', shortLabel: '60s', description: 'Minimal overhead', rightLabel: '60s', icon: Timer },
];

const GrafanaEndpoints: React.FC = () => {
  const [copiedEndpoint, setCopiedEndpoint] = useState<string | null>(null);
  const metricsStatus = use(getMetricsStatusPromise());
  const [scrapeInterval, setScrapeInterval] = useState<string>(String(metricsStatus.interval));
  const metricsSecured = metricsStatus.requiresAuthentication;

  const handleIntervalChange = async (value: string) => {
    setScrapeInterval(value);
    try {
      await fetch('/api/metrics/interval', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ interval: parseInt(value, 10) })
      });
    } catch (error) {
      console.error('Failed to update metrics interval:', error);
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
              Security can be toggled in docker-compose environment settings.
            </HelpNote>
          </HelpPopover>
        </div>
        <div
          className={`flex items-center gap-1.5 sm:gap-2 px-2 sm:px-3 py-1 rounded-full text-xs font-medium border ${
            metricsSecured ? 'access-indicator-secured' : 'access-indicator-public'
          }`}
        >
          {metricsSecured ? (
            <>
              <Lock className="w-3.5 h-3.5 sm:w-4 sm:h-4 flex-shrink-0" />
              <span className="whitespace-nowrap">API Key Required</span>
            </>
          ) : (
            <>
              <Unlock className="w-3.5 h-3.5 sm:w-4 sm:h-4 flex-shrink-0" />
              <span className="whitespace-nowrap">Public Access</span>
            </>
          )}
        </div>
      </div>

      <p className="text-themed-muted text-sm mb-4">
        {metricsSecured
          ? 'These endpoints require API key authentication. Configure your API key in Grafana or Prometheus.'
          : 'These endpoints are publicly accessible. Use them directly in Grafana or Prometheus.'}
      </p>

      <div
        className="p-4 rounded-lg"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
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
        <code
          className="text-xs block break-all px-3 py-2 rounded-md mb-2 font-mono"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            color: 'var(--theme-text-secondary)'
          }}
        >
          {apiBaseUrl}/metrics
        </code>
        <p className="text-xs text-themed-muted">
          OpenMetrics/Prometheus format for scraping
        </p>
      </div>

      {/* Scrape Interval Selector */}
      <div className="mt-4 flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Timer className="w-4 h-4 text-themed-muted" />
          <span className="text-sm text-themed-secondary">Scrape Interval</span>
        </div>
        <EnhancedDropdown
          options={scrapeIntervalOptions}
          value={scrapeInterval}
          onChange={handleIntervalChange}
          placeholder="Select interval"
          compactMode={true}
          dropdownWidth="w-56"
          alignRight={true}
          dropdownTitle="Scrape Interval"
          footerNote="How often Prometheus fetches metrics"
          footerIcon={Lightbulb}
          cleanStyle={true}
        />
      </div>

      {/* Prometheus Configuration */}
      <div className="mt-4 p-3 rounded-lg border" style={{ backgroundColor: 'var(--theme-bg-tertiary)', borderColor: 'var(--theme-border-secondary)' }}>
        <p className="text-xs font-medium text-themed-primary mb-2">Prometheus Configuration</p>
        <div className="bg-themed-secondary p-2 rounded font-mono text-[10px] text-themed-muted">
          <div>scrape_configs:</div>
          <div className="ml-2">- job_name: 'lancache-manager'</div>
          <div className="ml-4">scrape_interval: {scrapeInterval}s</div>
          <div className="ml-4">metrics_path: '/metrics'</div>
          {metricsSecured && (
            <>
              <div className="ml-4">headers:</div>
              <div className="ml-6">X-Api-Key: your-api-key-here</div>
            </>
          )}
        </div>
      </div>
    </Card>
  );
};

export default GrafanaEndpoints;
