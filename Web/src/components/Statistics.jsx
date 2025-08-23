import React, { useState, useEffect } from 'react';
import { Users, Server, TrendingUp, Clock } from 'lucide-react';
import axios from 'axios';

function Statistics() {
  const [clientStats, setClientStats] = useState([]);
  const [serviceStats, setServiceStats] = useState([]);
  const [useMockData, setUseMockData] = useState(true);

  useEffect(() => {
    // Check for mock data setting in localStorage
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, []);

  useEffect(() => {
    loadStatistics();
  }, [useMockData]);

  const loadStatistics = async () => {
    if (useMockData) {
      // Mock data for testing
      setClientStats([
        { 
          clientIp: '192.168.1.105', 
          totalDownloads: 45, 
          totalCacheHitBytes: 21474836480, 
          totalCacheMissBytes: 5368709120,
          lastSeen: new Date()
        },
        { 
          clientIp: '192.168.1.108', 
          totalDownloads: 32, 
          totalCacheHitBytes: 10737418240, 
          totalCacheMissBytes: 2684354560,
          lastSeen: new Date(Date.now() - 3600000)
        },
        { 
          clientIp: '192.168.1.110', 
          totalDownloads: 28, 
          totalCacheHitBytes: 32212254720, 
          totalCacheMissBytes: 1073741824,
          lastSeen: new Date(Date.now() - 7200000)
        },
      ]);

      setServiceStats([
        {
          service: 'steam',
          totalDownloads: 156,
          totalCacheHitBytes: 214748364800,
          totalCacheMissBytes: 53687091200,
          lastActivity: new Date()
        },
        {
          service: 'epic',
          totalDownloads: 89,
          totalCacheHitBytes: 107374182400,
          totalCacheMissBytes: 26843545600,
          lastActivity: new Date(Date.now() - 1800000)
        },
        {
          service: 'blizzard',
          totalDownloads: 67,
          totalCacheHitBytes: 322122547200,
          totalCacheMissBytes: 10737418240,
          lastActivity: new Date(Date.now() - 3600000)
        },
        {
          service: 'origin',
          totalDownloads: 45,
          totalCacheHitBytes: 64424509440,
          totalCacheMissBytes: 16106127360,
          lastActivity: new Date(Date.now() - 5400000)
        },
      ]);
    } else {
      try {
        const [clientsResponse, servicesResponse] = await Promise.all([
          axios.get('/api/stats/clients'),
          axios.get('/api/stats/services')
        ]);
        
        setClientStats(clientsResponse.data);
        setServiceStats(servicesResponse.data);
      } catch (error) {
        console.error('Error loading statistics:', error);
      }
    }
  }; // Fixed: Added missing closing bracket

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const formatDate = (date) => {
    if (!date) return 'Never';
    try {
      const d = new Date(date);
      return d.toLocaleDateString() + ' ' + d.toLocaleTimeString();
    } catch {
      return 'Invalid date';
    }
  };

  const getServiceIcon = (service) => {
    const icons = {
      steam: '🎮',
      epic: '🎯',
      origin: '🎲',
      blizzard: '❄️',
      riot: '⚔️',
      wsus: '🪟',
      other: '📦'
    };
    return icons[service?.toLowerCase()] || icons.other;
  };

  // Calculate totals for client stats
  const processedClientStats = clientStats.map(client => ({
    ...client,
    totalBytes: client.totalCacheHitBytes + client.totalCacheMissBytes,
    cacheHitPercent: client.totalCacheHitBytes + client.totalCacheMissBytes > 0
      ? (client.totalCacheHitBytes / (client.totalCacheHitBytes + client.totalCacheMissBytes)) * 100
      : 0
  }));

  // Calculate totals for service stats
  const processedServiceStats = serviceStats.map(service => ({
    ...service,
    totalBytes: service.totalCacheHitBytes + service.totalCacheMissBytes,
    cacheHitPercent: service.totalCacheHitBytes + service.totalCacheMissBytes > 0
      ? (service.totalCacheHitBytes / (service.totalCacheHitBytes + service.totalCacheMissBytes)) * 100
      : 0
  }));

  const getServiceColorClass = (service) => {
    const colors = {
      steam: 'border-l-primary',
      epic: 'border-l-secondary',
      origin: 'border-l-warning',
      blizzard: 'border-l-primary',
      default: 'border-l-gray-400'
    };
    return colors[service?.toLowerCase()] || colors.default;
  };

  // Toggle mock data function
  const toggleMockData = () => {
    const newValue = !useMockData;
    setUseMockData(newValue);
    localStorage.setItem('useMockData', newValue ? 'true' : 'false');
  };

  return (
    <div className="space-y-6">
      {/* Header with Mock Data Toggle */}
      <div className="flex justify-between items-center mb-4">
        <h2 className="text-2xl font-semibold">Statistics</h2>
        <button
          onClick={toggleMockData}
          className={`chip ${useMockData ? 'chip-warning' : 'chip-success'} cursor-pointer hover:opacity-80 transition-opacity`}
          title="Click to toggle between mock and real data"
        >
          {useMockData ? 'Mock Data Mode' : 'Live Data Mode'}
        </button>
      </div>

      {/* Client Statistics */}
      <div className="card">
        <div className="flex items-center gap-2 mb-4">
          <Users className="w-5 h-5 text-primary" />
          <h2 className="text-xl font-bold">Client Statistics</h2>
        </div>
        <div className="divider mb-4"></div>
        <div className="overflow-x-auto">
          <table className="table-auto">
            <thead>
              <tr>
                <th>Client IP</th>
                <th>Total Downloads</th>
                <th>Cache Hit</th>
                <th>Cache Miss</th>
                <th>Total Traffic</th>
                <th>Hit Rate</th>
                <th>Last Seen</th>
              </tr>
            </thead>
            <tbody>
              {processedClientStats.map(client => (
                <tr key={client.clientIp}>
                  <td className="font-mono font-semibold">{client.clientIp}</td>
                  <td>
                    <span className="chip chip-primary">
                      {client.totalDownloads}
                    </span>
                  </td>
                  <td className="text-success font-semibold">
                    {formatBytes(client.totalCacheHitBytes)}
                  </td>
                  <td className="text-danger font-semibold">
                    {formatBytes(client.totalCacheMissBytes)}
                  </td>
                  <td className="font-bold">{formatBytes(client.totalBytes)}</td>
                  <td>
                    <div className="flex items-center gap-2">
                      <div className="progress w-20">
                        <div 
                          className="progress-bar progress-bar-success"
                          style={{ width: `${client.cacheHitPercent}%` }}
                        ></div>
                      </div>
                      <span className="text-sm font-semibold">
                        {client.cacheHitPercent.toFixed(1)}%
                      </span>
                    </div>
                  </td>
                  <td className="text-sm">
                    <div className="flex items-center gap-1">
                      <Clock className="w-3 h-3" />
                      {formatDate(client.lastSeen)}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          
          {processedClientStats.length === 0 && (
            <div className="text-center py-8 text-muted">
              No client statistics available
            </div>
          )}
        </div>
      </div>

      {/* Service Statistics */}
      <div className="card">
        <div className="flex items-center gap-2 mb-4">
          <Server className="w-5 h-5 text-secondary" />
          <h2 className="text-xl font-bold">Service Statistics</h2>
        </div>
        <div className="divider mb-4"></div>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {processedServiceStats.map(service => (
            <div
              key={service.service}
              className={`card border-l-4 ${getServiceColorClass(service.service)}`}
            >
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-2">
                  <span className="text-2xl">{getServiceIcon(service.service)}</span>
                  <h3 className="font-bold text-lg">{service.service.toUpperCase()}</h3>
                </div>
                <TrendingUp className="w-5 h-5 opacity-50" />
              </div>
              
              <div className="space-y-2">
                <div className="flex justify-between text-sm">
                  <span className="text-muted">Downloads:</span>
                  <span className="font-semibold">{service.totalDownloads}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-muted">Cache Hit:</span>
                  <span className="text-success font-semibold">
                    {formatBytes(service.totalCacheHitBytes)}
                  </span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-muted">Cache Miss:</span>
                  <span className="text-danger font-semibold">
                    {formatBytes(service.totalCacheMissBytes)}
                  </span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-muted">Total:</span>
                  <span className="font-bold">{formatBytes(service.totalBytes)}</span>
                </div>
                
                <div className="divider my-2"></div>
                
                <div className="pt-2">
                  <div className="flex justify-between mb-1">
                    <span className="text-sm text-muted">Hit Rate:</span>
                    <span className="text-sm font-semibold text-success">
                      {service.cacheHitPercent.toFixed(1)}%
                    </span>
                  </div>
                  <div className="progress">
                    <div 
                      className="progress-bar progress-bar-success"
                      style={{ width: `${service.cacheHitPercent}%` }}
                    ></div>
                  </div>
                </div>
                
                <div className="text-xs text-muted pt-2">
                  Last activity: {formatDate(service.lastActivity)}
                </div>
              </div>
            </div>
          ))}
        </div>
        
        {processedServiceStats.length === 0 && (
          <div className="text-center py-8 text-muted">
            No service statistics available
          </div>
        )}
      </div>
    </div>
  );
}

export default Statistics;