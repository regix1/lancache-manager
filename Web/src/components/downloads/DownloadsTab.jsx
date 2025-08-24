import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';

const DownloadsTab = () => {
  const { latestDownloads } = useData();

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <h2 className="text-xl font-semibold mb-4">All Downloads</h2>
      <div className="space-y-3 max-h-[calc(100vh-300px)] overflow-y-auto">
        {latestDownloads.map((download, idx) => (
          <div key={download.id || idx} className="bg-gray-900 rounded-lg p-4 border border-gray-700">
            <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
              <div>
                <p className="text-xs text-gray-400">Service</p>
                <p className="text-sm font-medium text-blue-400">{download.service}</p>
              </div>
              <div>
                <p className="text-xs text-gray-400">Client</p>
                <p className="text-sm">{download.clientIp}</p>
              </div>
              <div>
                <p className="text-xs text-gray-400">Size</p>
                <p className="text-sm">{formatBytes(download.totalBytes)}</p>
              </div>
              <div>
                <p className="text-xs text-gray-400">Cache Hit Rate</p>
                <p className="text-sm">{formatPercent(download.cacheHitPercent)}</p>
              </div>
              <div>
                <p className="text-xs text-gray-400">Time</p>
                <p className="text-sm">{formatDateTime(download.startTime)}</p>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

export default DownloadsTab;