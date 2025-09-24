import React, { useState, useEffect, useCallback, useRef } from 'react';
import {
  ToggleLeft,
  ToggleRight,
  Database,
  Loader,
  FileText,
  Eye,
  CheckCircle,
  StopCircle
} from 'lucide-react';
import { useData } from '@contexts/DataContext';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import operationStateService from '@services/operationState.service';
import { formatBytes } from '@utils/formatters';

// Import manager components
import AuthenticationManager from './AuthenticationManager';
import CacheManager from './CacheManager';
import LogProcessingManager from './LogProcessingManager';
import ThemeManager from './ThemeManager';
import AlertsManager from './AlertsManager';
import GrafanaEndpoints from './GrafanaEndpoints';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';

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
          className="w-full sm:w-auto"
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
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}> = ({ isAuthenticated, mockMode, onError, onSuccess, onDataRefresh }) => {
  const [loading, setLoading] = useState(false);

  const handleResetDatabase = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    if (!window.confirm('Delete all download history?')) return;

    setLoading(true);
    try {
      const result = await ApiService.resetDatabase();
      if (result) {
        onSuccess?.(result.message || 'Database reset successfully');
        setTimeout(() => onDataRefresh?.(), 2000);
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to reset database');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Card>
      <div className="flex items-center gap-2 mb-4">
        <Database className="w-5 h-5 text-themed-accent flex-shrink-0" />
        <h3 className="text-lg font-semibold text-themed-primary">Database Management</h3>
      </div>
      <p className="text-themed-muted text-sm mb-4 break-words">Manage download history and statistics</p>
      <Button
        onClick={handleResetDatabase}
        disabled={loading || mockMode || !isAuthenticated}
        loading={loading}
        variant="filled"
        color="red"
        leftSection={<Database className="w-4 h-4" />}
        fullWidth
      >
        Reset Database
      </Button>
      <p className="text-xs text-themed-muted mt-2">
        Clears all download history (does not affect cached files)
      </p>
    </Card>
  );
};

// Log File Manager Component
const LogFileManager: React.FC<{
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onBackgroundOperation?: (service: string | null) => void;
}> = ({ isAuthenticated, mockMode, onError, onSuccess, onDataRefresh, onBackgroundOperation }) => {
  const [serviceCounts, setServiceCounts] = useState<Record<string, number>>({});
  const [config, setConfig] = useState({
    logPath: '/logs/access.log',
    services: [] as string[]
  });
  const [activeServiceRemoval, setActiveServiceRemoval] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const serviceRemovalOp = useBackendOperation('activeServiceRemoval', 'serviceRemoval', 30);

  useEffect(() => {
    loadConfig();
    restoreServiceRemoval();
  }, []);

  useEffect(() => {
    onBackgroundOperation?.(activeServiceRemoval);
  }, [activeServiceRemoval]);

  const loadConfig = async () => {
    try {
      const [configData, counts] = await Promise.all([
        ApiService.getConfig(),
        ApiService.getServiceLogCounts()
      ]);
      setConfig(configData);
      setServiceCounts(counts);
    } catch (err) {
      console.error('Failed to load config:', err);
      // Only use fallback if we truly failed to load
      setConfig({
        logPath: '/logs/access.log',
        services: ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']
      });
    } finally {
      setIsLoading(false);
    }
  };

  const restoreServiceRemoval = async () => {
    const serviceOp = await serviceRemovalOp.load();
    if (serviceOp?.data?.service) {
      setActiveServiceRemoval(serviceOp.data.service);
      onSuccess?.(`Removing ${serviceOp.data.service} entries from logs (operation resumed)...`);
      setTimeout(async () => {
        await serviceRemovalOp.clear();
        setActiveServiceRemoval(null);
        // Reload config without showing loading state
        try {
          const [configData, counts] = await Promise.all([
            ApiService.getConfig(),
            ApiService.getServiceLogCounts()
          ]);
          setConfig(configData);
          setServiceCounts(counts);
        } catch (err) {
          console.error('Failed to reload config:', err);
        }
        onDataRefresh?.();
      }, 10000);
    }
  };

  const handleRemoveServiceLogs = async (serviceName: string) => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    if (!window.confirm(`Remove all ${serviceName} entries?`)) return;

    try {
      setActiveServiceRemoval(serviceName);
      await serviceRemovalOp.save({ service: serviceName });
      const result = await ApiService.removeServiceFromLogs(serviceName);

      if (result) {
        onSuccess?.(result.message || `${serviceName} entries removed successfully`);
      }

      await serviceRemovalOp.clear();
      setTimeout(async () => {
        setActiveServiceRemoval(null);
        // Reload config without showing loading state
        try {
          const [configData, counts] = await Promise.all([
            ApiService.getConfig(),
            ApiService.getServiceLogCounts()
          ]);
          setConfig(configData);
          setServiceCounts(counts);
        } catch (err) {
          console.error('Failed to reload config:', err);
        }
        onDataRefresh?.();
      }, 2000);
    } catch (err: any) {
      await serviceRemovalOp.clear();
      setActiveServiceRemoval(null);

      const errorMessage = err.message?.includes('read-only')
        ? 'Logs directory is read-only. Remove :ro from docker-compose volume mount.'
        : err.message || 'Action failed';
      onError?.(errorMessage);
    }
  };

  const services = config.services.length > 0 ? config.services : [];

  return (
    <Card>
      <div className="flex items-center gap-2 mb-4">
        <FileText className="w-5 h-5 text-themed-accent flex-shrink-0" />
        <h3 className="text-lg font-semibold text-themed-primary">Log File Management</h3>
      </div>
      <p className="text-themed-muted text-sm mb-4 break-words">
        Remove service entries from{' '}
        <code className="bg-themed-tertiary px-2 py-1 rounded text-xs break-all">{config.logPath}</code>
      </p>
      {isLoading ? (
        <div className="flex items-center justify-center py-8">
          <Loader className="w-6 h-6 animate-spin text-themed-muted" />
        </div>
      ) : services.length > 0 ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {services.map((service) => {
          const isRemoving = activeServiceRemoval === service;
          return (
            <Button
              key={service}
              onClick={() => handleRemoveServiceLogs(service)}
              disabled={
                mockMode || !!activeServiceRemoval || serviceRemovalOp.loading || !isAuthenticated
              }
              variant="default"
              loading={isRemoving || serviceRemovalOp.loading}
              className="flex flex-col items-center min-h-[60px] justify-center"
              fullWidth
            >
              {!isRemoving && !serviceRemovalOp.loading ? (
                <>
                  <span className="capitalize font-medium text-sm sm:text-base">Clear {service}</span>
                  {serviceCounts[service] !== undefined && (
                    <span className="text-xs text-themed-muted mt-1">
                      ({serviceCounts[service].toLocaleString()} entries)
                    </span>
                  )}
                </>
              ) : (
                <span className="capitalize font-medium text-sm sm:text-base">Removing...</span>
              )}
            </Button>
          );
          })}
        </div>
      ) : (
        <div className="text-center py-8 text-themed-muted">
          No services configured
        </div>
      )}
      <div className="mt-4">
        <Alert color="yellow">
          <p className="text-xs">
            <strong>Warning:</strong> Requires write permissions to logs directory
          </p>
        </Alert>
      </div>
    </Card>
  );
};

// Main Management Tab Component
const ManagementTab: React.FC = () => {
  const { mockMode, setMockMode, fetchData } = useData();
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [alerts, setAlerts] = useState<{
    errors: { id: number; message: string }[];
    success: string | null;
  }>({ errors: [], success: null });

  // State for background operations from child components
  const [backgroundOperations, setBackgroundOperations] = useState<{
    cacheClearing?: any;
    logProcessing?: any;
    serviceRemoval?: string | null;
  }>({});

  // Use ref to ensure migration only happens once
  const hasMigratedRef = useRef(false);

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

  return (
    <div className="space-y-6">
      {/* Authentication - Always at top */}
      <AuthenticationManager
        onAuthChange={setIsAuthenticated}
        onError={addError}
        onSuccess={setSuccess}
      />

      {/* All Notifications Consolidated Here */}
      <div className="space-y-4">
        {/* Regular Alerts */}
        <AlertsManager alerts={alerts} onClearError={clearError} onClearSuccess={clearSuccess} />

        {/* Cache Clearing Background Operation */}
        {backgroundOperations.cacheClearing && (
          <Alert color="blue" icon={<Loader className="w-5 h-5 animate-spin" />}>
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
              <div className="flex-1">
                <p className="font-medium">Cache clearing in progress...</p>
                {backgroundOperations.cacheClearing.bytesDeleted > 0 && (
                  <p className="text-sm mt-1 opacity-75">
                    {formatBytes(backgroundOperations.cacheClearing.bytesDeleted)} cleared
                  </p>
                )}
                <p className="text-sm mt-1 opacity-75">
                  {(backgroundOperations.cacheClearing.progress || 0).toFixed(0)}% complete
                </p>
              </div>
              <Button
                variant="filled"
                color="blue"
                size="sm"
                leftSection={<Eye className="w-4 h-4" />}
                onClick={backgroundOperations.cacheClearing.showModal}
                className="w-full sm:w-auto"
              >
                View Details
              </Button>
            </div>
          </Alert>
        )}

        {/* Log Processing Background Operation */}
        {backgroundOperations.logProcessing && (
          <Alert
            color={backgroundOperations.logProcessing.status === 'complete' ? 'green' : 'yellow'}
            icon={
              backgroundOperations.logProcessing.status === 'complete' ? (
                <CheckCircle className="w-5 h-5" />
              ) : (
                <Loader className="w-5 h-5 animate-spin" />
              )
            }
          >
            <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
              <div className="flex-1">
                <p className="font-medium break-words">{backgroundOperations.logProcessing.message}</p>
                {backgroundOperations.logProcessing.detailMessage && (
                  <p className="text-sm mt-1 opacity-75 break-words">
                    {backgroundOperations.logProcessing.detailMessage}
                  </p>
                )}
                {backgroundOperations.logProcessing.progress > 0 &&
                  backgroundOperations.logProcessing.status !== 'complete' && (
                    <div className="mt-2">
                      <div className="w-full progress-track rounded-full h-2">
                        <div
                          className="progress-bar-low h-2 rounded-full smooth-transition"
                          style={{
                            width: `${Math.min(backgroundOperations.logProcessing.progress, 100)}%`
                          }}
                        />
                      </div>
                      <p className="text-xs opacity-75 mt-1 break-words">
                        {backgroundOperations.logProcessing.progress.toFixed(1)}% complete
                        {backgroundOperations.logProcessing.estimatedTime &&
                          ` • ${backgroundOperations.logProcessing.estimatedTime} remaining`}
                      </p>
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
                    Cancel
                  </Button>
                )}
            </div>
          </Alert>
        )}

        {/* Service Removal Background Operation */}
        {backgroundOperations.serviceRemoval && (
          <Alert color="orange" icon={<Loader className="w-5 h-5 animate-spin" />}>
            <div>
              <p className="font-medium">
                Removing {backgroundOperations.serviceRemoval} entries from logs...
              </p>
              <p className="text-sm mt-1">This may take several minutes for large log files</p>
            </div>
          </Alert>
        )}
      </div>

      {/* Mock Mode */}
      <MockModeManager
        mockMode={mockMode}
        onToggle={() => setMockMode(!mockMode)}
        disabled={false}
      />

      {/* Database Manager */}
      <DatabaseManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        onError={addError}
        onSuccess={setSuccess}
        onDataRefresh={fetchData}
      />

      {/* Cache Manager - Pass notification callback */}
      <CacheManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        onError={addError}
        onSuccess={setSuccess}
        onBackgroundOperation={(op) =>
          setBackgroundOperations((prev) => ({ ...prev, cacheClearing: op }))
        }
      />

      {/* Log Processing Manager - Pass notification callback */}
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

      {/* Log File Manager - Pass notification callback */}
      <LogFileManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        onError={addError}
        onSuccess={setSuccess}
        onDataRefresh={fetchData}
        onBackgroundOperation={(service) =>
          setBackgroundOperations((prev) => ({ ...prev, serviceRemoval: service }))
        }
      />

      {/* Grafana Endpoints */}
      <GrafanaEndpoints />

      {/* Theme Manager */}
      <ThemeManager isAuthenticated={isAuthenticated} />
    </div>
  );
};

export default ManagementTab;
