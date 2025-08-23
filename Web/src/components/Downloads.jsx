import React, { useState, useEffect } from 'react';
import { Download, Filter, Search, CheckCircle, Clock } from 'lucide-react';
import axios from 'axios';
import * as signalR from '@microsoft/signalr';

function Downloads() {
  const [downloads, setDownloads] = useState([]);
  const [filteredDownloads, setFilteredDownloads] = useState([]);
  const [searchTerm, setSearchTerm] = useState('');
  const [filterService, setFilterService] = useState('all');
  const [showActiveOnly, setShowActiveOnly] = useState(false);
  const [services, setServices] = useState(['all']);

  useEffect(() => {
    loadDownloads();
    setupSignalR();
  }, []);

  useEffect(() => {
    filterDownloads();
  }, [downloads, searchTerm, filterService, showActiveOnly]);

  const loadDownloads = async () => {
    try {
      const response = await axios.get('/api/downloads/latest?count=100');
      const downloadData = response.data.map(d => ({
        ...d,
        totalBytes: d.cacheHitBytes + d.cacheMissBytes,
        hitRate: d.cacheHitBytes + d.cacheMissBytes > 0 
          ? (d.cacheHitBytes / (d.cacheHitBytes + d.cacheMissBytes)) * 100 
          : 0
      }));
      setDownloads(downloadData);
      
      // Extract unique services
      const uniqueServices = ['all', ...new Set(downloadData.map(d => d.service))];
      setServices(uniqueServices);
    } catch (error) {
      console.error('Error loading downloads:', error);
    }
  };

  const setupSignalR = async () => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("/downloadHub")
      .withAutomaticReconnect()
      .build();

    connection.on("DownloadUpdate", (download) => {
      setDownloads(prev => {
        const existing = prev.find(d => d.id === download.id);
        const downloadWithCalc = {
          ...download,
          totalBytes: download.cacheHitBytes + download.cacheMissBytes,
          hitRate: download.cacheHitBytes + download.cacheMissBytes > 0 
            ? (download.cacheHitBytes / (download.cacheHitBytes + download.cacheMissBytes)) * 100 
            : 0
        };
        
        if (existing) {
          return prev.map(d => d.id === download.id ? downloadWithCalc : d);
        }
        return [downloadWithCalc, ...prev].slice(0, 100);
      });
    });

    connection.on("PreloadComplete", () => {
      loadDownloads();
    });

    try {
      await connection.start();
      console.log("SignalR Connected");
    } catch (err) {
      console.error("SignalR Connection Error: ", err);
      setTimeout(() => setupSignalR(), 5000);
    }
  };

  const filterDownloads = () => {
    let filtered = [...downloads];

    // Apply search filter
    if (searchTerm) {
      filtered = filtered.filter(d => 
        d.clientIp.toLowerCase().includes(searchTerm.toLowerCase()) ||
        d.service.toLowerCase().includes(searchTerm.toLowerCase())
      );
    }

    // Apply service filter
    if (filterService !== 'all') {
      filtered = filtered.filter(d => d.service === filterService);
    }

    // Apply active filter
    if (showActiveOnly) {
      filtered = filtered.filter(d => d.isActive);
    }

    setFilteredDownloads(filtered);
  };

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const formatDate = (dateString) => {
    const date = new Date(dateString);
    return date.toLocaleDateString() + ' • ' + date.toLocaleTimeString();
  };

  const getServiceColor = (service) => {
    const colors = {
      steam: 'bg-blue-500',
      epic: 'bg-purple-500',
      origin: 'bg-orange-500',
      uplay: 'bg-red-500',
      blizzard: 'bg-cyan-500',
      riot: 'bg-pink-500',
      wsus: 'bg-green-500'
    };
    return colors[service.toLowerCase()] || 'bg-gray-500';
  };

  return (
    <div className="p-6">
      <h2 className="text-2xl font-semibold mb-6">Download History</h2>

      {/* Search and Filter Bar - Fixed */}
      <div className="mb-6 flex flex-col md:flex-row gap-3">
        <div className="flex-1 relative">
          <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 text-gray-400 w-5 h-5 pointer-events-none" />
          <input
            type="text"
            placeholder="Search by IP or service..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="w-full pl-10 pr-4 py-2 border border-gray-300 dark:border-gray-600 rounded-lg 
                     bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100
                     focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>

        <select
          value={filterService}
          onChange={(e) => setFilterService(e.target.value)}
          className="px-4 py-2 border border-gray-300 dark:border-gray-600 rounded-lg 
                   bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100
                   focus:outline-none focus:ring-2 focus:ring-blue-500 cursor-pointer
                   appearance-none bg-no-repeat bg-right pr-10"
          style={{
            backgroundImage: `url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' fill='none' viewBox='0 0 20 20'%3E%3Cpath stroke='%236b7280' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.5' d='M6 8l4 4 4-4'/%3E%3C/svg%3E")`,
            backgroundPosition: 'right 0.5rem center',
            backgroundSize: '1.5em 1.5em'
          }}
        >
          <option value="all">All Services</option>
          {services.filter(s => s !== 'all').map(service => (
            <option key={service} value={service}>
              {service.toUpperCase()}
            </option>
          ))}
        </select>

        <button
          onClick={() => setShowActiveOnly(!showActiveOnly)}
          className={`px-4 py-2 rounded-lg border transition-colors flex items-center gap-2
                    ${showActiveOnly 
                      ? 'bg-blue-500 text-white border-blue-500 hover:bg-blue-600' 
                      : 'bg-white dark:bg-gray-700 text-gray-700 dark:text-gray-300 border-gray-300 dark:border-gray-600 hover:bg-gray-50 dark:hover:bg-gray-600'}`}
        >
          <Filter className="w-4 h-4" />
          Active Only
        </button>
      </div>

      <div className="text-sm text-gray-500 dark:text-gray-400 mb-4">
        Showing {filteredDownloads.length} of {downloads.length} downloads
      </div>

      {/* Downloads Table */}
      <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead className="bg-gray-50 dark:bg-gray-700 border-b border-gray-200 dark:border-gray-600">
              <tr>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Service
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Client IP
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Start Time
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  End Time
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Cache Hit
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Cache Miss
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Total
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Hit Rate
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                  Status
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
              {filteredDownloads.length === 0 ? (
                <tr>
                  <td colSpan="9" className="px-4 py-12 text-center text-gray-500 dark:text-gray-400">
                    <Download className="w-12 h-12 mx-auto mb-3 opacity-50" />
                    No downloads found
                  </td>
                </tr>
              ) : (
                filteredDownloads.map((download, index) => (
                  <tr key={download.id || index} className="hover:bg-gray-50 dark:hover:bg-gray-700">
                    <td className="px-4 py-3">
                      <span className="inline-flex items-center gap-2">
                        <span className={`w-2 h-2 rounded-full ${getServiceColor(download.service)}`}></span>
                        <span className="font-medium uppercase">{download.service}</span>
                      </span>
                    </td>
                    <td className="px-4 py-3 font-mono text-sm">{download.clientIp}</td>
                    <td className="px-4 py-3 text-sm">
                      {formatDate(download.startTime)}
                    </td>
                    <td className="px-4 py-3 text-sm">
                      {formatDate(download.endTime)}
                    </td>
                    <td className="px-4 py-3">
                      <span className="text-green-600 dark:text-green-400 font-medium">
                        {formatBytes(download.cacheHitBytes)}
                      </span>
                    </td>
                    <td className="px-4 py-3">
                      <span className="text-red-600 dark:text-red-400 font-medium">
                        {formatBytes(download.cacheMissBytes)}
                      </span>
                    </td>
                    <td className="px-4 py-3 font-semibold">
                      {formatBytes(download.totalBytes)}
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <div className="w-20 bg-gray-200 dark:bg-gray-600 rounded-full h-2">
                          <div 
                            className="bg-green-500 h-2 rounded-full transition-all duration-300"
                            style={{ width: `${download.hitRate}%` }}
                          ></div>
                        </div>
                        <span className="text-sm font-medium">{download.hitRate.toFixed(1)}%</span>
                      </div>
                    </td>
                    <td className="px-4 py-3">
                      {download.isActive ? (
                        <span className="inline-flex items-center gap-1 px-2 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200">
                          <Clock className="w-3 h-3 animate-pulse" />
                          Active
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 px-2 py-1 rounded-full text-xs font-medium bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300">
                          <CheckCircle className="w-3 h-3" />
                          Complete
                        </span>
                      )}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

export default Downloads;