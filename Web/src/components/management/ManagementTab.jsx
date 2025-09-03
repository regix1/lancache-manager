import React, { useState, useEffect, useCallback } from 'react';
import { ToggleLeft, ToggleRight, AlertCircle, Database, Loader, FileText } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import ApiService from '../../services/api.service';
import { useBackendOperation } from '../../hooks/useBackendOperation';
import operationStateService from '../../services/operationState.service';

// Import all the new manager components
import AuthenticationManager from './AuthenticationManager';
import CacheManager from './CacheManager';
import LogProcessingManager from './LogProcessingManager';
import ThemeManager from './ThemeManager';
import AlertsManager from './AlertsManager';

const MockModeManager = ({ mockMode, onToggle, disabled }) => {
    return (
        <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
            <h3 className="text-lg font-semibold text-white mb-4">Mock Mode</h3>
            <div className="flex items-center justify-between">
                <div>
                    <p className="text-gray-300">Enable mock data for demonstration</p>
                    <p className="text-sm text-gray-500 mt-1">
                        Simulates realistic cache data and download activity
                    </p>
                </div>
                <button
                    onClick={onToggle}
                    disabled={disabled}
                    className="flex items-center space-x-2 px-4 py-2 rounded-lg bg-blue-600 hover:bg-blue-700 disabled:opacity-50 transition-colors"
                >
                    {mockMode ? <ToggleRight className="w-5 h-5" /> : <ToggleLeft className="w-5 h-5" />}
                    <span>{mockMode ? 'Enabled' : 'Disabled'}</span>
                </button>
            </div>
            {mockMode && (
                <div className="mt-4 p-3 bg-blue-900 bg-opacity-30 rounded-lg border border-blue-700">
                    <div className="flex items-center space-x-2 text-blue-400">
                        <AlertCircle className="w-4 h-4" />
                        <span className="text-sm">Mock mode active - API actions disabled</span>
                    </div>
                </div>
            )}
        </div>
    );
};

const DatabaseManager = ({ isAuthenticated, mockMode, onError, onSuccess, onDataRefresh }) => {
    const [loading, setLoading] = useState(false);

    const handleResetDatabase = async () => {
        if (!isAuthenticated) {
            onError?.('Authentication required');
            return;
        }

        if (!confirm('Delete all download history?')) return;

        setLoading(true);
        try {
            const result = await ApiService.resetDatabase();
            if (result) {
                onSuccess?.(result.message || 'Database reset successfully');
                setTimeout(() => onDataRefresh?.(), 2000);
            }
        } catch (err) {
            onError?.(err.message || 'Failed to reset database');
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
            <div className="flex items-center space-x-2 mb-4">
                <Database className="w-5 h-5 text-purple-400" />
                <h3 className="text-lg font-semibold text-white">Database Management</h3>
            </div>
            <p className="text-gray-400 text-sm mb-4">
                Manage download history and statistics
            </p>
            <button
                onClick={handleResetDatabase}
                disabled={loading || mockMode || !isAuthenticated}
                className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 transition-colors"
            >
                {loading ? <Loader className="w-4 h-4 animate-spin" /> : <Database className="w-4 h-4" />}
                <span>Reset Database</span>
            </button>
            <p className="text-xs text-gray-500 mt-2">
                Clears all download history (does not affect cached files)
            </p>
        </div>
    );
};

const LogFileManager = ({ isAuthenticated, mockMode, onError, onSuccess, onDataRefresh }) => {
    const [serviceCounts, setServiceCounts] = useState({});
    const [config, setConfig] = useState({ 
        logPath: '/logs/access.log',
        services: [] 
    });
    const [activeServiceRemoval, setActiveServiceRemoval] = useState(null);
    
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

    const handleRemoveServiceLogs = async (serviceName) => {
        if (!isAuthenticated) {
            onError?.('Authentication required');
            return;
        }

        if (!confirm(`Remove all ${serviceName} entries?`)) return;

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
        } catch (err) {
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
                <div className="bg-orange-900 bg-opacity-30 rounded-lg p-4 border border-orange-700">
                    <div className="flex items-center space-x-3">
                        <Loader className="w-5 h-5 text-orange-500 animate-spin" />
                        <div>
                            <p className="font-medium text-orange-400">
                                Removing {activeServiceRemoval} entries from logs...
                            </p>
                            <p className="text-sm text-gray-300 mt-1">
                                This may take several minutes for large log files
                            </p>
                        </div>
                    </div>
                </div>
            )}

            <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
                <div className="flex items-center space-x-2 mb-4">
                    <FileText className="w-5 h-5 text-orange-400" />
                    <h3 className="text-lg font-semibold text-white">Log File Management</h3>
                </div>
                <p className="text-gray-400 text-sm mb-4">
                    Remove service entries from <code className="bg-gray-700 px-2 py-1 rounded">{config.logPath}</code>
                </p>
                <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
                    {services.map(service => {
                        const isRemoving = activeServiceRemoval === service;
                        return (
                            <button
                                key={service}
                                onClick={() => handleRemoveServiceLogs(service)}
                                disabled={mockMode || activeServiceRemoval || serviceRemovalOp.loading || !isAuthenticated}
                                className={`px-4 py-3 rounded-lg transition-colors flex flex-col items-center ${
                                    isRemoving 
                                        ? 'bg-orange-700 cursor-not-allowed opacity-75' 
                                        : 'bg-gray-700 hover:bg-gray-600 disabled:opacity-50'
                                }`}
                            >
                                {isRemoving || serviceRemovalOp.loading ? (
                                    <>
                                        <Loader className="w-4 h-4 animate-spin mb-1" />
                                        <span className="capitalize font-medium">Removing...</span>
                                    </>
                                ) : (
                                    <>
                                        <span className="capitalize font-medium">Clear {service}</span>
                                        {serviceCounts[service] !== undefined && (
                                            <span className="text-xs text-gray-400 mt-1">
                                                ({serviceCounts[service].toLocaleString()} entries)
                                            </span>
                                        )}
                                    </>
                                )}
                            </button>
                        );
                    })}
                </div>
                <div className="mt-4 p-3 bg-yellow-900 bg-opacity-30 rounded-lg border border-yellow-700">
                    <p className="text-xs text-yellow-400">
                        <strong>Warning:</strong> Requires write permissions to logs directory
                    </p>
                </div>
            </div>

            {serviceRemovalOp.error && (
                <div className="bg-orange-900 bg-opacity-30 rounded-lg p-4 border border-orange-700">
                    <p className="text-sm text-orange-400">
                        Backend storage error: {serviceRemovalOp.error}
                    </p>
                </div>
            )}
        </>
    );
};

const ManagementTab = () => {
    const { mockMode, setMockMode, fetchData } = useData();
    const [isAuthenticated, setIsAuthenticated] = useState(false);
    const [alerts, setAlerts] = useState({ errors: [], success: null });
    const [hasMigrated, setHasMigrated] = useState(false);

    // Alert management
    const addError = useCallback((message) => {
        setAlerts(prev => ({
            ...prev,
            errors: [...prev.errors, { id: Date.now(), message }]
        }));
    }, []);

    const setSuccess = useCallback((message) => {
        setAlerts(prev => ({ ...prev, success: message }));
        setTimeout(() => setAlerts(prev => ({ ...prev, success: null })), 10000);
    }, []);

    const clearAlerts = useCallback(() => {
        setAlerts({ errors: [], success: null });
    }, []);

    const clearError = useCallback((id) => {
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
            if (!hasMigrated) {
                const migrated = await operationStateService.migrateFromLocalStorage();
                if (migrated > 0) {
                    setSuccess(`Migrated ${migrated} operations from local storage to server`);
                }
                setHasMigrated(true);
            }
        };
        
        initialize();
    }, [hasMigrated, setSuccess]);

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