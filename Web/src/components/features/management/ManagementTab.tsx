import React, { useState, useEffect, useCallback, useRef } from 'react';
import { useStats } from '@contexts/StatsContext';
import { useNotifications } from '@contexts/NotificationsContext';
import { useMockMode } from '@contexts/MockModeContext';
import { useSignalR } from '@contexts/SignalRContext';
import type {
  LogRemovalCompletePayload,
  CorruptionRemovalCompletePayload
} from '@contexts/SignalRContext/types';
import { useAuth } from '@contexts/AuthContext';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import operationStateService from '@services/operationState.service';
import { Card } from '@components/ui/Card';

// Import navigation and sections
import ManagementNav, { type ManagementSection } from './ManagementNav';
import {
  AuthenticationSection,
  IntegrationsSection,
  StorageSection,
  DataSection,
  PreferencesSection
} from './sections';

// Main Management Tab Component
interface ManagementTabProps {
  onApiKeyRegenerated?: () => void;
}

const ManagementTab: React.FC<ManagementTabProps> = ({ onApiKeyRegenerated }) => {
  const { refreshStats } = useStats();
  const { addNotification, notifications } = useNotifications();
  const { mockMode, setMockMode } = useMockMode();
  const signalR = useSignalR();
  const { isAuthenticated, authMode } = useAuth();
  const { steamAuthMode } = useSteamAuth();

  // Active section state - persisted to localStorage
  const [activeSection, setActiveSection] = useState<ManagementSection>(() => {
    const saved = localStorage.getItem('management-active-section');
    return (saved as ManagementSection) || 'authentication';
  });

  const [optimizationsEnabled, setOptimizationsEnabled] = useState(false);
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

  // Refs to interact with LogAndCorruptionManager
  const logAndCorruptionReloadRef = useRef<(() => Promise<void>) | null>(null);
  const logAndCorruptionClearOpRef = useRef<(() => Promise<void>) | null>(null);

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

  // Helper function to refresh log & corruption management
  const refreshLogAndCorruption = useCallback(async () => {
    if (logAndCorruptionReloadRef.current) {
      await logAndCorruptionReloadRef.current();
    }
  }, []);

  // Refs for callbacks to avoid dependency issues in SignalR subscriptions
  const addErrorRef = useRef(addError);
  const setSuccessRef = useRef(setSuccess);
  const refreshLogAndCorruptionRef = useRef(refreshLogAndCorruption);

  // Keep refs up to date
  useEffect(() => {
    addErrorRef.current = addError;
    setSuccessRef.current = setSuccess;
    refreshLogAndCorruptionRef.current = refreshLogAndCorruption;
  }, [addError, setSuccess, refreshLogAndCorruption]);

  // Persist active section to localStorage
  useEffect(() => {
    localStorage.setItem('management-active-section', activeSection);
  }, [activeSection]);

  // Check if optimizations (GC management) is enabled
  useEffect(() => {
    const checkOptimizations = async () => {
      try {
        const response = await fetch('/api/gc/settings');
        setOptimizationsEnabled(response.ok);
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
          setSuccess(`Migrated ${migrated} operations from local storage to server`);
        }
        hasMigratedRef.current = true;
      }
    };

    initialize();
  }, [setSuccess]);

  // Subscribe to SignalR events for management-specific operations
  useEffect(() => {
    if (mockMode) return;

    const handleLogRemovalComplete = async (payload: LogRemovalCompletePayload) => {
      if (payload.success) {
        if (logAndCorruptionClearOpRef.current) {
          await logAndCorruptionClearOpRef.current();
        }
        await refreshLogAndCorruptionRef.current();
      } else {
        if (logAndCorruptionClearOpRef.current) {
          await logAndCorruptionClearOpRef.current();
        }
      }
    };

    const handleCorruptionRemovalComplete = async (payload: CorruptionRemovalCompletePayload) => {
      if (payload.success) {
        await refreshLogAndCorruptionRef.current();
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
    // Authentication section is always available
    if (activeSection === 'authentication') {
      return (
        <AuthenticationSection
          mockMode={mockMode}
          onToggleMockMode={() => setMockMode(!mockMode)}
          onError={addError}
          onSuccess={setSuccess}
          onApiKeyRegenerated={onApiKeyRegenerated}
        />
      );
    }

    // Other sections require authentication
    if (authMode !== 'authenticated') {
      return (
        <Card>
          <div className="text-center py-12">
            <p className="text-themed-secondary text-lg mb-2">Authentication Required</p>
            <p className="text-themed-muted text-sm">
              Please authenticate to access this section.
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
            logAndCorruptionReloadRef={logAndCorruptionReloadRef}
            logAndCorruptionClearOpRef={logAndCorruptionClearOpRef}
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
            optimizationsEnabled={optimizationsEnabled}
          />
        );

      default:
        return null;
    }
  };

  return (
    <div className="management-tab-container">
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
      {authMode === 'guest' && activeSection !== 'authentication' && (
        <div className="mt-4">
          <Card>
            <div className="text-center py-6">
              <p className="text-themed-secondary text-lg mb-2">Guest Mode Active</p>
              <p className="text-themed-muted text-sm">
                Management features are disabled in guest mode. Please authenticate to access full
                functionality.
              </p>
            </div>
          </Card>
        </div>
      )}
    </div>
  );
};

export default ManagementTab;
