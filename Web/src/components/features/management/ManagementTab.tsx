import React, { useState, useEffect, useCallback, useRef, useDeferredValue } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { useNotifications } from '@contexts/notifications';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';
import { useSteamAuth } from '@contexts/useSteamAuth';
import operationStateService from '@services/operationState.service';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import ErrorBoundary from '@components/common/ErrorBoundary';

// Import navigation and sections
import ManagementNav, { type ManagementSection } from './ManagementNav';
import {
  SettingsSection,
  IntegrationsSection,
  StorageSection,
  DataSection,
  SchedulesSection,
  PreferencesSection,
  ClientsSection,
  PrefillSessionsSection,
  StatusCheckSection
} from './sections';

// Main Management Tab Component
const ManagementTab: React.FC = () => {
  const { t } = useTranslation();
  const { refreshStats } = useStats();
  const { addNotification } = useNotifications();
  const { mockMode } = useMockMode();
  const { isAdmin, authMode } = useAuth();
  const { steamAuthMode } = useSteamAuth();

  // Active section state - persisted to localStorage
  const [activeSection, setActiveSection] = useState<ManagementSection>(() => {
    const saved = localStorage.getItem('management-active-section');
    // Migrate old 'authentication' value to 'settings'
    if (saved === 'authentication') return 'settings';
    return (saved as ManagementSection) || 'settings';
  });

  const [optimizationsEnabled, setOptimizationsEnabled] = useState(false);
  const [gameCacheRefreshKey, setGameCacheRefreshKey] = useState(0);
  const [highlightSteamApi, setHighlightSteamApi] = useState(false);
  const [highlightBattleNet, setHighlightBattleNet] = useState(false);
  const [highlightScheduleKey, setHighlightScheduleKey] = useState<string | null>(null);
  const [highlightEviction, setHighlightEviction] = useState(false);

  // Derive log processing state from notifications for DepotMappingManager
  const isProcessingLogs = useOperationBusy({ types: ['log_processing'] });

  // Wrapper to refresh both stats and game cache
  const refreshStatsAndGameCache = useCallback(() => {
    refreshStats();
    setGameCacheRefreshKey((prev) => prev + 1);
  }, [refreshStats]);

  // Use ref to ensure migration only happens once
  const hasMigratedRef = useRef(false);

  // Notification management
  const addError = useCallback(
    (message: string) => {
      addNotification({
        type: 'generic',
        status: 'failed',
        message,
        details: { notificationType: 'error' }
      });
    },
    [addNotification]
  );

  const setSuccess = useCallback(
    (message: string) => {
      addNotification({
        type: 'generic',
        status: 'completed',
        message,
        details: { notificationType: 'success' }
      });
    },
    [addNotification]
  );

  // Persist active section to localStorage
  useEffect(() => {
    localStorage.setItem('management-active-section', activeSection);
  }, [activeSection]);

  // Check if optimizations (GC management) is enabled
  useEffect(() => {
    const checkOptimizations = async () => {
      try {
        const data = (await ApiService.getGcManagementStatus()) as { enabled: boolean };
        setOptimizationsEnabled(data.enabled === true);
      } catch {
        setOptimizationsEnabled(false);
      }
    };

    checkOptimizations();
  }, []);

  // Initialize with migration
  useEffect(() => {
    const initialize = async () => {
      if (!hasMigratedRef.current) {
        const migrated = await operationStateService.migrateFromLocalStorage();
        if (migrated > 0) {
          setSuccess(t('management.sections.migratedOperations', { count: migrated }));
        }
        hasMigratedRef.current = true;
      }
    };

    initialize();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setSuccess]);

  // Handle section change
  const handleSectionChange = useCallback((section: ManagementSection) => {
    setActiveSection(section);
  }, []);

  // Handle navigation to Steam API section with highlight
  const handleNavigateToSteamApi = useCallback(() => {
    setActiveSection('integrations');
    setHighlightSteamApi(true);
    // Clear highlight after animation completes
    setTimeout(() => {
      setHighlightSteamApi(false);
    }, 2000);
  }, []);

  // Handle navigation to Battle.net daemon status in Integrations section.
  // Battle.net is anonymous (no login), so this only highlights the daemon card.
  const handleNavigateToBattleNetLogin = useCallback(() => {
    setActiveSection('integrations');
    setHighlightBattleNet(true);
    // Clear highlight after animation completes
    setTimeout(() => {
      setHighlightBattleNet(false);
    }, 2000);
  }, []);

  // Handle navigation to a specific schedule card in the Schedules section
  const handleNavigateToSchedule = useCallback((scheduleKey: string) => {
    setActiveSection('schedules');
    setHighlightScheduleKey(scheduleKey);
    // Clear highlight after flash animation completes (matches schedule-completed-flash 3s)
    setTimeout(() => {
      setHighlightScheduleKey(null);
    }, 3000);
  }, []);

  // Reverse of handleNavigateToSchedule: jump from the Eviction Scan schedule card to the
  // Eviction Detection and Removal card in the Storage section and glow it into view.
  const handleNavigateToEvictionSettings = useCallback(() => {
    setActiveSection('storage');
    setHighlightEviction(true);
    // Clear highlight after the navigate-variant glow completes (HighlightGlow default 2s)
    setTimeout(() => {
      setHighlightEviction(false);
    }, 3000);
  }, []);

  // Defer the section used for rendering content. Switching to a heavy section
  // (e.g. Storage, which mounts the GameCacheDetector card list + several managers)
  // used to block the click and stutter the fade-in, while light sections like
  // Schedules swapped instantly. With a deferred value React renders the heavy tree
  // concurrently (time-sliced) and keeps the previous section painted until it's
  // ready, so the transition stays smooth. The nav tab still highlights immediately
  // because ManagementNav reads the urgent `activeSection`.
  const renderedSection = useDeferredValue(activeSection);

  // Render the active section
  const renderActiveSection = () => {
    // Settings section is always available
    if (renderedSection === 'settings') {
      return <SettingsSection optimizationsEnabled={optimizationsEnabled} isAdmin={isAdmin} />;
    }

    // Other sections require authentication
    if (authMode !== 'authenticated') {
      return (
        <Card>
          <div className="text-center py-12">
            <p className="text-themed-secondary text-lg mb-2">
              {t('management.sections.authRequired')}
            </p>
            <p className="text-themed-muted text-sm">{t('management.sections.authRequiredDesc')}</p>
          </div>
        </Card>
      );
    }

    switch (renderedSection) {
      case 'integrations':
        return (
          <IntegrationsSection
            authMode={authMode}
            mockMode={mockMode}
            onError={addError}
            onSuccess={setSuccess}
            highlightSteamApi={highlightSteamApi}
            highlightBattleNet={highlightBattleNet}
          />
        );

      case 'storage':
        // refreshStatsAndGameCache (not plain refreshStats): StorageSection's per-entity removal
        // flows call onDataRefresh directly and must also bump gameCacheRefreshKey so
        // GameCacheDetector reloads its cached detection lists.
        return (
          <StorageSection
            isAdmin={isAdmin}
            authMode={authMode}
            mockMode={mockMode}
            gameCacheRefreshKey={gameCacheRefreshKey}
            highlightEviction={highlightEviction}
            onError={addError}
            onSuccess={setSuccess}
            onDataRefresh={refreshStatsAndGameCache}
          />
        );

      case 'data':
        return (
          <DataSection
            isAdmin={isAdmin}
            authMode={authMode}
            steamAuthMode={steamAuthMode}
            mockMode={mockMode}
            isProcessingLogs={isProcessingLogs}
            onError={addError}
            onSuccess={setSuccess}
            onDataRefresh={refreshStatsAndGameCache}
            onNavigateToSteamApi={handleNavigateToSteamApi}
            onNavigateToBattleNetLogin={handleNavigateToBattleNetLogin}
            onNavigateToSchedule={handleNavigateToSchedule}
          />
        );

      case 'schedules':
        return (
          <SchedulesSection
            isAdmin={isAdmin}
            highlightScheduleKey={highlightScheduleKey}
            onNavigateToEvictionSettings={handleNavigateToEvictionSettings}
          />
        );

      case 'preferences':
        return <PreferencesSection isAdmin={isAdmin} />;

      case 'clients':
        return (
          <ClientsSection
            isAdmin={isAdmin}
            authMode={authMode}
            mockMode={mockMode}
            onError={addError}
            onSuccess={setSuccess}
          />
        );

      case 'prefill-sessions':
        return (
          <PrefillSessionsSection isAdmin={isAdmin} onError={addError} onSuccess={setSuccess} />
        );

      case 'status-check':
        return <StatusCheckSection />;

      default:
        return null;
    }
  };

  return (
    <div className="management-tab-container animate-fadeIn">
      {/* Navigation Tabs */}
      <ManagementNav
        activeSection={activeSection}
        onSectionChange={handleSectionChange}
        isAdmin={isAdmin}
      />

      {/* Active Section Content - keyed by section so a crash in one tab falls back
          locally (never blanks the whole app) and switching tabs recovers cleanly. */}
      <div className="management-content">
        <ErrorBoundary key={renderedSection}>{renderActiveSection()}</ErrorBoundary>
      </div>

      {/* Guest Mode Info - shown in nav area when not authenticated */}
      {authMode === 'guest' && activeSection !== 'settings' && (
        <div className="mt-4">
          <Card>
            <div className="text-center py-6">
              <p className="text-themed-secondary text-lg mb-2">
                {t('management.sections.guestModeActive')}
              </p>
              <p className="text-themed-muted text-sm">{t('management.sections.guestModeDesc')}</p>
            </div>
          </Card>
        </div>
      )}
    </div>
  );
};

export default ManagementTab;
