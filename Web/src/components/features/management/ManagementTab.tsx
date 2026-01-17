import React, { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/StatsContext';
import { useNotifications } from '@contexts/NotificationsContext';
import { useMockMode } from '@contexts/MockModeContext';
import { useSignalR } from '@contexts/SignalRContext';
import type {
  LogRemovalCompleteEvent,
  CorruptionRemovalCompleteEvent
} from '@contexts/SignalRContext/types';
import { useAuth } from '@contexts/AuthContext';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import operationStateService from '@services/operationState.service';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';

// Import navigation and sections
import ManagementNav, { type ManagementSection } from './ManagementNav';
import {
  SettingsSection,
  IntegrationsSection,
  StorageSection,
  DataSection,
  PreferencesSection,
  ClientsSection,
  PrefillSessionsSection
} from './sections';

// Main Management Tab Component
interface ManagementTabProps {
  onApiKeyRegenerated?: () => void;
}

const ManagementTab: React.FC<ManagementTabProps> = ({ onApiKeyRegenerated }) => {
  const { t } = useTranslation();
  const { refreshStats } = useStats();
  const { addNotification, notifications } = useNotifications();
  const { mockMode } = useMockMode();
  const signalR = useSignalR();
  const { isAuthenticated, authMode } = useAuth();
  const { steamAuthMode } = useSteamAuth();

  // Active section state - persisted to localStorage
  const [activeSection, setActiveSection] = useState<ManagementSection>(() => {
    const saved = localStorage.getItem('management-active-section');
    // Migrate old 'authentication' value to 'settings'
    if (saved === 'authentication') return 'settings';
    return (saved as ManagementSection) || 'settings';
  });

  const [optimizationsEnabled, setOptimizationsEnabled] = useState(false);
  const [logRotationEnabled, setLogRotationEnabled] = useState(false);
  const [gameCacheRefreshKey, setGameCacheRefreshKey] = useState(0);

  // Derive log processing state from notifications for DepotMappingManager
  const activeProcessingNotification = notifications.find(
    (n) => n.type === 'log_processing' && n.status === 'running'
  );
  const isProcessingLogs = !!activeProcessingNotification;

  // Wrapper to refresh both stats and game cache
  const refreshStatsAndGameCache = useCallback(() => {
    refreshStats();
    setGameCacheRefreshKey((prev) => prev + 1);
  }, [refreshStats]);

  // Use ref to ensure migration only happens once
  const hasMigratedRef = useRef(false);

  // Refs to interact with LogRemovalManager and CorruptionManager
  const logRemovalReloadRef = useRef<(() => Promise<void>) | null>(null);
  const corruptionReloadRef = useRef<(() => Promise<void>) | null>(null);

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

  // Helper function to refresh log removal data
  const refreshLogRemoval = useCallback(async () => {
    if (logRemovalReloadRef.current) {
      await logRemovalReloadRef.current();
    }
  }, []);

  // Helper function to refresh corruption data
  const refreshCorruption = useCallback(async () => {
    if (corruptionReloadRef.current) {
      await corruptionReloadRef.current();
    }
  }, []);

  // Refs for callbacks to avoid dependency issues in SignalR subscriptions
  const addErrorRef = useRef(addError);
  const setSuccessRef = useRef(setSuccess);
  const refreshLogRemovalRef = useRef(refreshLogRemoval);
  const refreshCorruptionRef = useRef(refreshCorruption);

  // Keep refs up to date
  useEffect(() => {
    addErrorRef.current = addError;
    setSuccessRef.current = setSuccess;
    refreshLogRemovalRef.current = refreshLogRemoval;
    refreshCorruptionRef.current = refreshCorruption;
  }, [addError, setSuccess, refreshLogRemoval, refreshCorruption]);

  // Persist active section to localStorage
  useEffect(() => {
    localStorage.setItem('management-active-section', activeSection);
  }, [activeSection]);

  // Check if optimizations (GC management) is enabled
  useEffect(() => {
    const checkOptimizations = async () => {
      try {
        const response = await fetch('/api/gc/settings', ApiService.getFetchOptions());
        setOptimizationsEnabled(response.ok);
      } catch {
        setOptimizationsEnabled(false);
      }
    };

    checkOptimizations();
  }, []);

  // Check if log rotation is enabled
  useEffect(() => {
    const checkLogRotation = async () => {
      try {
        const response = await fetch('/api/system/log-rotation/status', ApiService.getFetchOptions());
        if (response.ok) {
          const data = await response.json();
          setLogRotationEnabled(data.enabled === true);
        } else {
          setLogRotationEnabled(false);
        }
      } catch {
        setLogRotationEnabled(false);
      }
    };

    checkLogRotation();
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
  }, [setSuccess]);

  // Subscribe to SignalR events for management-specific operations
  useEffect(() => {
    if (mockMode) return;

    const handleLogRemovalComplete = async (result: LogRemovalCompleteEvent) => {
      if (result.success) {
        await refreshLogRemovalRef.current();
      }
      // State is derived from notifications, no need to clear operation state
    };

    const handleCorruptionRemovalComplete = async (result: CorruptionRemovalCompleteEvent) => {
      if (result.success) {
        await refreshCorruptionRef.current();
      }
    };

    signalR.on('LogRemovalComplete', handleLogRemovalComplete);
    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);

    return () => {
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode]);

  // Handle section change
  const handleSectionChange = useCallback((section: ManagementSection) => {
    setActiveSection(section);
  }, []);

  // Render the active section
  const renderActiveSection = () => {
    // Settings section is always available
    if (activeSection === 'settings') {
      return (
        <SettingsSection
          onApiKeyRegenerated={onApiKeyRegenerated}
          optimizationsEnabled={optimizationsEnabled}
          logRotationEnabled={logRotationEnabled}
          isAuthenticated={isAuthenticated}
        />
      );
    }

    // Other sections require authentication
    if (authMode !== 'authenticated') {
      return (
        <Card>
          <div className="text-center py-12">
            <p className="text-themed-secondary text-lg mb-2">{t('management.sections.authRequired')}</p>
            <p className="text-themed-muted text-sm">
              {t('management.sections.authRequiredDesc')}
            </p>
          </div>
        </Card>
      );
    }

    switch (activeSection) {
      case 'integrations':
        return (
          <IntegrationsSection
            authMode={authMode}
            steamAuthMode={steamAuthMode}
            mockMode={mockMode}
            onError={addError}
            onSuccess={setSuccess}
          />
        );

      case 'storage':
        return (
          <StorageSection
            isAuthenticated={isAuthenticated}
            authMode={authMode}
            mockMode={mockMode}
            gameCacheRefreshKey={gameCacheRefreshKey}
            onError={addError}
            onSuccess={setSuccess}
            onDataRefresh={refreshStats}
            logRemovalReloadRef={logRemovalReloadRef}
            corruptionReloadRef={corruptionReloadRef}
          />
        );

      case 'data':
        return (
          <DataSection
            isAuthenticated={isAuthenticated}
            authMode={authMode}
            steamAuthMode={steamAuthMode}
            mockMode={mockMode}
            isProcessingLogs={isProcessingLogs}
            onError={addError}
            onSuccess={setSuccess}
            onDataRefresh={refreshStatsAndGameCache}
          />
        );

      case 'preferences':
        return (
          <PreferencesSection
            isAuthenticated={isAuthenticated}
          />
        );

      case 'clients':
        return (
          <ClientsSection
            isAuthenticated={isAuthenticated}
            authMode={authMode}
            mockMode={mockMode}
            onError={addError}
            onSuccess={setSuccess}
          />
        );

      case 'prefill-sessions':
        return (
          <PrefillSessionsSection
            isAuthenticated={isAuthenticated}
            authMode={authMode}
            mockMode={mockMode}
            onError={addError}
            onSuccess={setSuccess}
          />
        );

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
        isAuthenticated={authMode === 'authenticated'}
      />

      {/* Active Section Content */}
      <div className="management-content">
        {renderActiveSection()}
      </div>

      {/* Guest Mode Info - shown in nav area when not authenticated */}
      {authMode === 'guest' && activeSection !== 'settings' && (
        <div className="mt-4">
          <Card>
            <div className="text-center py-6">
              <p className="text-themed-secondary text-lg mb-2">{t('management.sections.guestModeActive')}</p>
              <p className="text-themed-muted text-sm">
                {t('management.sections.guestModeDesc')}
              </p>
            </div>
          </Card>
        </div>
      )}
    </div>
  );
};

export default ManagementTab;
