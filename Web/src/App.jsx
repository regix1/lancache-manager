import React, { useState, useEffect } from 'react';
import { 
  Home, Download, BarChart3, Settings, Menu, X, Sun, Moon,
  Server, Wifi, WifiOff, Activity, Shield, ChevronRight
} from 'lucide-react';
import Dashboard from './components/Dashboard';
import Downloads from './components/Downloads';
import Statistics from './components/Statistics';
import Management from './components/Management';
import StatusIndicator from './components/StatusIndicator';

function App() {
  const [darkMode, setDarkMode] = useState(() => {
    // Check localStorage or system preference
    if (localStorage.theme === 'dark' || 
        (!('theme' in localStorage) && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
      return true;
    }
    return false;
  });
  
  const [activeTab, setActiveTab] = useState('dashboard');
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const [isOnline, setIsOnline] = useState(true);

  useEffect(() => {
    // Apply dark mode class to HTML element
    if (darkMode) {
      document.documentElement.classList.add('dark');
      localStorage.theme = 'dark';
    } else {
      document.documentElement.classList.remove('dark');
      localStorage.theme = 'light';
    }
  }, [darkMode]);

  useEffect(() => {
    // Check online status
    const checkOnline = () => {
      fetch('/api/health')
        .then(() => setIsOnline(true))
        .catch(() => setIsOnline(false));
    };
    
    checkOnline();
    const interval = setInterval(checkOnline, 30000);
    return () => clearInterval(interval);
  }, []);

  const toggleDarkMode = () => {
    setDarkMode(!darkMode);
  };

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

  const handleTabChange = (tabId) => {
    setActiveTab(tabId);
    setMobileMenuOpen(false);
  };

  return (
    <div className="min-h-screen bg-gradient-to-br from-gray-50 to-gray-100 dark:from-gray-900 dark:to-gray-800">
      {/* Header */}
      <header className="sticky top-0 z-40 border-b border-gray-200 bg-white/80 backdrop-blur-lg dark:border-gray-700 dark:bg-gray-900/80">
        <div className="mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
          <div className="flex h-16 items-center justify-between">
            {/* Logo and Title */}
            <div className="flex items-center">
              {/* Mobile menu button */}
              <button
                type="button"
                onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
                className="inline-flex items-center justify-center rounded-lg p-2 text-gray-700 transition-colors hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-800 md:hidden"
                aria-label="Toggle menu"
              >
                {mobileMenuOpen ? (
                  <X className="h-6 w-6" />
                ) : (
                  <Menu className="h-6 w-6" />
                )}
              </button>
              
              {/* Logo */}
              <div className="ml-3 flex items-center md:ml-0">
                <div className="relative">
                  <div className="mr-3 flex h-10 w-10 items-center justify-center rounded-xl bg-gradient-to-br from-blue-500 to-purple-600 shadow-lg">
                    <Server className="h-6 w-6 text-white" />
                  </div>
                  {isOnline && (
                    <div className="absolute -bottom-1 -right-1 h-3 w-3 animate-pulse rounded-full border-2 border-white bg-green-500 dark:border-gray-900"></div>
                  )}
                </div>
                <div>
                  <h1 className="bg-gradient-to-r from-blue-600 to-purple-600 bg-clip-text text-xl font-bold text-transparent dark:from-blue-400 dark:to-purple-400">
                    LanCache Manager
                  </h1>
                  <p className="text-xs text-gray-500 dark:text-gray-400">Network Cache Monitor</p>
                </div>
              </div>
            </div>

            {/* Desktop Navigation */}
            <nav className="hidden items-center space-x-1 md:flex">
              {navigation.map((item) => {
                const Icon = item.icon;
                const isActive = activeTab === item.id;
                return (
                  <button
                    key={item.id}
                    onClick={() => handleTabChange(item.id)}
                    className={`
                      group relative flex items-center gap-2 rounded-lg px-4 py-2 font-medium transition-all duration-200
                      ${isActive 
                        ? 'scale-105 bg-gradient-to-r from-blue-500 to-purple-600 text-white shadow-lg' 
                        : 'text-gray-700 hover:scale-105 hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-800'
                      }
                    `}
                  >
                    <Icon className="h-4 w-4" />
                    <span>{item.name}</span>
                    {isActive && (
                      <div className="absolute inset-0 animate-pulse rounded-lg bg-gradient-to-r from-blue-400 to-purple-500 opacity-20"></div>
                    )}
                  </button>
                );
              })}
            </nav>

            {/* Right side controls */}
            <div className="flex items-center gap-3">
              {/* Connection Status */}
              <div className={`
                hidden items-center gap-2 rounded-full px-3 py-1.5 transition-colors sm:flex
                ${isOnline 
                  ? 'bg-green-100 dark:bg-green-900/30' 
                  : 'bg-red-100 dark:bg-red-900/30'
                }
              `}>
                {isOnline ? (
                  <>
                    <Wifi className="h-4 w-4 text-green-600 dark:text-green-400" />
                    <span className="text-xs font-medium text-green-700 dark:text-green-300">Connected</span>
                    <div className="h-2 w-2 animate-pulse rounded-full bg-green-500"></div>
                  </>
                ) : (
                  <>
                    <WifiOff className="h-4 w-4 text-red-600 dark:text-red-400" />
                    <span className="text-xs font-medium text-red-700 dark:text-red-300">Offline</span>
                  </>
                )}
              </div>

              {/* Dark mode toggle */}
              <button
                type="button"
                onClick={toggleDarkMode}
                className="rounded-lg bg-gray-100 p-2 text-gray-700 transition-all hover:scale-110 hover:bg-gray-200 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700"
                aria-label="Toggle dark mode"
              >
                {darkMode ? (
                  <Sun className="h-5 w-5" />
                ) : (
                  <Moon className="h-5 w-5" />
                )}
              </button>
            </div>
          </div>
        </div>

        {/* Mobile Navigation Menu */}
        <div className={`
          overflow-hidden transition-all duration-300 md:hidden
          ${mobileMenuOpen ? 'max-h-64' : 'max-h-0'}
        `}>
          <div className="space-y-1 border-t border-gray-200 bg-white px-2 pb-3 pt-2 dark:border-gray-700 dark:bg-gray-900">
            {navigation.map((item) => {
              const Icon = item.icon;
              const isActive = activeTab === item.id;
              return (
                <button
                  key={item.id}
                  onClick={() => handleTabChange(item.id)}
                  className={`
                    flex w-full items-center gap-3 rounded-lg px-3 py-2 font-medium transition-all
                    ${isActive 
                      ? 'bg-gradient-to-r from-blue-500 to-purple-600 text-white shadow-lg' 
                      : 'text-gray-700 hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-800'
                    }
                  `}
                >
                  <Icon className="h-5 w-5" />
                  <span>{item.name}</span>
                  {isActive && <ChevronRight className="ml-auto h-4 w-4" />}
                </button>
              );
            })}
          </div>
        </div>
      </header>

      {/* Main Content Area */}
      <main className="flex-1">
        <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6 lg:px-8">
          {/* Content with fade-in animation */}
          <div className="animate-in fade-in duration-500">
            {renderContent()}
          </div>
        </div>
      </main>

      {/* Footer */}
      <footer className="mt-8 border-t border-gray-200 bg-white/80 backdrop-blur-lg dark:border-gray-700 dark:bg-gray-900/80">
        <div className="mx-auto max-w-7xl px-4 py-4 sm:px-6 lg:px-8">
          <div className="flex flex-col items-center justify-between gap-4 sm:flex-row">
            <div className="flex items-center gap-2">
              <Shield className="h-4 w-4 text-gray-400" />
              <p className="text-sm text-gray-500 dark:text-gray-400">
                © 2024 LanCache Manager • Optimizing your network cache
              </p>
            </div>
            <div className="flex items-center gap-6 text-sm">
              <a 
                href="https://lancache.net" 
                target="_blank" 
                rel="noopener noreferrer"
                className="flex items-center gap-1 text-gray-500 transition-colors hover:text-blue-500 dark:text-gray-400 dark:hover:text-blue-400"
              >
                <Activity className="h-3 w-3" />
                LanCache.NET
              </a>
              <a 
                href="https://github.com/regix1/lancache-manager" 
                target="_blank" 
                rel="noopener noreferrer"
                className="flex items-center gap-1 text-gray-500 transition-colors hover:text-blue-500 dark:text-gray-400 dark:hover:text-blue-400"
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
    </div>
  );
}

export default App;