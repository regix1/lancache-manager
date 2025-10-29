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
import { useData } from '@contexts/DataContext';
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
import AlertsManager from './AlertsManager';
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
  const {
    mockMode,
    setMockMode,
    fetchData,
    setBackgroundDepotMapping,
    updateBackgroundDepotMapping,
    setBackgroundDatabaseReset,
    updateBackgroundDatabaseReset
  } = useData();
  const signalR = useSignalR();
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [alerts, setAlerts] = useState<{
    errors: { id: number; message: string }[];
    success: string | null;
  }>({ errors: [], success: null });

  // Use ref to ensure migration only happens once
  const hasMigratedRef = useRef(false);

  // Refs to interact with LogAndCorruptionManager
  const logAndCorruptionReloadRef = useRef<(() => Promise<void>) | null>(null);
  const logAndCorruptionClearOpRef = useRef<(() => Promise<void>) | null>(null);

  // Alert management
  const addError = useCallback((message: string) => {
    setAlerts((prev) => ({
      ...prev,
      errors: [...prev.errors, { id: Date.now(), message }]
    }));
  }, []);

  const setSuccess = useCallback((message: string) => {
    setAlerts((prev) => ({ ...prev, success: message }));
    setTimeout(() => setAlerts((prev) => ({ ...prev, success: null })), 10000);
  }, []);

  const clearError = useCallback((id: number) => {
    setAlerts((prev) => ({
      ...prev,
      errors: prev.errors.filter((e) => e.id !== id)
    }));
  }, []);

  // Helper function to refresh log & corruption management
  const refreshLogAndCorruption = useCallback(async () => {
    // Reload LogAndCorruptionManager only (don't refresh dashboard data)
    if (logAndCorruptionReloadRef.current) {
      await logAndCorruptionReloadRef.current();
    }
  }, []);

  const clearSuccess = useCallback(() => {
    setAlerts((prev) => ({ ...prev, success: null }));
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

  // Subscribe to SignalR events for depot mapping and management operations
  useEffect(() => {
    if (mockMode) {
      // Don't subscribe in mock mode
      return;
    }

    // Depot mapping event handlers
    const handleDepotMappingStarted = (payload: any) => {
      console.log('SignalR DepotMappingStarted received:', payload);
      setBackgroundDepotMapping({
        id: 'depot-mapping',
        isProcessing: true,
        totalMappings: 0,
        processedMappings: 0,
        percentComplete: 0,
        status: 'starting',
        message: payload.message || 'Starting depot mapping post-processing...',
        startedAt: new Date()
      });
    };

    const handleDepotMappingProgress = (payload: any) => {
      console.log('SignalR DepotMappingProgress received:', payload);
      updateBackgroundDepotMapping({
        isProcessing: payload.isProcessing,
        totalMappings: payload.totalMappings,
        processedMappings: payload.processedMappings,
        mappingsApplied: payload.mappingsApplied,
        percentComplete: payload.percentComplete,
        status: payload.status,
        message: payload.message
      });

      // Clear progress when complete
      if (!payload.isProcessing || payload.status === 'complete') {
        setTimeout(() => {
          setBackgroundDepotMapping(null);
        }, 5000);
      }
    };

    const handleDepotPostProcessingFailed = (payload: any) => {
      setBackgroundDepotMapping(null);
      addErrorRef.current(payload?.error
        ? `Depot mapping post-processing failed: ${payload.error}`
        : 'Depot mapping post-processing failed.');
    };

    const handleDatabaseResetProgress = (payload: any) => {
      console.log('SignalR DatabaseResetProgress received:', payload);
      if (payload.status === 'complete') {
        updateBackgroundDatabaseReset({
          status: 'complete',
          message: 'Database reset completed - reloading page...',
          progress: 100
        });
        setSuccessRef.current('Database reset completed successfully - reloading page...');
        // Wait longer to ensure database is fully reset before reload
        setTimeout(() => {
          // Force hard reload to ensure fresh data (bypasses cache)
          window.location.reload();
        }, 2500);
      } else if (payload.status === 'error') {
        updateBackgroundDatabaseReset({
          status: 'failed',
          error: payload.message
        });
        addErrorRef.current(`Database reset failed: ${payload.message}`);
        // Clear after a delay
        setTimeout(() => {
          setBackgroundDatabaseReset(null);
        }, 10000);
      } else {
        // Update progress
        setBackgroundDatabaseReset({
          id: 'database-reset',
          message: payload.message || 'Resetting database...',
          progress: payload.percentComplete || 0,
          status: 'resetting',
          startedAt: new Date()
        });
      }
    };

    const handleBulkProcessingComplete = async (result: any) => {
      console.log('Log processing complete:', result);
      // Log processing is done, depot mapping can be triggered manually if needed
    };

    const handleLogRemovalProgress = (payload: any) => {
      console.log('SignalR LogRemovalProgress received:', payload);
      // Update UI with progress if needed
    };

    const handleLogRemovalComplete = async (payload: any) => {
      console.log('SignalR LogRemovalComplete received:', payload);
      if (payload.success) {
        console.log(`Service ${payload.service} removal completed successfully`);

        // Clear LogAndCorruptionManager operation state
        if (logAndCorruptionClearOpRef.current) {
          await logAndCorruptionClearOpRef.current();
        }

        // Refresh LogAndCorruptionManager
        await refreshLogAndCorruptionRef.current();
      } else {
        console.error(`Service ${payload.service} removal failed:`, payload.message);
        addErrorRef.current(`Failed to remove ${payload.service} logs: ${payload.message}`);

        // Clear operation states on failure too
        if (logAndCorruptionClearOpRef.current) {
          await logAndCorruptionClearOpRef.current();
        }
      }
    };

    // Subscribe to all events
    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotPostProcessingFailed', handleDepotPostProcessingFailed);
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    signalR.on('BulkProcessingComplete', handleBulkProcessingComplete);
    signalR.on('LogRemovalProgress', handleLogRemovalProgress);
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);

    console.log('[ManagementTab] Subscribed to SignalR events');

    // Check for existing depot mapping status after subscription
    const checkDepotMappingStatus = async () => {
      try {
        const response = await fetch('/api/management/depot-mapping-status');
        if (response.ok) {
          const status = await response.json();
          if (status.isProcessing) {
            console.log('Recovering depot mapping state:', status);
            setBackgroundDepotMapping({
              id: 'depot-mapping',
              isProcessing: status.isProcessing,
              totalMappings: status.totalMappings || 0,
              processedMappings: status.processedMappings || 0,
              mappingsApplied: status.mappingsApplied,
              percentComplete: status.percentComplete || 0,
              status: status.status || 'processing',
              message: status.message || 'Depot mapping in progress...',
              startedAt: new Date()
            });
          }
        }
      } catch (err) {
        console.warn('Failed to check depot mapping status on mount:', err);
      }
    };

    checkDepotMappingStatus();

    // Cleanup: unsubscribe from all events
    return () => {
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotPostProcessingFailed', handleDepotPostProcessingFailed);
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      signalR.off('BulkProcessingComplete', handleBulkProcessingComplete);
      signalR.off('LogRemovalProgress', handleLogRemovalProgress);
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      console.log('[ManagementTab] Unsubscribed from SignalR events');
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode]); // signalR.on/off are stable, don't need signalR as dependency

  return (
    <>
      <div className="space-y-6">
        {/* Regular Alerts Only - Background operations now shown in UniversalNotificationBar */}
        <AlertsManager alerts={alerts} onClearError={clearError} onClearSuccess={clearSuccess} />

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
                onDataRefresh={fetchData}
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
                onDataRefresh={fetchData}
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
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={fetchData}
              />
            </CollapsibleSection>

            {/* Preferences Section */}
            <CollapsibleSection title="Preferences" icon={Settings}>
              <ThemeManager isAuthenticated={isAuthenticated} />
              <div className="mt-6">
                <GcManager isAuthenticated={isAuthenticated} />
              </div>
            </CollapsibleSection>
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
