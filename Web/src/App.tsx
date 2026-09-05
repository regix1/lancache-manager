import React, { useState, useEffect, useCallback, useRef, lazy, Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { AutomaticScanSkippedEvent } from '@contexts/SignalRContext/types';
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
import { LoadingState } from '@components/ui/ManagerCard';
import UniversalNotificationBar from '@components/common/UniversalNotificationBar';
import DepotInitializationModal from '@components/modals/setup/DepotInitializationModal';
import AuthenticationModal from '@components/modals/auth/AuthenticationModal';
import { AccessSetup } from '@components/initialization/AccessSetup';
import { usesOidc } from '@utils/accountMode';
import { FullScanRequiredModal } from '@components/modals/setup/FullScanRequiredModal';
import ApiService from '@services/api.service';
import { useConfig } from '@contexts/useConfig';
import { isAdminAccountRequired } from '@utils/adminAccountSetup';
import { isAbortError } from '@utils/error';
import { ApiError } from '@services/apiError';
import themeService from '@services/theme.service';
import preferencesService from '@services/preferences.service';
import { ScheduledPrefillEditSessionCleanupRecovery } from '@components/features/management/schedules/scheduled-prefill/ScheduledPrefillEditSessionCleanupRecovery';
import type { PendingFullScan } from '@components/features/management/schedules/types';

const Dashboard = lazy(() => import('@components/features/dashboard/Dashboard'));
const DownloadsTab = lazy(() => import('@components/features/downloads/DownloadsTab'));
const ClientsTab = lazy(() => import('@components/features/clients/ClientsTab'));
const AuthenticateTab = lazy(() => import('@components/features/auth/AuthenticateTab'));
const UserTab = lazy(() => import('@components/features/user/UserTab'));
const EventsTab = lazy(() => import('@components/features/events'));
const ManagementTab = lazy(() => import('@components/features/management/ManagementTab'));
const MemoryDiagnostics = lazy(() => import('@components/features/memory/MemoryDiagnostics'));
const PrefillPanel = lazy(() =>
  import('@components/features/prefill/PrefillPanel').then((m) => ({ default: m.PrefillPanel }))
);
import ActiveEventBorder from '@components/common/ActiveEventBorder';
import { APP_EVENTS } from '@utils/constants';
import { sessionStore } from '@utils/storage';

const preloadMap: Record<string, () => void> = {
  dashboard: () => import('@components/features/dashboard/Dashboard'),
  downloads: () => import('@components/features/downloads/DownloadsTab'),
  clients: () => import('@components/features/clients/ClientsTab'),
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
  const [editingAccess, setEditingAccess] = useState(
    () => new URLSearchParams(window.location.search).get('accessSetup') === '1'
  );

  const handleTabChange = useCallback((tab: string) => {
    setActiveTab(tab);
  }, []);

  const handleTabHover = useCallback((tab: string) => {
    preloadMap[tab]?.();
  }, []);
  const { connectionStatus } = useStats();
  const { setupStatus, isLoading: checkingSetupStatus } = useSetupStatus();
  const {
    authMode,
    sessionId,
    hasSession,
    isLoading: checkingAuth,
    refreshAuth,
    prefillEnabled,
    isBanned,
    authenticationEnabled,
    accountMode
  } = useAuth();
  const { status: steamApiStatus, refresh: refreshSteamWebApiStatus } = useSteamWebApiStatus();
  const { refreshSteamAuth } = useSteamAuth();
  const { isDockerAvailable } = useDockerSocket();
  const [_depotInitialized, setDepotInitialized] = useState<boolean | null>(null);
  const [checkingDepotStatus, setCheckingDepotStatus] = useState(true);
  const [showFullScanRequiredModal, setShowFullScanRequiredModal] = useState(false);
  // Both come from the viability check the backend already ran. They stay undefined when the
  // backend sends nothing, which the modal renders as "no figure" rather than a made-up one.
  const [fullScanModalChangeGap, setFullScanModalChangeGap] = useState<number | undefined>(
    undefined
  );
  const [fullScanModalEstimatedApps, setFullScanModalEstimatedApps] = useState<number | undefined>(
    undefined
  );
  const signalR = useSignalR();
  const hydratedThemeSessionRef = useRef<string | null>(null);
  // True while the scan or download the user picked is still running. The server keeps
  // reporting that a full scan is needed until that work finishes, so without this the
  // periodic state check would put the modal straight back on screen.
  const fullScanActionRunningRef = useRef(false);

  // Shared unauthenticated access is not an individual account identity. Keep account preference
  // hydration tied to sign-in mode and an authenticated session.
  const hasServerSession = authenticationEnabled && hasSession;

  // Derive setup state from context
  const setupCompleted = setupStatus?.isCompleted ?? null;
  const hasProcessedLogs = setupStatus?.hasProcessedLogs ?? null;
  // The setup wizard performs required first-run work (entering Postgres credentials,
  // marking setup complete, initial log processing) independently of sign-in requirements.
  // Show it on setup status regardless of the selected access mode. Unauthenticated access suppresses the login prompt
  // (see the AuthenticationModal gate below), not genuine setup work — otherwise a
  // fresh external-Postgres install with no env credentials would be stranded.
  // An installation with no account row has nothing to sign in with, whether it is brand new or
  // finished setup before accounts existed, so the wizard is the only screen that can get it there.
  const adminAccountRequired = isAdminAccountRequired({
    accountMode,
    authenticationEnabled,
    accountExists: setupStatus?.accountExists ?? null,
    needsPostgresCredentials: setupStatus?.needsPostgresCredentials === true,
    mainAdminRecoveryAvailable: setupStatus?.mainAdminRecoveryAvailable === true
  });
  const shouldShowInitializationFlow =
    !setupCompleted || Boolean(setupStatus?.needsPostgresCredentials) || adminAccountRequired;

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
      if (customEvent.detail.tab) {
        handleTabChange(customEvent.detail.tab);
      }
    };

    window.addEventListener(APP_EVENTS.NAVIGATE_TO_TAB, handleNavigateToTab);
    return () => window.removeEventListener(APP_EVENTS.NAVIGATE_TO_TAB, handleNavigateToTab);
  }, [handleTabChange]);

  // Redirect banned users away from prefill tab (but keep them there to see the error message)
  // The error message is shown in renderContent() when isBanned && activeTab === 'prefill'

  // Setup SignalR listeners for preferences and theme
  useEffect(() => {
    preferencesService.setupSignalRListener(signalR);
    themeService.setupPreferenceListeners();
  }, [signalR]);

  // Check if modal was dismissed this session
  const wasModalDismissed = useCallback(() => {
    return sessionStore.getItem('fullScanModalDismissed') === 'true';
  }, []);

  const markModalDismissed = useCallback(() => {
    sessionStore.setItem('fullScanModalDismissed', 'true');
  }, []);

  // The Steam Game Mapping card on the Schedules page asks for the modal back after Cancel hid it
  // for the rest of the tab. That dismissal is deliberate for the SignalR path, so this is the only
  // place the flag is ever cleared, and only because the user asked for the modal by name. The
  // figures ride along on the event because they came from the schedules response, which a reload
  // refetches - unlike the SignalR event, whose numbers only ever lived in the state above.
  useEffect(() => {
    const handleShowFullScanModal = (event: Event) => {
      const requirement = (event as CustomEvent<PendingFullScan>).detail;
      sessionStore.removeItem('fullScanModalDismissed');
      setFullScanModalChangeGap(requirement.changeGap);
      setFullScanModalEstimatedApps(requirement.estimatedAppsToScan);
      setShowFullScanRequiredModal(true);
    };

    window.addEventListener(APP_EVENTS.SHOW_FULL_SCAN_MODAL, handleShowFullScanModal);
    return () =>
      window.removeEventListener(APP_EVENTS.SHOW_FULL_SCAN_MODAL, handleShowFullScanModal);
  }, []);

  // Listen for automatic scan skipped event via SignalR (for authenticated users)
  useEffect(() => {
    if (authMode !== 'authenticated') return;

    const handleAutomaticScanSkipped = (event?: AutomaticScanSkippedEvent) => {
      // Only show if not already showing, not dismissed, and nothing is already running
      if (!showFullScanRequiredModal && !wasModalDismissed() && !fullScanActionRunningRef.current) {
        setFullScanModalChangeGap(event?.context?.changeGap);
        setFullScanModalEstimatedApps(event?.context?.estimatedAppsToScan);
        setShowFullScanRequiredModal(true);
      }
    };

    signalR.on('AutomaticScanSkipped', handleAutomaticScanSkipped);

    return () => {
      signalR.off('AutomaticScanSkipped', handleAutomaticScanSkipped);
    };
  }, [signalR, showFullScanRequiredModal, authMode, wasModalDismissed]);

  const { refreshConfig } = useConfig();

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
    await themeService.reloadThemeAfterAuth(hasServerSession);

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
    themeService.reloadThemeAfterAuth(hasServerSession).catch((error) => {
      console.error('[App] Failed to hydrate theme after auth:', error);
    });
  }, [checkingAuth, authMode, sessionId, hasServerSession]);

  const handleFullScanModalDismiss = () => {
    setShowFullScanRequiredModal(false);
    markModalDismissed(); // Don't show again this session
  };

  const handleRunFullScan = async () => {
    // Close the modal and let the scan run in the background. Not marked as dismissed,
    // so a failure can put it back on screen for a retry.
    fullScanActionRunningRef.current = true;
    setShowFullScanRequiredModal(false);

    // Trigger full scan via API
    try {
      await ApiService.triggerSteamKitRebuild(false); // false = full scan
    } catch (error) {
      // A rebuild was already under way. That is the state the user wanted, so leave the
      // modal closed rather than asking them to start something that is already going.
      if (error instanceof ApiError && error.kind === 'conflict') {
        console.warn('Depot rebuild already in progress:', error.message);
        return;
      }

      console.error('Failed to trigger full scan:', error);
      fullScanActionRunningRef.current = false;
      setShowFullScanRequiredModal(true);
    }
  };

  const handleDownloadFromGitHub = async () => {
    // Close the modal and let the download run in the background. Not marked as
    // dismissed, so a failure can put it back on screen for a retry.
    fullScanActionRunningRef.current = true;
    setShowFullScanRequiredModal(false);

    // Trigger download from GitHub
    try {
      await ApiService.downloadPrecreatedDepotData();
    } catch (error) {
      // A run was already under way. That is the state the user wanted, so leave the
      // modal closed rather than asking them to start something that is already going.
      if (error instanceof ApiError && error.kind === 'conflict') {
        console.warn('Depot download already in progress:', error.message);
        return;
      }

      fullScanActionRunningRef.current = false;
      // Don't log abort errors (user cancelled)
      if (!isAbortError(error)) {
        console.error('Failed to download from GitHub:', error);
        setShowFullScanRequiredModal(true);
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

  if (!checkingAuth && !checkingSetupStatus && editingAccess && !adminAccountRequired) {
    return (
      <AccessSetup
        onClose={() => {
          const url = new URL(window.location.href);
          url.searchParams.delete('accessSetup');
          url.searchParams.delete('oidcError');
          window.history.replaceState(null, '', url);
          setEditingAccess(false);
        }}
      />
    );
  }

  // Show login page if not authenticated. An installation that still needs its first account is the
  // one case where this screen cannot be passed - there is nothing to sign in with - so it goes to
  // the wizard below instead. The setup status has to be in hand before that can be told apart:
  // while it is still loading the account state reads as unknown and this screen would flash in
  // front of the wizard.
  if (
    !checkingAuth &&
    !checkingSetupStatus &&
    authMode === 'unauthenticated' &&
    authenticationEnabled &&
    !(usesOidc(accountMode) && !setupCompleted) &&
    !adminAccountRequired
  ) {
    // The config fetched for this screen came back without the cache, logs and data paths, because
    // it was requested with no session. ConfigProvider sits above AuthProvider and signing in does
    // not remount it, so the paths only arrive if the response is asked for again here.
    return (
      <AuthenticationModal
        onAuthComplete={() => {
          void refreshAuth();
          void refreshConfig();
        }}
      />
    );
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
      <Suspense fallback={<LoadingState shape="cards" rows={3} />}>
        <MemoryDiagnostics />
      </Suspense>
    );
  }

  return (
    <>
      {authMode === 'authenticated' && <ScheduledPrefillEditSessionCleanupRecovery />}

      {/* Full Scan Required Modal - Shows globally on all pages */}
      {showFullScanRequiredModal && authMode === 'authenticated' && (
        <FullScanRequiredModal
          onCancel={handleFullScanModalDismiss}
          onConfirm={handleRunFullScan}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
          isSteamWebApiAvailable={steamApiStatus?.isFullyOperational ?? false}
          title={t('app.fullScanRequired.title')}
          changeGap={fullScanModalChangeGap}
          estimatedApps={fullScanModalEstimatedApps}
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
          <div className="app-content-area">
            <Suspense
              fallback={
                <LoadingState
                  shape={
                    activeTab === 'dashboard'
                      ? 'dashboard'
                      : activeTab === 'downloads'
                        ? 'downloads'
                        : activeTab === 'events'
                          ? 'calendar'
                          : activeTab === 'clients'
                            ? 'table'
                            : activeTab === 'authenticate'
                              ? 'form'
                              : activeTab === 'management'
                                ? 'settings'
                                : activeTab === 'users'
                                  ? 'list'
                                  : 'cards'
                  }
                  rows={activeTab === 'dashboard' ? 8 : 5}
                />
              }
            >
              {renderContent()}
            </Suspense>
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
