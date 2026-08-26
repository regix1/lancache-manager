import React, { useState, useEffect, useCallback, useDeferredValue } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { useNotifications } from '@contexts/notifications';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import ErrorBoundary from '@components/common/ErrorBoundary';
import { AccordionGroupProvider } from '@components/ui/AccordionGroupProvider';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import { useNotifySuccess } from '@/hooks/useErrorHandler';
import { storage } from '@utils/storage';

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
  const { notifySuccess } = useNotifySuccess();
  const { mockMode } = useMockMode();
  const { isAdmin, authMode } = useAuth();
  const scheduleSteamApiHighlightClear = useTimeoutCallback(2000);
  const scheduleBattleNetHighlightClear = useTimeoutCallback(2000);
  const scheduleEvictionHighlightClear = useTimeoutCallback(3000);

  // Active section state - persisted to localStorage
  const [activeSection, setActiveSection] = useState<ManagementSection>(() => {
    const saved = storage.getItem('management-active-section');
    // Migrate old 'authentication' value to 'settings'
    if (saved === 'authentication') return 'settings';
    return (saved as ManagementSection) || 'settings';
  });

  const [optimizationsEnabled, setOptimizationsEnabled] = useState(false);
  const [gameCacheRefreshKey, setGameCacheRefreshKey] = useState(0);
  const [highlightSteamApi, setHighlightSteamApi] = useState(false);
  const [highlightBattleNet, setHighlightBattleNet] = useState(false);
  const [highlightEviction, setHighlightEviction] = useState(false);

  // Wrapper to refresh both stats and game cache
  const refreshStatsAndGameCache = useCallback(() => {
    refreshStats();
    setGameCacheRefreshKey((prev) => prev + 1);
  }, [refreshStats]);

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
      notifySuccess(message);
    },
    [notifySuccess]
  );

  // Persist active section to localStorage
  useEffect(() => {
    storage.setItem('management-active-section', activeSection);
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

  // Handle section change
  const handleSectionChange = useCallback((section: ManagementSection) => {
    setActiveSection(section);
  }, []);

  // Handle navigation to Steam API section with highlight
  const handleNavigateToSteamApi = useCallback(() => {
    setActiveSection('integrations');
    setHighlightSteamApi(true);
    // Reset so a later click can re-trigger; the glow itself runs to completion on its own
    scheduleSteamApiHighlightClear(() => setHighlightSteamApi(false));
  }, [scheduleSteamApiHighlightClear]);

  // Handle navigation to Battle.net daemon status in Integrations section.
  // Battle.net is anonymous (no login), so this only highlights the daemon card.
  const handleNavigateToBattleNetLogin = useCallback(() => {
    setActiveSection('integrations');
    setHighlightBattleNet(true);
    // Reset so a later click can re-trigger; the glow itself runs to completion on its own
    scheduleBattleNetHighlightClear(() => setHighlightBattleNet(false));
  }, [scheduleBattleNetHighlightClear]);

  // Jump from the Eviction Scan schedule card to the Eviction Detection and Removal card
  // in the Storage section and glow it into view.
  const handleNavigateToEvictionSettings = useCallback(() => {
    setActiveSection('storage');
    setHighlightEviction(true);
    // Reset so a later click can re-trigger; the glow itself runs to completion on its own
    scheduleEvictionHighlightClear(() => setHighlightEviction(false));
  }, [scheduleEvictionHighlightClear]);

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
            mockMode={mockMode}
            onError={addError}
            onSuccess={setSuccess}
            onDataRefresh={refreshStatsAndGameCache}
            onNavigateToBattleNetLogin={handleNavigateToBattleNetLogin}
          />
        );

      case 'schedules':
        return (
          <SchedulesSection
            isAdmin={isAdmin}
            onNavigateToEvictionSettings={handleNavigateToEvictionSettings}
            onNavigateToSteamApi={handleNavigateToSteamApi}
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
          locally (never blanks the whole app) and switching tabs recovers cleanly. The
          same key remounts AccordionGroupProvider on every section change, so its
          expand/collapse registry always starts empty for the newly active section. */}
      <div className="management-content">
        <AccordionGroupProvider key={renderedSection}>
          <ErrorBoundary key={renderedSection}>{renderActiveSection()}</ErrorBoundary>
        </AccordionGroupProvider>
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
