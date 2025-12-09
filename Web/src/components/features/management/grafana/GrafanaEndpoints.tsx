import React, { useState, use } from 'react';
import { Link, Copy, CheckCircle, Lock, Unlock } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';

// Fetch metrics security status
const fetchMetricsStatus = async (): Promise<boolean> => {
  try {
    const res = await fetch('/api/metrics/status');
    const data = await res.json();
    return data.requiresAuthentication;
  } catch {
    return false;
  }
};

// Cache the promise to avoid refetching on every render
let metricsStatusPromise: Promise<boolean> | null = null;

const getMetricsStatusPromise = () => {
  if (!metricsStatusPromise) {
    metricsStatusPromise = fetchMetricsStatus();
  }
  return metricsStatusPromise;
};

const GrafanaEndpoints: React.FC = () => {
  const [copiedEndpoint, setCopiedEndpoint] = useState<string | null>(null);
  const metricsSecured = use(getMetricsStatusPromise());

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
          ? 'These endpoints provide real-time metrics with API key authentication. Configure your API key in Grafana or Prometheus.'
          : 'These endpoints provide real-time metrics without authentication. Use them directly in Grafana or Prometheus.'}
      </p>

      <div className="space-y-3">
        <div className="p-3 rounded-lg border" style={{ backgroundColor: 'var(--theme-card-bg)', borderColor: 'var(--theme-card-border)' }}>
          <div className="flex items-center justify-between mb-1">
            <span className="text-sm font-medium text-themed-primary">Prometheus Metrics</span>
            <Button
              size="xs"
              variant="default"
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
          <code className="text-xs text-themed-muted block break-all">{apiBaseUrl}/metrics</code>
          <p className="text-xs text-themed-muted mt-1">
            OpenMetrics/Prometheus format for scraping
          </p>
        </div>
      </div>

      {/* Security Configuration Info */}
      {metricsSecured && (
        <div className="mt-4 p-3 rounded-lg border" style={{ backgroundColor: 'var(--theme-bg-tertiary)', borderColor: 'var(--theme-border-secondary)' }}>
          <p className="text-xs font-medium text-themed-primary mb-2">Prometheus Configuration</p>
          <div className="bg-themed-secondary p-2 rounded font-mono text-[10px] text-themed-muted">
            <div>scrape_configs:</div>
            <div className="ml-2">- job_name: 'lancache-manager'</div>
            <div className="ml-4">metrics_path: '/metrics'</div>
            <div className="ml-4">headers:</div>
            <div className="ml-6">X-Api-Key: your-api-key-here</div>
          </div>
        </div>
      )}
    </Card>
  );
};

export default GrafanaEndpoints;
