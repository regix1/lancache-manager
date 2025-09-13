import React, { useState } from 'react';
import { Download, Activity, Database, Clock, TrendingUp } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import ApiService from '@services/api.service';
import type { ClientStat, ServiceStat, CacheInfo, DashboardStats } from '../../types';

interface DataExportManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

type ExportFormat = 'json' | 'csv' | 'prometheus' | 'influxdb';
type DataType = 'clients' | 'services' | 'cache' | 'dashboard' | 'downloads';

interface ExportOption {
  type: DataType;
  label: string;
  description: string;
  formats: ExportFormat[];
  icon: React.ComponentType<any>;
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
  isAuthenticated,
  mockMode,
  onError,
  onSuccess
}) => {
  const [loading, setLoading] = useState(false);
  const [selectedType, setSelectedType] = useState<DataType | null>(null);
  const [selectedFormat, setSelectedFormat] = useState<ExportFormat>('json');

  const convertToCSV = (data: any[]): string => {
    if (!data || data.length === 0) return '';

    const headers = Object.keys(data[0]);
    const csvHeaders = headers.join(',');
    
    const csvRows = data.map(row => {
      return headers.map(header => {
        const value = row[header];
        if (value === null || value === undefined) return '';
        if (typeof value === 'string' && value.includes(',')) {
          return `"${value.replace(/"/g, '""')}"`;
        }
        return value;
      }).join(',');
    });

    return [csvHeaders, ...csvRows].join('\n');
  };

  const convertToPrometheus = (data: any, type: DataType): string => {
    const timestamp = Date.now();
    let metrics: string[] = [];

    switch (type) {
      case 'clients':
        const clients = data as ClientStat[];
        clients.forEach(client => {
          metrics.push(`# HELP lancache_client_cache_hit_bytes Total cache hit bytes for client`);
          metrics.push(`# TYPE lancache_client_cache_hit_bytes counter`);
          metrics.push(`lancache_client_cache_hit_bytes{client="${client.clientIp}"} ${client.totalCacheHitBytes} ${timestamp}`);
          metrics.push(`lancache_client_cache_miss_bytes{client="${client.clientIp}"} ${client.totalCacheMissBytes} ${timestamp}`);
          metrics.push(`lancache_client_download_count{client="${client.clientIp}"} ${client.totalDownloads} ${timestamp}`);
        });
        break;
      
      case 'services':
        const services = data as ServiceStat[];
        services.forEach(service => {
          metrics.push(`# HELP lancache_service_cache_hit_bytes Total cache hit bytes for service`);
          metrics.push(`# TYPE lancache_service_cache_hit_bytes counter`);
          metrics.push(`lancache_service_cache_hit_bytes{service="${service.service}"} ${service.totalCacheHitBytes} ${timestamp}`);
          metrics.push(`lancache_service_cache_miss_bytes{service="${service.service}"} ${service.totalCacheMissBytes} ${timestamp}`);
          metrics.push(`lancache_service_download_count{service="${service.service}"} ${service.totalDownloads} ${timestamp}`);
          metrics.push(`lancache_service_hit_ratio{service="${service.service}"} ${service.cacheHitPercent / 100} ${timestamp}`);
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

  const convertToInfluxDB = (data: any, type: DataType): string => {
    const timestamp = Date.now() * 1000000; // InfluxDB uses nanoseconds
    let lines: string[] = [];

    switch (type) {
      case 'services':
        const services = data as ServiceStat[];
        services.forEach(service => {
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

  const handleExport = async () => {
    if (!selectedType) {
      onError?.('Please select data to export');
      return;
    }

    if (!isAuthenticated && !mockMode) {
      onError?.('Authentication required for export');
      return;
    }

    setLoading(true);
    try {
      let data: any;
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
          data = await ApiService.getLatestDownloads(undefined, 1000);
          break;
      }

      // Convert data based on format
      switch (selectedFormat) {
        case 'csv':
          content = convertToCSV(Array.isArray(data) ? data : [data]);
          mimeType = 'text/csv';
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

      onSuccess?.(`Exported ${selectedType} data as ${selectedFormat.toUpperCase()}`);
    } catch (error: any) {
      onError?.(error.message || 'Failed to export data');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Card>
      <div className="flex items-center space-x-2 mb-4">
        <Download className="w-5 h-5 text-themed-accent" />
        <h3 className="text-lg font-semibold text-themed-primary">Data Export</h3>
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
            {exportOptions.map(option => {
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
                  className={`p-3 rounded-lg border-2 transition-all text-left ${
                    selectedType === option.type
                      ? 'border-themed-accent bg-themed-tertiary'
                      : 'border-themed-border hover:border-themed-muted'
                  }`}
                  disabled={mockMode && option.type !== 'cache'}
                >
                  <div className="flex items-start space-x-3">
                    <Icon className="w-5 h-5 text-themed-accent mt-0.5" />
                    <div className="flex-1">
                      <div className="font-medium text-themed-primary">{option.label}</div>
                      <div className="text-xs text-themed-muted mt-1">{option.description}</div>
                    </div>
                  </div>
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
                .find(o => o.type === selectedType)
                ?.formats.map(format => (
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
                  <strong>Prometheus Format:</strong> Metrics in OpenMetrics text format.
                  Can be scraped by Prometheus or imported into Grafana.
                </>
              )}
              {selectedFormat === 'influxdb' && (
                <>
                  <strong>InfluxDB Format:</strong> Line protocol format for direct import
                  into InfluxDB or Telegraf.
                </>
              )}
              {selectedFormat === 'csv' && (
                <>
                  <strong>CSV Format:</strong> Comma-separated values for spreadsheet
                  applications or data analysis tools.
                </>
              )}
              {selectedFormat === 'json' && (
                <>
                  <strong>JSON Format:</strong> Structured data for programmatic processing
                  or custom visualizations.
                </>
              )}
            </div>
          </Alert>
        )}

        <Button
          onClick={handleExport}
          disabled={!selectedType || loading || (!isAuthenticated && !mockMode)}
          loading={loading}
          variant="filled"
          color="green"
          leftSection={<Download className="w-4 h-4" />}
          fullWidth
        >
          Export Data
        </Button>

        {mockMode && (
          <Alert color="yellow">
            <p className="text-xs">
              Mock mode active - only cache info export available
            </p>
          </Alert>
        )}
      </div>
    </Card>
  );
};

export default DataExportManager;