import React, { memo, useMemo } from 'react';
import { formatBytes, formatPercent } from '../../utils/formatters';

// Helper to format time with relative date
const formatTimeWithDate = (dateString) => {
  if (!dateString) return 'Unknown';
  
  try {
    const date = new Date(dateString);
    const now = new Date();
    
    // Check if invalid date
    if (isNaN(date.getTime())) {
      return 'Invalid time';
    }
    
    // Calculate difference in days
    const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const startOfDate = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    const daysDiff = Math.floor((startOfToday - startOfDate) / (1000 * 60 * 60 * 24));
    
    const timeString = date.toLocaleTimeString();
    
    if (daysDiff === 0) {
      // Today - just show time
      return timeString;
    } else if (daysDiff === 1) {
      // Yesterday
      return `Yesterday, ${timeString}`;
    } else if (daysDiff > 1 && daysDiff <= 7) {
      // Within a week - show day name
      const dayName = date.toLocaleDateString('en-US', { weekday: 'short' });
      return `${dayName}, ${timeString}`;
    } else {
      // Older - show date
      const dateStr = date.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
      return `${dateStr}, ${timeString}`;
    }
  } catch (error) {
    console.error('Error formatting time:', dateString, error);
    return 'Invalid time';
  }
};

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

  // Sort downloads by startTime (most recent first) and then slice
  const displayDownloads = useMemo(() => {
    const sorted = [...downloads].sort((a, b) => 
      new Date(b.startTime) - new Date(a.startTime)
    );
    return sorted.slice(0, displayCount);
  }, [downloads, displayCount]);

  // Calculate totals for the header
  const stats = useMemo(() => {
    const totalDownloads = downloads.length;
    const totalBytes = downloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
    const totalCacheHits = downloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
    const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;
    
    return { totalDownloads, totalBytes, totalCacheHits, overallHitRate };
  }, [downloads]);

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700 h-full flex flex-col">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-white">Recent Downloads</h3>
        <div className="flex items-center gap-3">
          {downloads.length > 0 && (
            <>
              <span className="text-xs text-gray-400 smooth-number">
                {stats.totalDownloads} total
              </span>
              <span className={`text-xs px-2 py-0.5 rounded transition-colors animated-badge ${
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
      
      {/* Downloads list container - flex-1 to fill remaining space */}
      <div className="flex-1 flex flex-col min-h-0">
        {displayDownloads.length > 0 ? (
          <>
            <div className="space-y-3 overflow-y-auto flex-1 pr-1">
              {displayDownloads.map((download, idx) => (
                <div 
                  key={download.id || idx} 
                  className="bg-gray-900 rounded-lg p-3 border border-gray-700 hover:border-gray-600 transition-all duration-200 download-card"
                  style={{ animationDelay: `${idx * 0.05}s` }}
                >
                  <div className="flex justify-between items-start mb-1">
                    <span className="text-sm font-medium text-blue-400">{download.service}</span>
                    <span className="text-xs text-gray-500">
                      {formatTimeWithDate(download.startTime)}
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
                      <span className="text-sm text-white transition-all duration-300 smooth-number">{formatBytes(download.totalBytes)}</span>
                      <div className="flex gap-2 text-xs">
                        <span className="text-green-400 smooth-number">↓ {formatBytes(download.cacheHitBytes)}</span>
                        <span className="text-yellow-400 smooth-number">→ {formatBytes(download.cacheMissBytes)}</span>
                      </div>
                    </div>
                    <span className={`text-xs px-2 py-1 rounded transition-colors animated-badge ${
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
              ))}
            </div>
            
            {downloads.length > displayCount && (
              <div className="mt-3 pt-3 border-t border-gray-700 text-center">
                <span className="text-xs text-gray-500 smooth-number">
                  Showing {displayCount} of {downloads.length} downloads
                </span>
              </div>
            )}
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center text-gray-500">
            {timeRange === '15m' || timeRange === '30m' || timeRange === '1h'
              ? `No downloads in the ${getTimeRangeLabel.toLowerCase()}`
              : 'No downloads yet'}
          </div>
        )}
      </div>
    </div>
  );
}, (prevProps, nextProps) => {
  // Only re-render if downloads actually changed
  return prevProps.downloads === nextProps.downloads && 
         prevProps.timeRange === nextProps.timeRange;
});

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default RecentDownloadsPanel;