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

  useEffect(() => {
    loadDashboardData();
    setupSignalR();
    const interval = setInterval(loadDashboardData, 30000);
    return () => clearInterval(interval);
  }, []);

  const loadDashboardData = async () => {
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
  };

  const setupSignalR = async () => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("/downloadHub")
      .withAutomaticReconnect()
      .build();

    connection.on("DownloadUpdate", () => {
      loadDashboardData();
    });

    try {
      await connection.start();
    } catch (err) {
      console.error("SignalR Connection Error: ", err);
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
    <div className="p-6 space-y-6">
      {/* Stats Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-gray-500 dark:text-gray-400">Total Cache</span>
            <HardDrive className="w-5 h-5 text-blue-500" />
          </div>
          <div className="text-2xl font-bold">{formatBytes(stats.totalCache)}</div>
          <div className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            {stats.totalFiles.toLocaleString()} files
          </div>
        </div>

        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-gray-500 dark:text-gray-400">Used Space</span>
            <Server className="w-5 h-5 text-green-500" />
          </div>
          <div className="text-2xl font-bold">{formatBytes(stats.usedSpace)}</div>
          <div className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            {((stats.usedSpace / stats.totalCache) * 100).toFixed(1)}% utilized
          </div>
        </div>

        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-gray-500 dark:text-gray-400">Active Downloads</span>
            <Download className="w-5 h-5 text-purple-500" />
          </div>
          <div className="text-2xl font-bold">{stats.activeDownloads}</div>
          <div className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            {stats.activeDownloads === 1 ? '1 download' : `${stats.activeDownloads} total`}
          </div>
        </div>

        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm text-gray-500 dark:text-gray-400">Active Clients</span>
            <Users className="w-5 h-5 text-yellow-500" />
          </div>
          <div className="text-2xl font-bold">{stats.activeClients}</div>
          <div className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            {stats.activeClients === 0 ? '0 services' : `${serviceStats.length} services`}
          </div>
        </div>
      </div>

      {/* Charts Row */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Service Distribution */}
        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <h3 className="text-lg font-semibold mb-4">Service Distribution</h3>
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
              <div className="text-gray-500 dark:text-gray-400">No data available</div>
            )}
          </div>
        </div>

        {/* Recent Downloads */}
        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <h3 className="text-lg font-semibold mb-4">Recent Downloads</h3>
          <div className="space-y-3">
            {recentDownloads.length > 0 ? (
              recentDownloads.map((download, idx) => (
                <div key={idx} className="flex items-center justify-between py-2 border-b border-gray-200 dark:border-gray-700 last:border-0">
                  <div className="flex items-center gap-3">
                    <div className="w-2 h-2 bg-purple-500 rounded-full"></div>
                    <div>
                      <div className="font-medium uppercase text-sm">{download.service}</div>
                      <div className="text-xs text-gray-500 dark:text-gray-400">{download.clientIp}</div>
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="font-medium text-sm">
                      {formatBytes(download.cacheHitBytes + download.cacheMissBytes)}
                    </div>
                    <div className="text-xs text-green-500">
                      {download.cacheHitBytes > 0 ? 
                        `${((download.cacheHitBytes / (download.cacheHitBytes + download.cacheMissBytes)) * 100).toFixed(0)}%` 
                        : '0%'} hit
                    </div>
                  </div>
                </div>
              ))
            ) : (
              <div className="text-center py-8 text-gray-500 dark:text-gray-400">
                No downloads yet
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Top Clients */}
      <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
        <h3 className="text-lg font-semibold mb-4">Top Clients</h3>
        <div className="overflow-x-auto">
          {topClients.length > 0 ? (
            <table className="w-full">
              <thead>
                <tr className="text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase">
                  <th className="pb-3">Client IP</th>
                  <th className="pb-3">Downloads</th>
                  <th className="pb-3">Cache Hit</th>
                  <th className="pb-3">Cache Miss</th>
                  <th className="pb-3">Total</th>
                  <th className="pb-3">Hit Rate</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
                {topClients.map((client, idx) => (
                  <tr key={idx}>
                    <td className="py-3 font-mono text-sm">{client.clientIp}</td>
                    <td className="py-3">{client.totalDownloads}</td>
                    <td className="py-3 text-green-600 dark:text-green-400">
                      {formatBytes(client.totalCacheHitBytes)}
                    </td>
                    <td className="py-3 text-red-600 dark:text-red-400">
                      {formatBytes(client.totalCacheMissBytes)}
                    </td>
                    <td className="py-3 font-semibold">
                      {formatBytes(client.totalCacheHitBytes + client.totalCacheMissBytes)}
                    </td>
                    <td className="py-3">
                      <div className="flex items-center gap-2">
                        <div className="w-16 bg-gray-200 dark:bg-gray-600 rounded-full h-2">
                          <div 
                            className="bg-green-500 h-2 rounded-full"
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
            <div className="text-center py-8 text-gray-500 dark:text-gray-400">
              No client data available
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default Dashboard;