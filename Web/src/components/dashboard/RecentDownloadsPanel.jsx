import React, { useState } from 'react';
import { formatBytes, formatPercent, formatTime } from '../../utils/formatters';
import { ChevronDown, ChevronUp } from 'lucide-react';

const RecentDownloadsPanel = ({ downloads = [], timeRange = '24h' }) => {
  const [expanded, setExpanded] = useState(false);
  
  // Get time range label for the panel
  const getTimeRangeLabel = () => {
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
  };

  // Determine how many downloads to show based on time range and expansion state
  const getDisplayCount = () => {
    if (expanded) {
      return downloads.length; // Show all when expanded
    }
    
    // Show more downloads for longer time ranges
    switch(timeRange) {
      case '15m':
      case '30m':
        return 3;
      case '1h':
      case '6h':
        return 5;
      case '12h':
      case '24h':
        return 7;
      case '7d':
      case '30d':
        return 10;
      case '90d':
      case 'all':
        return 15;
      default:
        return 5;
    }
  };

  const displayCount = getDisplayCount();
  const displayDownloads = downloads.slice(0, displayCount);
  const hasMore = downloads.length > displayCount;

  // Calculate totals for the header
  const totalDownloads = downloads.length;
  const totalBytes = downloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
  const totalCacheHits = downloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
  const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h3 className="text-lg font-semibold text-white">Recent Downloads</h3>
          <div className="flex items-center gap-4 mt-1">
            <span className="text-xs text-gray-500">{getTimeRangeLabel()}</span>
            {downloads.length > 0 && (
              <>
                <span className="text-xs text-gray-400">
                  {totalDownloads} downloads
                </span>
                <span className="text-xs text-gray-400">
                  {formatBytes(totalBytes)} total
                </span>
                <span className={`text-xs px-2 py-0.5 rounded ${
                  overallHitRate > 50 
                    ? 'bg-green-900/50 text-green-400' 
                    : 'bg-yellow-900/50 text-yellow-400'
                }`}>
                  {formatPercent(overallHitRate)} hit rate
                </span>
              </>
            )}
          </div>
        </div>
        {hasMore && (
          <button
            onClick={() => setExpanded(!expanded)}
            className="flex items-center gap-1 text-xs text-blue-400 hover:text-blue-300 transition-colors"
          >
            {expanded ? 'Show less' : `Show all (${downloads.length})`}
            {expanded ? <ChevronUp className="w-3 h-3" /> : <ChevronDown className="w-3 h-3" />}
          </button>
        )}
      </div>
      
      <div className={`space-y-3 overflow-y-auto ${expanded ? 'max-h-[600px]' : 'max-h-[400px]'}`}>
        {displayDownloads.length > 0 ? (
          displayDownloads.map((download, idx) => (
            <div key={download.id || idx} className="bg-gray-900 rounded-lg p-3 border border-gray-700 hover:border-gray-600 transition-colors">
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
                  <span className="text-sm text-white">{formatBytes(download.totalBytes)}</span>
                  <div className="flex gap-2 text-xs">
                    <span className="text-green-400">↓ {formatBytes(download.cacheHitBytes)}</span>
                    <span className="text-yellow-400">↑ {formatBytes(download.cacheMissBytes)}</span>
                  </div>
                </div>
                <span className={`text-xs px-2 py-1 rounded ${
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
          <div className="flex items-center justify-center h-[250px] text-gray-500">
            {timeRange === '15m' || timeRange === '30m' 
              ? `No downloads in the ${getTimeRangeLabel().toLowerCase()}`
              : 'No downloads yet'}
          </div>
        )}
      </div>
      
      {downloads.length > displayCount && !expanded && (
        <div className="mt-3 pt-3 border-t border-gray-700 text-center">
          <span className="text-xs text-gray-500">
            Showing {displayCount} of {downloads.length} downloads
          </span>
        </div>
      )}
    </div>
  );
};

export default RecentDownloadsPanel;