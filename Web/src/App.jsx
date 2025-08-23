import React, { useState, useEffect } from 'react';
import { 
  Home, Download, BarChart3, Settings, 
  Server, Wifi, WifiOff, Sun, Moon, Activity, Menu, X, Database
} from 'lucide-react';
import Dashboard from './components/Dashboard';
import Downloads from './components/Downloads';
import Statistics from './components/Statistics';
import Management from './components/Management';
import StatusIndicator from './components/StatusIndicator';
import SettingsModal from './components/SettingsModal';

function App() {
  const [activeTab, setActiveTab] = useState('dashboard');
  const [isMenuOpen, setIsMenuOpen] = useState(false);
  const [isOnline, setIsOnline] = useState(true);
  const [isDark, setIsDark] = useState(true);
  const [showSettings, setShowSettings] = useState(false);
  const [useMockData, setUseMockData] = useState(true);

  useEffect(() => {
    // Check mock data setting
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, []);

  useEffect(() => {
    // Check online status
    const checkOnline = () => {
      // If using mock data, always show as online
      if (useMockData) {
        setIsOnline(true);
        return;
      }
      
      fetch('/api/health')
        .then(() => setIsOnline(true))
        .catch(() => setIsOnline(false));
    };
    
    checkOnline();
    const interval = setInterval(checkOnline, 30000);
    return () => clearInterval(interval);
  }, [useMockData]);

  useEffect(() => {
    // Update dark mode class on body
    if (isDark) {
      document.body.classList.add('dark');
    } else {
      document.body.classList.remove('dark');
    }
  }, [isDark]);

  const navigation = [
    { id: 'dashboard', name: 'Dashboard', icon: Home },
    { id: 'downloads', name: 'Downloads', icon: Download },
    { id: 'statistics', name: 'Statistics', icon: BarChart3 },
    { id: 'management', name: 'Management', icon: Settings },
  ];

  const renderContent = () => {
    switch (activeTab) {
      case 'dashboard':
        return <Dashboard />;
      case 'downloads':
        return <Downloads />;
      case 'statistics':
        return <Statistics />;
      case 'management':
        return <Management />;
      default:
        return <Dashboard />;
    }
  };

  return (
    <div className="min-h-screen flex flex-col">
      {/* Navbar */}
      <nav className="navbar sticky top-0 z-50 backdrop-blur-md bg-white/70 dark:bg-dark-surface/70">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between h-16">
            {/* Logo and Brand */}
            <div className="flex items-center">
              <button
                onClick={() => setIsMenuOpen(!isMenuOpen)}
                className="sm:hidden p-2 rounded-md hover:bg-gray-100 dark:hover:bg-dark-border"
              >
                {isMenuOpen ? <X className="h-6 w-6" /> : <Menu className="h-6 w-6" />}
              </button>
              
              <div className="flex items-center gap-3 ml-2 sm:ml-0">
                <div className="relative">
                  <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-gradient-to-br from-primary to-secondary shadow-lg">
                    <Server className="h-6 w-6 text-white" />
                  </div>
                  {isOnline && (
                    <div className="absolute -bottom-1 -right-1 h-3 w-3 animate-pulse rounded-full border-2 border-white dark:border-dark-bg bg-success"></div>
                  )}
                </div>
                <div>
                  <p className="font-bold text-xl">LanCache Manager</p>
                  <p className="text-xs text-muted">Network Cache Monitor</p>
                </div>
              </div>
            </div>

            {/* Desktop Navigation */}
            <div className="hidden sm:flex items-center gap-2">
              {navigation.map((item) => {
                const Icon = item.icon;
                const isActive = activeTab === item.id;
                return (
                  <button
                    key={item.id}
                    onClick={() => setActiveTab(item.id)}
                    className={`btn btn-sm ${isActive ? 'btn-primary' : 'btn-ghost'}`}
                  >
                    <Icon className="h-4 w-4" />
                    {item.name}
                  </button>
                );
              })}
            </div>

            {/* Right side items */}
            <div className="flex items-center gap-3">
              {/* Data Mode Indicator */}
              {useMockData && (
                <button
                  onClick={() => setShowSettings(true)}
                  className="chip chip-warning hidden sm:flex cursor-pointer hover:opacity-80"
                  title="Click to change data source"
                >
                  <Database className="h-3 w-3" />
                  Mock Data
                </button>
              )}
              
              {/* Connection Status */}
              <div className={`chip ${isOnline ? 'chip-success' : 'chip-danger'} hidden sm:flex`}>
                {isOnline ? <Wifi className="h-3 w-3" /> : <WifiOff className="h-3 w-3" />}
                {isOnline ? 'Connected' : 'Offline'}
              </div>
              
              {/* Settings Button */}
              <button
                onClick={() => setShowSettings(true)}
                className="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-dark-border transition-colors"
                title="Settings"
              >
                <Settings className="h-5 w-5" />
              </button>
              
              {/* Dark mode toggle */}
              <button
                onClick={() => setIsDark(!isDark)}
                className="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-dark-border transition-colors"
                title={isDark ? 'Switch to light mode' : 'Switch to dark mode'}
              >
                {isDark ? <Sun className="h-5 w-5" /> : <Moon className="h-5 w-5" />}
              </button>
            </div>
          </div>
        </div>

        {/* Mobile menu */}
        {isMenuOpen && (
          <div className="sm:hidden border-t border-gray-200 dark:border-dark-border">
            <div className="px-2 pt-2 pb-3 space-y-1">
              {navigation.map((item) => {
                const Icon = item.icon;
                const isActive = activeTab === item.id;
                return (
                  <button
                    key={item.id}
                    onClick={() => {
                      setActiveTab(item.id);
                      setIsMenuOpen(false);
                    }}
                    className={`w-full flex items-center gap-3 px-3 py-2 rounded-md text-base font-medium ${
                      isActive 
                        ? 'bg-primary text-white' 
                        : 'hover:bg-gray-100 dark:hover:bg-dark-border'
                    }`}
                  >
                    <Icon className="h-5 w-5" />
                    {item.name}
                  </button>
                );
              })}
            </div>
            
            {/* Mobile status indicators */}
            <div className="px-2 pb-3 border-t border-gray-200 dark:border-dark-border pt-3">
              <div className="flex gap-2">
                {useMockData && (
                  <span className="chip chip-warning">
                    <Database className="h-3 w-3" />
                    Mock Data
                  </span>
                )}
                <span className={`chip ${isOnline ? 'chip-success' : 'chip-danger'}`}>
                  {isOnline ? <Wifi className="h-3 w-3" /> : <WifiOff className="h-3 w-3" />}
                  {isOnline ? 'Connected' : 'Offline'}
                </span>
              </div>
            </div>
          </div>
        )}
      </nav>

      {/* Main Content */}
      <main className="flex-1 container mx-auto max-w-7xl px-4 sm:px-6 lg:px-8 py-8">
        {renderContent()}
      </main>

      {/* Footer */}
      <footer className="divider mt-auto">
        <div className="container mx-auto max-w-7xl px-4 sm:px-6 lg:px-8 py-4">
          <div className="flex flex-col sm:flex-row justify-between items-center gap-4">
            <p className="text-sm text-muted">
              © 2025 LanCache Manager • Optimizing your network cache
            </p>
            <div className="flex items-center gap-6">
              <a
                href="https://lancache.net"
                target="_blank"
                rel="noopener noreferrer"
                className="text-sm text-muted hover:text-primary flex items-center gap-1"
              >
                <Activity className="h-3 w-3" />
                LanCache.NET
              </a>
              <a
                href="https://github.com/regix1/lancache-manager"
                target="_blank"
                rel="noopener noreferrer"
                className="text-sm text-muted hover:text-primary flex items-center gap-1"
              >
                <Server className="h-3 w-3" />
                GitHub
              </a>
            </div>
          </div>
        </div>
      </footer>

      {/* Status Indicator */}
      <StatusIndicator />
      
      {/* Settings Modal */}
      <SettingsModal 
        isOpen={showSettings} 
        onClose={() => setShowSettings(false)} 
      />
    </div>
  );
}

export default App;