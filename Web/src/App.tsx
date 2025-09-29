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
import authService, { AuthMode } from '@services/auth.service';

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
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [checkingAuth, setCheckingAuth] = useState(true);
  const [showApiKeyRegenerationModal, setShowApiKeyRegenerationModal] = useState(false);
  const [wasGuestMode, setWasGuestMode] = useState(false);
  const [isUpgradingAuth, setIsUpgradingAuth] = useState(false);

  // Check authentication status first
  useEffect(() => {
    const checkAuthStatus = async () => {
      try {
        const authResult = await authService.checkAuth();
        setIsAuthenticated(authResult.isAuthenticated);
        setAuthMode(authResult.authMode);

        // Track if we started in guest mode
        if (authResult.authMode === 'guest') {
          setWasGuestMode(true);
        }
      } catch (error) {
        console.error('Failed to check auth status:', error);
        setIsAuthenticated(false);
        setAuthMode('unauthenticated');
      } finally {
        setCheckingAuth(false);
      }
    };

    checkAuthStatus();
  }, []);

  // Check authentication status periodically
  useEffect(() => {
    if (!checkingAuth) {
      const interval = setInterval(async () => {
        setIsAuthenticated(authService.isAuthenticated);
        setAuthMode(authService.authMode);

        // Track if we're in guest mode
        if (authService.authMode === 'guest') {
          setWasGuestMode(true);
        }

        // Re-check auth if in guest mode to get updated time
        if (authService.authMode === 'guest' || authService.authMode === 'expired') {
          const result = await authService.checkAuth();
          setAuthMode(result.authMode);
        }
      }, 1000);
      return () => clearInterval(interval);
    }
  }, [checkingAuth]);

  // Check if depot data exists (only after auth check is done)
  useEffect(() => {
    if (checkingAuth) {
      return; // Don't check depot status until auth check is complete
    }

    // Skip depot check for guest mode or users who were in guest mode
    if (authMode === 'guest' || wasGuestMode) {
      setDepotInitialized(true);
      setCheckingDepotStatus(false);
      return;
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
  }, [checkingAuth, depotInitialized, authMode, wasGuestMode]);

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

  const handleAuthChanged = async () => {
    // If upgrading from guest to authenticated, set flag
    if (authMode === 'guest' || wasGuestMode) {
      setIsUpgradingAuth(true);
    }

    // Small delay to ensure auth service state is fully updated
    await new Promise(resolve => setTimeout(resolve, 100));

    const authResult = await authService.checkAuth();
    setIsAuthenticated(authResult.isAuthenticated);
    setAuthMode(authResult.authMode);

    // Track if we're in guest mode
    if (authResult.authMode === 'guest') {
      setWasGuestMode(true);
    }

    // If we're now authenticated (upgraded from guest), skip depot check
    if (authResult.isAuthenticated && wasGuestMode) {
      setDepotInitialized(true);
    }

    // Clear upgrading flag after a delay
    if (authResult.isAuthenticated && wasGuestMode) {
      setTimeout(() => setIsUpgradingAuth(false), 500);
    } else {
      setIsUpgradingAuth(false);
    }
  };

  const handleApiKeyRegenerated = () => {
    // Set authentication to false and show the API key regeneration modal
    setIsAuthenticated(false);
    setShowApiKeyRegenerationModal(true);
  };

  const handleApiKeyRegenerationCompleted = () => {
    // Close the regeneration modal and update authentication status
    setShowApiKeyRegenerationModal(false);
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
        {activeTab === 'management' ? (
          <ManagementTab onApiKeyRegenerated={handleApiKeyRegenerated} />
        ) : (
          <TabComponent />
        )}
      </Suspense>
    );
  };

  // Show loading while checking auth or depot status (but skip depot check for guest/was guest)
  // Also skip during auth upgrade
  if (!isUpgradingAuth && (checkingAuth || (checkingDepotStatus && !wasGuestMode && authMode !== 'guest'))) {
    return (
      <div className="min-h-screen flex items-center justify-center"
           style={{ backgroundColor: 'var(--theme-bg-primary)' }}>
        <LoadingSpinner fullScreen={false} message={checkingAuth ? "Checking authentication..." : "Checking depot initialization status..."} />
      </div>
    );
  }

  // Show API key regeneration modal if needed
  if (showApiKeyRegenerationModal) {
    return (
      <>
        <DepotInitializationModal
          onInitialized={handleApiKeyRegenerationCompleted}
          isAuthenticated={false}
          onAuthChanged={handleAuthChanged}
          apiKeyOnlyMode={true}
        />
      </>
    );
  }

  // Check if user is authenticated or in guest mode
  const hasAccess = isAuthenticated || authMode === 'guest';

  // Show initialization modal only if not authenticated AND not in guest mode AND not expired
  // AND never show for users who were in guest mode or during upgrade
  if (!hasAccess && authMode !== 'expired' && !wasGuestMode && !isUpgradingAuth) {
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

  // If guest session expired, show modal to re-authenticate (but not during upgrade)
  if (authMode === 'expired' && !isUpgradingAuth) {
    return (
      <>
        <DepotInitializationModal
          onInitialized={handleApiKeyRegenerationCompleted}
          isAuthenticated={false}
          onAuthChanged={handleAuthChanged}
          apiKeyOnlyMode={true}
        />
      </>
    );
  }

  // Show initialization modal if depot data doesn't exist (after authentication) - but skip for guest mode or if user was ever in guest mode or during upgrade
  if (!depotInitialized && authMode === 'authenticated' && !wasGuestMode && !isUpgradingAuth) {
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
