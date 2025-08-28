import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatTime } from '../../utils/formatters';

const RecentDownloadsPanel = () => {
  const { latestDownloads } = useData();

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <h3 className="text-lg font-semibold text-white mb-4">Recent Downloads</h3>
      <div className="space-y-3 max-h-[300px] overflow-y-auto">
        {latestDownloads.length > 0 ? (
          latestDownloads.slice(0, 5).map((download, idx) => (
            <div key={download.id || idx} className="bg-gray-900 rounded-lg p-3 border border-gray-700">
              <div className="flex justify-between items-start mb-1">
                <span className="text-sm font-medium text-blue-400">{download.service}</span>
                <span className="text-xs text-gray-500">
                  {formatTime(download.startTime)}
                </span>
              </div>
              <div className="text-xs text-gray-400">{download.clientIp}</div>
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
            No downloads yet
          </div>
        )}
      </div>
    </div>
  );
};

export default RecentDownloadsPanel;