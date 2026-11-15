import React, { useState, useEffect, useCallback, useDeferredValue, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';
import { Card } from '@components/ui/Card';
import ErrorBoundary from '@components/common/ErrorBoundary';
import { AccordionGroupProvider } from '@components/ui/AccordionGroupProvider';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import { useErrorHandler, useNotifySuccess } from '@/hooks/useErrorHandler';
import { storage } from '@utils/storage';
import { clearScanJump, readScanJump } from '@utils/scanJump';

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
  const { notifyError } = useErrorHandler();
  const { notifySuccess } = useNotifySuccess();
  const { mockMode } = useMockMode();
  const { isAdmin, authMode, sessionType } = useAuth();
  const scheduleSteamApiHighlightClear = useTimeoutCallback(2000);
  const scheduleBattleNetHighlightClear = useTimeoutCallback(2000);
  const scheduleEvictionHighlightClear = useTimeoutCallback(3000);

  // Set by a stale scan chip on the dashboard just before it asked App to switch tabs. This tab
  // was not mounted then, so the target is taken here on mount and outranks the saved section.
  const [scanJump, setScanJump] = useState(readScanJump);
  const scheduleScanJumpClear = useTimeoutCallback(3000);

  // Active section state - persisted to localStorage
  const [activeSection, setActiveSection] = useState<ManagementSection>(() => {
    if (scanJump) return 'storage';
    const saved = storage.getItem('management-active-section');
    // Migrate old 'authentication' value to 'settings'
    if (saved === 'authentication') return 'settings';
    return (saved as ManagementSection) || 'settings';
  });

  const [gameCacheRefreshKey, setGameCacheRefreshKey] = useState(0);
  const [highlightSteamApi, setHighlightSteamApi] = useState(false);
  const [highlightBattleNet, setHighlightBattleNet] = useState(false);
  const [highlightEviction, setHighlightEviction] = useState(false);

  // Wrapper to refresh both stats and game cache
  const refreshStatsAndGameCache = useCallback(() => {
    refreshStats();
    setGameCacheRefreshKey((prev) => prev + 1);
  }, [refreshStats]);

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

  // The jump has been acted on by the time this runs, so the target is spent. The local copy
  // outlives it by the length of the glow, then goes too, so stepping away to another section and
  // back does not replay it.
  useEffect(() => {
    if (!scanJump) return;
    clearScanJump();
    scheduleScanJumpClear(() => setScanJump(null));
  }, [scanJump, scheduleScanJumpClear]);

  // The tab's least height: what keeps the page's scroll where it is. A shorter section would
  // otherwise cut the page short, and the browser would pull the scroll, the header and the nav up
  // the screen. Taken while the old section is still drawn; scrolling up hands the room back.
  const tabRef = useRef<HTMLDivElement>(null);
  const heldHeightRef = useRef(0);

  const holdTabHeight = useCallback((limit: number) => {
    const tab = tabRef.current;
    if (tab === null) return;
    const scrolled = globalThis.scrollY;
    // At the page's top there is no scroll to pull, so nothing is held.
    const needed =
      scrolled > 0
        ? tab.offsetHeight -
          (document.documentElement.scrollHeight - scrolled - globalThis.innerHeight)
        : 0;
    heldHeightRef.current = Math.max(0, Math.min(limit, needed));
    tab.style.setProperty('--management-tab-held-height', `${heldHeightRef.current}px`);
  }, []);

  // Listens only while a hold is up, and lets go once scrolling has handed all of it back.
  const handleScroll = useCallback((): void => {
    holdTabHeight(heldHeightRef.current);
    if (heldHeightRef.current === 0) globalThis.removeEventListener('scroll', handleScroll);
  }, [holdTabHeight]);

  useEffect(() => () => globalThis.removeEventListener('scroll', handleScroll), [handleScroll]);

  // Handle section change
  const handleSectionChange = useCallback(
    (section: ManagementSection) => {
      holdTabHeight(Number.POSITIVE_INFINITY);
      if (heldHeightRef.current > 0) {
        globalThis.addEventListener('scroll', handleScroll, { passive: true });
      } else {
        globalThis.removeEventListener('scroll', handleScroll);
      }
      setActiveSection(section);
    },
    [handleScroll, holdTabHeight]
  );

  // Handle navigation to Steam API section with highlight
  const handleNavigateToSteamApi = useCallback(() => {
    handleSectionChange('integrations');
    setHighlightSteamApi(true);
    // Reset so a later click can re-trigger; the glow itself runs to completion on its own
    scheduleSteamApiHighlightClear(() => setHighlightSteamApi(false));
  }, [handleSectionChange, scheduleSteamApiHighlightClear]);

  // Handle navigation to Battle.net daemon status in Integrations section.
  // Battle.net is anonymous (no login), so this only highlights the daemon card.
  const handleNavigateToBattleNetLogin = useCallback(() => {
    handleSectionChange('integrations');
    setHighlightBattleNet(true);
    // Reset so a later click can re-trigger; the glow itself runs to completion on its own
    scheduleBattleNetHighlightClear(() => setHighlightBattleNet(false));
  }, [handleSectionChange, scheduleBattleNetHighlightClear]);

  // Jump from the Eviction Scan schedule card to the Eviction Detection and Removal card
  // in the Storage section and glow it into view.
  const handleNavigateToEvictionSettings = useCallback(() => {
    handleSectionChange('storage');
    setHighlightEviction(true);
    // Reset so a later click can re-trigger; the glow itself runs to completion on its own
    scheduleEvictionHighlightClear(() => setHighlightEviction(false));
  }, [handleSectionChange, scheduleEvictionHighlightClear]);

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
      return <SettingsSection />;
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

    // Every section past here changes the whole installation, which a user-role account may not;
    // the prefill sessions list stays, read-only for it.
    if (sessionType === 'user' && renderedSection !== 'prefill-sessions') {
      return (
        <Card>
          <div className="text-center py-12">
            <p className="text-themed-secondary text-lg mb-2">
              {t('management.sections.adminOnly')}
            </p>
            <p className="text-themed-muted text-sm">{t('management.sections.adminOnlyDesc')}</p>
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
            onError={notifyError}
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
            highlightCacheScan={scanJump === 'cacheFiles'}
            highlightGameDetection={scanJump === 'gameDetection'}
            onError={notifyError}
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
            onError={notifyError}
            onSuccess={setSuccess}
            onDataRefresh={refreshStatsAndGameCache}
            onNavigateToBattleNetLogin={handleNavigateToBattleNetLogin}
            onNavigateToSection={handleSectionChange}
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
            onError={notifyError}
            onSuccess={setSuccess}
          />
        );

      case 'prefill-sessions':
        return (
          // Terminate, ban and lift change other people's prefill, which a user-role account may not do.
          <PrefillSessionsSection
            isAdmin={isAdmin && sessionType !== 'user'}
            onError={notifyError}
            onSuccess={setSuccess}
          />
        );

      case 'status-check':
        return <StatusCheckSection />;

      default:
        return null;
    }
  };

  return (
    <div ref={tabRef} className="management-tab-container animate-fadeIn">
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
