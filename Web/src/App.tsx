import React, { useState, Suspense, lazy, useEffect } from 'react';
import { NotificationsProvider } from '@contexts/NotificationsContext';
import { StatsProvider, useStats } from '@contexts/StatsContext';
import { DownloadsProvider } from '@contexts/DownloadsContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import { PollingRateProvider } from '@contexts/PollingRateContext';
import { SignalRProvider } from '@contexts/SignalRContext';
import { MockModeProvider, useMockMode } from '@contexts/MockModeContext';
import { GuestConfigProvider } from '@contexts/GuestConfigContext';
import Header from '@components/layout/Header';
import Navigation from '@components/layout/Navigation';
import Footer from '@components/layout/Footer';
import ErrorBoundary from '@components/common/ErrorBoundary';
import LoadingSpinner from '@components/common/LoadingSpinner';
import UniversalNotificationBar from '@components/common/UniversalNotificationBar';
import DepotInitializationModal from '@components/initialization/DepotInitializationModal';
import AuthenticationModal from '@components/auth/AuthenticationModal';
import { FullScanRequiredModal } from '@components/shared/FullScanRequiredModal';
import { useSignalR } from '@contexts/SignalRContext';
import ApiService from '@services/api.service';
import authService, { AuthMode } from '@services/auth.service';
import { setServerTimezone } from '@utils/timezone';
import { storage } from '@utils/storage';

// Lazy load heavy components
const Dashboard = lazy(() => import('@components/dashboard/Dashboard'));
const DownloadsTab = lazy(() => import('@components/downloads/DownloadsTab'));
const ClientsTab = lazy(() => import('@components/clients/ClientsTab'));
const ServicesTab = lazy(() => import('@components/services/ServicesTab'));
const UserTab = lazy(() => import('@components/user/UserTab'));
const ManagementTab = lazy(() => import('@components/management/ManagementTab'));
const MemoryDiagnostics = lazy(() => import('@components/memory/MemoryDiagnostics'));

// Wrapper components to inject mockMode from context into providers
const StatsProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { mockMode } = useMockMode();
  return <StatsProvider mockMode={mockMode}>{children}</StatsProvider>;
};

const DownloadsProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { mockMode } = useMockMode();
  return <DownloadsProvider mockMode={mockMode}>{children}</DownloadsProvider>;
};

const AppContent: React.FC = () => {
  // Check if we're on a special route like /memory
  const isMemoryRoute = window.location.pathname === '/memory';

  const [activeTab, setActiveTab] = useState('dashboard');
  const { connectionStatus } = useStats();
  const [depotInitialized, setDepotInitialized] = useState<boolean | null>(null);
  const [checkingDepotStatus, setCheckingDepotStatus] = useState(true);
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [checkingAuth, setCheckingAuth] = useState(true);
  const [showApiKeyRegenerationModal, setShowApiKeyRegenerationModal] = useState(false);
  const [isInitializationFlowActive, setIsInitializationFlowActive] = useState(false);
  const [setupCompleted, setSetupCompleted] = useState<boolean | null>(null);
  const [hasProcessedLogs, setHasProcessedLogs] = useState<boolean | null>(null);
  const [checkingSetupStatus, setCheckingSetupStatus] = useState(true);
  const [showAutomaticScanSkippedModal, setShowAutomaticScanSkippedModal] = useState(false);
  const [hasShownScanSkippedModal, setHasShownScanSkippedModal] = useState(false);
  const signalR = useSignalR();

  // Listen for automatic scan skipped event via SignalR (for authenticated users)
  useEffect(() => {
    if (!signalR || authMode !== 'authenticated') return;

    const handleAutomaticScanSkipped = () => {
      if (!hasShownScanSkippedModal && !showAutomaticScanSkippedModal) {
        console.log('[App] Automatic scan skipped event received, showing modal');
        setShowAutomaticScanSkippedModal(true);
        setHasShownScanSkippedModal(true);
      }
    };

    signalR.on('AutomaticScanSkipped', handleAutomaticScanSkipped);

    return () => {
      signalR.off('AutomaticScanSkipped', handleAutomaticScanSkipped);
    };
  }, [signalR, hasShownScanSkippedModal, showAutomaticScanSkippedModal, authMode]);

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

  // NOTE: Automatic GC on page load is now handled by the backend GcMiddleware
  // which properly respects the memory threshold and aggressiveness settings.
  // The manual trigger endpoint (/api/gc/trigger) bypasses threshold checks and
  // is only intended for the "Run GC Now" button in the management UI.

  // Check authentication status first
  useEffect(() => {
    const checkAuthStatus = async () => {
      try {
        const authResult = await authService.checkAuth();
        setIsAuthenticated(authResult.isAuthenticated);
        setAuthMode(authResult.authMode);
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
      let lastAuthState = authService.isAuthenticated;
      let lastAuthMode = authService.authMode;

      const interval = setInterval(async () => {
        const currentAuthState = authService.isAuthenticated;
        const currentAuthMode = authService.authMode;

        // Only update state if values actually changed
        if (currentAuthState !== lastAuthState) {
          setIsAuthenticated(currentAuthState);
          lastAuthState = currentAuthState;
        }
        if (currentAuthMode !== lastAuthMode) {
          setAuthMode(currentAuthMode);
          lastAuthMode = currentAuthMode;
        }

        // Re-check auth with backend to detect revoked devices
        if (currentAuthState && authService.authMode === 'authenticated') {
          try {
            const result = await authService.checkAuth();
            if (!result.isAuthenticated || result.authMode !== 'authenticated') {
              // Device was revoked! Force page reload to show login
              console.warn('[Auth] Device authentication was revoked. Forcing reload...');
              window.location.reload();
            }
          } catch (error) {
            console.error('[Auth] Failed to verify authentication:', error);
          }
        }

        // Re-check auth if in guest mode to get updated time
        if (authService.authMode === 'guest' || authService.authMode === 'expired') {
          const result = await authService.checkAuth();
          if (result.authMode !== lastAuthMode) {
            setAuthMode(result.authMode);
            lastAuthMode = result.authMode;
          }
        }

        // Detect if authentication state changed from authenticated to unauthenticated
        if (lastAuthState && !currentAuthState && authService.authMode === 'unauthenticated') {
          console.warn('[Auth] Authentication lost. Forcing reload...');
          window.location.reload();
        }
      }, 5000); // Check every 5 seconds for revoked devices
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
      setHasProcessedLogs(true);
      setCheckingSetupStatus(false);
      return;
    }

    const checkSetupCompletionStatus = async () => {
      try {
        const response = await fetch('/api/management/setup-status');
        if (response.ok) {
          const data = await response.json();
          const setupComplete = data.isCompleted === true;
          const logsProcessed = data.hasProcessedLogs === true;

          setSetupCompleted(setupComplete);
          setHasProcessedLogs(logsProcessed);

          // If setup is complete OR logs have been processed, clear any stale initialization flow
          if (setupComplete || logsProcessed) {
            setIsInitializationFlowActive(false);
            storage.removeItem('initializationFlowActive');
            storage.removeItem('initializationCurrentStep');
            storage.removeItem('initializationInProgress');
            storage.removeItem('initializationMethod');
            storage.removeItem('initializationDownloadStatus');
          } else {
            // Only restore initialization flow from localStorage if setup is NOT complete
            const storedFlow = storage.getItem('initializationFlowActive');
            if (storedFlow === 'true') {
              setIsInitializationFlowActive(true);
            }
          }
        } else {
          setSetupCompleted(false);
          setHasProcessedLogs(false);
        }
      } catch (error) {
        console.error('Failed to check setup completion status:', error);
        setSetupCompleted(false);
        setHasProcessedLogs(false);
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
    };

    checkDepotStatus();
  }, [checkingAuth, authMode]);

  const handleDepotInitialized = async () => {
    // Initialization flow is complete
    setIsInitializationFlowActive(false);
    storage.removeItem('initializationFlowActive');

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
    // Immediately check auth status without delay
    const authResult = await authService.checkAuth();
    setIsAuthenticated(authResult.isAuthenticated);
    setAuthMode(authResult.authMode);
  };

  const handleApiKeyRegenerated = () => {
    // Set authentication to false and show the API key regeneration modal
    setIsAuthenticated(false);
    setShowApiKeyRegenerationModal(true);
  };

  const handleApiKeyRegenerationCompleted = async () => {
    // Close the regeneration modal and update authentication status
    setShowApiKeyRegenerationModal(false);
    setIsAuthenticated(authService.isAuthenticated);
    setAuthMode(authService.authMode);
  };

  const handleAutomaticScanSkippedClose = () => {
    setShowAutomaticScanSkippedModal(false);
  };

  const handleRunFullScan = async () => {
    // Close modal but keep user on current page
    setShowAutomaticScanSkippedModal(false);
    // Trigger full scan via API
    try {
      await ApiService.triggerSteamKitRebuild(false); // false = full scan
      console.log('[App] Full scan triggered successfully');
    } catch (error) {
      console.error('Failed to trigger full scan:', error);
    }
  };

  const handleDownloadFromGitHub = async () => {
    // Close modal but keep user on current page
    setShowAutomaticScanSkippedModal(false);

    // Set downloading flag in localStorage for UniversalNotificationBar
    storage.setItem('githubDownloading', 'true');
    storage.removeItem('githubDownloadComplete');

    // Trigger download from GitHub
    try {
      await ApiService.downloadPrecreatedDepotData();
      console.log('[App] GitHub download triggered successfully');

      // Update localStorage flags on success
      storage.removeItem('githubDownloading');
      storage.setItem('githubDownloadComplete', 'true');
      storage.setItem('githubDownloadTime', new Date().toISOString());
    } catch (error) {
      console.error('Failed to download from GitHub:', error);

      // Clear downloading flag on error
      storage.removeItem('githubDownloading');
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
        case 'users':
          return UserTab;
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
        ) : activeTab === 'users' ? (
          <UserTab />
        ) : (
          <TabComponent />
        )}
      </Suspense>
    );
  };

  // Show loading while checking initial status
  if (checkingAuth || checkingSetupStatus || (checkingDepotStatus && authMode !== 'guest')) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-themed-primary">
        <LoadingSpinner fullScreen={false} message={checkingAuth ? "Checking authentication..." : checkingSetupStatus ? "Checking setup status..." : "Checking depot status..."} />
      </div>
    );
  }

  // Show API key regeneration modal if needed
  if (showApiKeyRegenerationModal) {
    return (
      <>
        <AuthenticationModal
          onAuthComplete={handleApiKeyRegenerationCompleted}
          onAuthChanged={handleAuthChanged}
          title="API Key Regenerated"
          subtitle="Please enter your new API key"
          allowGuestMode={false}
        />
      </>
    );
  }

  // Check if user is authenticated or in guest mode
  const hasAccess = isAuthenticated || authMode === 'guest';

  // If guest session expired, show authentication modal
  if (authMode === 'expired') {
    return (
      <AuthenticationModal
        onAuthComplete={handleApiKeyRegenerationCompleted}
        onAuthChanged={handleAuthChanged}
        title="Session Expired"
        subtitle="Your guest session has expired. Please authenticate to continue."
        allowGuestMode={true}
      />
    );
  }

  // Show initialization modal if user is authenticated and in the middle of setup
  if (authMode === 'authenticated' && isInitializationFlowActive) {
    return (
      <DepotInitializationModal
        onInitialized={handleDepotInitialized}
        onAuthChanged={handleAuthChanged}
      />
    );
  }

  // Show authentication/initialization modal if not authenticated
  if (!hasAccess) {
    // Check if this is first-time setup or just auth needed
    const isFirstTimeSetup = setupCompleted === false && !depotInitialized && hasProcessedLogs === false;

    if (isFirstTimeSetup) {
      // Mark initialization flow as active when showing the modal
      if (!isInitializationFlowActive) {
        setIsInitializationFlowActive(true);
        storage.setItem('initializationFlowActive', 'true');
      }

      // Show full 6-step initialization modal for first-time setup
      return (
        <DepotInitializationModal
          onInitialized={handleDepotInitialized}
          onAuthChanged={handleAuthChanged}
        />
      );
    }

    // Just need authentication (e.g., new browser)
    return (
      <AuthenticationModal
        onAuthComplete={handleDepotInitialized}
        onAuthChanged={handleAuthChanged}
        title="Authentication Required"
        subtitle="Please enter your API key to continue"
        allowGuestMode={true}
      />
    );
  }

  // Show initialization modal if user is authenticated but hasn't completed first-time setup
  if (authMode === 'authenticated' && setupCompleted === false && !depotInitialized && hasProcessedLogs === false) {
    // Mark initialization flow as active
    if (!isInitializationFlowActive) {
      setIsInitializationFlowActive(true);
      storage.setItem('initializationFlowActive', 'true');
    }

    return (
      <DepotInitializationModal
        onInitialized={handleDepotInitialized}
        onAuthChanged={handleAuthChanged}
      />
    );
  }

  // Handle special routes like /memory
  if (isMemoryRoute) {
    return (
      <Suspense fallback={<LoadingSpinner fullScreen={false} message="Loading memory diagnostics..." />}>
        <MemoryDiagnostics />
      </Suspense>
    );
  }

  return (
    <>
      {/* Automatic Scan Skipped Modal - Only show for authenticated users */}
      {showAutomaticScanSkippedModal && authMode === 'authenticated' && (
        <FullScanRequiredModal
          onCancel={handleAutomaticScanSkippedClose}
          onConfirm={handleRunFullScan}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={authMode === 'authenticated'}
          title="Automatic Scan Skipped"
          isAutomaticScanSkipped={true}
        />
      )}

      <div
        className="min-h-screen flex flex-col"
        style={{
          backgroundColor: 'var(--theme-bg-primary)',
          color: 'var(--theme-text-primary)'
        }}
      >
        <Header connectionStatus={connectionStatus as 'connected' | 'disconnected' | 'reconnecting'} />
        <Navigation activeTab={activeTab} setActiveTab={setActiveTab} authMode={authMode} />
        {/* Only show Universal Notification Bar to authenticated users */}
        {authMode === 'authenticated' && <UniversalNotificationBar />}
        <main className="container mx-auto px-4 py-6 flex-grow">{renderContent()}</main>
        <Footer />
      </div>
    </>
  );
};

const App: React.FC = () => {
  return (
    <ErrorBoundary>
      <MockModeProvider>
        <PollingRateProvider>
          <TimeFilterProvider>
            <SignalRProvider>
              <GuestConfigProvider>
                <NotificationsProvider>
                  <StatsProviderWithMockMode>
                    <DownloadsProviderWithMockMode>
                      <AppContent />
                    </DownloadsProviderWithMockMode>
                  </StatsProviderWithMockMode>
                </NotificationsProvider>
              </GuestConfigProvider>
            </SignalRProvider>
          </TimeFilterProvider>
        </PollingRateProvider>
      </MockModeProvider>
    </ErrorBoundary>
  );
};

export default App;
