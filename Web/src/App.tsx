import React, { useState, Suspense, lazy, useEffect } from 'react';
import { DataProvider, useData } from '@contexts/DataContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import { PollingRateProvider } from '@contexts/PollingRateContext';
import Header from '@components/layout/Header';
import Navigation from '@components/layout/Navigation';
import Footer from '@components/layout/Footer';
import ErrorBoundary from '@components/common/ErrorBoundary';
import LoadingSpinner from '@components/common/LoadingSpinner';
import PicsProgressBar from '@components/common/PicsProgressBar';
import DepotInitializationModal from '@components/initialization/DepotInitializationModal';
import ApiService from '@services/api.service';
import authService, { AuthMode } from '@services/auth.service';
import { setServerTimezone } from '@utils/timezone';

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
  const [, setWasGuestMode] = useState(false);
  const [isUpgradingAuth, setIsUpgradingAuth] = useState(false);
  const [isInitializationFlowActive, setIsInitializationFlowActive] = useState(() => {
    // Initialize from localStorage to survive page reloads
    const stored = localStorage.getItem('initializationFlowActive');
    return stored === 'true';
  });
  const [setupCompleted, setSetupCompleted] = useState<boolean | null>(null);
  const [checkingSetupStatus, setCheckingSetupStatus] = useState(true);

  // Fetch server timezone on mount
  useEffect(() => {
    const fetchTimezone = async () => {
      try {
        const config = await ApiService.getConfig();
        if (config.timezone) {
          setServerTimezone(config.timezone);
        }
      } catch (error) {
        console.error('Failed to fetch server timezone:', error);
      }
    };

    fetchTimezone();
  }, []);

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

        // Clear stale initialization state if user is not authenticated
        // This handles the case where user closed browser mid-setup and comes back later
        if (!authResult.isAuthenticated && authResult.authMode !== 'guest') {
          const storedStep = localStorage.getItem('initializationCurrentStep');
          if (storedStep && storedStep !== 'api-key') {
            console.log('[App] Clearing stale initialization state (not authenticated)');
            localStorage.removeItem('initializationCurrentStep');
            localStorage.removeItem('initializationInProgress');
            localStorage.removeItem('initializationMethod');
            localStorage.removeItem('initializationDownloadStatus');
            localStorage.removeItem('initializationFlowActive');
            setIsInitializationFlowActive(false);
          }
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

  // Check setup completion status (only after auth check is done)
  useEffect(() => {
    if (checkingAuth) {
      return; // Don't check setup status until auth check is complete
    }

    // Skip setup check for guest mode
    if (authMode === 'guest') {
      setSetupCompleted(true);
      setCheckingSetupStatus(false);
      return;
    }

    const checkSetupCompletionStatus = async () => {
      try {
        const response = await fetch('/api/management/setup-status');
        if (response.ok) {
          const data = await response.json();
          setSetupCompleted(data.isCompleted === true);
        } else {
          setSetupCompleted(false);
        }
      } catch (error) {
        console.error('Failed to check setup completion status:', error);
        setSetupCompleted(false);
      } finally {
        setCheckingSetupStatus(false);
      }
    };

    checkSetupCompletionStatus();
  }, [checkingAuth, authMode]);

  // Check if depot data exists (only after auth check is done)
  useEffect(() => {
    if (checkingAuth) {
      return; // Don't check depot status until auth check is complete
    }

    // Only skip depot check for guest mode
    // Don't skip for users who WERE in guest mode - they need to see depot setup after auth
    if (authMode === 'guest') {
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
          // Consider initialized if we have database mappings or SteamKit2 is ready with depots
          const hasData = (data.database?.totalMappings > 0) ||
                         (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);

          // Don't clear initialization flag just because we have some depot data
          // The user might be in the middle of setup (e.g., on step 4 after completing step 2)
          // Only clear the flag when initialization actually completes (handled in modal's onInitialized)

          setDepotInitialized(hasData);
        } else {
          // If we can't check status, assume not initialized for safety
          if (!isInitializationFlowActive) {
            setDepotInitialized(false);
          }
        }
      } catch (error) {
        console.error('Failed to check depot initialization status:', error);
        if (!isInitializationFlowActive) {
          setDepotInitialized(false);
        }
      } finally {
        setCheckingDepotStatus(false);
      }
    };

    checkDepotStatus();

    // Also recheck when window regains focus (in case of external changes)
    const handleFocus = () => {
      if (depotInitialized !== null && !isInitializationFlowActive) {
        checkDepotStatus();
      }
    };

    window.addEventListener('focus', handleFocus);
    return () => window.removeEventListener('focus', handleFocus);
  }, [checkingAuth, authMode, isInitializationFlowActive]); // Added isInitializationFlowActive to dependencies

  const handleDepotInitialized = async () => {
    // Initialization flow is complete
    setIsInitializationFlowActive(false);
    localStorage.removeItem('initializationFlowActive');

    // Mark setup as completed
    setSetupCompleted(true);

    // Double-check that depot is actually initialized before updating state
    try {
      const response = await fetch('/api/gameinfo/pics-status', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        const hasData = (data.database?.totalMappings > 0) ||
                       (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);
        if (hasData) {
          setDepotInitialized(true);
        } else {
          console.warn('Depot initialization reported complete but no data found');
          // Don't keep showing initialization modal if setup is completed
          // User may have skipped data steps, which is fine
          setDepotInitialized(true);
        }
      }
    } catch (error) {
      console.error('Error verifying depot initialization:', error);
    }
  };

  const handleAuthChanged = async () => {
    // If upgrading from guest to authenticated
    const wasGuest = authMode === 'guest';
    if (wasGuest) {
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

    // If we're now authenticated (upgraded from guest), we need to check depot status
    if (authResult.isAuthenticated && wasGuest) {
      // Reset depot initialized state so the check will run
      setDepotInitialized(null);
      setCheckingDepotStatus(true);

      // Force check depot status now (but respect initialization flow)
      try {
        const response = await fetch('/api/gameinfo/pics-status', {
          headers: ApiService.getHeaders()
        });
        if (response.ok) {
          const data = await response.json();
          const hasData = (data.database?.totalMappings > 0) ||
                         (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);

          // Only update if we're not in the middle of initialization flow
          if (!isInitializationFlowActive) {
            setDepotInitialized(hasData);
          }
        } else {
          if (!isInitializationFlowActive) {
            setDepotInitialized(false);
          }
        }
      } catch (error) {
        console.error('Failed to check depot initialization status:', error);
        if (!isInitializationFlowActive) {
          setDepotInitialized(false);
        }
      } finally {
        setCheckingDepotStatus(false);
      }
    }

    // Clear upgrading flag
    setIsUpgradingAuth(false);
  };

  const handleApiKeyRegenerated = () => {
    // Set authentication to false and show the API key regeneration modal
    setIsAuthenticated(false);
    setShowApiKeyRegenerationModal(true);

    // If user was in guest mode, reset the wasGuestMode flag so they go through depot initialization
    if (authMode === 'guest') {
      setWasGuestMode(false);
      setDepotInitialized(null);
      setCheckingDepotStatus(true);
    }
  };

  const handleApiKeyRegenerationCompleted = async () => {
    // Close the regeneration modal and update authentication status
    setShowApiKeyRegenerationModal(false);
    setIsAuthenticated(authService.isAuthenticated);

    // Check setup status after API key regeneration
    if (authService.isAuthenticated) {
      // Check if setup was already completed
      setCheckingSetupStatus(true);
      try {
        const setupResponse = await fetch('/api/management/setup-status');
        if (setupResponse.ok) {
          const setupData = await setupResponse.json();
          setSetupCompleted(setupData.isCompleted === true);
        } else {
          setSetupCompleted(false);
        }
      } catch (error) {
        console.error('Failed to check setup status:', error);
        setSetupCompleted(false);
      } finally {
        setCheckingSetupStatus(false);
      }

      // Check if depot needs initialization after authentication
      setCheckingDepotStatus(true);
      try {
        const response = await fetch('/api/gameinfo/pics-status', {
          headers: ApiService.getHeaders()
        });
        if (response.ok) {
          const data = await response.json();
          const hasData = (data.database?.totalMappings > 0) ||
                         (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);
          setDepotInitialized(hasData);
        } else {
          setDepotInitialized(false);
        }
      } catch (error) {
        console.error('Failed to check depot initialization status:', error);
        setDepotInitialized(false);
      } finally {
        setCheckingDepotStatus(false);
      }
    }
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

  // Show loading while checking auth, setup status, or depot status
  // Skip depot check only for active guest mode, not for users who were guests
  if (!isUpgradingAuth && (checkingAuth || checkingSetupStatus || (checkingDepotStatus && authMode !== 'guest'))) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-themed-primary">
        <LoadingSpinner fullScreen={false} message={checkingAuth ? "Checking authentication..." : checkingSetupStatus ? "Checking setup status..." : "Checking depot initialization status..."} />
      </div>
    );
  }

  // Show API key regeneration modal if needed
  if (showApiKeyRegenerationModal) {
    return (
      <>
        <DepotInitializationModal
          onInitialized={handleApiKeyRegenerationCompleted}
          onAuthChanged={handleAuthChanged}
          apiKeyOnlyMode={true}
        />
      </>
    );
  }

  // Check if user is authenticated or in guest mode
  const hasAccess = isAuthenticated || authMode === 'guest';

  // Show initialization modal only if not authenticated AND not in guest mode AND not expired
  // Don't skip for users who WERE in guest mode if they're not currently in guest mode
  if (!hasAccess && authMode !== 'expired' && !isUpgradingAuth) {
    // Mark initialization flow as active when showing the modal
    if (!isInitializationFlowActive) {
      setIsInitializationFlowActive(true);
      localStorage.setItem('initializationFlowActive', 'true');
    }

    // Show initialization modal with auth form
    return (
      <>
        <DepotInitializationModal
          onInitialized={handleDepotInitialized}
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
          onAuthChanged={handleAuthChanged}
          apiKeyOnlyMode={true}
        />
      </>
    );
  }

  // Show initialization modal if:
  // 1. User is authenticated (not guest or expired), AND
  // 2. EITHER:
  //    a) Initialization flow is active (user is mid-setup, even if setup was marked complete after step 3)
  //    b) Setup is NOT completed AND depot data doesn't exist
  // This ensures the modal shows for all 6 steps if user is in the flow, even after refreshing
  if (authMode === 'authenticated' &&
      (isInitializationFlowActive || (!setupCompleted && !depotInitialized)) &&
      !isUpgradingAuth) {
    // Mark initialization flow as active when showing the modal
    if (!isInitializationFlowActive) {
      setIsInitializationFlowActive(true);
      localStorage.setItem('initializationFlowActive', 'true');
    }

    return (
      <>
        <DepotInitializationModal
          onInitialized={handleDepotInitialized}
          onAuthChanged={handleAuthChanged}
        />
      </>
    );
  }

  return (
    <div
      className="min-h-screen flex flex-col"
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
      <main className="container mx-auto px-4 py-6 flex-grow">{renderContent()}</main>
      <Footer />
    </div>
  );
};

const App: React.FC = () => {
  return (
    <ErrorBoundary>
      <PollingRateProvider>
        <TimeFilterProvider>
          <DataProvider>
            <AppContent />
          </DataProvider>
        </TimeFilterProvider>
      </PollingRateProvider>
    </ErrorBoundary>
  );
};

export default App;
