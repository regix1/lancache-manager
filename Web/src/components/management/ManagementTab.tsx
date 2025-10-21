import React, { useState, useEffect, useCallback, useRef } from 'react';
import {
  ToggleLeft,
  ToggleRight,
  Database,
  Loader,
  CheckCircle,
  StopCircle,
  AlertTriangle,
  Shield,
  HardDrive,
  Plug,
  Settings
} from 'lucide-react';
import * as signalR from '@microsoft/signalr';
import { useData } from '@contexts/DataContext';
import ApiService from '@services/api.service';
import { AuthMode } from '@services/auth.service';
import operationStateService from '@services/operationState.service';

// Import manager components
import AuthenticationManager from './AuthenticationManager';
import SteamLoginManager from './SteamLoginManager';
import CacheManager from './CacheManager';
import LogProcessingManager from './LogProcessingManager';
import LogAndCorruptionManager from './LogAndCorruptionManager';
import ThemeManager from './ThemeManager';
import AlertsManager from './AlertsManager';
import GrafanaEndpoints from './GrafanaEndpoints';
import { CollapsibleSection } from './CollapsibleSection';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { SIGNALR_BASE } from '@utils/constants';

interface DepotMappingProgress {
  isProcessing: boolean;
  totalMappings: number;
  processedMappings: number;
  mappingsApplied?: number;
  percentComplete: number;
  status: string;
  message: string;
}

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
  onBackgroundOperation?: (operation: any) => void;
}> = ({ authMode, mockMode, onError, onSuccess, onDataRefresh, onBackgroundOperation }) => {
  const [loading, setLoading] = useState(false);
  const [showResetModal, setShowResetModal] = useState(false);
  const [showClearDepotModal, setShowClearDepotModal] = useState(false);
  const [clearingDepotMappings, setClearingDepotMappings] = useState(false);
  const [resetProgress, setResetProgress] = useState<{
    isProcessing: boolean;
    percentComplete: number;
    status: string;
    message: string;
  } | null>(null);
  const pollingInterval = useRef<NodeJS.Timeout | null>(null);

  useEffect(() => {
    return () => {
      if (pollingInterval.current) {
        clearInterval(pollingInterval.current);
      }
    };
  }, []);

  useEffect(() => {
    if (resetProgress) {
      onBackgroundOperation?.({
        message: resetProgress.message,
        progress: resetProgress.percentComplete,
        status: resetProgress.status
      });
    } else {
      onBackgroundOperation?.(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetProgress]); // Only depend on resetProgress, not onBackgroundOperation

  const pollResetStatus = async () => {
    try {
      const response = await fetch('/api/management/database/reset-status');
      if (response.ok) {
        const status = await response.json();

        if (status.isProcessing) {
          setResetProgress({
            isProcessing: true,
            percentComplete: status.percentComplete || 0,
            status: status.status || 'processing',
            message: status.message || 'Resetting database...'
          });
        } else if (status.status === 'complete') {
          setResetProgress({
            isProcessing: false,
            percentComplete: 100,
            status: 'complete',
            message: 'Database reset completed successfully'
          });

          if (pollingInterval.current) {
            clearInterval(pollingInterval.current);
            pollingInterval.current = null;
          }

          setTimeout(() => {
            setResetProgress(null);
            onDataRefresh?.();
          }, 3000);
        } else {
          // Not processing
          if (pollingInterval.current) {
            clearInterval(pollingInterval.current);
            pollingInterval.current = null;
          }
          setResetProgress(null);
        }
      }
    } catch (err) {
      console.error('Failed to poll reset status:', err);
    }
  };

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

        // Start polling for progress
        setResetProgress({
          isProcessing: true,
          percentComplete: 0,
          status: 'starting',
          message: 'Starting database reset...'
        });

        if (pollingInterval.current) {
          clearInterval(pollingInterval.current);
        }

        pollingInterval.current = setInterval(pollResetStatus, 500);
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to reset database');
      setResetProgress(null);
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
  const { mockMode, setMockMode, fetchData } = useData();
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [alerts, setAlerts] = useState<{
    errors: { id: number; message: string }[];
    success: string | null;
  }>({ errors: [], success: null });

  // State for background operations from child components
  const [backgroundOperations, setBackgroundOperations] = useState<{
    cacheClearing?: any;
    logProcessing?: any;
    serviceRemoval?: string | null;
    databaseReset?: any;
  }>({});

  const [depotMappingProgress, setDepotMappingProgress] = useState<DepotMappingProgress | null>(null);
  const signalRConnection = useRef<signalR.HubConnection | null>(null);

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

  // Setup SignalR for depot mapping progress
  useEffect(() => {
    if (mockMode) {
      // Don't setup SignalR in mock mode
      return;
    }

    const setupSignalR = async () => {
      try {
        const connection = new signalR.HubConnectionBuilder()
          .withUrl(`${SIGNALR_BASE}/downloads`)
          .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
          .configureLogging(signalR.LogLevel.Information)
          .build();

        // Depot mapping event handlers
        connection.on('DepotMappingStarted', (payload: any) => {
          console.log('SignalR DepotMappingStarted received:', payload);
          setDepotMappingProgress({
            isProcessing: true,
            totalMappings: 0,
            processedMappings: 0,
            percentComplete: 0,
            status: 'starting',
            message: payload.message || 'Starting depot mapping post-processing...'
          });
        });

        connection.on('DepotMappingProgress', (payload: any) => {
          console.log('SignalR DepotMappingProgress received:', payload);
          setDepotMappingProgress({
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
              setDepotMappingProgress(null);
            }, 5000);
          }
        });

        connection.on('DepotPostProcessingFailed', (payload: any) => {
          setDepotMappingProgress(null);
          addError(payload?.error
            ? `Depot mapping post-processing failed: ${payload.error}`
            : 'Depot mapping post-processing failed.');
        });

        // Listen for database reset progress
        connection.on('DatabaseResetProgress', (payload: any) => {
          console.log('SignalR DatabaseResetProgress received:', payload);
          if (payload.status === 'complete') {
            setSuccess('Database reset completed successfully - reloading page...');
            // Wait longer to ensure database is fully reset before reload
            setTimeout(() => {
              // Force hard reload to ensure fresh data (bypasses cache)
              window.location.reload();
            }, 2500);
          } else if (payload.status === 'error') {
            addError(`Database reset failed: ${payload.message}`);
          }
          // You can add more UI updates here if needed (progress bar, etc.)
        });

        // Listen for log processing completion
        connection.on('BulkProcessingComplete', async (result: any) => {
          console.log('Log processing complete:', result);
          // Log processing is done, depot mapping can be triggered manually if needed
        });

        // Listen for log removal progress
        connection.on('LogRemovalProgress', (payload: any) => {
          console.log('SignalR LogRemovalProgress received:', payload);
          // Update UI with progress if needed
        });

        // Listen for log removal completion
        connection.on('LogRemovalComplete', async (payload: any) => {
          console.log('SignalR LogRemovalComplete received:', payload);
          if (payload.success) {
            console.log(`Service ${payload.service} removal completed successfully`);

            // Clear LogAndCorruptionManager operation state
            if (logAndCorruptionClearOpRef.current) {
              await logAndCorruptionClearOpRef.current();
            }

            // Clear parent operation state
            setBackgroundOperations((prev) => ({ ...prev, serviceRemoval: null }));

            // Refresh LogAndCorruptionManager
            await refreshLogAndCorruption();
          } else {
            console.error(`Service ${payload.service} removal failed:`, payload.message);
            addError(`Failed to remove ${payload.service} logs: ${payload.message}`);

            // Clear operation states on failure too
            if (logAndCorruptionClearOpRef.current) {
              await logAndCorruptionClearOpRef.current();
            }
            setBackgroundOperations((prev) => ({ ...prev, serviceRemoval: null }));
          }
        });

        await connection.start();
        signalRConnection.current = connection;
        console.log('ManagementTab SignalR connection established');

        // Check for existing depot mapping status after connection is established
        try {
          const response = await fetch('/api/management/depot-mapping-status');
          if (response.ok) {
            const status = await response.json();
            if (status.isProcessing) {
              console.log('Recovering depot mapping state:', status);
              setDepotMappingProgress({
                isProcessing: status.isProcessing,
                totalMappings: status.totalMappings || 0,
                processedMappings: status.processedMappings || 0,
                mappingsApplied: status.mappingsApplied,
                percentComplete: status.percentComplete || 0,
                status: status.status || 'processing',
                message: status.message || 'Depot mapping in progress...'
              });
            }
          }
        } catch (err) {
          console.warn('Failed to check depot mapping status on mount:', err);
        }

      } catch (err) {
        console.error('ManagementTab SignalR connection failed:', err);
      }
    };

    setupSignalR();

    return () => {
      if (signalRConnection.current) {
        signalRConnection.current.stop();
      }
    };
  }, [mockMode, addError, setSuccess, refreshLogAndCorruption]);

  return (
    <>
      <div className="space-y-6">
        {/* All Notifications Consolidated at Top */}
        <div className="space-y-4">
          {/* Regular Alerts */}
          <AlertsManager alerts={alerts} onClearError={clearError} onClearSuccess={clearSuccess} />

          {/* Cache Clearing Background Operation */}
          {backgroundOperations.cacheClearing && (
            <Alert color="blue" icon={<Loader className="w-5 h-5 animate-spin text-themed-muted" />}>
              <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
                <div className="flex-1">
                  <p className="font-medium">Cache clearing in progress...</p>
                  {backgroundOperations.cacheClearing.filesDeleted > 0 && (
                    <p className="text-sm mt-1 opacity-75">
                      {(backgroundOperations.cacheClearing.filesDeleted || 0).toLocaleString()} files deleted
                    </p>
                  )}
                  <p className="text-sm mt-1 opacity-75">
                    {(backgroundOperations.cacheClearing.progress || 0).toFixed(0)}% complete
                  </p>
                </div>
                {backgroundOperations.cacheClearing.cancel && (
                  <Button
                    variant="filled"
                    color="red"
                    size="sm"
                    leftSection={<StopCircle className="w-4 h-4" />}
                    onClick={backgroundOperations.cacheClearing.cancel}
                    className="w-full sm:w-auto"
                  >
                    Cancel
                  </Button>
                )}
              </div>
            </Alert>
          )}

          {/* Log Processing Background Operation */}
          {backgroundOperations.logProcessing && (
            <div className="relative">
              <Alert
                color={backgroundOperations.logProcessing.status === 'complete' ? 'green' : 'blue'}
                icon={
                  backgroundOperations.logProcessing.status === 'complete' ? (
                    <CheckCircle className="w-6 h-6" />
                  ) : (
                    <Loader className="w-6 h-6 animate-spin text-themed-muted" />
                  )
                }
              >
                <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
                  <div className="flex-1">
                    <p className="font-semibold text-lg break-words">{backgroundOperations.logProcessing.message}</p>
                    {backgroundOperations.logProcessing.detailMessage && (
                      <p className="text-sm mt-2 opacity-85 break-words">
                        {backgroundOperations.logProcessing.detailMessage}
                      </p>
                    )}
                    {backgroundOperations.logProcessing.progress > 0 &&
                      backgroundOperations.logProcessing.status !== 'complete' && (
                        <div className="mt-4">
                          <div className="w-full progress-track rounded-full h-4 relative overflow-hidden shadow-inner">
                            <div
                              className="progress-bar-info h-4 rounded-full smooth-transition"
                              style={{
                                width: `${Math.min(backgroundOperations.logProcessing.progress, 100)}%`
                              }}
                            />
                            <div className="absolute inset-0 flex items-center justify-center">
                              <span className="text-xs font-bold text-themed-button drop-shadow">
                                {backgroundOperations.logProcessing.progress.toFixed(1)}%
                              </span>
                            </div>
                          </div>
                          <div className="flex justify-between items-center mt-2">
                            <p className="text-sm font-medium">
                              {backgroundOperations.logProcessing.progress.toFixed(1)}% complete
                            </p>
                            {backgroundOperations.logProcessing.estimatedTime && (
                              <p className="text-sm opacity-75">
                                {backgroundOperations.logProcessing.estimatedTime} remaining
                              </p>
                            )}
                          </div>
                        </div>
                      )}
                  </div>
                  {backgroundOperations.logProcessing.status !== 'complete' &&
                    backgroundOperations.logProcessing.onCancel && (
                      <Button
                        variant="filled"
                        color="red"
                        size="sm"
                        leftSection={<StopCircle className="w-4 h-4" />}
                        onClick={backgroundOperations.logProcessing.onCancel}
                        className="w-full sm:w-auto"
                      >
                        Cancel Processing
                      </Button>
                    )}
                </div>
              </Alert>
            </div>
          )}

          {/* Service Removal Background Operation */}
          {backgroundOperations.serviceRemoval && (
            <Alert color="orange" icon={<Loader className="w-5 h-5 animate-spin text-themed-muted" />}>
              <div>
                <p className="font-medium">
                  Removing {backgroundOperations.serviceRemoval} entries from logs...
                </p>
                <p className="text-sm mt-1">This may take several minutes for large log files</p>
              </div>
            </Alert>
          )}

          {/* Database Reset Background Operation */}
          {backgroundOperations.databaseReset && (
            <Alert
              color={backgroundOperations.databaseReset.status === 'complete' ? 'green' : 'blue'}
              icon={
                backgroundOperations.databaseReset.status === 'complete' ? (
                  <CheckCircle className="w-6 h-6" />
                ) : (
                  <Loader className="w-6 h-6 animate-spin text-themed-muted" />
                )
              }
            >
              <div className="flex flex-col gap-3">
                <div className="flex-1">
                  <p className="font-semibold text-lg break-words">{backgroundOperations.databaseReset.message}</p>
                  {backgroundOperations.databaseReset.progress > 0 &&
                    backgroundOperations.databaseReset.status !== 'complete' && (
                      <div className="mt-4">
                        <div className="w-full progress-track rounded-full h-4 relative overflow-hidden shadow-inner">
                          <div
                            className="progress-bar-info h-4 rounded-full smooth-transition"
                            style={{
                              width: `${Math.min(backgroundOperations.databaseReset.progress, 100)}%`
                            }}
                          />
                          <div className="absolute inset-0 flex items-center justify-center">
                            <span className="text-xs font-bold text-themed-button drop-shadow">
                              {backgroundOperations.databaseReset.progress.toFixed(1)}%
                            </span>
                          </div>
                        </div>
                        <div className="flex justify-between items-center mt-2">
                          <p className="text-sm font-medium">
                            {backgroundOperations.databaseReset.progress.toFixed(1)}% complete
                          </p>
                        </div>
                      </div>
                    )}
                </div>
              </div>
            </Alert>
          )}

          {/* Depot Mapping Background Operation */}
          {depotMappingProgress && (
            <Alert
              color={depotMappingProgress.status === 'complete' ? 'green' : 'orange'}
              icon={
                depotMappingProgress.status === 'complete' ? (
                  <CheckCircle className="w-6 h-6" />
                ) : (
                  <Loader className="w-6 h-6 animate-spin text-themed-muted" />
                )
              }
            >
              <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
                <div className="flex-1">
                  <p className="font-semibold text-lg break-words">
                    Depot Mapping: {depotMappingProgress.processedMappings} / {depotMappingProgress.totalMappings} downloads
                  </p>
                  <p className="text-sm mt-2 opacity-85 break-words">
                    {depotMappingProgress.message}
                    {depotMappingProgress.mappingsApplied !== undefined && (
                      <span> • {depotMappingProgress.mappingsApplied} mappings applied</span>
                    )}
                  </p>
                  {depotMappingProgress.percentComplete > 0 && depotMappingProgress.isProcessing && (
                    <div className="mt-4">
                      <div className="w-full progress-track rounded-full h-4 relative overflow-hidden shadow-inner">
                        <div
                          className="progress-bar-warning h-4 rounded-full smooth-transition"
                          style={{
                            width: `${Math.min(depotMappingProgress.percentComplete, 100)}%`
                          }}
                        />
                        <div className="absolute inset-0 flex items-center justify-center">
                          <span className="text-xs font-bold text-themed-button drop-shadow">
                            {depotMappingProgress.percentComplete.toFixed(1)}%
                          </span>
                        </div>
                      </div>
                      <div className="flex justify-between items-center mt-2">
                        <p className="text-sm font-medium">
                          {depotMappingProgress.percentComplete.toFixed(1)}% complete
                        </p>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            </Alert>
          )}
        </div>

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
                onBackgroundOperation={(op) =>
                  setBackgroundOperations((prev) => ({ ...prev, databaseReset: op }))
                }
              />

              <CacheManager
                isAuthenticated={isAuthenticated}
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onBackgroundOperation={(op) =>
                  setBackgroundOperations((prev) => ({ ...prev, cacheClearing: op }))
                }
              />

              <LogProcessingManager
                isAuthenticated={isAuthenticated}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={fetchData}
                onBackgroundOperation={(op) =>
                  setBackgroundOperations((prev) => ({ ...prev, logProcessing: op }))
                }
              />

              <LogAndCorruptionManager
                isAuthenticated={isAuthenticated}
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={refreshLogAndCorruption}
                onBackgroundOperation={(service) =>
                  setBackgroundOperations((prev) => ({ ...prev, serviceRemoval: service }))
                }
                onReloadRef={logAndCorruptionReloadRef}
                onClearOperationRef={logAndCorruptionClearOpRef}
              />
            </CollapsibleSection>

            {/* Preferences Section */}
            <CollapsibleSection title="Preferences" icon={Settings}>
              <ThemeManager isAuthenticated={isAuthenticated} />
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
