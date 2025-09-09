import React, { useState, useEffect, useCallback, useRef } from 'react';
import { ToggleLeft, ToggleRight, AlertCircle, Database, Loader, FileText } from 'lucide-react';
import { useData } from '@contexts/DataContext';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import operationStateService from '@services/operationState.service';

// Import manager components
import AuthenticationManager from './AuthenticationManager';
import CacheManager from './CacheManager';
import LogProcessingManager from './LogProcessingManager';
import ThemeManager from './ThemeManager';
import AlertsManager from './AlertsManager';
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
      <div className="flex items-center justify-between">
        <div>
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
          leftSection={mockMode ? <ToggleRight className="w-5 h-5" /> : <ToggleLeft className="w-5 h-5" />}
        >
          {mockMode ? 'Enabled' : 'Disabled'}
        </Button>
      </div>
      {mockMode && (
        <div className="mt-4">
          <Alert color="blue">
            <div className="flex items-center space-x-2">
              <AlertCircle className="w-4 h-4" />
              <span className="text-sm">Mock mode active - API actions disabled</span>
            </div>
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
      <div className="flex items-center space-x-2 mb-4">
        <Database className="w-5 h-5 text-themed-accent" />
        <h3 className="text-lg font-semibold text-themed-primary">Database Management</h3>
      </div>
      <p className="text-themed-muted text-sm mb-4">
        Manage download history and statistics
      </p>
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
}> = ({ isAuthenticated, mockMode, onError, onSuccess, onDataRefresh }) => {
  const [serviceCounts, setServiceCounts] = useState<Record<string, number>>({});
  const [config, setConfig] = useState({ 
    logPath: '/logs/access.log',
    services: [] as string[]
  });
  const [activeServiceRemoval, setActiveServiceRemoval] = useState<string | null>(null);
  
  const serviceRemovalOp = useBackendOperation('activeServiceRemoval', 'serviceRemoval', 30);

  useEffect(() => {
    loadConfig();
    restoreServiceRemoval();
  }, []);

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
      setConfig({
        logPath: '/logs/access.log',
        services: ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']
      });
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
        loadConfig();
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
      setTimeout(() => {
        setActiveServiceRemoval(null);
        loadConfig();
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

  const services = config.services.length > 0 
    ? config.services 
    : ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'];

  return (
    <>
      {activeServiceRemoval && (
        <Alert color="orange">
          <div className="flex items-center space-x-3">
            <Loader className="w-5 h-5 animate-spin" />
            <div>
              <p className="font-medium">
                Removing {activeServiceRemoval} entries from logs...
              </p>
              <p className="text-sm mt-1">
                This may take several minutes for large log files
              </p>
            </div>
          </div>
        </Alert>
      )}

      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <FileText className="w-5 h-5 text-themed-accent" />
          <h3 className="text-lg font-semibold text-themed-primary">Log File Management</h3>
        </div>
        <p className="text-themed-muted text-sm mb-4">
          Remove service entries from <code className="bg-themed-tertiary px-2 py-1 rounded">{config.logPath}</code>
        </p>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {services.map(service => {
            const isRemoving = activeServiceRemoval === service;
            return (
              <Button
                key={service}
                onClick={() => handleRemoveServiceLogs(service)}
                disabled={mockMode || !!activeServiceRemoval || serviceRemovalOp.loading || !isAuthenticated}
                variant="default"
                loading={isRemoving || serviceRemovalOp.loading}
                className="flex flex-col items-center"
                fullWidth
              >
                {!isRemoving && !serviceRemovalOp.loading ? (
                  <>
                    <span className="capitalize font-medium">Clear {service}</span>
                    {serviceCounts[service] !== undefined && (
                      <span className="text-xs text-themed-muted mt-1">
                        ({serviceCounts[service].toLocaleString()} entries)
                      </span>
                    )}
                  </>
                ) : (
                  <span className="capitalize font-medium">Removing...</span>
                )}
              </Button>
            );
          })}
        </div>
        <div className="mt-4">
          <Alert color="yellow">
            <p className="text-xs">
              <strong>Warning:</strong> Requires write permissions to logs directory
            </p>
          </Alert>
        </div>
      </Card>

      {serviceRemovalOp.error && (
        <Alert color="orange">
          Backend storage error: {serviceRemovalOp.error}
        </Alert>
      )}
    </>
  );
};

// Main Management Tab Component
const ManagementTab: React.FC = () => {
  const { mockMode, setMockMode, fetchData } = useData();
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [alerts, setAlerts] = useState<{
    errors: Array<{ id: number; message: string }>;
    success: string | null;
  }>({ errors: [], success: null });
  
  // Use ref to ensure migration only happens once
  const hasMigratedRef = useRef(false);

  // Alert management
  const addError = useCallback((message: string) => {
    setAlerts(prev => ({
      ...prev,
      errors: [...prev.errors, { id: Date.now(), message }]
    }));
  }, []);

  const setSuccess = useCallback((message: string) => {
    setAlerts(prev => ({ ...prev, success: message }));
    setTimeout(() => setAlerts(prev => ({ ...prev, success: null })), 10000);
  }, []);

  const clearError = useCallback((id: number) => {
    setAlerts(prev => ({
      ...prev,
      errors: prev.errors.filter(e => e.id !== id)
    }));
  }, []);

  const clearSuccess = useCallback(() => {
    setAlerts(prev => ({ ...prev, success: null }));
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
      {/* Authentication */}
      <AuthenticationManager 
        onAuthChange={setIsAuthenticated}
        onError={addError}
        onSuccess={setSuccess}
      />

      {/* Alerts */}
      <AlertsManager 
        alerts={alerts}
        onClearError={clearError}
        onClearSuccess={clearSuccess}
      />

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

      {/* Cache Manager */}
      <CacheManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        onError={addError}
        onSuccess={setSuccess}
      />

      {/* Log Processing Manager */}
      <LogProcessingManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        onError={addError}
        onSuccess={setSuccess}
        onDataRefresh={fetchData}
      />

      {/* Log File Manager */}
      <LogFileManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        onError={addError}
        onSuccess={setSuccess}
        onDataRefresh={fetchData}
      />

      {/* Theme Manager */}
      <ThemeManager isAuthenticated={isAuthenticated} />
    </div>
  );
};

export default ManagementTab;