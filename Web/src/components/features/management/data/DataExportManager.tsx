import React, { useState, useEffect } from 'react';
import {
  Download as DownloadIcon,
  Activity,
  Database,
  Clock,
  TrendingUp,
  Link,
  Copy,
  CheckCircle,
  Lock,
  Unlock
} from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import ApiService from '@services/api.service';
import { formatDateTime } from '@utils/formatters';
import type { ClientStat, ServiceStat, CacheInfo, DashboardStats, Download } from '../../../../types';

interface DataExportManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

type ExportFormat = 'json' | 'csv' | 'prometheus' | 'influxdb';
type DataType = 'clients' | 'services' | 'cache' | 'dashboard' | 'downloads';
type ExportData = ClientStat[] | ServiceStat[] | CacheInfo | DashboardStats | Download[];

/** Props interface for icon components */
interface IconComponentProps {
  className?: string;
  style?: React.CSSProperties;
}

interface ExportOption {
  type: DataType;
  label: string;
  description: string;
  formats: ExportFormat[];
  icon: React.ComponentType<IconComponentProps>;
}

const exportOptions: ExportOption[] = [
  {
    type: 'clients',
    label: 'Client Statistics',
    description: 'Export client usage data including bandwidth and cache hits',
    formats: ['json', 'csv', 'prometheus'],
    icon: Database
  },
  {
    type: 'services',
    label: 'Service Statistics',
    description: 'Export service-level metrics (Steam, Epic, etc.)',
    formats: ['json', 'csv', 'prometheus', 'influxdb'],
    icon: Activity
  },
  {
    type: 'cache',
    label: 'Cache Information',
    description: 'Export current cache usage and storage metrics',
    formats: ['json', 'prometheus'],
    icon: Database
  },
  {
    type: 'dashboard',
    label: 'Dashboard Statistics',
    description: 'Export aggregated dashboard metrics',
    formats: ['json', 'prometheus', 'influxdb'],
    icon: TrendingUp
  },
  {
    type: 'downloads',
    label: 'Download History',
    description: 'Export recent download history (last 1000 entries)',
    formats: ['json', 'csv'],
    icon: Clock
  }
];

const DataExportManager: React.FC<DataExportManagerProps> = ({
  isAuthenticated: _isAuthenticated,
  mockMode: _mockMode,
  onError,
  onSuccess
}) => {
  const [loading, setLoading] = useState(false);
  const [selectedType, setSelectedType] = useState<DataType | null>(null);
  const [selectedFormat, setSelectedFormat] = useState<ExportFormat>('json');
  const [loadingProgress, setLoadingProgress] = useState<Record<string, number>>({});
  const [copiedEndpoint, setCopiedEndpoint] = useState<string | null>(null);
  const [metricsSecured, setMetricsSecured] = useState<boolean | null>(null);

  useEffect(() => {
    // Check metrics security status
    fetch('/api/metrics/status')
      .then((res) => res.json())
      .then((data) => setMetricsSecured(data.requiresAuthentication))
      .catch(() => setMetricsSecured(false));
  }, []);

  const convertToCSV = (data: object[]): string => {
    if (!data || data.length === 0) return '';

    // UTF-8 BOM for proper special character encoding (™, ®, etc.)
    const BOM = '\uFEFF';

    const headers = Object.keys(data[0]);
    const csvHeaders = headers.join(',');

    // Helper to escape CSV values
    const escapeCSV = (value: unknown): string => {
      if (value === null || value === undefined) return '';
      const str = String(value);
      // Escape if contains comma, quote, or newline
      if (str.includes(',') || str.includes('"') || str.includes('\n') || str.includes('\r')) {
        return `"${str.replace(/"/g, '""')}"`;
      }
      return str;
    };

    // Helper to format value for CSV
    const formatValue = (header: string, value: unknown): string => {
      if (value === null || value === undefined) return '';

      // Format timestamps (UTC or Local variants)
      if (
        header === 'startTimeUtc' ||
        header === 'endTimeUtc' ||
        header === 'startTimeLocal' ||
        header === 'endTimeLocal' ||
        header.toLowerCase().includes('time') ||
        header.toLowerCase().includes('date')
      ) {
        // Check if it's a valid date string or Date object
        const dateValue = value instanceof Date ? value : typeof value === 'string' || typeof value === 'number' ? new Date(value) : null;
        if (dateValue && !isNaN(dateValue.getTime())) {
          return formatDateTime(value as string | Date | null);
        }
      }

      // Format booleans
      if (typeof value === 'boolean') {
        return value ? 'TRUE' : 'FALSE';
      }

      // Format numbers with precision
      if (typeof value === 'number') {
        // If it's a percentage field, format to 2 decimals
        if (header.toLowerCase().includes('percent')) {
          return value.toFixed(2);
        }
        return String(value);
      }

      return String(value);
    };

    const csvRows = data.map((row) => {
      const rowRecord = row as Record<string, unknown>;
      return headers
        .map((header) => {
          const formattedValue = formatValue(header, rowRecord[header]);
          return escapeCSV(formattedValue);
        })
        .join(',');
    });

    return BOM + [csvHeaders, ...csvRows].join('\n');
  };

  const convertToPrometheus = (data: ExportData, type: DataType): string => {
    const timestamp = Date.now();
    const metrics: string[] = [];

    switch (type) {
      case 'clients':
        const clients = data as ClientStat[];
        clients.forEach((client) => {
          metrics.push(`# HELP lancache_client_cache_hit_bytes Total cache hit bytes for client`);
          metrics.push(`# TYPE lancache_client_cache_hit_bytes counter`);
          metrics.push(
            `lancache_client_cache_hit_bytes{client="${client.clientIp}"} ${client.totalCacheHitBytes} ${timestamp}`
          );
          metrics.push(
            `lancache_client_cache_miss_bytes{client="${client.clientIp}"} ${client.totalCacheMissBytes} ${timestamp}`
          );
          metrics.push(
            `lancache_client_download_count{client="${client.clientIp}"} ${client.totalDownloads} ${timestamp}`
          );
        });
        break;

      case 'services':
        const services = data as ServiceStat[];
        services.forEach((service) => {
          metrics.push(`# HELP lancache_service_cache_hit_bytes Total cache hit bytes for service`);
          metrics.push(`# TYPE lancache_service_cache_hit_bytes counter`);
          metrics.push(
            `lancache_service_cache_hit_bytes{service="${service.service}"} ${service.totalCacheHitBytes} ${timestamp}`
          );
          metrics.push(
            `lancache_service_cache_miss_bytes{service="${service.service}"} ${service.totalCacheMissBytes} ${timestamp}`
          );
          metrics.push(
            `lancache_service_download_count{service="${service.service}"} ${service.totalDownloads} ${timestamp}`
          );
          metrics.push(
            `lancache_service_hit_ratio{service="${service.service}"} ${service.cacheHitPercent / 100} ${timestamp}`
          );
        });
        break;

      case 'cache':
        const cache = data as CacheInfo;
        metrics.push(`# HELP lancache_cache_total_bytes Total cache size in bytes`);
        metrics.push(`# TYPE lancache_cache_total_bytes gauge`);
        metrics.push(`lancache_cache_total_bytes ${cache.totalCacheSize} ${timestamp}`);
        metrics.push(`lancache_cache_used_bytes ${cache.usedCacheSize} ${timestamp}`);
        metrics.push(`lancache_cache_free_bytes ${cache.freeCacheSize} ${timestamp}`);
        metrics.push(`lancache_cache_usage_percent ${cache.usagePercent / 100} ${timestamp}`);
        metrics.push(`lancache_cache_file_count ${cache.totalFiles} ${timestamp}`);
        break;

      case 'dashboard':
        const stats = data as DashboardStats;
        metrics.push(`# HELP lancache_bandwidth_saved_bytes Total bandwidth saved`);
        metrics.push(`# TYPE lancache_bandwidth_saved_bytes counter`);
        metrics.push(`lancache_bandwidth_saved_bytes ${stats.totalBandwidthSaved} ${timestamp}`);
        metrics.push(`lancache_cache_added_bytes ${stats.totalAddedToCache} ${timestamp}`);
        metrics.push(`lancache_served_bytes ${stats.totalServed} ${timestamp}`);
        metrics.push(`lancache_hit_ratio ${stats.cacheHitRatio / 100} ${timestamp}`);
        metrics.push(`lancache_active_downloads ${stats.activeDownloads} ${timestamp}`);
        metrics.push(`lancache_unique_clients ${stats.uniqueClients} ${timestamp}`);
        break;
    }

    return metrics.join('\n');
  };

  const convertToInfluxDB = (data: ExportData, type: DataType): string => {
    const timestamp = Date.now() * 1000000; // InfluxDB uses nanoseconds
    const lines: string[] = [];

    switch (type) {
      case 'services':
        const services = data as ServiceStat[];
        services.forEach((service) => {
          lines.push(
            `lancache,service=${service.service} ` +
              `cache_hit_bytes=${service.totalCacheHitBytes}i,` +
              `cache_miss_bytes=${service.totalCacheMissBytes}i,` +
              `total_downloads=${service.totalDownloads}i,` +
              `hit_ratio=${service.cacheHitPercent / 100} ${timestamp}`
          );
        });
        break;

      case 'dashboard':
        const stats = data as DashboardStats;
        lines.push(
          `lancache_stats ` +
            `bandwidth_saved=${stats.totalBandwidthSaved}i,` +
            `cache_added=${stats.totalAddedToCache}i,` +
            `total_served=${stats.totalServed}i,` +
            `hit_ratio=${stats.cacheHitRatio / 100},` +
            `active_downloads=${stats.activeDownloads}i,` +
            `unique_clients=${stats.uniqueClients}i ${timestamp}`
        );
        break;
    }

    return lines.join('\n');
  };

  const simulateProgress = (type: DataType) => {
    let progress = 0;
    const interval = setInterval(() => {
      progress += Math.random() * 30;
      if (progress >= 95) {
        progress = 95;
        clearInterval(interval);
      }
      setLoadingProgress((prev) => ({ ...prev, [type]: progress }));
    }, 200);
    return interval;
  };

  const handleExport = async () => {
    if (!selectedType) {
      onError?.('Please select data to export');
      return;
    }

    setLoading(true);
    setLoadingProgress({ [selectedType]: 0 });
    const progressInterval = simulateProgress(selectedType);

    try {
      let data: ExportData;
      let filename: string;
      let mimeType: string;
      let content: string;

      // Fetch the data based on type
      switch (selectedType) {
        case 'clients':
          data = await ApiService.getClientStats();
          break;
        case 'services':
          data = await ApiService.getServiceStats();
          break;
        case 'cache':
          data = await ApiService.getCacheInfo();
          break;
        case 'dashboard':
          data = await ApiService.getDashboardStats();
          break;
        case 'downloads':
          data = await ApiService.getLatestDownloads(undefined, 'unlimited');
          break;
      }

      // Convert data based on format
      switch (selectedFormat) {
        case 'csv':
          content = convertToCSV(Array.isArray(data) ? data : [data]);
          mimeType = 'text/csv;charset=utf-8';
          filename = `lancache_${selectedType}_${new Date().toISOString().split('T')[0]}.csv`;
          break;

        case 'prometheus':
          content = convertToPrometheus(data, selectedType);
          mimeType = 'text/plain';
          filename = `lancache_${selectedType}_metrics.txt`;
          break;

        case 'influxdb':
          content = convertToInfluxDB(data, selectedType);
          mimeType = 'text/plain';
          filename = `lancache_${selectedType}_influx.txt`;
          break;

        case 'json':
        default:
          content = JSON.stringify(data, null, 2);
          mimeType = 'application/json';
          filename = `lancache_${selectedType}_${new Date().toISOString().split('T')[0]}.json`;
          break;
      }

      // Create download
      const blob = new Blob([content], { type: mimeType });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = filename;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);

      clearInterval(progressInterval);
      setLoadingProgress({ [selectedType]: 100 });
      setTimeout(() => setLoadingProgress({}), 500);

      onSuccess?.(`Exported ${selectedType} data as ${selectedFormat.toUpperCase()}`);
    } catch (error: unknown) {
      clearInterval(progressInterval);
      setLoadingProgress({});
      onError?.((error instanceof Error ? error.message : String(error)) || 'Failed to export data');
    } finally {
      setLoading(false);
    }
  };

  const copyToClipboard = (text: string, endpoint: string) => {
    navigator.clipboard.writeText(text);
    setCopiedEndpoint(endpoint);
    setTimeout(() => setCopiedEndpoint(null), 2000);
  };

  const apiBaseUrl = window.location.origin;

  return (
    <div className="space-y-4">
      {/* Live API Endpoints for Grafana */}
      <Card>
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center space-x-2">
            <Link className="w-5 h-5 text-themed-accent" />
            <h3 className="text-lg font-semibold text-themed-primary">
              Live API Endpoints for Grafana
            </h3>
          </div>
          {metricsSecured !== null && (
            <div
              className={`flex items-center space-x-1 px-3 py-1 rounded-full text-xs font-medium border ${
                metricsSecured ? 'access-indicator-secured' : 'access-indicator-public'
              }`}
            >
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

      </Card>

      {/* Data Export */}
      <Card>
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-green">
            <DownloadIcon className="w-5 h-5 icon-green" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Data Export</h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title="Export Formats">
              <div className="space-y-1.5">
                <HelpDefinition term="JSON" termColor="blue">
                  Structured data for programmatic processing
                </HelpDefinition>
                <HelpDefinition term="CSV" termColor="green">
                  Spreadsheet-compatible for Excel/analysis tools
                </HelpDefinition>
                <HelpDefinition term="Prometheus" termColor="purple">
                  OpenMetrics format for monitoring systems
                </HelpDefinition>
                <HelpDefinition term="InfluxDB" termColor="orange">
                  Line protocol for time-series databases
                </HelpDefinition>
              </div>
            </HelpSection>

            <HelpNote type="info">
              Download history includes the last 1000 entries.
            </HelpNote>
          </HelpPopover>
        </div>

        <p className="text-themed-muted text-sm mb-4">
          Export cache statistics in various formats for analysis and monitoring
        </p>

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              Select Data Type
            </label>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              {exportOptions.map((option) => {
                const Icon = option.icon;
                return (
                  <button
                    key={option.type}
                    onClick={() => {
                      setSelectedType(option.type);
                      // Reset format if not supported
                      if (!option.formats.includes(selectedFormat)) {
                        setSelectedFormat(option.formats[0]);
                      }
                    }}
                    className={`p-3 rounded-lg transition-all text-left relative overflow-hidden themed-card ${
                      selectedType === option.type ? 'bg-themed-tertiary' : 'hover:bg-themed-hover'
                    }`}
                    style={{
                      border:
                        selectedType === option.type
                          ? '2px solid var(--theme-primary)'
                          : '2px solid var(--theme-border)'
                    }}
                  >
                    <div className="flex items-start space-x-3 relative z-10">
                      <Icon className="w-5 h-5 text-themed-accent mt-0.5" />
                      <div className="flex-1">
                        <div className="font-medium text-themed-primary">{option.label}</div>
                        <div className="text-xs text-themed-muted mt-1">{option.description}</div>
                      </div>
                    </div>
                    {loadingProgress[option.type] !== undefined && (
                      <div className="absolute bottom-0 left-0 w-full h-1 bg-themed-secondary">
                        <div
                          className="progress-bar-medium h-full transition-all duration-300"
                          style={{ width: `${loadingProgress[option.type]}%` }}
                        />
                      </div>
                    )}
                  </button>
                );
              })}
            </div>
          </div>

          {selectedType && (
            <div>
              <label className="block text-sm font-medium text-themed-primary mb-2">
                Export Format
              </label>
              <div className="flex flex-wrap gap-2">
                {exportOptions
                  .find((o) => o.type === selectedType)
                  ?.formats.map((format) => (
                    <Button
                      key={format}
                      onClick={() => setSelectedFormat(format)}
                      variant={selectedFormat === format ? 'filled' : 'default'}
                      color="blue"
                      size="sm"
                    >
                      {format.toUpperCase()}
                    </Button>
                  ))}
              </div>
            </div>
          )}

          {selectedType && selectedFormat && (
            <Alert color="blue">
              <div className="text-sm">
                {selectedFormat === 'prometheus' && (
                  <>
                    <strong>Prometheus Format:</strong> Metrics in OpenMetrics text format. Can be
                    scraped by Prometheus or imported into Grafana.
                  </>
                )}
                {selectedFormat === 'influxdb' && (
                  <>
                    <strong>InfluxDB Format:</strong> Line protocol format for direct import into
                    InfluxDB or Telegraf.
                  </>
                )}
                {selectedFormat === 'csv' && (
                  <>
                    <strong>CSV Format:</strong> Comma-separated values for spreadsheet applications
                    or data analysis tools.
                  </>
                )}
                {selectedFormat === 'json' && (
                  <>
                    <strong>JSON Format:</strong> Structured data for programmatic processing or
                    custom visualizations.
                  </>
                )}
              </div>
            </Alert>
          )}

          <Button
            onClick={handleExport}
            disabled={!selectedType || loading}
            loading={loading}
            variant="filled"
            color="green"
            leftSection={<DownloadIcon className="w-4 h-4" />}
            fullWidth
          >
            Export Data
          </Button>
        </div>
      </Card>
    </div>
  );
};

export default DataExportManager;
