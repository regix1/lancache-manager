import React from 'react';
import { formatBytes, formatDate, getServiceIcon } from '../utils/formatters';

export default function LatestDownloads({ downloads, darkMode }) {
  return (
    <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
      <h2 className="text-xl font-bold mb-4">All Downloads</h2>
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className={`border-b ${darkMode ? 'border-dark-border' : 'border-gray-200'}`}>
              <th className="text-left p-3">Service</th>
              <th className="text-left p-3">Time</th>
              <th className="text-left p-3">Client</th>
              <th className="text-left p-3">Cache Hit</th>
              <th className="text-left p-3">Cache Miss</th>
              <th className="text-left p-3">Total</th>
              <th className="text-left p-3">Status</th>
            </tr>
          </thead>
          <tbody>
            {downloads.map((download) => (
              <tr
                key={download.id}
                className={`border-b ${
                  darkMode ? 'border-dark-border hover:bg-dark-bg' : 'border-gray-100 hover:bg-gray-50'
                } transition-all`}
              >
                <td className="p-3">
                  <div className="flex items-center space-x-2">
                    <span>{getServiceIcon(download.service)}</span>
                    <span className="font-semibold">{download.service.toUpperCase()}</span>
                  </div>
                </td>
                <td className="p-3">
                  <div>
                    <p className="text-sm">{formatDate(download.startTime)}</p>
                    <p className="text-xs opacity-70">{formatDate(download.endTime)}</p>
                  </div>
                </td>
                <td className="p-3">{download.clientIp}</td>
                <td className="p-3">
                  <span className="text-accent-green">{formatBytes(download.cacheHitBytes)}</span>
                  <span className="text-xs ml-1 opacity-70">({download.cacheHitPercent.toFixed(1)}%)</span>
                </td>
                <td className="p-3">
                  <span className="text-accent-red">{formatBytes(download.cacheMissBytes)}</span>
                  <span className="text-xs ml-1 opacity-70">({(100 - download.cacheHitPercent).toFixed(1)}%)</span>
                </td>
                <td className="p-3 font-semibold">{formatBytes(download.totalBytes)}</td>
                <td className="p-3">
                  <span className={`px-2 py-1 rounded-full text-xs ${
                    download.isActive
                      ? 'bg-accent-green text-white'
                      : 'bg-gray-500 text-white'
                  }`}>
                    {download.isActive ? 'Active' : 'Complete'}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}