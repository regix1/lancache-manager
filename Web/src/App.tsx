import React, { useState, Suspense, lazy, useEffect } from 'react';
import { DataProvider, useData } from '@contexts/DataContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import Header from '@components/layout/Header';
import Navigation from '@components/layout/Navigation';
import ErrorBoundary from '@components/common/ErrorBoundary';
import LoadingSpinner from '@components/common/LoadingSpinner';
import PicsProgressBar from '@components/common/PicsProgressBar';
import DepotInitializationModal from '@components/initialization/DepotInitializationModal';
import ApiService from '@services/api.service';
import authService from '@services/auth.service';

// Lazy load heavy components
const Dashboard = lazy(() => import('@components/dashboard/Dashboard'));
const DownloadsTab = lazy(() => import('@components/downloads/DownloadsTab'));
const ClientsTab = lazy(() => import('@components/clients/ClientsTab'));
const ServicesTab = lazy(() => import('@components/services/ServicesTab'));
const ManagementTab = lazy(() => import('@components/management/ManagementTab'));

const AppContent: React.FC = () => {
  const [activeTab, setActiveTab] = useState('dashboard');
  const { connectionStatus } = useData();
  const [depotInitialized, setDepotInitialized] = useState<boolean | null>(null);
  const [checkingDepotStatus, setCheckingDepotStatus] = useState(true);
  const [isAuthenticated, setIsAuthenticated] = useState(authService.isAuthenticated);

  // Check authentication status periodically
  useEffect(() => {
    const checkAuth = () => {
      setIsAuthenticated(authService.isAuthenticated);
    };

    checkAuth();
    const interval = setInterval(checkAuth, 1000); // Check every second

    return () => clearInterval(interval);
  }, []);

  // Check if depot data exists
  useEffect(() => {
    const checkDepotStatus = async () => {
      try {
        const response = await fetch('/api/gameinfo/pics-status', {
          headers: ApiService.getHeaders()
        });
        if (response.ok) {
          const data = await response.json();
          // Consider initialized if we have database mappings or SteamKit2 is ready with mappings
          const hasData = (data.database?.totalMappings > 0) ||
                         (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);
          setDepotInitialized(hasData);
        } else {
          // If we can't check status, assume not initialized for safety
          setDepotInitialized(false);
        }
      } catch (error) {
        console.error('Failed to check depot initialization status:', error);
        setDepotInitialized(false);
      } finally {
        setCheckingDepotStatus(false);
      }
    };

    checkDepotStatus();
  }, []);

  const handleDepotInitialized = () => {
    setDepotInitialized(true);
    // No need to refresh the page - just update state and let React re-render
  };

  const handleAuthChanged = () => {
    setIsAuthenticated(authService.isAuthenticated);
  };

  const renderContent = () => {
    const TabComponent = (() => {
      switch (activeTab) {
        case 'dashboard':
          return Dashboard;
        case 'downloads':
          return DownloadsTab;
        case 'clients':
          return ClientsTab;
        case 'services':
          return ServicesTab;
        case 'management':
          return ManagementTab;
        default:
          return Dashboard;
      }
    })();

    return (
      <Suspense fallback={<LoadingSpinner fullScreen={false} message="Loading..." />}>
        <TabComponent />
      </Suspense>
    );
  };

  // Show loading while checking depot status
  if (checkingDepotStatus) {
    return (
      <div className="min-h-screen flex items-center justify-center"
           style={{ backgroundColor: 'var(--theme-bg-primary)' }}>
        <LoadingSpinner fullScreen={false} message="Checking depot initialization status..." />
      </div>
    );
  }

  // Show initialization modal if depot data doesn't exist
  if (depotInitialized === false) {
    return (
      <>
        <DepotInitializationModal
          onInitialized={handleDepotInitialized}
          isAuthenticated={isAuthenticated}
          onAuthChanged={handleAuthChanged}
        />
      </>
    );
  }

  return (
    <div
      className="min-h-screen"
      style={{
        backgroundColor: 'var(--theme-bg-primary)',
        color: 'var(--theme-text-primary)',
        opacity: depotInitialized === false ? 0.5 : 1,
        pointerEvents: depotInitialized === false ? 'none' : 'auto'
      }}
    >
      <Header connectionStatus={connectionStatus as 'connected' | 'disconnected' | 'reconnecting'} />
      <Navigation activeTab={activeTab} setActiveTab={setActiveTab} />
      <PicsProgressBar />
      <main className="container mx-auto px-4 py-6">{renderContent()}</main>
    </div>
  );
};

const App: React.FC = () => {
  return (
    <ErrorBoundary>
      <TimeFilterProvider>
        <DataProvider>
          <AppContent />
        </DataProvider>
      </TimeFilterProvider>
    </ErrorBoundary>
  );
};

export default App;
