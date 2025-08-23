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

    if (searchTerm) {
      filtered = filtered.filter(d => 
        d.clientIp.toLowerCase().includes(searchTerm.toLowerCase()) ||
        d.service.toLowerCase().includes(searchTerm.toLowerCase())
      );
    }

    if (filterService !== 'all') {
      filtered = filtered.filter(d => d.service === filterService);
    }

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

      {/* Fixed Search and Filter Bar */}
      <div className="mb-6 flex flex-col md:flex-row gap-3">
        {/* Search Input - Fixed with proper padding */}
        <div className="flex-1 relative">
          <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
            <Search className="h-5 w-5 text-gray-400" />
          </div>
          <input
            type="text"
            placeholder="Search by IP or service..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="block w-full pl-10 pr-3 py-2 border border-gray-300 dark:border-gray-600 
                     rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100
                     placeholder-gray-500 dark:placeholder-gray-400
                     focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
          />
        </div>

        {/* Service Filter Dropdown - Fixed styling */}
        <div className="relative">
          <select
            value={filterService}
            onChange={(e) => setFilterService(e.target.value)}
            className="block w-full px-4 py-2 pr-8 border border-gray-300 dark:border-gray-600 
                     rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100
                     focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent
                     appearance-none cursor-pointer"
          >
            <option value="all">All Services</option>
            {services.filter(s => s !== 'all').map(service => (
              <option key={service} value={service}>
                {service.toUpperCase()}
              </option>
            ))}
          </select>
          {/* Custom dropdown arrow */}
          <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center px-2 text-gray-500">
            <svg className="fill-current h-4 w-4" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20">
              <path d="M5.293 7.293a1 1 0 011.414 0L10 10.586l3.293-3.293a1 1 0 111.414 1.414l-4 4a1 1 0 01-1.414 0l-4-4a1 1 0 010-1.414z" />
            </svg>
          </div>
        </div>

        {/* Active Only Button */}
        <button
          onClick={() => setShowActiveOnly(!showActiveOnly)}
          className={`px-4 py-2 rounded-lg border transition-colors flex items-center justify-center gap-2 min-w-[140px]
                    ${showActiveOnly 
                      ? 'bg-blue-500 text-white border-blue-500 hover:bg-blue-600' 
                      : 'bg-white dark:bg-gray-700 text-gray-700 dark:text-gray-300 border-gray-300 dark:border-gray-600 hover:bg-gray-50 dark:hover:bg-gray-600'}`}
        >
          <Filter className="w-4 h-4" />
          <span>Active Only</span>
        </button>
      </div>

      {/* Results count */}
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
                    <div>No downloads found</div>
                  </td>
                </tr>
              ) : (
                filteredDownloads.map((download, index) => (
                  <tr key={download.id || index} className="hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors">
                    <td className="px-4 py-3 whitespace-nowrap">
                      <span className="inline-flex items-center gap-2">
                        <span className={`w-2 h-2 rounded-full ${getServiceColor(download.service)}`}></span>
                        <span className="font-medium uppercase">{download.service}</span>
                      </span>
                    </td>
                    <td className="px-4 py-3 font-mono text-sm whitespace-nowrap">{download.clientIp}</td>
                    <td className="px-4 py-3 text-sm whitespace-nowrap">
                      {formatDate(download.startTime)}
                    </td>
                    <td className="px-4 py-3 text-sm whitespace-nowrap">
                      {formatDate(download.endTime)}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap">
                      <span className="text-green-600 dark:text-green-400 font-medium">
                        {formatBytes(download.cacheHitBytes)}
                      </span>
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap">
                      <span className="text-red-600 dark:text-red-400 font-medium">
                        {formatBytes(download.cacheMissBytes)}
                      </span>
                    </td>
                    <td className="px-4 py-3 font-semibold whitespace-nowrap">
                      {formatBytes(download.totalBytes)}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap">
                      <div className="flex items-center gap-2">
                        <div className="w-16 bg-gray-200 dark:bg-gray-600 rounded-full h-2">
                          <div 
                            className="bg-green-500 h-2 rounded-full transition-all duration-300"
                            style={{ width: `${download.hitRate}%` }}
                          ></div>
                        </div>
                        <span className="text-sm font-medium min-w-[45px]">{download.hitRate.toFixed(1)}%</span>
                      </div>
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap">
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