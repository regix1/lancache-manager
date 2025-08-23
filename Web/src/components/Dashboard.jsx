import React, { useState, useEffect } from 'react';
import { HardDrive, Download, Activity, Users, TrendingUp, Server } from 'lucide-react';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, PieChart, Pie, Cell } from 'recharts';
import axios from 'axios';
import * as signalR from '@microsoft/signalr';

function Dashboard() {
  const [stats, setStats] = useState({
    totalCache: 0,
    usedSpace: 0,
    activeDownloads: 0,
    activeClients: 0,
    cacheHitRate: 0,
    totalFiles: 0
  });

  const [recentDownloads, setRecentDownloads] = useState([]);
  const [serviceStats, setServiceStats] = useState([]);
  const [topClients, setTopClients] = useState([]);
  const [useMockData, setUseMockData] = useState(true);

  useEffect(() => {
    // Check for mock data setting in localStorage
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, []);

  useEffect(() => {
    loadDashboardData();
    if (!useMockData) {
      setupSignalR();
    }
    const interval = setInterval(loadDashboardData, 30000);
    return () => clearInterval(interval);
  }, [useMockData]);

  const loadDashboardData = async () => {
    if (useMockData) {
      // Mock data for testing
      setStats({
        totalCache: 5497558138880,
        usedSpace: 3298534883328,
        activeDownloads: 3,
        activeClients: 12,
        cacheHitRate: 78.5,
        totalFiles: 45782
      });

      setRecentDownloads([
        { id: 1, service: 'steam', clientIp: '192.168.1.105', cacheHitBytes: 2147483648, cacheMissBytes: 536870912 },
        { id: 2, service: 'epic', clientIp: '192.168.1.108', cacheHitBytes: 1073741824, cacheMissBytes: 268435456 },
        { id: 3, service: 'blizzard', clientIp: '192.168.1.110', cacheHitBytes: 3221225472, cacheMissBytes: 0 },
      ]);

      setServiceStats([
        { service: 'steam', totalCacheHitBytes: 2147483648000, totalCacheMissBytes: 536870912000 },
        { service: 'epic', totalCacheHitBytes: 1073741824000, totalCacheMissBytes: 268435456000 },
        { service: 'blizzard', totalCacheHitBytes: 3221225472000, totalCacheMissBytes: 805306368000 },
        { service: 'origin', totalCacheHitBytes: 644245094400, totalCacheMissBytes: 161061273600 },
      ]);

      setTopClients([
        { clientIp: '192.168.1.105', totalDownloads: 45, totalCacheHitBytes: 21474836480, totalCacheMissBytes: 5368709120 },
        { clientIp: '192.168.1.108', totalDownloads: 32, totalCacheHitBytes: 10737418240, totalCacheMissBytes: 2684354560 },
        { clientIp: '192.168.1.110', totalDownloads: 28, totalCacheHitBytes: 32212254720, totalCacheMissBytes: 1073741824 },
        { clientIp: '192.168.1.112', totalDownloads: 19, totalCacheHitBytes: 8589934592, totalCacheMissBytes: 4294967296 },
      ]);
    } else {
      try {
        const [cacheResponse, downloadsResponse, clientsResponse, servicesResponse] = await Promise.all([
          axios.get('/api/management/cache'),
          axios.get('/api/downloads/active'),
          axios.get('/api/stats/clients'),
          axios.get('/api/stats/services')
        ]);

        const cacheData = cacheResponse.data;
        setStats({
          totalCache: cacheData.totalCacheSize,
          usedSpace: cacheData.usedCacheSize,
          activeDownloads: downloadsResponse.data.length,
          activeClients: clientsResponse.data.filter(c => {
            const lastSeen = new Date(c.lastSeen);
            const fiveMinutesAgo = new Date(Date.now() - 5 * 60 * 1000);
            return lastSeen > fiveMinutesAgo;
          }).length,
          cacheHitRate: calculateHitRate(servicesResponse.data),
          totalFiles: cacheData.totalFiles
        });

        setRecentDownloads(downloadsResponse.data.slice(0, 5));
        setServiceStats(servicesResponse.data);
        setTopClients(clientsResponse.data.slice(0, 5));
      } catch (error) {
        console.error('Error loading dashboard data:', error);
      }
    }
  };

  const setupSignalR = async () => {
    try {
      const connection = new signalR.HubConnectionBuilder()
        .withUrl("/downloadHub")
        .withAutomaticReconnect()
        .build();

      connection.on("DownloadUpdate", () => {
        loadDashboardData();
      });

      await connection.start();
    } catch (err) {
      console.log("SignalR not available, using polling");
    }
  };

  const calculateHitRate = (services) => {
    const totalHit = services.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
    const totalMiss = services.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
    const total = totalHit + totalMiss;
    return total > 0 ? (totalHit / total) * 100 : 0;
  };

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const pieData = serviceStats.map(s => ({
    name: s.service.toUpperCase(),
    value: s.totalCacheHitBytes + s.totalCacheMissBytes
  }));

  const COLORS = ['#3B82F6', '#8B5CF6', '#EF4444', '#F59E0B', '#10B981', '#EC4899'];

  return (
    <div className="space-y-6">
      {/* Mock Data Indicator */}
      {useMockData && (
        <div className="flex justify-end">
          <span className="chip chip-warning">
            Mock Data Mode
          </span>
        </div>
      )}

      {/* Stats Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="card">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-muted">Total Cache</span>
            <HardDrive className="w-5 h-5 text-primary" />
          </div>
          <div className="text-2xl font-bold">{formatBytes(stats.totalCache)}</div>
          <div className="text-xs text-muted">
            {stats.totalFiles.toLocaleString()} files
          </div>
        </div>

        <div className="card">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-muted">Used Space</span>
            <Server className="w-5 h-5 text-success" />
          </div>
          <div className="text-2xl font-bold">{formatBytes(stats.usedSpace)}</div>
          <div className="text-xs text-muted">
            {((stats.usedSpace / stats.totalCache) * 100).toFixed(1)}% utilized
          </div>
        </div>

        <div className="card">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-muted">Active Downloads</span>
            <Download className="w-5 h-5 text-secondary" />
          </div>
          <div className="text-2xl font-bold">{stats.activeDownloads}</div>
          <div className="text-xs text-muted">
            {stats.activeDownloads === 1 ? '1 download' : `${stats.activeDownloads} total`}
          </div>
        </div>

        <div className="card">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-muted">Active Clients</span>
            <Users className="w-5 h-5 text-warning" />
          </div>
          <div className="text-2xl font-bold">{stats.activeClients}</div>
          <div className="text-xs text-muted">
            {stats.activeClients === 0 ? '0 services' : `${serviceStats.length} services`}
          </div>
        </div>
      </div>

      {/* Charts Row */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Service Distribution */}
        <div className="card">
          <h3 className="text-lg font-semibold mb-4">Service Distribution</h3>
          <div className="divider mb-4"></div>
          <div className="h-64 flex items-center justify-center">
            {pieData.length > 0 ? (
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={pieData}
                    cx="50%"
                    cy="50%"
                    labelLine={false}
                    label={({ name, percent }) => `${name} ${(percent * 100).toFixed(0)}%`}
                    outerRadius={80}
                    fill="#8884d8"
                    dataKey="value"
                  >
                    {pieData.map((entry, index) => (
                      <Cell key={`cell-${index}`} fill={COLORS[index % COLORS.length]} />
                    ))}
                  </Pie>
                  <Tooltip formatter={(value) => formatBytes(value)} />
                </PieChart>
              </ResponsiveContainer>
            ) : (
              <div className="text-muted">No data available</div>
            )}
          </div>
        </div>

        {/* Recent Downloads */}
        <div className="card">
          <h3 className="text-lg font-semibold mb-4">Recent Downloads</h3>
          <div className="divider mb-4"></div>
          <div className="space-y-3">
            {recentDownloads.length > 0 ? (
              recentDownloads.map((download, idx) => (
                <div key={idx} className="flex items-center justify-between py-2 border-b border-gray-100 dark:border-dark-border last:border-0">
                  <div className="flex items-center gap-3">
                    <div className="w-2 h-2 bg-secondary rounded-full"></div>
                    <div>
                      <div className="font-medium uppercase text-sm">{download.service}</div>
                      <div className="text-xs text-muted">{download.clientIp}</div>
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="font-medium text-sm">
                      {formatBytes(download.cacheHitBytes + download.cacheMissBytes)}
                    </div>
                    <div className="text-xs text-success">
                      {download.cacheHitBytes > 0 ? 
                        `${((download.cacheHitBytes / (download.cacheHitBytes + download.cacheMissBytes)) * 100).toFixed(0)}%` 
                        : '0%'} hit
                    </div>
                  </div>
                </div>
              ))
            ) : (
              <div className="text-center py-8 text-muted">
                No downloads yet
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Top Clients */}
      <div className="card">
        <h3 className="text-lg font-semibold mb-4">Top Clients</h3>
        <div className="divider mb-4"></div>
        <div className="overflow-x-auto">
          {topClients.length > 0 ? (
            <table className="table-auto">
              <thead>
                <tr>
                  <th>Client IP</th>
                  <th>Downloads</th>
                  <th>Cache Hit</th>
                  <th>Cache Miss</th>
                  <th>Total</th>
                  <th>Hit Rate</th>
                </tr>
              </thead>
              <tbody>
                {topClients.map((client, idx) => (
                  <tr key={idx}>
                    <td className="font-mono">{client.clientIp}</td>
                    <td>{client.totalDownloads}</td>
                    <td className="text-success font-medium">
                      {formatBytes(client.totalCacheHitBytes)}
                    </td>
                    <td className="text-danger font-medium">
                      {formatBytes(client.totalCacheMissBytes)}
                    </td>
                    <td className="font-semibold">
                      {formatBytes(client.totalCacheHitBytes + client.totalCacheMissBytes)}
                    </td>
                    <td>
                      <div className="flex items-center gap-2">
                        <div className="progress w-20">
                          <div 
                            className="progress-bar progress-bar-success"
                            style={{
                              width: `${client.totalCacheHitBytes + client.totalCacheMissBytes > 0 ? 
                                (client.totalCacheHitBytes / (client.totalCacheHitBytes + client.totalCacheMissBytes)) * 100 : 0}%`
                            }}
                          ></div>
                        </div>
                        <span className="text-sm">
                          {client.totalCacheHitBytes + client.totalCacheMissBytes > 0 ? 
                            `${((client.totalCacheHitBytes / (client.totalCacheHitBytes + client.totalCacheMissBytes)) * 100).toFixed(1)}%` 
                            : '0%'}
                        </span>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="text-center py-8 text-muted">
              No client data available
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default Dashboard;