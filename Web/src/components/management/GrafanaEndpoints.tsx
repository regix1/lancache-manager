import React, { useState, useEffect } from 'react';
import { Link, Copy, CheckCircle, Lock, Unlock } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';

const GrafanaEndpoints: React.FC = () => {
  const [copiedEndpoint, setCopiedEndpoint] = useState<string | null>(null);
  const [metricsSecured, setMetricsSecured] = useState<boolean | null>(null);

  useEffect(() => {
    // Check metrics security status
    fetch('/api/metrics/status')
      .then(res => res.json())
      .then(data => setMetricsSecured(data.requiresAuthentication))
      .catch(() => setMetricsSecured(false));
  }, []);

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
        <div className="flex items-center space-x-2">
          <Link className="w-5 h-5 text-themed-accent" />
          <h3 className="text-lg font-semibold text-themed-primary">Live API Endpoints for Grafana</h3>
        </div>
        {metricsSecured !== null && (
          <div className={`flex items-center space-x-1 px-3 py-1 rounded-full text-xs font-medium border ${
            metricsSecured
              ? 'access-indicator-secured'
              : 'access-indicator-public'
          }`}>
            {metricsSecured ? (
              <>
                <Lock className="w-3 h-3" />
                <span>API Key Required</span>
              </>
            ) : (
              <>
                <Unlock className="w-3 h-3" />
                <span>Public Access</span>
              </>
            )}
          </div>
        )}
      </div>

      <p className="text-themed-muted text-sm mb-4">
        {metricsSecured
          ? 'These endpoints provide real-time metrics with API key authentication. Configure your API key in Grafana or Prometheus.'
          : 'These endpoints provide real-time metrics without authentication. Use them directly in Grafana or Prometheus.'}
      </p>

      <div className="space-y-3">
        <div className="p-3 rounded-lg themed-card border">
          <div className="flex items-center justify-between mb-1">
            <span className="text-sm font-medium text-themed-primary">Prometheus Metrics</span>
            <Button
              size="xs"
              variant="default"
              onClick={() => copyToClipboard(`${apiBaseUrl}/metrics`, 'prometheus')}
              leftSection={copiedEndpoint === 'prometheus' ? <CheckCircle className="w-3 h-3" /> : <Copy className="w-3 h-3" />}
            >
              {copiedEndpoint === 'prometheus' ? 'Copied!' : 'Copy'}
            </Button>
          </div>
          <code className="text-xs text-themed-muted block break-all">{apiBaseUrl}/metrics</code>
          <p className="text-xs text-themed-muted mt-1">OpenMetrics/Prometheus format for scraping</p>
        </div>
      </div>

      <div className="mt-4">
        <Alert color={metricsSecured ? "yellow" : "blue"}>
          <div className="space-y-2">
            <p className="text-sm">
              <strong>Security:</strong> {metricsSecured
                ? "Metrics endpoint requires API key authentication."
                : "Metrics endpoint is public (no authentication required)."}
            </p>
            {metricsSecured && (
              <div className="text-sm">
                <p className="font-medium mb-1">Configure Prometheus with API key:</p>
                <div className="bg-themed-tertiary p-2 rounded font-mono text-xs mt-2">
                  <div>scrape_configs:</div>
                  <div className="ml-2">- job_name: 'lancache-manager'</div>
                  <div className="ml-4">metrics_path: '/metrics'</div>
                  <div className="ml-4">headers:</div>
                  <div className="ml-6">X-Api-Key: your-api-key-here</div>
                </div>
                <p className="mt-2 text-xs opacity-75">
                  To make metrics public, set <code>Security__RequireAuthForMetrics=false</code> in docker-compose.yml
                </p>
              </div>
            )}
            {!metricsSecured && (
              <p className="text-xs opacity-75">
                To require API key, set <code>Security__RequireAuthForMetrics=true</code> in docker-compose.yml
              </p>
            )}
          </div>
        </Alert>
      </div>

      <div className="mt-2">
        <Alert color="green">
          <div className="space-y-2">
            <p className="text-sm">
              <strong>Live Updates:</strong> Configure Grafana to poll every 10-30 seconds for real-time monitoring.
              Works with both Prometheus and JSON datasource plugins.
            </p>
            <p className="text-xs opacity-75">
              All dashboard stat card metrics are available: cache capacity, usage ratio, hit/miss bytes,
              hit ratio, active downloads, active clients, and per-service counters.
            </p>
          </div>
        </Alert>
      </div>
    </Card>
  );
};

export default GrafanaEndpoints;