import React, { memo, useMemo } from 'react';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { Card } from '../ui/Card';

interface RecentDownloadsPanelProps {
  downloads?: any[];
  timeRange?: string;
}

const RecentDownloadsPanel: React.FC<RecentDownloadsPanelProps> = memo(({ 
  downloads = [], 
  timeRange = '24h' 
}) => {
  const getTimeRangeLabel = useMemo(() => {
    const labels: Record<string, string> = {
      '15m': 'Last 15 Minutes',
      '30m': 'Last 30 Minutes', 
      '1h': 'Last Hour',
      '6h': 'Last 6 Hours',
      '12h': 'Last 12 Hours',
      '24h': 'Last 24 Hours',
      '7d': 'Last 7 Days',
      '30d': 'Last 30 Days',
      '90d': 'Last 90 Days',
      'all': 'All Time'
    };
    return labels[timeRange] || 'Recent';
  }, [timeRange]);

  const displayCount = 8;
  const displayDownloads = useMemo(() => {
    return [...downloads].sort((a, b) => 
      new Date(b.startTime).getTime() - new Date(a.startTime).getTime()
    ).slice(0, displayCount);
  }, [downloads]);

  const stats = useMemo(() => {
    const totalDownloads = downloads.length;
    const totalBytes = downloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
    const totalCacheHits = downloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
    const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;
    
    return { totalDownloads, totalBytes, totalCacheHits, overallHitRate };
  }, [downloads]);

  return (
    <Card>
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-themed-primary">Recent Downloads</h3>
        <div className="flex items-center gap-3">
          {downloads.length > 0 && (
            <>
              <span className="text-xs text-themed-muted">
                {stats.totalDownloads} total
              </span>
              <span className={`text-xs px-2 py-0.5 rounded ${
                stats.overallHitRate > 50 
                  ? 'hit-rate-high' 
                  : 'hit-rate-warning'
              }`}>
                {formatPercent(stats.overallHitRate)} hit
              </span>
            </>
          )}
          <span className="text-xs text-themed-muted">{getTimeRangeLabel}</span>
        </div>
      </div>
      
      <div className="space-y-3 max-h-[400px] overflow-y-auto">
        {displayDownloads.length > 0 ? (
          displayDownloads.map((download, idx) => (
            <div 
              key={download.id || idx} 
              className="rounded-lg p-3 border transition-all duration-200 themed-card hover:shadow-lg"
              style={{
                backgroundColor: 'var(--theme-bg-primary)',
                borderColor: 'var(--theme-border-primary)'
              }}
              onMouseEnter={(e) => e.currentTarget.style.borderColor = 'var(--theme-border-secondary)'}
              onMouseLeave={(e) => e.currentTarget.style.borderColor = 'var(--theme-border-primary)'}
            >
              <div className="flex justify-between items-start mb-1">
                <span className="font-medium text-sm text-themed-accent">
                  {download.service}
                </span>
                <span className="text-xs text-themed-muted">
                  {formatDateTime(download.startTime)}
                </span>
              </div>
              <div className="text-xs text-themed-muted">{download.clientIp}</div>
              {download.gameName && (
                <div className="text-xs text-themed-muted mt-1 truncate">
                  {download.gameName}
                </div>
              )}
              <div className="flex justify-between items-center mt-2">
                <div className="flex items-center gap-3">
                  <span className="text-themed-primary text-sm">
                    {formatBytes(download.totalBytes)}
                  </span>
                  <div className="flex gap-2 text-xs">
                    <span className="cache-hit">↓ {formatBytes(download.cacheHitBytes)}</span>
                    <span className="cache-miss">↑ {formatBytes(download.cacheMissBytes)}</span>
                  </div>
                </div>
                <span className={`text-xs px-2 py-1 rounded hit-rate-badge ${
                  download.cacheHitPercent > 75 
                    ? 'high' 
                    : download.cacheHitPercent > 50
                    ? 'medium'
                    : download.cacheHitPercent > 25
                    ? 'low'
                    : 'warning'
                }`}>
                  {formatPercent(download.cacheHitPercent)} Hit
                </span>
              </div>
            </div>
          ))
        ) : (
          <div className="flex items-center justify-center h-32 text-themed-muted">
            No downloads in the {getTimeRangeLabel.toLowerCase()}
          </div>
        )}
      </div>
      
      {downloads.length > displayCount && (
        <div className="mt-3 pt-3 border-t text-center" style={{ borderColor: 'var(--theme-border-primary)' }}>
          <span className="text-xs text-themed-muted">
            Showing {displayCount} of {downloads.length} downloads
          </span>
        </div>
      )}
    </Card>
  );
});

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default RecentDownloadsPanel;