import React, {
  useState,
  useEffect,
  useCallback,
  useRef,
  lazy,
  Suspense,
  useTransition
} from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useSetupStatus } from '@contexts/useSetupStatus';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useAuth } from '@contexts/useAuth';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { useDockerSocket } from '@contexts/useDockerSocket';
import AppProviders from '@components/AppProviders';
import Header from '@components/layout/Header';
import Navigation from '@components/layout/Navigation';
import Footer from '@components/layout/Footer';
import LoadingSpinner from '@components/common/LoadingSpinner';
import UniversalNotificationBar from '@components/common/UniversalNotificationBar';
import DepotInitializationModal from '@components/modals/setup/DepotInitializationModal';
import AuthenticationModal from '@components/modals/auth/AuthenticationModal';
import { FullScanRequiredModal } from '@components/modals/setup/FullScanRequiredModal';
import ApiService from '@services/api.service';
import { useConfig } from '@contexts/useConfig';
import { setServerTimezone } from '@utils/timezone';
import { isAbortError } from '@utils/error';
import themeService from '@services/theme.service';
import preferencesService from '@services/preferences.service';

const Dashboard = lazy(() => import('@components/features/dashboard/Dashboard'));
const DownloadsTab = lazy(() => import('@components/features/downloads/DownloadsTab'));
const ClientsTab = lazy(() => import('@components/features/clients/ClientsTab'));
const ServicesTab = lazy(() => import('@components/features/services/ServicesTab'));
const AuthenticateTab = lazy(() => import('@components/features/auth/AuthenticateTab'));
const UserTab = lazy(() => import('@components/features/user/UserTab'));
const EventsTab = lazy(() => import('@components/features/events'));
const ManagementTab = lazy(() => import('@components/features/management/ManagementTab'));
const MemoryDiagnostics = lazy(() => import('@components/features/memory/MemoryDiagnostics'));
const PrefillPanel = lazy(() =>
  import('@components/features/prefill/PrefillPanel').then((m) => ({ default: m.PrefillPanel }))
);
import ActiveEventBorder from '@components/common/ActiveEventBorder';

const preloadMap: Record<string, () => void> = {
  dashboard: () => import('@components/features/dashboard/Dashboard'),
  downloads: () => import('@components/features/downloads/DownloadsTab'),
  clients: () => import('@components/features/clients/ClientsTab'),
  services: () => import('@components/features/services/ServicesTab'),
  authenticate: () => import('@components/features/auth/AuthenticateTab'),
  prefill: () => import('@components/features/prefill/PrefillPanel'),
  users: () => import('@components/features/user/UserTab'),
  events: () => import('@components/features/events'),
  management: () => import('@components/features/management/ManagementTab')
};

// Eagerly preload the default tab
preloadMap.dashboard();

const AppContent: React.FC = () => {
  const { t } = useTranslation();
  // Check if we're on a special route like /memory
  const isMemoryRoute = window.location.pathname === '/memory';

  const [activeTab, setActiveTab] = useState('dashboard');
  const [isPending, startTransition] = useTransition();

  const handleTabChange = useCallback((tab: string) => {
    startTransition(() => {
      setActiveTab(tab);
    });
  }, []);

  const handleTabHover = useCallback((tab: string) => {
    preloadMap[tab]?.();
  }, []);
  const { connectionStatus } = useStats();
  const { setupStatus, isLoading: checkingSetupStatus } = useSetupStatus();
  const {
    authMode,
    sessionId,
    isLoading: checkingAuth,
    refreshAuth,
    prefillEnabled,
    isBanned
  } = useAuth();
  const { status: steamApiStatus, refresh: refreshSteamWebApiStatus } = useSteamWebApiStatus();
  const { refreshSteamAuth } = useSteamAuth();
  const { isDockerAvailable } = useDockerSocket();
  const [_depotInitialized, setDepotInitialized] = useState<boolean | null>(null);
  const [checkingDepotStatus, setCheckingDepotStatus] = useState(true);
  const [showFullScanRequiredModal, setShowFullScanRequiredModal] = useState(false);
  const [fullScanModalChangeGap, setFullScanModalChangeGap] = useState(0);
  const signalR = useSignalR();
  const hydratedThemeSessionRef = useRef<string | null>(null);

  // Derive setup state from context
  const setupCompleted = setupStatus?.isCompleted ?? null;
  const hasProcessedLogs = setupStatus?.hasProcessedLogs ?? null;
  const shouldShowInitializationFlow =
    !setupCompleted || Boolean(setupStatus?.needsPostgresCredentials);

  // Switch away from auth-required tabs if auth is lost
  useEffect(() => {
    if (authMode !== 'authenticated' && (activeTab === 'users' || activeTab === 'management')) {
      handleTabChange('dashboard');
    }
  }, [authMode, activeTab, handleTabChange]);

  // Switch away from prefill tab when guest loses prefill access (live via SignalR)
  useEffect(() => {
    if (authMode === 'guest' && !prefillEnabled && activeTab === 'prefill') {
      handleTabChange('dashboard');
    }
  }, [authMode, prefillEnabled, activeTab, handleTabChange]);

  // Handle custom navigation events (used by components that can't access setActiveTab directly)
  useEffect(() => {
    const handleNavigateToTab = (event: Event) => {
      const customEvent = event as CustomEvent<{ tab: string }>;
      if (customEvent.detail?.tab) {
        handleTabChange(customEvent.detail.tab);
      }
    };

    window.addEventListener('navigate-to-tab', handleNavigateToTab);
    return () => window.removeEventListener('navigate-to-tab', handleNavigateToTab);
  }, [handleTabChange]);

  // Redirect banned users away from prefill tab (but keep them there to see the error message)
  // The error message is shown in renderContent() when isBanned && activeTab === 'prefill'

  // Setup SignalR listeners for preferences and theme
  useEffect(() => {
    if (signalR) {
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
        const response = await fetch('/api/system/state', ApiService.getFetchOptions());
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

  // Set server timezone from config context (guaranteed non-null)
  const { config } = useConfig();
  useEffect(() => {
    if (config.timeZone) {
      setServerTimezone(config.timeZone);
    }
  }, [config.timeZone]);

  // NOTE: Automatic GC on page load is now handled by the backend GcMiddleware
  // which properly respects the memory threshold and aggressiveness settings.
  // The manual trigger endpoint (/api/gc/trigger) bypasses threshold checks and
  // is only intended for the "Run GC Now" button in the management UI.

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

    // Depot status is an authenticated concern. If unauthenticated, don't call protected endpoint.
    if (authMode !== 'authenticated') {
      setDepotInitialized(null);
      setCheckingDepotStatus(false);
      return;
    }

    const checkDepotStatus = async () => {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => {
        controller.abort();
      }, 10000);

      try {
        setCheckingDepotStatus(true);
        const response = await fetch(
          '/api/depots/status',
          ApiService.getFetchOptions({ cache: 'no-store', signal: controller.signal })
        );
        if (response.ok) {
          const data = await response.json();
          const hasData =
            data.database?.totalMappings > 0 ||
            (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);

          setDepotInitialized(hasData);
        } else {
          setDepotInitialized(false);
        }
      } catch (error) {
        if (error instanceof Error && error.name === 'AbortError') {
          console.warn('[App] checkDepotStatus timed out after 10000ms');
        } else {
          console.error('Failed to check depot initialization status:', error);
        }
        setDepotInitialized(false);
      } finally {
        clearTimeout(timeoutId);
        setCheckingDepotStatus(false);
      }
    };

    checkDepotStatus();
  }, [checkingAuth, checkingSetupStatus, authMode, setupCompleted, hasProcessedLogs]);

  const handleDepotInitialized = async () => {
    // Double-check that depot is actually initialized before updating state
    try {
      const response = await fetch(
        '/api/depots/status',
        ApiService.getFetchOptions({ cache: 'no-store' })
      );
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
    await Promise.all([refreshSteamWebApiStatus(), refreshSteamAuth()]);
  };

  // Hydrate session-specific theme/preferences once auth is settled for the current session.
  useEffect(() => {
    if (checkingAuth) {
      return;
    }

    if (authMode === 'unauthenticated') {
      hydratedThemeSessionRef.current = null;
      return;
    }

    const sessionKey = `${authMode}:${sessionId ?? 'no-session'}`;
    if (hydratedThemeSessionRef.current === sessionKey) {
      return;
    }

    hydratedThemeSessionRef.current = sessionKey;
    themeService.reloadThemeAfterAuth().catch((error) => {
      console.error('[App] Failed to hydrate theme after auth:', error);
    });
  }, [checkingAuth, authMode, sessionId]);

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

    // Trigger download from GitHub
    try {
      await ApiService.downloadPrecreatedDepotData();
    } catch (error) {
      // Don't log abort errors (user cancelled)
      if (!isAbortError(error)) {
        console.error('Failed to download from GitHub:', error);
      }
    }
  };

  const renderContent = () => {
    // Tabs that should show the active event border
    const eventBorderTabs = ['dashboard', 'downloads', 'clients'];
    const shouldShowEventBorder = eventBorderTabs.includes(activeTab);

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

    // Wrap content with ActiveEventBorder for applicable tabs
    const wrapWithEventBorder = (content: React.ReactNode) => {
      if (shouldShowEventBorder) {
        return <ActiveEventBorder>{content}</ActiveEventBorder>;
      }
      return content;
    };

    return (
      <>
        {activeTab === 'prefill' && !isDockerAvailable ? (
          <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 animate-fadeIn">
            <div className="rounded-lg p-6 bg-[var(--theme-warning-subtle)] border border-[var(--theme-warning-strong)]">
              <div className="min-w-0">
                <p className="font-medium text-[var(--theme-warning-text)] mb-2">
                  {t('app.prefill.dockerNotAvailable.title')}
                </p>
                <p className="text-sm mb-3 text-themed-secondary">
                  {t('app.prefill.dockerNotAvailable.description')}
                </p>

                {/* Linux instructions */}
                <div className="mb-3">
                  <p className="text-sm font-medium text-themed-primary mb-2">
                    {t('app.prefill.dockerNotAvailable.helpLinux')}
                  </p>
                  <pre className="px-3 py-2 rounded text-xs overflow-x-auto break-all whitespace-pre-wrap bg-themed-tertiary">
                    /var/run/docker.sock:/var/run/docker.sock
                  </pre>
                </div>

                {/* Windows instructions */}
                <div className="mb-3">
                  <p className="text-sm font-medium text-themed-primary mb-2">
                    {t('app.prefill.dockerNotAvailable.helpWindows')}
                  </p>
                </div>

                <p className="text-sm text-themed-muted">
                  {t('app.prefill.dockerNotAvailable.helpGeneric')}
                </p>
              </div>
            </div>
          </div>
        ) : activeTab === 'prefill' && isBanned ? (
          <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6">
            <div className="rounded-xl p-6 text-center bg-themed-error border border-[var(--theme-error)]">
              <div className="flex flex-col items-center gap-4">
                <div className="w-16 h-16 rounded-full flex items-center justify-center bg-[var(--theme-error)]">
                  <svg
                    className="w-8 h-8 text-white"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636"
                    />
                  </svg>
                </div>
                <div>
                  <h2 className="text-xl font-bold mb-2 icon-error">
                    {t('app.prefill.accessDenied.title')}
                  </h2>
                  <p className="text-themed-secondary">
                    {t('app.prefill.accessDenied.description')}
                  </p>
                  <p className="text-sm mt-2 text-themed-muted">
                    {t('app.prefill.accessDenied.help')}
                  </p>
                </div>
              </div>
            </div>
          </div>
        ) : activeTab === 'management' ? (
          <ManagementTab />
        ) : activeTab === 'users' ? (
          <UserTab />
        ) : activeTab === 'authenticate' ? (
          <AuthenticateTab />
        ) : (
          wrapWithEventBorder(<TabComponent />)
        )}
      </>
    );
  };

  // Show login page if not authenticated
  if (!checkingAuth && authMode === 'unauthenticated') {
    return <AuthenticationModal onAuthComplete={refreshAuth} />;
  }

  // Show loading while checking initial status
  if (checkingAuth || checkingSetupStatus || checkingDepotStatus) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-themed-primary">
        <LoadingSpinner
          fullScreen={false}
          message={
            checkingSetupStatus ? t('app.loading.checkingSetup') : t('app.loading.checkingDepot')
          }
        />
      </div>
    );
  }

  // Show initialization modal if user is authenticated and in the middle of setup
  if (shouldShowInitializationFlow) {
    return <DepotInitializationModal onInitialized={handleDepotInitialized} />;
  }

  // Handle special routes like /memory
  if (isMemoryRoute) {
    return (
      <Suspense fallback={<LoadingSpinner fullScreen={false} />}>
        <MemoryDiagnostics />
      </Suspense>
    );
  }

  return (
    <>
      {/* Full Scan Required Modal - Shows globally on all pages */}
      {showFullScanRequiredModal && authMode === 'authenticated' && (
        <FullScanRequiredModal
          onCancel={handleFullScanModalDismiss}
          onConfirm={handleRunFullScan}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
          hasSteamApiKey={steamApiStatus?.hasApiKey ?? false}
          title={t('app.fullScanRequired.title')}
          changeGap={fullScanModalChangeGap}
          estimatedApps={270000}
        />
      )}

      <div className="flex flex-col min-h-screen bg-themed-primary text-themed-primary">
        <Header
          connectionStatus={connectionStatus as 'connected' | 'disconnected' | 'reconnecting'}
        />
        <Navigation
          activeTab={activeTab}
          setActiveTab={handleTabChange}
          onTabHover={handleTabHover}
          authMode={authMode}
          prefillEnabled={prefillEnabled}
          isBanned={isBanned}
          dockerAvailable={isDockerAvailable}
        />
        {/* Only show Universal Notification Bar to authenticated users */}
        {authMode === 'authenticated' && <UniversalNotificationBar />}
        <main className="container mx-auto px-4 py-6 flex-grow">
          <div className={`app-content-area${isPending ? ' app-content-pending' : ''}`}>
            <Suspense fallback={<LoadingSpinner fullScreen={false} />}>{renderContent()}</Suspense>
          </div>
        </main>
        <Footer />
      </div>
    </>
  );
};

const App: React.FC = () => {
  return (
    <AppProviders>
      <AppContent />
    </AppProviders>
  );
};

export default App;
