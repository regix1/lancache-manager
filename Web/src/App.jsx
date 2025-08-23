import React, { useState, useEffect } from 'react';
import { Moon, Sun, RefreshCw, HardDrive } from 'lucide-react';
import * as signalR from '@microsoft/signalr';
import Dashboard from './components/Dashboard';
import LatestDownloads from './components/LatestDownloads';
import ClientStats from './components/ClientStats';
import ServiceStats from './components/ServiceStats';
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

  useEffect(() => {
    if (darkMode) {
      document.documentElement.classList.add('dark');
    } else {
      document.documentElement.classList.remove('dark');
    }
  }, [darkMode]);

  useEffect(() => {
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl('/downloadHub')
      .withAutomaticReconnect()
      .build();

    newConnection.start()
      .then(() => {
        console.log('Connected to SignalR hub');
        newConnection.on('DownloadUpdate', (download) => {
          setDownloads(prev => {
            const index = prev.findIndex(d => d.id === download.id);
            if (index >= 0) {
              const updated = [...prev];
              updated[index] = download;
              return updated;
            }
            return [download, ...prev].slice(0, 50);
          });
        });
      })
      .catch(err => console.error('SignalR Connection Error: ', err));

    setConnection(newConnection);
    loadData();

    return () => {
      newConnection.stop();
    };
  }, []);

  const loadData = async () => {
    setIsRefreshing(true);
    try {
      const [downloadsRes, clientRes, serviceRes, cacheRes] = await Promise.all([
        api.getLatestDownloads(50),
        api.getClientStats(),
        api.getServiceStats(),
        api.getCacheInfo()
      ]);

      setDownloads(downloadsRes);
      setClientStats(clientRes);
      setServiceStats(serviceRes);
      setCacheInfo(cacheRes);
    } catch (error) {
      console.error('Error loading data:', error);
    }
    setIsRefreshing(false);
  };

  const clearCache = async (service = null) => {
    if (!confirm(`Are you sure you want to clear ${service || 'all'} cache?`)) return;
    
    try {
      await api.clearCache(service);
      await loadData();
      alert('Cache cleared successfully');
    } catch (error) {
      alert('Failed to clear cache');
    }
  };

  const resetDatabase = async () => {
    if (!confirm('Are you sure you want to reset the database? This will delete all statistics.')) return;
    
    try {
      await api.resetDatabase();
      await loadData();
      alert('Database reset successfully');
    } catch (error) {
      alert('Failed to reset database');
    }
  };

  return (
    <div className={`min-h-screen ${darkMode ? 'bg-dark-bg text-white' : 'bg-gray-50 text-gray-900'}`}>
      <header className={`${darkMode ? 'bg-dark-surface border-dark-border' : 'bg-white border-gray-200'} border-b`}>
        <div className="container mx-auto px-4 py-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-4">
              <div className="flex items-center space-x-2">
                <HardDrive className="w-8 h-8 text-accent-blue" />
                <h1 className="text-2xl font-bold text-gradient">
                  LanCache Monitor
                </h1>
              </div>
              <nav className="flex space-x-1">
                {['dashboard', 'downloads', 'statistics', 'management'].map((tab) => (
                  <button
                    key={tab}
                    onClick={() => setActiveTab(tab)}
                    className={`px-4 py-2 rounded-lg capitalize transition-all ${
                      activeTab === tab
                        ? 'bg-accent-blue text-white'
                        : darkMode 
                          ? 'hover:bg-dark-border text-gray-300'
                          : 'hover:bg-gray-100 text-gray-600'
                    }`}
                  >
                    {tab}
                  </button>
                ))}
              </nav>
            </div>
            <div className="flex items-center space-x-3">
              <button
                onClick={loadData}
                disabled={isRefreshing}
                className={`p-2 rounded-lg transition-all ${
                  darkMode ? 'hover:bg-dark-border' : 'hover:bg-gray-100'
                } ${isRefreshing ? 'animate-spin' : ''}`}
              >
                <RefreshCw className="w-5 h-5" />
              </button>
              <button
                onClick={() => setDarkMode(!darkMode)}
                className={`p-2 rounded-lg transition-all ${
                  darkMode ? 'hover:bg-dark-border' : 'hover:bg-gray-100'
                }`}
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
            cacheInfo={cacheInfo}
            clientStats={clientStats}
            serviceStats={serviceStats}
            downloads={downloads}
            darkMode={darkMode}
          />
        )}
        {activeTab === 'downloads' && (
          <LatestDownloads 
            downloads={downloads}
            darkMode={darkMode}
          />
        )}
        {activeTab === 'statistics' && (
          <>
            <ClientStats 
              clientStats={clientStats}
              darkMode={darkMode}
            />
            <ServiceStats 
              serviceStats={serviceStats}
              darkMode={darkMode}
            />
          </>
        )}
        {activeTab === 'management' && (
          <Management
            cacheInfo={cacheInfo}
            connection={connection}
            darkMode={darkMode}
            clearCache={clearCache}
            resetDatabase={resetDatabase}
          />
        )}
      </main>
    </div>
  );
}