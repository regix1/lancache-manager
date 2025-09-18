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

  const copyToClipboard = (text: string, endpoint: string) => {
    navigator.clipboard.writeText(text);
    setCopiedEndpoint(endpoint);
    setTimeout(() => setCopiedEndpoint(null), 2000);
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
          <div className={`flex items-center space-x-1 px-3 py-1 rounded-full text-xs font-medium ${
            metricsSecured
              ? 'access-indicator-secured'
              : 'access-indicator-public'
          }`}
          style={{
            border: '1px solid var(--theme-border)'
          }}>
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
        <div className="p-3 rounded-lg themed-card" style={{ border: '1px solid var(--theme-border)' }}>
          <div className="flex items-center justify-between mb-1">
            <span className="text-sm font-medium text-themed-primary">Prometheus Metrics</span>
            <Button
              size="xs"
              variant="default"
              onClick={() => copyToClipboard(`${apiBaseUrl}/api/metrics`, 'prometheus')}
              leftSection={copiedEndpoint === 'prometheus' ? <CheckCircle className="w-3 h-3" /> : <Copy className="w-3 h-3" />}
            >
              {copiedEndpoint === 'prometheus' ? 'Copied!' : 'Copy'}
            </Button>
          </div>
          <code className="text-xs text-themed-muted block break-all">{apiBaseUrl}/api/metrics</code>
          <p className="text-xs text-themed-muted mt-1">OpenMetrics format for Prometheus scraping</p>
        </div>

        <div className="p-3 rounded-lg themed-card" style={{ border: '1px solid var(--theme-border)' }}>
          <div className="flex items-center justify-between mb-1">
            <span className="text-sm font-medium text-themed-primary">JSON Metrics</span>
            <Button
              size="xs"
              variant="default"
              onClick={() => copyToClipboard(`${apiBaseUrl}/api/metrics/json`, 'json-api')}
              leftSection={copiedEndpoint === 'json-api' ? <CheckCircle className="w-3 h-3" /> : <Copy className="w-3 h-3" />}
            >
              {copiedEndpoint === 'json-api' ? 'Copied!' : 'Copy'}
            </Button>
          </div>
          <code className="text-xs text-themed-muted block break-all">{apiBaseUrl}/api/metrics/json</code>
          <p className="text-xs text-themed-muted mt-1">JSON format for direct Grafana integration</p>
        </div>
      </div>

      <div className="mt-4">
        <Alert color="blue">
          <p className="text-sm">
            <strong>Security Options:</strong> By default, these endpoints are public.
            To require API key authentication, set <code>RequireAuthForMetrics: true</code> in your config.
            Then add header <code>X-Api-Key: your-key</code> to Grafana/Prometheus.
          </p>
        </Alert>
      </div>

      <div className="mt-2">
        <Alert color="green">
          <p className="text-sm">
            <strong>Live Updates:</strong> Configure Grafana to poll every 10-30 seconds for real-time monitoring.
            Works with both Prometheus and JSON datasource plugins.
          </p>
        </Alert>
      </div>
    </Card>
  );
};

export default GrafanaEndpoints;