import React, { useState, useEffect, useCallback, useRef, Suspense } from 'react';
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
import type {
  LogRemovalCompletePayload,
  CorruptionRemovalCompletePayload
} from '@contexts/SignalRContext/types';
import { useAuth } from '@contexts/AuthContext';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import operationStateService from '@services/operationState.service';

// Import manager components
import AuthenticationManager from './steam/AuthenticationManager';
import SteamLoginManager from './steam/SteamLoginManager';
import SteamWebApiStatus from './steam/SteamWebApiStatus';
import CacheManager from './cache/CacheManager';
import LogProcessingManager from './log-processing/LogProcessingManager';
import LogAndCorruptionManager from './log-processing/LogAndCorruptionManager';
import GameCacheDetector from './game-detection/GameCacheDetector';
import ThemeManager from './theme/ThemeManager';
import GcManager from './gc/GcManager';
import GrafanaEndpoints from './grafana/GrafanaEndpoints';
import DataImporter from './data/DataImporter';
import { CollapsibleSection } from '@components/ui/CollapsibleSection';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';

// Mock Mode Manager Component
const MockModeManager: React.FC<{
  mockMode: boolean;
  onToggle: () => void;
  disabled: boolean;
}> = ({ mockMode, onToggle, disabled }) => {
  return (
    <Card>
      <div className="flex items-center gap-3 mb-4">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-gray">
          <Settings className="w-5 h-5 icon-gray" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary">Mock Mode</h3>
      </div>
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

// Database Manager Component - Redesigned with selective table clearing
const DatabaseManager: React.FC<{
  isAuthenticated: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}> = ({ authMode, mockMode, onError, onSuccess, onDataRefresh }) => {
  const [loading, setLoading] = useState(false);
  const [showClearModal, setShowClearModal] = useState(false);
  const [selectedTables, setSelectedTables] = useState<string[]>([]);

  // Table definitions with descriptions
  const tables = [
    {
      name: 'LogEntries',
      label: 'Log Entries',
      description: 'Raw access log entries from nginx cache logs',
      details: 'Individual log line records used for analytics and reporting'
    },
    {
      name: 'Downloads',
      label: 'Downloads',
      description: 'Download records with game associations and statistics',
      details: 'Tracked downloads with game names, sizes, and timestamps'
    },
    {
      name: 'ClientStats',
      label: 'Client Statistics',
      description: 'Per-client download statistics and metrics',
      details: 'Bandwidth and download counts grouped by IP address'
    },
    {
      name: 'ServiceStats',
      label: 'Service Statistics',
      description: 'Per-service (Steam, Epic, etc.) download statistics',
      details: 'Total downloads and bandwidth usage by CDN service'
    },
    {
      name: 'SteamDepotMappings',
      label: 'Steam Depot Mappings',
      description: 'Depot ID to game name associations from SteamKit',
      details: 'Mappings used to identify which game a depot belongs to. Also clears game names from existing downloads.'
    },
    {
      name: 'CachedGameDetections',
      label: 'Game Cache Detection',
      description: 'Cached results from game and service detection scans',
      details: 'Pre-computed game and service detections from cache files to speed up dashboard loading'
    },
    {
      name: 'UserSessions',
      label: 'User Sessions',
      description: 'Active and historical user session records',
      details: 'Session tracking with device info, IP addresses, and authentication status'
    },
    {
      name: 'UserPreferences',
      label: 'User Preferences',
      description: 'Per-session user interface preferences',
      details: 'Theme selections and UI customization settings linked to sessions'
    }
  ];

  const handleTableToggle = (tableName: string) => {
    setSelectedTables((prev) =>
      prev.includes(tableName) ? prev.filter((t) => t !== tableName) : [...prev, tableName]
    );
  };

  const handleSelectAll = () => {
    if (selectedTables.length === tables.length) {
      setSelectedTables([]);
    } else {
      setSelectedTables(tables.map((t) => t.name));
    }
  };

  const handleClearSelected = () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    if (selectedTables.length === 0) {
      onError?.('Please select at least one table to clear');
      return;
    }

    setShowClearModal(true);
  };

  const confirmClear = async () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setLoading(true);
    setShowClearModal(false);

    try {
      // Start the background reset operation (returns 202 Accepted immediately)
      const result = await ApiService.resetSelectedTables(selectedTables);
      if (result) {
        // Show success message
        onSuccess?.(result.message || `Database reset started for ${selectedTables.length} table(s)`);
        setSelectedTables([]);

        // Note: If UserSessions is being cleared, the SignalR 'UserSessionsCleared' event
        // will be broadcast immediately after deletion (not at the end).
        // App.tsx will handle the logout via the 'user-sessions-cleared' custom event.
        // This ensures users are logged out as soon as their sessions are invalidated,
        // while other table deletions continue in the background.

        // For non-UserSessions operations, refresh data
        // For UserSessions operations, the page will reload automatically via SignalR event
        if (!selectedTables.includes('UserSessions')) {
          onDataRefresh?.();
        }

        setLoading(false);
      }
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to clear selected tables');
      setLoading(false);
    }
  };

  const getSelectedTableInfo = () => {
    return tables.filter((t) => selectedTables.includes(t.name));
  };

  return (
    <>
      <Card>
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
            <Database className="w-5 h-5 icon-cyan" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Database Management</h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title="What Gets Cleared">
              <div className="space-y-1.5">
                <HelpDefinition term="Log Entries" termColor="blue">
                  Raw nginx access log records used for analytics
                </HelpDefinition>
                <HelpDefinition term="Downloads" termColor="green">
                  Game download records with statistics and timestamps
                </HelpDefinition>
                <HelpDefinition term="Depot Mappings" termColor="purple">
                  Steam depot-to-game associations (also clears game names)
                </HelpDefinition>
              </div>
            </HelpSection>

            <HelpNote type="info">
              Cached files on disk are never touched — only database records are cleared.
            </HelpNote>
          </HelpPopover>
        </div>

        <p className="text-themed-secondary mb-4">
          Select which database tables you want to clear. Cached files on disk will remain
          untouched.
        </p>

        {/* Select All / Deselect All */}
        <div className="mb-4 pb-4 border-b" style={{ borderColor: 'var(--theme-border-primary)' }}>
          <Checkbox
            checked={selectedTables.length === tables.length}
            onChange={handleSelectAll}
            label={
              selectedTables.length === tables.length ? 'Deselect All Tables' : 'Select All Tables'
            }
            variant="rounded"
          />
        </div>

        {/* Table Selection */}
        <div className="space-y-3 mb-4">
          {tables.map((table) => (
            <label
              key={table.name}
              className="p-3 rounded-lg cursor-pointer flex items-start gap-3"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                border: `1px solid ${selectedTables.includes(table.name) ? 'var(--theme-primary)' : 'var(--theme-border-primary)'}`
              }}
            >
              <input
                type="checkbox"
                checked={selectedTables.includes(table.name)}
                onChange={() => handleTableToggle(table.name)}
                className="rounded mt-1"
              />
              <div className="flex-1">
                <div className="font-medium text-themed-primary">{table.label}</div>
                <div className="text-sm text-themed-secondary mt-1">{table.description}</div>
                <div className="text-xs text-themed-muted mt-1">{table.details}</div>
              </div>
            </label>
          ))}
        </div>

        {/* Action Button */}
        <div
          className="flex items-center justify-between pt-4 border-t"
          style={{ borderColor: 'var(--theme-border-primary)' }}
        >
          <div className="text-sm text-themed-secondary">
            {selectedTables.length > 0
              ? `${selectedTables.length} table(s) selected`
              : 'No tables selected'}
          </div>
          <Button
            onClick={handleClearSelected}
            disabled={
              loading || mockMode || authMode !== 'authenticated' || selectedTables.length === 0
            }
            loading={loading}
            variant="filled"
            color="red"
          >
            <span className="hidden sm:inline">Clear Selected Tables</span>
            <span className="sm:hidden">Clear Selected</span>
          </Button>
        </div>
      </Card>

      {/* Confirmation Modal */}
      <Modal
        opened={showClearModal}
        onClose={() => {
          if (!loading) {
            setShowClearModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Clear Selected Tables</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            You are about to permanently delete data from the following table(s):
          </p>

          <div className="space-y-2 max-h-[40vh] overflow-y-auto custom-scrollbar pr-2">
            {getSelectedTableInfo().map((table) => (
              <div
                key={table.name}
                className="p-3 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
              >
                <div className="font-medium text-themed-primary">{table.label}</div>
                <div className="text-sm text-themed-secondary mt-1">{table.description}</div>
              </div>
            ))}
          </div>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>Export any data you need before continuing</li>
                <li>Historical reports may be affected</li>
                {selectedTables.includes('SteamDepotMappings') && (
                  <li>Games will show as "Unknown" until mappings are rebuilt</li>
                )}
                {selectedTables.includes('UserSessions') && (
                  <li className="font-semibold">All devices will be logged out and the application will reload</li>
                )}
                {selectedTables.includes('UserSessions') && (
                  <li className="font-semibold">All registered devices will be cleared - you will need to re-authenticate</li>
                )}
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowClearModal(false)} disabled={loading}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmClear}
              loading={loading}
            >
              Clear {selectedTables.length} Table{selectedTables.length !== 1 ? 's' : ''}
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
  const { isAuthenticated, authMode } = useAuth();
  const { steamAuthMode } = useSteamAuth();
  const [optimizationsEnabled, setOptimizationsEnabled] = useState(false);
  const [gameCacheRefreshKey, setGameCacheRefreshKey] = useState(0);

  // Wrapper to refresh both stats and game cache
  const refreshStatsAndGameCache = () => {
    refreshStats();
    setGameCacheRefreshKey((prev) => prev + 1);
  };

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

    const handleLogRemovalComplete = async (payload: LogRemovalCompletePayload) => {
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

    const handleCorruptionRemovalComplete = async (payload: CorruptionRemovalCompletePayload) => {
      // Management-specific: Refresh LogAndCorruptionManager component after corruption removal
      if (payload.success) {
        // Refresh LogAndCorruptionManager to show updated corruption summary
        await refreshLogAndCorruptionRef.current();
      }
    };

    // Subscribe to management-specific events
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);
    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);

    // Cleanup: unsubscribe from all events
    return () => {
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
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

              <SteamWebApiStatus steamAuthMode={steamAuthMode} />

              <Suspense fallback={
                <Card>
                  <div className="flex items-center justify-center py-8">
                    <div className="text-themed-muted">Loading endpoints...</div>
                  </div>
                </Card>
              }>
                <GrafanaEndpoints />
              </Suspense>
            </CollapsibleSection>

            {/* Data Management Section - Default Open */}
            <CollapsibleSection title="Data Management" icon={HardDrive} defaultOpen>
              <DatabaseManager
                isAuthenticated={isAuthenticated}
                authMode={authMode}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={refreshStatsAndGameCache}
              />

              <DataImporter
                isAuthenticated={isAuthenticated}
                mockMode={mockMode}
                onError={addError}
                onSuccess={setSuccess}
                onDataRefresh={refreshStatsAndGameCache}
              />

              <Suspense fallback={
                <Card>
                  <div className="flex items-center justify-center py-8">
                    <div className="text-themed-muted">Loading cache configuration...</div>
                  </div>
                </Card>
              }>
                <CacheManager
                  isAuthenticated={isAuthenticated}
                  authMode={authMode}
                  mockMode={mockMode}
                  onError={addError}
                  onSuccess={setSuccess}
                />
              </Suspense>

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
                refreshKey={gameCacheRefreshKey}
              />
            </CollapsibleSection>

            {/* Preferences Section */}
            <CollapsibleSection title="Preferences" icon={Settings}>
              <ThemeManager isAuthenticated={isAuthenticated} />
            </CollapsibleSection>

            {/* Optimizations Section - Only show if enabled via environment variable */}
            {optimizationsEnabled && (
              <CollapsibleSection title="Optimizations" icon={Settings}>
                <Suspense fallback={
                  <Card>
                    <div className="flex items-center justify-center py-8">
                      <div className="text-themed-muted">Loading GC settings...</div>
                    </div>
                  </Card>
                }>
                  <GcManager isAuthenticated={isAuthenticated} />
                </Suspense>
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
                Management features are disabled in guest mode. Please authenticate to access full
                functionality.
              </p>
            </div>
          </Card>
        )}
      </div>
    </>
  );
};

export default ManagementTab;
