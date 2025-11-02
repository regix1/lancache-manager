import React, { useState, useEffect, useCallback, useRef } from 'react';
import {
  ToggleLeft,
  ToggleRight,
  Database,
  AlertTriangle,
  Shield,
  HardDrive,
  Plug,
  Settings
} from 'lucide-react';
import { useStats } from '@contexts/StatsContext';
import { useNotifications } from '@contexts/NotificationsContext';
import { useMockMode } from '@contexts/MockModeContext';
import { useSignalR } from '@contexts/SignalRContext';
import ApiService from '@services/api.service';
import { AuthMode } from '@services/auth.service';
import operationStateService from '@services/operationState.service';

// Import manager components
import AuthenticationManager from './AuthenticationManager';
import SteamLoginManager from './SteamLoginManager';
import CacheManager from './CacheManager';
import LogProcessingManager from './LogProcessingManager';
import LogAndCorruptionManager from './LogAndCorruptionManager';
import GameCacheDetector from './GameCacheDetector';
import ThemeManager from './ThemeManager';
import GcManager from './GcManager';
import GrafanaEndpoints from './GrafanaEndpoints';
import { CollapsibleSection } from './CollapsibleSection';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';

// Mock Mode Manager Component
const MockModeManager: React.FC<{
  mockMode: boolean;
  onToggle: () => void;
  disabled: boolean;
}> = ({ mockMode, onToggle, disabled }) => {
  return (
    <Card>
      <h3 className="text-lg font-semibold text-themed-primary mb-4">Mock Mode</h3>
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="flex-1">
          <p className="text-themed-secondary">Enable mock data for demonstration</p>
          <p className="text-sm text-themed-muted mt-1">
            Simulates realistic cache data and download activity
          </p>
        </div>
        <Button
          onClick={onToggle}
          disabled={disabled}
          variant="filled"
          color="blue"
          leftSection={
            mockMode ? <ToggleRight className="w-5 h-5" /> : <ToggleLeft className="w-5 h-5" />
          }
          className="w-full sm:w-48"
        >
          {mockMode ? 'Enabled' : 'Disabled'}
        </Button>
      </div>
      {mockMode && (
        <div className="mt-4">
          <Alert color="blue">
            <span className="text-sm">Mock mode active - API actions disabled</span>
          </Alert>
        </div>
      )}
    </Card>
  );
};

// Database Manager Component
const DatabaseManager: React.FC<{
  isAuthenticated: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}> = ({ authMode, mockMode, onError, onSuccess, onDataRefresh }) => {
  const [loading, setLoading] = useState(false);
  const [showResetModal, setShowResetModal] = useState(false);
  const [showClearDepotModal, setShowClearDepotModal] = useState(false);
  const [clearingDepotMappings, setClearingDepotMappings] = useState(false);


  const confirmResetDatabase = async () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setLoading(true);
    setShowResetModal(false);

    try {
      const result = await ApiService.resetDatabase();
      if (result) {
        onSuccess?.(result.message || 'Database reset started');
        // Progress is now handled by SignalR events in ManagementTab
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to reset database');
    } finally {
      setLoading(false);
    }
  };

  const handleResetDatabase = () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setShowResetModal(true);
  };

  const handleClearDepotMappings = () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setShowClearDepotModal(true);
  };

  const confirmClearDepotMappings = async () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setClearingDepotMappings(true);
    setShowClearDepotModal(false);

    try {
      const result = await ApiService.clearDepotMappings();
      if (result) {
        onSuccess?.(result.message || `Cleared ${result.count} depot mappings from database`);
        onDataRefresh?.();
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to clear depot mappings');
    } finally {
      setClearingDepotMappings(false);
    }
  };

  return (
    <>
      <Card>
      <div className="flex items-center gap-2 mb-4">
        <Database className="w-5 h-5 icon-cyan flex-shrink-0" />
        <h3 className="text-lg font-semibold text-themed-primary">Database Management</h3>
      </div>

      {/* Reset Database Section */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-4">
        <div className="flex-1">
          <p className="text-themed-secondary">Manage download history and statistics</p>
          <p className="text-xs text-themed-muted mt-1">
            Clears all download history (does not affect cached files)
          </p>
        </div>
        <Button
          onClick={handleResetDatabase}
          disabled={loading || mockMode || authMode !== 'authenticated'}
          loading={loading}
          variant="filled"
          color="red"
          leftSection={<Database className="w-4 h-4" />}
          className="w-full sm:w-48"
        >
          Reset Database
        </Button>
      </div>

      {/* Clear Depot Mappings Section */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 pt-4 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
        <div className="flex-1">
          <p className="text-themed-secondary">Remove all Steam depot-to-game mappings from database</p>
        </div>
        <Button
          onClick={handleClearDepotMappings}
          disabled={clearingDepotMappings || mockMode || authMode !== 'authenticated'}
          loading={clearingDepotMappings}
          variant="filled"
          color="blue"
          leftSection={<Database className="w-4 h-4" />}
          className="w-full sm:w-48"
          style={{
            backgroundColor: 'var(--theme-steam)',
            borderColor: 'var(--theme-steam)'
          }}
        >
          Clear Mappings
        </Button>
      </div>
      </Card>

      <Modal
        opened={showResetModal}
        onClose={() => {
          if (!loading) {
            setShowResetModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Reset Database</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            This will permanently delete all download history and statistics. Cached files on disk will remain untouched.
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>Export any data you need before continuing</li>
                <li>Historical reports will be empty after reset</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowResetModal(false)} disabled={loading}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Database className="w-4 h-4" />}
              onClick={confirmResetDatabase}
              loading={loading}
            >
              Delete History
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={showClearDepotModal}
        onClose={() => {
          if (!clearingDepotMappings) {
            setShowClearDepotModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Clear Depot Mappings</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            This will permanently delete all Steam depot-to-game mappings from the database. This is useful if you have incorrect game name associations.
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>Games will show as "Unknown" until mappings are rebuilt</li>
                <li>You can rebuild mappings by logging into Steam or downloading precreated data</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowClearDepotModal(false)} disabled={clearingDepotMappings}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="blue"
              leftSection={<Database className="w-4 h-4" />}
              onClick={confirmClearDepotMappings}
              loading={clearingDepotMappings}
              style={{
                backgroundColor: 'var(--theme-steam)',
                borderColor: 'var(--theme-steam)'
              }}
            >
              Clear Mappings
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

// Main Management Tab Component
interface ManagementTabProps {
  onApiKeyRegenerated?: () => void;
}

const ManagementTab: React.FC<ManagementTabProps> = ({ onApiKeyRegenerated }) => {
  const { refreshStats } = useStats();
  const { addNotification } = useNotifications();
  const { mockMode, setMockMode } = useMockMode();
  const signalR = useSignalR();
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [optimizationsEnabled, setOptimizationsEnabled] = useState(false);

  // Use ref to ensure migration only happens once
  const hasMigratedRef = useRef(false);

  // Refs to interact with LogAndCorruptionManager
  const logAndCorruptionReloadRef = useRef<(() => Promise<void>) | null>(null);
  const logAndCorruptionClearOpRef = useRef<(() => Promise<void>) | null>(null);

  // Notification management
  const addError = useCallback((message: string) => {
    addNotification({
      type: 'generic',
      status: 'failed',
      message,
      details: { notificationType: 'error' }
    });
  }, [addNotification]);

  const setSuccess = useCallback((message: string) => {
    addNotification({
      type: 'generic',
      status: 'completed',
      message,
      details: { notificationType: 'success' }
    });
  }, [addNotification]);

  // Helper function to refresh log & corruption management
  const refreshLogAndCorruption = useCallback(async () => {
    // Reload LogAndCorruptionManager only (don't refresh dashboard data)
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

  // Check if optimizations (GC management) is enabled
  useEffect(() => {
    const checkOptimizations = async () => {
      try {
        const response = await fetch('/api/gc/settings');
        // If we get a successful response, optimizations are enabled
        // 404 means the feature doesn't exist, which is fine
        setOptimizationsEnabled(response.ok);
      } catch (err) {
        // On error (network, etc.), assume disabled
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

  // Subscribe to SignalR events for management-specific operations (operationStateService & component refreshes)
  useEffect(() => {
    if (mockMode) {
      // Don't subscribe in mock mode
      return;
    }

    // Note: Depot mapping events are now handled by NotificationsContext via SignalR

    const handleLogRemovalComplete = async (payload: any) => {
      // Management-specific: Refresh LogAndCorruptionManager component
      if (payload.success) {
        // Clear LogAndCorruptionManager operation state
        if (logAndCorruptionClearOpRef.current) {
          await logAndCorruptionClearOpRef.current();
        }

        // Refresh LogAndCorruptionManager
        await refreshLogAndCorruptionRef.current();
      } else {
        // Clear operation states on failure too
        if (logAndCorruptionClearOpRef.current) {
          await logAndCorruptionClearOpRef.current();
        }
      }
    };

    // Subscribe to management-specific events
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);

    console.log('[ManagementTab] Subscribed to management-specific SignalR events');

    // Cleanup: unsubscribe from all events
    return () => {
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      console.log('[ManagementTab] Unsubscribed from management-specific SignalR events');
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode]); // signalR.on/off are stable, don't need signalR as dependency

  return (
    <>
      <div className="space-y-6">
        {/* All notifications now shown in UniversalNotificationBar */}

        {/* Authentication & Access Section - Always Open */}
        <CollapsibleSection title="Authentication & Access" icon={Shield} alwaysOpen>
          <AuthenticationManager
            onAuthChange={setIsAuthenticated}
            onAuthModeChange={setAuthMode}
            onError={addError}
            onSuccess={setSuccess}
            onApiKeyRegenerated={onApiKeyRegenerated}
          />
          <MockModeManager
            mockMode={mockMode}
            onToggle={() => setMockMode(!mockMode)}
            disabled={false}
          />
        </CollapsibleSection>

        {/* Only show management features when fully authenticated */}
        {authMode === 'authenticated' && (
          <>
            {/* Integration & Services Section */}
            <CollapsibleSection title="Integration & Services" icon={Plug}>
              <SteamLoginManager
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
              />

              <GrafanaEndpoints />
            </CollapsibleSection>

            {/* Data Management Section - Default Open */}
            <CollapsibleSection title="Data Management" icon={HardDrive} defaultOpen>
              <DatabaseManager
                isAuthenticated={isAuthenticated}
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={refreshStats}
              />

              <CacheManager
                isAuthenticated={isAuthenticated}
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
              />

              <LogProcessingManager
                isAuthenticated={isAuthenticated}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={refreshStats}
              />

              <LogAndCorruptionManager
                isAuthenticated={isAuthenticated}
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={refreshLogAndCorruption}
                onReloadRef={logAndCorruptionReloadRef}
                onClearOperationRef={logAndCorruptionClearOpRef}
              />

              <GameCacheDetector
                mockMode={mockMode}
                isAuthenticated={authMode === 'authenticated'}
                onDataRefresh={refreshStats}
              />
            </CollapsibleSection>

            {/* Preferences Section */}
            <CollapsibleSection title="Preferences" icon={Settings}>
              <ThemeManager isAuthenticated={isAuthenticated} />
            </CollapsibleSection>

            {/* Optimizations Section - Only show if enabled via environment variable */}
            {optimizationsEnabled && (
              <CollapsibleSection title="Optimizations" icon={Settings}>
                <GcManager isAuthenticated={isAuthenticated} />
              </CollapsibleSection>
            )}
          </>
        )}

        {/* Guest Mode Info */}
        {authMode === 'guest' && (
          <Card>
            <div className="text-center py-8">
              <p className="text-themed-secondary text-lg mb-2">Guest Mode Active</p>
              <p className="text-themed-muted text-sm">
                Management features are disabled in guest mode. Please authenticate to access full functionality.
              </p>
            </div>
          </Card>
        )}
      </div>
    </>
  );
};

export default ManagementTab;
