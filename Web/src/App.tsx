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
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [checkingAuth, setCheckingAuth] = useState(true);

  // Check authentication status first
  useEffect(() => {
    const checkAuthStatus = async () => {
      try {
        const authResult = await authService.checkAuth();
        setIsAuthenticated(authResult.isAuthenticated);
      } catch (error) {
        console.error('Failed to check auth status:', error);
        setIsAuthenticated(false);
      } finally {
        setCheckingAuth(false);
      }
    };

    checkAuthStatus();
  }, []);

  // Check authentication status periodically
  useEffect(() => {
    if (!checkingAuth) {
      const interval = setInterval(() => {
        setIsAuthenticated(authService.isAuthenticated);
      }, 1000);
      return () => clearInterval(interval);
    }
  }, [checkingAuth]);

  // Check if depot data exists (only after auth check is done)
  useEffect(() => {
    if (checkingAuth) {
      return; // Don't check depot status until auth check is complete
    }

    const checkDepotStatus = async () => {
      try {
        const response = await fetch('/api/gameinfo/pics-status', {
          headers: ApiService.getHeaders()
        });
        if (response.ok) {
          const data = await response.json();
          // Consider initialized if we have database mappings, SteamKit2 is ready, or rebuild is running
          const hasData = (data.database?.totalMappings > 0) ||
                         (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0) ||
                         (data.steamKit2?.isRebuildRunning === true);
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

    // Also recheck when window regains focus (in case of external changes)
    const handleFocus = () => {
      if (depotInitialized !== null) {
        checkDepotStatus();
      }
    };

    window.addEventListener('focus', handleFocus);
    return () => window.removeEventListener('focus', handleFocus);
  }, [checkingAuth, depotInitialized]);

  const handleDepotInitialized = async () => {
    // Double-check that depot is actually initialized before updating state
    try {
      const response = await fetch('/api/gameinfo/pics-status', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        const hasData = (data.database?.totalMappings > 0) ||
                       (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0) ||
                       (data.steamKit2?.isRebuildRunning === true);
        if (hasData) {
          setDepotInitialized(true);
        } else {
          console.warn('Depot initialization reported complete but no data found');
          // Keep showing initialization modal
        }
      }
    } catch (error) {
      console.error('Error verifying depot initialization:', error);
    }
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

  // Show loading while checking auth or depot status
  if (checkingAuth || checkingDepotStatus) {
    return (
      <div className="min-h-screen flex items-center justify-center"
           style={{ backgroundColor: 'var(--theme-bg-primary)' }}>
        <LoadingSpinner fullScreen={false} message={checkingAuth ? "Checking authentication..." : "Checking depot initialization status..."} />
      </div>
    );
  }

  // Check if user is authenticated first
  if (!isAuthenticated) {
    // Show initialization modal with auth form
    return (
      <>
        <DepotInitializationModal
          onInitialized={handleDepotInitialized}
          isAuthenticated={false}
          onAuthChanged={handleAuthChanged}
        />
      </>
    );
  }

  // Show initialization modal if depot data doesn't exist (after authentication)
  if (!depotInitialized) {
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
        opacity: !depotInitialized ? 0.5 : 1,
        pointerEvents: !depotInitialized ? 'none' : 'auto'
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
