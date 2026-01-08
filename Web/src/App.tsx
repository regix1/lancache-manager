import React, { useState, useEffect, useCallback } from 'react';
import { NotificationsProvider } from '@contexts/NotificationsContext';
import { CacheSizeProvider } from '@contexts/CacheSizeContext';
import { StatsProvider, useStats } from '@contexts/StatsContext';
import { DownloadsProvider } from '@contexts/DownloadsContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import { EventProvider } from '@contexts/EventContext';
import { CalendarSettingsProvider } from '@contexts/CalendarSettingsContext';
import { ClientGroupProvider } from '@contexts/ClientGroupContext';
import { DownloadAssociationsProvider } from '@contexts/DownloadAssociationsContext';
import { RefreshRateProvider } from '@contexts/RefreshRateContext';
import { SignalRProvider, useSignalR } from '@contexts/SignalRContext';
import { MockModeProvider, useMockMode } from '@contexts/MockModeContext';
import { GuestConfigProvider } from '@contexts/GuestConfigContext';
import { PicsProgressProvider } from '@contexts/PicsProgressContext';
import { SetupStatusProvider, useSetupStatus } from '@contexts/SetupStatusContext';
import { SteamAuthProvider, useSteamAuth } from '@contexts/SteamAuthContext';
import { PrefillProvider } from '@contexts/PrefillContext';
import { AuthProvider, useAuth } from '@contexts/AuthContext';
import { SteamWebApiStatusProvider, useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import { TimezoneProvider } from '@contexts/TimezoneContext';
import { TimezoneAwareWrapper } from '@components/common/TimezoneAwareWrapper';
import Header from '@components/layout/Header';
import Navigation from '@components/layout/Navigation';
import Footer from '@components/layout/Footer';
import ErrorBoundary from '@components/common/ErrorBoundary';
import LoadingSpinner from '@components/common/LoadingSpinner';
import UniversalNotificationBar from '@components/common/UniversalNotificationBar';
import DepotInitializationModal from '@components/modals/setup/DepotInitializationModal';
import AuthenticationModal from '@components/modals/auth/AuthenticationModal';
import { FullScanRequiredModal } from '@components/modals/setup/FullScanRequiredModal';
import ApiService from '@services/api.service';
import { setServerTimezone } from '@utils/timezone';
import { storage } from '@utils/storage';
import themeService from '@services/theme.service';
import preferencesService from '@services/preferences.service';
import authService from '@services/auth.service';
import heartbeatService from '@services/heartbeat.service';
import { useActivityTracker } from '@hooks/useActivityTracker';

import Dashboard from '@components/features/dashboard/Dashboard';
import DownloadsTab from '@components/features/downloads/DownloadsTab';
import ClientsTab from '@components/features/clients/ClientsTab';
import ServicesTab from '@components/features/services/ServicesTab';
import AuthenticateTab from '@components/features/auth/AuthenticateTab';
import UserTab from '@components/features/user/UserTab';
import EventsTab from '@components/features/events';
import ManagementTab from '@components/features/management/ManagementTab';
import MemoryDiagnostics from '@components/features/memory/MemoryDiagnostics';
import { PrefillPanel } from '@components/features/prefill';

// Wrapper components to inject mockMode from context into providers
const StatsProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { mockMode } = useMockMode();
  return <StatsProvider mockMode={mockMode}>{children}</StatsProvider>;
};

const DownloadsProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { mockMode } = useMockMode();
  return <DownloadsProvider mockMode={mockMode}>{children}</DownloadsProvider>;
};

const PicsProgressProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({
  children
}) => {
  const { mockMode } = useMockMode();
  return <PicsProgressProvider mockMode={mockMode}>{children}</PicsProgressProvider>;
};

const AppContent: React.FC = () => {
  // Check if we're on a special route like /memory
  const isMemoryRoute = window.location.pathname === '/memory';

  const [activeTab, setActiveTab] = useState('dashboard');
  const { connectionStatus } = useStats();
  const { setupStatus, isLoading: checkingSetupStatus, markSetupCompleted } = useSetupStatus();
  const { isAuthenticated, authMode, isLoading: checkingAuth, refreshAuth, prefillEnabled, isBanned } = useAuth();
  const { status: steamApiStatus, refresh: refreshSteamWebApiStatus } = useSteamWebApiStatus();
  const { refreshSteamAuth } = useSteamAuth();
  const [depotInitialized, setDepotInitialized] = useState<boolean | null>(null);
  const [checkingDepotStatus, setCheckingDepotStatus] = useState(true);
  const [showApiKeyRegenerationModal, setShowApiKeyRegenerationModal] = useState(false);
  const [isInitializationFlowActive, setIsInitializationFlowActive] = useState(false);
  const [showFullScanRequiredModal, setShowFullScanRequiredModal] = useState(false);
  const [fullScanModalChangeGap, setFullScanModalChangeGap] = useState(0);
  const signalR = useSignalR();

  // Track user activity and send heartbeats to keep session alive
  useActivityTracker(
    () => {
      // User became active - send heartbeat
      heartbeatService.setActive(true);
    },
    () => {
      // User became idle
      heartbeatService.setActive(false);
    }
  );

  // Start heartbeat service when component mounts
  useEffect(() => {
    heartbeatService.startHeartbeat();

    return () => {
      heartbeatService.stopHeartbeat();
    };
  }, []);

  // Derive setup state from context
  const setupCompleted = setupStatus?.isCompleted ?? null;
  const hasProcessedLogs = setupStatus?.hasProcessedLogs ?? null;

  // Switch away from auth-required tabs if auth is lost
  useEffect(() => {
    if (authMode !== 'authenticated' && (activeTab === 'users' || activeTab === 'management')) {
      console.log('[App] Auth lost while on protected tab, switching to dashboard');
      setActiveTab('dashboard');
    }
  }, [authMode, activeTab]);

  // Handle custom navigation events (used by components that can't access setActiveTab directly)
  useEffect(() => {
    const handleNavigateToTab = (event: Event) => {
      const customEvent = event as CustomEvent<{ tab: string }>;
      if (customEvent.detail?.tab) {
        setActiveTab(customEvent.detail.tab);
      }
    };

    window.addEventListener('navigate-to-tab', handleNavigateToTab);
    return () => window.removeEventListener('navigate-to-tab', handleNavigateToTab);
  }, []);

  // Redirect banned users away from prefill tab (but keep them there to see the error message)
  // The error message is shown in renderContent() when isBanned && activeTab === 'prefill'

  // Setup SignalR listeners for preferences and theme
  useEffect(() => {
    if (signalR) {
      console.log('[App] Setting up preferences and theme SignalR listeners');
      preferencesService.setupSignalRListener(signalR);
      themeService.setupPreferenceListeners();
    }
  }, [signalR]);

  // Check if modal was dismissed this session
  const wasModalDismissed = useCallback(() => {
    return sessionStorage.getItem('fullScanModalDismissed') === 'true';
  }, []);

  const markModalDismissed = useCallback(() => {
    sessionStorage.setItem('fullScanModalDismissed', 'true');
  }, []);

  // Check state periodically after startup to catch backend initialization
  useEffect(() => {
    // Only run for authenticated users
    if (authMode !== 'authenticated' || checkingAuth) {
      return;
    }

    // Don't check if already dismissed
    if (wasModalDismissed()) {
      return;
    }

    // Don't check if modal is already showing
    if (showFullScanRequiredModal) {
      return;
    }

    let checkCount = 0;
    const maxChecks = 6; // Check for 60 seconds (every 10 seconds)
    let intervalTimer: NodeJS.Timeout | null = null;

    const checkCachedViabilityState = async () => {
      try {
        const response = await fetch('/api/system/state', {
          headers: ApiService.getHeaders()
        });
        if (response.ok) {
          const state = await response.json();

          // Show modal if cached state says full scan is required
          if (state.requiresFullScan && !wasModalDismissed()) {
            setFullScanModalChangeGap(state.viabilityChangeGap || 160000);
            setShowFullScanRequiredModal(true);

            // Clear interval once modal is shown
            if (intervalTimer) {
              clearInterval(intervalTimer);
            }
          }
        } else {
          console.error('[App] Failed to fetch state:', response.status, response.statusText);
        }
      } catch (error) {
        console.error('[App] Error checking cached viability state:', error);
      }
    };

    // Initial check after 2 seconds (backend is already running)
    const initialTimer = setTimeout(() => {
      checkCachedViabilityState();
    }, 2000);

    // Then check every 10 seconds for up to 60 seconds
    intervalTimer = setInterval(() => {
      checkCount++;

      if (checkCount >= maxChecks || wasModalDismissed()) {
        if (intervalTimer) {
          clearInterval(intervalTimer);
        }
        return;
      }

      checkCachedViabilityState();
    }, 10000);

    return () => {
      clearTimeout(initialTimer);
      if (intervalTimer) {
        clearInterval(intervalTimer);
      }
    };
  }, [authMode, checkingAuth, showFullScanRequiredModal, wasModalDismissed]);

  // Listen for automatic scan skipped event via SignalR (for authenticated users)
  useEffect(() => {
    if (!signalR || authMode !== 'authenticated') return;

    const handleAutomaticScanSkipped = () => {
      // Only show if not already showing and not dismissed
      if (!showFullScanRequiredModal && !wasModalDismissed()) {
        setFullScanModalChangeGap(160000); // Default large gap
        setShowFullScanRequiredModal(true);
      }
    };

    signalR.on('AutomaticScanSkipped', handleAutomaticScanSkipped);

    return () => {
      signalR.off('AutomaticScanSkipped', handleAutomaticScanSkipped);
    };
  }, [signalR, showFullScanRequiredModal, authMode, wasModalDismissed]);

  // Handle user sessions cleared event (dispatched by preferences service)
  // Use a ref to track if we're already processing to prevent duplicate handling
  const isProcessingSessionClear = React.useRef(false);

  useEffect(() => {
    const handleSessionsCleared = async () => {
      // CRITICAL: Prevent duplicate processing - both UserSessionsCleared and UserSessionRevoked
      // can dispatch this event, causing a spam of logout attempts
      if (isProcessingSessionClear.current) {
        console.log('[App] Already processing session clear - skipping duplicate');
        return;
      }

      isProcessingSessionClear.current = true;
      console.log('[App] User sessions cleared - forcing logout and clearing cookies');

      // Clear local authentication data
      authService.clearAuthAndDevice();

      // Clear theme/preferences cache
      preferencesService.clearCache();

      // IMPORTANT: Clear HttpOnly session cookies by making a request to backend
      // Since cookies are HttpOnly, JavaScript can't clear them directly
      // The backend must send Set-Cookie with expired date
      try {
        await fetch('/api/auth/clear-session', {
          method: 'POST',
          credentials: 'include' // Include cookies in request so backend can clear them
        });
        console.log('[App] Session cookies cleared via backend');
      } catch (error) {
        console.error('[App] Failed to clear session cookies:', error);
      }

      // Refresh auth context to trigger authentication modal
      await refreshAuth();

      // IMPORTANT: Also refresh Steam auth and Web API status
      // When API key is regenerated, backend also clears Steam auth data
      try {
        await refreshSteamAuth();
        refreshSteamWebApiStatus();
        console.log('[App] Refreshed Steam auth and Web API status after session clear');
      } catch (error) {
        console.error('[App] Failed to refresh Steam status:', error);
      }

      // Reset the flag after a delay to allow future legitimate clears
      setTimeout(() => {
        isProcessingSessionClear.current = false;
      }, 5000);
    };

    window.addEventListener('user-sessions-cleared', handleSessionsCleared);

    return () => {
      window.removeEventListener('user-sessions-cleared', handleSessionsCleared);
    };
  }, [refreshAuth, refreshSteamAuth, refreshSteamWebApiStatus]);

  // Fetch server timezone on mount
  useEffect(() => {
    const fetchTimezone = async () => {
      try {
        const config = await ApiService.getConfig();
        if (config.timeZone) {
          setServerTimezone(config.timeZone);
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

  // Handle initialization flow based on setup status from context
  useEffect(() => {
    if (checkingAuth || checkingSetupStatus) {
      return; // Don't proceed until both checks are complete
    }

    const storedFlow = storage.getItem('initializationFlowActive');
    const storedStep = storage.getItem('initializationCurrentStep');

    // IMPORTANT: If user has an active initialization flow in localStorage, respect that first
    // This ensures that page refreshes during initialization don't kick the user to the dashboard
    if (storedFlow === 'true' || storedStep) {
      console.log('[App] Active initialization flow detected in localStorage, restoring...');
      setIsInitializationFlowActive(true);
      return;
    }

    // If setup is complete OR logs have been processed, clear any stale initialization flow
    if (setupCompleted || hasProcessedLogs) {
      console.log('[App] Setup complete or logs processed, clearing initialization flow');
      setIsInitializationFlowActive(false);
      storage.removeItem('initializationFlowActive');
      storage.removeItem('initializationCurrentStep');
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
      storage.removeItem('usingSteamAuth');
      storage.removeItem('dataSourceChoice');
    } else {
      // Backend shows setup is NOT complete
      // Check if we have stale initialization state that needs clearing

      // If backend was reset (setup not complete) but we have advanced initialization state,
      // this indicates /data was deleted while browser was open - clear everything
      if (storedStep && storedStep !== 'api-key' && storedStep !== 'import-historical-data' && setupCompleted === false && hasProcessedLogs === false) {
        console.log('[App] Detected backend reset with stale browser state, clearing all initialization localStorage');
        storage.removeItem('initializationFlowActive');
        storage.removeItem('initializationCurrentStep');
        storage.removeItem('initializationInProgress');
        storage.removeItem('initializationMethod');
        storage.removeItem('initializationDownloadStatus');
        storage.removeItem('usingSteamAuth');
        storage.removeItem('dataSourceChoice');
        setIsInitializationFlowActive(false);
      }
    }
  }, [checkingAuth, checkingSetupStatus, setupCompleted, hasProcessedLogs]);

  // Check if depot data exists (only after auth check is done)
  useEffect(() => {
    if (checkingAuth || checkingSetupStatus) {
      return; // Don't check depot status until auth and setup checks are complete
    }

    if (authMode === 'guest') {
      setDepotInitialized(true);
      setCheckingDepotStatus(false);
      return;
    }

    const checkDepotStatus = async () => {
      try {
        const response = await fetch('/api/depots/status', {
          headers: ApiService.getHeaders()
        });
        if (response.ok) {
          const data = await response.json();
          const hasData =
            data.database?.totalMappings > 0 ||
            (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);

          console.log('[App] Depot status check:', {
            totalMappings: data.database?.totalMappings,
            steamKit2Ready: data.steamKit2?.isReady,
            steamKit2DepotCount: data.steamKit2?.depotCount,
            hasData
          });

          setDepotInitialized(hasData);
          setCheckingDepotStatus(false);
        } else {
          setDepotInitialized(false);
          setCheckingDepotStatus(false);
        }
      } catch (error) {
        console.error('Failed to check depot initialization status:', error);
        setDepotInitialized(false);
        setCheckingDepotStatus(false);
      }
    };

    checkDepotStatus();
  }, [checkingAuth, checkingSetupStatus, authMode, setupCompleted, hasProcessedLogs]);

  const handleDepotInitialized = async () => {
    // Initialization flow is complete
    setIsInitializationFlowActive(false);
    storage.removeItem('initializationFlowActive');

    // Mark setup as completed
    markSetupCompleted();

    // Double-check that depot is actually initialized before updating state
    try {
      const response = await fetch('/api/depots/status', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        const hasData =
          data.database?.totalMappings > 0 ||
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

    // Reload theme from server after initialization
    await themeService.reloadThemeAfterAuth();

    // Refresh Steam-related contexts to pick up data saved during setup
    // This ensures ManagementTab shows the correct Steam API status and auth mode
    await Promise.all([
      refreshSteamWebApiStatus(),
      refreshSteamAuth()
    ]);
  };

  const handleAuthChanged = async () => {
    // Immediately check auth status without delay
    await refreshAuth();
    // Reload theme from server after authentication changes
    await themeService.reloadThemeAfterAuth();
  };

  const handleApiKeyRegenerated = () => {
    // Show the API key regeneration modal
    setShowApiKeyRegenerationModal(true);
  };

  const handleApiKeyRegenerationCompleted = async () => {
    // Close the regeneration modal and update authentication status
    setShowApiKeyRegenerationModal(false);
    await refreshAuth();
    // Reload theme from server after re-authentication
    await themeService.reloadThemeAfterAuth();
  };

  const handleFullScanModalDismiss = () => {
    setShowFullScanRequiredModal(false);
    markModalDismissed(); // Don't show again this session
  };

  const handleRunFullScan = async () => {
    // Close modal WITHOUT marking as dismissed (allow retry if it fails)
    setShowFullScanRequiredModal(false);

    // Trigger full scan via API
    try {
      await ApiService.triggerSteamKitRebuild(false); // false = full scan
    } catch (error) {
      console.error('Failed to trigger full scan:', error);
    }
  };

  const handleDownloadFromGitHub = async () => {
    // Close modal WITHOUT marking as dismissed (allow retry if it fails)
    setShowFullScanRequiredModal(false);

    // Set downloading flag in localStorage for UniversalNotificationBar
    storage.setItem('githubDownloading', 'true');
    storage.removeItem('githubDownloadComplete');

    // Trigger download from GitHub
    try {
      await ApiService.downloadPrecreatedDepotData();

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
        case 'authenticate':
          return AuthenticateTab;
        case 'prefill':
          return PrefillPanel;
        case 'users':
          return UserTab;
        case 'events':
          return EventsTab;
        case 'management':
          return ManagementTab;
        default:
          return Dashboard;
      }
    })();

    return (
      <>
        {activeTab === 'prefill' && isBanned ? (
          <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6">
            <div className="rounded-xl p-6 text-center bg-themed-error border border-[var(--theme-error)]">
              <div className="flex flex-col items-center gap-4">
                <div className="w-16 h-16 rounded-full flex items-center justify-center bg-[var(--theme-error)]">
                  <svg className="w-8 h-8 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                  </svg>
                </div>
                <div>
                  <h2 className="text-xl font-bold mb-2 icon-error">Access Denied</h2>
                  <p className="text-themed-secondary">
                    Your account has been banned from using the Prefill feature.
                  </p>
                  <p className="text-sm mt-2 text-themed-muted">
                    Please contact an administrator if you believe this is a mistake.
                  </p>
                </div>
              </div>
            </div>
          </div>
        ) : activeTab === 'management' ? (
          <ManagementTab onApiKeyRegenerated={handleApiKeyRegenerated} />
        ) : activeTab === 'users' ? (
          <UserTab />
        ) : activeTab === 'authenticate' ? (
          <AuthenticateTab />
        ) : (
          <TabComponent />
        )}
      </>
    );
  };

  // Show loading while checking initial status
  if (checkingAuth || checkingSetupStatus || (checkingDepotStatus && authMode !== 'guest')) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-themed-primary">
        <LoadingSpinner
          fullScreen={false}
          message={
            checkingAuth
              ? 'Checking authentication...'
              : checkingSetupStatus
                ? 'Checking setup status...'
                : 'Checking depot status...'
          }
        />
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
    const isFirstTimeSetup =
      setupCompleted === false && !depotInitialized && hasProcessedLogs === false;

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
  if (
    authMode === 'authenticated' &&
    setupCompleted === false &&
    !depotInitialized &&
    hasProcessedLogs === false
  ) {
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
    return <MemoryDiagnostics />;
  }

  return (
    <TimezoneAwareWrapper>
      {/* Full Scan Required Modal - Shows globally on all pages */}
      {showFullScanRequiredModal && authMode === 'authenticated' && (
        <FullScanRequiredModal
          onCancel={handleFullScanModalDismiss}
          onConfirm={handleRunFullScan}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
          hasSteamApiKey={steamApiStatus?.hasApiKey ?? false}
          title="Full Scan Required"
          changeGap={fullScanModalChangeGap}
          estimatedApps={270000}
        />
      )}

      <div className="flex flex-col min-h-screen bg-themed-primary text-themed-primary">
        <Header
          connectionStatus={connectionStatus as 'connected' | 'disconnected' | 'reconnecting'}
        />
        <Navigation activeTab={activeTab} setActiveTab={setActiveTab} authMode={authMode} prefillEnabled={prefillEnabled} isBanned={isBanned} />
        {/* Only show Universal Notification Bar to authenticated users */}
        {authMode === 'authenticated' && <UniversalNotificationBar />}
        <main className="container mx-auto px-4 py-6 flex-grow">{renderContent()}</main>
        <Footer />
      </div>
    </TimezoneAwareWrapper>
  );
};

const App: React.FC = () => {
  return (
    <ErrorBoundary>
      <MockModeProvider>
        <TimeFilterProvider>
          <SignalRProvider>
            <AuthProvider>
              <RefreshRateProvider>
                <TimezoneProvider>
                  <SteamWebApiStatusProvider>
                    <GuestConfigProvider>
                      <SetupStatusProvider>
                        <SteamAuthProvider>
                          <PrefillProvider>
                          <PicsProgressProviderWithMockMode>
                            <NotificationsProvider>
                              <CacheSizeProvider>
                                <StatsProviderWithMockMode>
                                  <DownloadsProviderWithMockMode>
                                    <CalendarSettingsProvider>
                                      <EventProvider>
                                        <ClientGroupProvider>
                                        <DownloadAssociationsProvider>
                                          <AppContent />
                                        </DownloadAssociationsProvider>
                                      </ClientGroupProvider>
                                    </EventProvider>
                                    </CalendarSettingsProvider>
                                  </DownloadsProviderWithMockMode>
                                </StatsProviderWithMockMode>
                              </CacheSizeProvider>
                            </NotificationsProvider>
                          </PicsProgressProviderWithMockMode>
                          </PrefillProvider>
                        </SteamAuthProvider>
                      </SetupStatusProvider>
                    </GuestConfigProvider>
                  </SteamWebApiStatusProvider>
                </TimezoneProvider>
              </RefreshRateProvider>
            </AuthProvider>
          </SignalRProvider>
        </TimeFilterProvider>
      </MockModeProvider>
    </ErrorBoundary>
  );
};

export default App;
