import React, { useState } from 'react';
import { Search, Filter, Download as DownloadIcon } from 'lucide-react';
import clsx from 'clsx';
import { formatBytes, formatDate, getServiceIcon } from '../utils/formatters';

export default function Downloads({ downloads, darkMode }) {
  const [searchTerm, setSearchTerm] = useState('');
  const [filterService, setFilterService] = useState('all');
  const [showActiveOnly, setShowActiveOnly] = useState(false);

  const services = [...new Set(downloads?.map(d => d.service) || [])];
  
  const filteredDownloads = downloads?.filter(download => {
    const matchesSearch = download.clientIp.includes(searchTerm) || 
                          download.service.toLowerCase().includes(searchTerm.toLowerCase());
    const matchesService = filterService === 'all' || download.service === filterService;
    const matchesActive = !showActiveOnly || download.isActive;
    return matchesSearch && matchesService && matchesActive;
  }) || [];

  return (
    <div className={clsx(
      'rounded-lg shadow-lg p-6',
      darkMode ? 'bg-gray-800' : 'bg-white'
    )}>
      <div className="mb-6">
        <h2 className="text-xl font-bold mb-4">Download History</h2>
        
        {/* Filters */}
        <div className="flex flex-wrap gap-4 mb-4">
          <div className="flex-1 min-w-[200px]">
            <div className="relative">
              <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-gray-400" />
              <input
                type="text"
                placeholder="Search by IP or service..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className={clsx(
                  'w-full pl-10 pr-4 py-2 rounded-lg border transition-colors',
                  darkMode 
                    ? 'bg-gray-700 border-gray-600 text-white placeholder-gray-400'
                    : 'bg-white border-gray-300 text-gray-900 placeholder-gray-500'
                )}
              />
            </div>
          </div>
          
          <select
            value={filterService}
            onChange={(e) => setFilterService(e.target.value)}
            className={clsx(
              'px-4 py-2 rounded-lg border transition-colors',
              darkMode 
                ? 'bg-gray-700 border-gray-600 text-white'
                : 'bg-white border-gray-300 text-gray-900'
            )}
          >
            <option value="all">All Services</option>
            {services.map(service => (
              <option key={service} value={service}>{service.toUpperCase()}</option>
            ))}
          </select>
          
          <button
            onClick={() => setShowActiveOnly(!showActiveOnly)}
            className={clsx(
              'px-4 py-2 rounded-lg border transition-all duration-200',
              showActiveOnly
                ? 'bg-green-500 text-white border-green-500'
                : darkMode
                ? 'bg-gray-700 border-gray-600 text-white hover:bg-gray-600'
                : 'bg-white border-gray-300 text-gray-900 hover:bg-gray-50'
            )}
          >
            <div className="flex items-center gap-2">
              <Filter className="w-4 h-4" />
              Active Only
            </div>
          </button>
        </div>

        <div className="text-sm text-gray-500">
          Showing {filteredDownloads.length} of {downloads?.length || 0} downloads
        </div>
      </div>

      {/* Downloads Table */}
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className={clsx(
              'border-b',
              darkMode ? 'border-gray-700' : 'border-gray-200'
            )}>
              <th className="text-left p-3">Service</th>
              <th className="text-left p-3">Client IP</th>
              <th className="text-left p-3">Start Time</th>
              <th className="text-left p-3">End Time</th>
              <th className="text-left p-3">Cache Hit</th>
              <th className="text-left p-3">Cache Miss</th>
              <th className="text-left p-3">Total</th>
              <th className="text-left p-3">Hit Rate</th>
              <th className="text-left p-3">Status</th>
            </tr>
          </thead>
          <tbody>
            {filteredDownloads.map(download => (
              <tr
                key={download.id}
                className={clsx(
                  'border-b transition-colors',
                  darkMode 
                    ? 'border-gray-700 hover:bg-gray-700'
                    : 'border-gray-100 hover:bg-gray-50'
                )}
              >
                <td className="p-3">
                  <div className="flex items-center gap-2">
                    <span className="text-xl">{getServiceIcon(download.service)}</span>
                    <span className="font-semibold">{download.service.toUpperCase()}</span>
                  </div>
                </td>
                <td className="p-3 font-mono text-sm">{download.clientIp}</td>
                <td className="p-3 text-sm">{formatDate(download.startTime)}</td>
                <td className="p-3 text-sm">{formatDate(download.endTime)}</td>
                <td className="p-3">
                  <span className="text-green-500 font-semibold">
                    {formatBytes(download.cacheHitBytes)}
                  </span>
                </td>
                <td className="p-3">
                  <span className="text-red-500 font-semibold">
                    {formatBytes(download.cacheMissBytes)}
                  </span>
                </td>
                <td className="p-3 font-bold">{formatBytes(download.totalBytes)}</td>
                <td className="p-3">
                  <div className="flex items-center gap-2">
                    <div className={clsx(
                      'w-16 h-2 rounded-full',
                      darkMode ? 'bg-gray-600' : 'bg-gray-200'
                    )}>
                      <div
                        className="h-2 rounded-full bg-green-500"
                        style={{ width: `${download.cacheHitPercent}%` }}
                      />
                    </div>
                    <span className="text-sm font-semibold">
                      {download.cacheHitPercent.toFixed(1)}%
                    </span>
                  </div>
                </td>
                <td className="p-3">
                  <span className={clsx(
                    'px-2 py-1 rounded text-xs font-semibold',
                    download.isActive
                      ? 'bg-green-500 text-white'
                      : 'bg-gray-500 text-white'
                  )}>
                    {download.isActive ? 'Active' : 'Complete'}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        
        {filteredDownloads.length === 0 && (
          <div className="text-center py-8 text-gray-500">
            <DownloadIcon className="w-12 h-12 mx-auto mb-2 opacity-50" />
            <p>No downloads found</p>
          </div>
        )}
      </div>
    </div>
  );
}