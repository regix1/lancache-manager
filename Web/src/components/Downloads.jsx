import React, { useState, useEffect } from 'react';
import { Download, Filter, Search, CheckCircle, Clock, X } from 'lucide-react';
import axios from 'axios';
import * as signalR from '@microsoft/signalr';

function Downloads() {
  const [downloads, setDownloads] = useState([]);
  const [filteredDownloads, setFilteredDownloads] = useState([]);
  const [searchTerm, setSearchTerm] = useState('');
  const [filterService, setFilterService] = useState('all');
  const [showActiveOnly, setShowActiveOnly] = useState(false);
  const [services, setServices] = useState(['all', 'steam', 'epic', 'origin', 'blizzard']);
  const [useMockData, setUseMockData] = useState(true);

  useEffect(() => {
    // Check for mock data setting in localStorage
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, []);

  useEffect(() => {
    loadDownloads();
    if (!useMockData) {
      setupSignalR();
    }
  }, [useMockData]);

  useEffect(() => {
    filterDownloads();
  }, [downloads, searchTerm, filterService, showActiveOnly]);

  const loadDownloads = async () => {
    if (useMockData) {
      // Mock data for testing
      const mockData = [
        { id: 1, service: 'steam', clientIp: '192.168.1.105', startTime: new Date(), endTime: new Date(), cacheHitBytes: 2147483648, cacheMissBytes: 536870912, isActive: true },
        { id: 2, service: 'epic', clientIp: '192.168.1.108', startTime: new Date(Date.now() - 3600000), endTime: new Date(), cacheHitBytes: 1073741824, cacheMissBytes: 268435456, isActive: false },
        { id: 3, service: 'blizzard', clientIp: '192.168.1.110', startTime: new Date(Date.now() - 7200000), endTime: new Date(), cacheHitBytes: 3221225472, cacheMissBytes: 0, isActive: false },
        { id: 4, service: 'origin', clientIp: '192.168.1.112', startTime: new Date(Date.now() - 1800000), endTime: new Date(), cacheHitBytes: 644245094, cacheMissBytes: 161061273, isActive: true },
        { id: 5, service: 'steam', clientIp: '192.168.1.115', startTime: new Date(Date.now() - 5400000), endTime: new Date(), cacheHitBytes: 5368709120, cacheMissBytes: 1342177280, isActive: false },
      ];

      const downloadData = mockData.map(d => ({
        ...d,
        totalBytes: d.cacheHitBytes + d.cacheMissBytes,
        hitRate: d.cacheHitBytes + d.cacheMissBytes > 0 
          ? (d.cacheHitBytes / (d.cacheHitBytes + d.cacheMissBytes)) * 100 
          : 0
      }));
      setDownloads(downloadData);
    } else {
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
    }
  };

  const setupSignalR = async () => {
    try {
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

      await connection.start();
    } catch (err) {
      console.log("SignalR not available, using polling");
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

  const clearSearch = () => {
    setSearchTerm('');
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
      steam: 'chip-primary',
      epic: 'chip-secondary',
      origin: 'chip-warning',
      blizzard: 'chip-primary',
      default: 'chip-default'
    };
    return colors[service.toLowerCase()] || colors.default;
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h2 className="text-2xl font-semibold">Download History</h2>
        {useMockData && (
          <span className="chip chip-warning">
            Mock Data Mode
          </span>
        )}
      </div>

      {/* Search and Filter Bar */}
      <div className="flex flex-col md:flex-row gap-3">
        <div className="flex-1 relative">
          <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 text-gray-400 h-4 w-4" />
          <input
            type="text"
            placeholder="Search by IP or service..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="input pl-10 pr-10"
          />
          {searchTerm && (
            <button
              onClick={clearSearch}
              className="absolute right-3 top-1/2 transform -translate-y-1/2 text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
            >
              <X className="h-4 w-4" />
            </button>
          )}
        </div>

        <select
          value={filterService}
          onChange={(e) => setFilterService(e.target.value)}
          className="select w-full md:w-48"
        >
          {services.map((service) => (
            <option key={service} value={service}>
              {service === 'all' ? 'All Services' : service.toUpperCase()}
            </option>
          ))}
        </select>

        <button
          onClick={() => setShowActiveOnly(!showActiveOnly)}
          className={`btn ${showActiveOnly ? 'btn-primary' : 'btn-default'} min-w-[140px]`}
        >
          <Filter className="h-4 w-4" />
          {showActiveOnly ? 'Active Only' : 'All Status'}
        </button>
      </div>

      {/* Results count */}
      <div className="text-sm text-muted">
        Showing {filteredDownloads.length} of {downloads.length} downloads
        {searchTerm && ` • Searching for "${searchTerm}"`}
      </div>

      {/* Downloads Table */}
      <div className="card p-0 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="table-auto">
            <thead>
              <tr>
                <th>SERVICE</th>
                <th>CLIENT IP</th>
                <th>START TIME</th>
                <th>END TIME</th>
                <th>CACHE HIT</th>
                <th>CACHE MISS</th>
                <th>TOTAL</th>
                <th>HIT RATE</th>
                <th>STATUS</th>
              </tr>
            </thead>
            <tbody>
              {filteredDownloads.length > 0 ? (
                filteredDownloads.map((download) => (
                  <tr key={download.id}>
                    <td>
                      <span className={`chip ${getServiceColor(download.service)}`}>
                        {download.service.toUpperCase()}
                      </span>
                    </td>
                    <td>
                      <span className="font-mono text-sm">{download.clientIp}</span>
                    </td>
                    <td>
                      <span className="text-sm">{formatDate(download.startTime)}</span>
                    </td>
                    <td>
                      <span className="text-sm">{formatDate(download.endTime)}</span>
                    </td>
                    <td>
                      <span className="text-success font-medium">
                        {formatBytes(download.cacheHitBytes)}
                      </span>
                    </td>
                    <td>
                      <span className="text-danger font-medium">
                        {formatBytes(download.cacheMissBytes)}
                      </span>
                    </td>
                    <td>
                      <span className="font-semibold">
                        {formatBytes(download.totalBytes)}
                      </span>
                    </td>
                    <td>
                      <div className="flex items-center gap-2">
                        <div className="progress w-16">
                          <div 
                            className="progress-bar progress-bar-success"
                            style={{ width: `${download.hitRate}%` }}
                          ></div>
                        </div>
                        <span className="text-sm font-medium min-w-[45px]">
                          {download.hitRate.toFixed(1)}%
                        </span>
                      </div>
                    </td>
                    <td>
                      {download.isActive ? (
                        <span className="chip chip-success">
                          <Clock className="h-3 w-3" />
                          Active
                        </span>
                      ) : (
                        <span className="chip chip-default">
                          <CheckCircle className="h-3 w-3" />
                          Complete
                        </span>
                      )}
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td colSpan="9" className="text-center py-8 text-muted">
                    {searchTerm || filterService !== 'all' || showActiveOnly 
                      ? 'No downloads match your filters' 
                      : 'No downloads found'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

export default Downloads;