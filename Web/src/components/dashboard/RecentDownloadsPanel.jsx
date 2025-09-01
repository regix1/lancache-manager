import React, { memo, useMemo } from 'react';
import { formatBytes, formatPercent, formatTime } from '../../utils/formatters';

const RecentDownloadsPanel = memo(({ downloads = [], timeRange = '24h' }) => {
  // Get time range label for the panel
  const getTimeRangeLabel = useMemo(() => {
    const labels = {
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

  // Determine how many downloads to show based on time range
  const displayCount = useMemo(() => {
    switch(timeRange) {
      case '15m':
      case '30m':
        return 5;
      case '1h':
      case '6h':
      case '12h':
      case '24h':
        return 8;
      case '7d':
      case '30d':
      case '90d':
      case 'all':
        return 10;
      default:
        return 8;
    }
  }, [timeRange]);

  const displayDownloads = useMemo(() => downloads.slice(0, displayCount), [downloads, displayCount]);

  // Calculate totals for the header
  const stats = useMemo(() => {
    const totalDownloads = downloads.length;
    const totalBytes = downloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
    const totalCacheHits = downloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
    const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;
    
    return { totalDownloads, totalBytes, totalCacheHits, overallHitRate };
  }, [downloads]);

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-white">Recent Downloads</h3>
        <div className="flex items-center gap-3">
          {downloads.length > 0 && (
            <>
              <span className="text-xs text-gray-400">
                {stats.totalDownloads} total
              </span>
              <span className={`text-xs px-2 py-0.5 rounded transition-colors ${
                stats.overallHitRate > 50 
                  ? 'bg-green-900/50 text-green-400' 
                  : 'bg-yellow-900/50 text-yellow-400'
              }`}>
                {formatPercent(stats.overallHitRate)} hit
              </span>
            </>
          )}
          <span className="text-xs text-gray-500">{getTimeRangeLabel}</span>
        </div>
      </div>
      
      <div className="space-y-3 max-h-[400px] overflow-y-auto">
        {displayDownloads.length > 0 ? (
          displayDownloads.map((download, idx) => (
            <div 
              key={download.id || idx} 
              className="bg-gray-900 rounded-lg p-3 border border-gray-700 hover:border-gray-600 transition-all duration-200"
            >
              <div className="flex justify-between items-start mb-1">
                <span className="text-sm font-medium text-blue-400">{download.service}</span>
                <span className="text-xs text-gray-500">
                  {formatTime(download.startTime)}
                </span>
              </div>
              <div className="text-xs text-gray-400">{download.clientIp}</div>
              {download.gameName && (
                <div className="text-xs text-gray-500 mt-1 truncate" title={download.gameName}>
                  {download.gameName}
                </div>
              )}
              <div className="flex justify-between items-center mt-2">
                <div className="flex items-center gap-3">
                  <span className="text-sm text-white transition-all duration-300">{formatBytes(download.totalBytes)}</span>
                  <div className="flex gap-2 text-xs">
                    <span className="text-green-400">↓ {formatBytes(download.cacheHitBytes)}</span>
                    <span className="text-yellow-400">→ {formatBytes(download.cacheMissBytes)}</span>
                  </div>
                </div>
                <span className={`text-xs px-2 py-1 rounded transition-colors ${
                  download.cacheHitPercent > 75 
                    ? 'bg-green-900 text-green-300' 
                    : download.cacheHitPercent > 50
                    ? 'bg-blue-900 text-blue-300'
                    : download.cacheHitPercent > 25
                    ? 'bg-yellow-900 text-yellow-300'
                    : 'bg-orange-900 text-orange-300'
                }`}>
                  {formatPercent(download.cacheHitPercent)} Hit
                </span>
              </div>
            </div>
          ))
        ) : (
          <div className="flex items-center justify-center h-[300px] text-gray-500">
            {timeRange === '15m' || timeRange === '30m' || timeRange === '1h'
              ? `No downloads in the ${getTimeRangeLabel.toLowerCase()}`
              : 'No downloads yet'}
          </div>
        )}
      </div>
      
      {downloads.length > displayCount && (
        <div className="mt-3 pt-3 border-t border-gray-700 text-center">
          <span className="text-xs text-gray-500">
            Showing {displayCount} of {downloads.length} downloads
          </span>
        </div>
      )}
    </div>
  );
}, (prevProps, nextProps) => {
  // Only re-render if downloads actually changed
  return prevProps.downloads === nextProps.downloads && 
         prevProps.timeRange === nextProps.timeRange;
});

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default RecentDownloadsPanel;