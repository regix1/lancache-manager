import React, { useState, useEffect } from 'react';
import { Moon, Sun, RefreshCw, HardDrive, Download, Users, Activity, Settings } from 'lucide-react';
import * as signalR from '@microsoft/signalr';
import clsx from 'clsx';
import Dashboard from './components/Dashboard';
import Downloads from './components/Downloads';
import Statistics from './components/Statistics';
import Management from './components/Management';
import api from './services/api';

export default function App() {
  const [darkMode, setDarkMode] = useState(true);
  const [activeTab, setActiveTab] = useState('dashboard');
  const [downloads, setDownloads] = useState([]);
  const [clientStats, setClientStats] = useState([]);
  const [serviceStats, setServiceStats] = useState([]);
  const [cacheInfo, setCacheInfo] = useState(null);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [connection, setConnection] = useState(null);
  const [connectionStatus, setConnectionStatus] = useState('disconnected');

  useEffect(() => {
    document.documentElement.classList.toggle('dark', darkMode);
  }, [darkMode]);

  useEffect(() => {
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl('/downloadHub')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build();

    newConnection.onreconnecting(() => {
      setConnectionStatus('reconnecting');
    });

    newConnection.onreconnected(() => {
      setConnectionStatus('connected');
    });

    newConnection.onclose(() => {
      setConnectionStatus('disconnected');
    });

    const startConnection = async () => {
      try {
        await newConnection.start();
        console.log('Connected to SignalR hub');
        setConnectionStatus('connected');
        
        newConnection.on('DownloadUpdate', (download) => {
          setDownloads(prev => {
            const index = prev.findIndex(d => d.id === download.id);
            if (index >= 0) {
              const updated = [...prev];
              updated[index] = download;
              return updated.sort((a, b) => new Date(b.startTime) - new Date(a.startTime));
            }
            return [download, ...prev].slice(0, 100);
          });
        });
      } catch (err) {
        console.error('SignalR Connection Error:', err);
        setConnectionStatus('disconnected');
      }
    };

    startConnection();
    setConnection(newConnection);
    loadData();

    return () => {
      newConnection.stop();
    };
  }, []);

  const loadData = async () => {
    setIsRefreshing(true);
    try {
      const [downloadsData, clientData, serviceData, cacheData] = await Promise.all([
        api.getLatestDownloads(100),
        api.getClientStats(),
        api.getServiceStats(),
        api.getCacheInfo()
      ]);
      setDownloads(downloadsData || []);
      setClientStats(clientData || []);
      setServiceStats(serviceData || []);
      setCacheInfo(cacheData || {});
    } catch (error) {
      console.error('Error loading data:', error);
    } finally {
      setIsRefreshing(false);
    }
  };

  const tabs = [
    { id: 'dashboard', label: 'Dashboard', icon: HardDrive },
    { id: 'downloads', label: 'Downloads', icon: Download },
    { id: 'statistics', label: 'Statistics', icon: Activity },
    { id: 'management', label: 'Management', icon: Settings },
  ];

  return (
    <div className={clsx(
      'min-h-screen transition-colors duration-200',
      darkMode ? 'bg-gray-900 text-gray-100' : 'bg-gray-50 text-gray-900'
    )}>
      <header className={clsx(
        'border-b transition-colors duration-200',
        darkMode ? 'bg-gray-800 border-gray-700' : 'bg-white border-gray-200'
      )}>
        <div className="container mx-auto px-4 py-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-6">
              <div className="flex items-center gap-2">
                <HardDrive className="w-8 h-8 text-blue-500" />
                <h1 className="text-2xl font-bold">LanCache Manager</h1>
              </div>
              <nav className="flex gap-1">
                {tabs.map(tab => {
                  const Icon = tab.icon;
                  return (
                    <button
                      key={tab.id}
                      onClick={() => setActiveTab(tab.id)}
                      className={clsx(
                        'px-4 py-2 rounded-lg flex items-center gap-2 transition-all duration-200',
                        activeTab === tab.id
                          ? 'bg-blue-500 text-white'
                          : darkMode
                          ? 'hover:bg-gray-700 text-gray-300'
                          : 'hover:bg-gray-100 text-gray-600'
                      )}
                    >
                      <Icon className="w-4 h-4" />
                      <span>{tab.label}</span>
                    </button>
                  );
                })}
              </nav>
            </div>
            <div className="flex items-center gap-3">
              <div className={clsx(
                'w-2 h-2 rounded-full',
                connectionStatus === 'connected' ? 'bg-green-500' :
                connectionStatus === 'reconnecting' ? 'bg-yellow-500 animate-pulse' :
                'bg-red-500'
              )} />
              <button
                onClick={loadData}
                disabled={isRefreshing}
                className={clsx(
                  'p-2 rounded-lg transition-all duration-200',
                  darkMode ? 'hover:bg-gray-700' : 'hover:bg-gray-100',
                  isRefreshing && 'animate-spin'
                )}
              >
                <RefreshCw className="w-5 h-5" />
              </button>
              <button
                onClick={() => setDarkMode(!darkMode)}
                className={clsx(
                  'p-2 rounded-lg transition-all duration-200',
                  darkMode ? 'hover:bg-gray-700' : 'hover:bg-gray-100'
                )}
              >
                {darkMode ? <Sun className="w-5 h-5" /> : <Moon className="w-5 h-5" />}
              </button>
            </div>
          </div>
        </div>
      </header>

      <main className="container mx-auto px-4 py-6">
        {activeTab === 'dashboard' && (
          <Dashboard
            downloads={downloads}
            clientStats={clientStats}
            serviceStats={serviceStats}
            cacheInfo={cacheInfo}
            darkMode={darkMode}
          />
        )}
        {activeTab === 'downloads' && (
          <Downloads downloads={downloads} darkMode={darkMode} />
        )}
        {activeTab === 'statistics' && (
          <Statistics clientStats={clientStats} serviceStats={serviceStats} darkMode={darkMode} />
        )}
        {activeTab === 'management' && (
          <Management cacheInfo={cacheInfo} darkMode={darkMode} onRefresh={loadData} />
        )}
      </main>
    </div>
  );
}