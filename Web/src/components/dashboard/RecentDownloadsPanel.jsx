import React from 'react';
import { formatBytes, formatPercent, formatTime } from '../../utils/formatters';

const RecentDownloadsPanel = ({ downloads = [], timeRange = '24h' }) => {
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

  // Show only the first 5 downloads
  const displayDownloads = downloads.slice(0, 5);

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-white">Recent Downloads</h3>
        <span className="text-xs text-gray-500">{getTimeRangeLabel()}</span>
      </div>
      <div className="space-y-3 max-h-[300px] overflow-y-auto">
        {displayDownloads.length > 0 ? (
          displayDownloads.map((download, idx) => (
            <div key={download.id || idx} className="bg-gray-900 rounded-lg p-3 border border-gray-700">
              <div className="flex justify-between items-start mb-1">
                <span className="text-sm font-medium text-blue-400">{download.service}</span>
                <span className="text-xs text-gray-500">
                  {formatTime(download.startTime)}
                </span>
              </div>
              <div className="text-xs text-gray-400">{download.clientIp}</div>
              {download.gameName && (
                <div className="text-xs text-gray-500 mt-1">{download.gameName}</div>
              )}
              <div className="flex justify-between items-center mt-2">
                <span className="text-sm text-white">{formatBytes(download.totalBytes)}</span>
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
    </div>
  );
};

export default RecentDownloadsPanel;