import React, { useState, useEffect } from 'react';
import { HardDrive, Trash2, Loader, CheckCircle, AlertCircle, StopCircle, Eye } from 'lucide-react';
import ApiService from '../../services/api.service';
import { useBackendOperation } from '../../hooks/useBackendOperation';
import { formatBytes } from '../../utils/formatters';

const CacheManager = ({ isAuthenticated, mockMode, onError, onSuccess }) => {
    const [cacheClearProgress, setCacheClearProgress] = useState(null);
    const [showCacheClearModal, setShowCacheClearModal] = useState(false);
    const [actionLoading, setActionLoading] = useState(false);
    const [config, setConfig] = useState({ cachePath: '/cache' });
    
    const cacheOp = useBackendOperation('activeCacheClearOperation', 'cacheClearing', 30);
    
    useEffect(() => {
        loadConfig();
        restoreCacheOperation();
    }, []);

    const loadConfig = async () => {
        try {
            const configData = await ApiService.getConfig();
            setConfig(configData);
        } catch (err) {
            console.error('Failed to load config:', err);
        }
    };

    const restoreCacheOperation = async () => {
        const cacheClear = await cacheOp.load();
        if (cacheClear?.data?.operationId) {
            try {
                const status = await ApiService.getCacheClearStatus(cacheClear.data.operationId);
                if (status && ['Running', 'Preparing'].includes(status.status)) {
                    setCacheClearProgress(status);
                    startCacheClearPolling(cacheClear.data.operationId);
                } else {
                    await cacheOp.clear();
                }
            } catch (err) {
                await cacheOp.clear();
            }
        }
    };

    const startCacheClearPolling = (operationId) => {
        const pollInterval = setInterval(async () => {
            try {
                const status = await ApiService.getCacheClearStatus(operationId);
                setCacheClearProgress(status);
                
                if (['Running', 'Preparing'].includes(status.status)) {
                    await cacheOp.update({ lastProgress: status.percentComplete || 0 });
                } else {
                    handleCacheClearComplete(status);
                    clearInterval(pollInterval);
                }
            } catch (err) {
                console.error('Error polling cache clear status:', err);
                clearInterval(pollInterval);
            }
        }, 1000);
    };

    const handleCacheClearComplete = async (progress) => {
        await cacheOp.clear();
        
        if (progress.status === 'Completed') {
            onSuccess?.(`Cache cleared successfully! ${formatBytes(progress.bytesDeleted || 0)} freed.`);
            setTimeout(() => {
                setShowCacheClearModal(false);
                setCacheClearProgress(null);
            }, 2000);
        } else if (progress.status === 'Failed') {
            onError?.(`Cache clearing failed: ${progress.error || 'Unknown error'}`);
            setTimeout(() => {
                setShowCacheClearModal(false);
                setCacheClearProgress(null);
            }, 5000);
        } else if (progress.status === 'Cancelled') {
            onSuccess?.('Cache clearing cancelled');
            setShowCacheClearModal(false);
            setCacheClearProgress(null);
        }
    };

    const handleClearAllCache = async () => {
        if (!isAuthenticated) {
            onError?.('Authentication required');
            return;
        }
        
        if (!confirm('This will clear ALL cached game files. Continue?')) return;
        
        setActionLoading(true);
        
        try {
            const result = await ApiService.clearAllCache();
            if (result.operationId) {
                await cacheOp.save({ operationId: result.operationId });
                setCacheClearProgress({
                    operationId: result.operationId,
                    status: 'Preparing',
                    statusMessage: 'Starting cache clear...',
                    percentComplete: 0,
                    bytesDeleted: 0,
                    totalBytesToDelete: 0
                });
                setShowCacheClearModal(true);
                startCacheClearPolling(result.operationId);
            }
        } catch (err) {
            onError?.('Failed to start cache clearing: ' + err.message);
        } finally {
            setActionLoading(false);
        }
    };

    const handleCancelCacheClear = async () => {
        if (!cacheOp.operation?.data?.operationId) return;
        
        try {
            setCacheClearProgress(prev => ({ 
                ...prev, 
                status: 'Cancelling',
                statusMessage: 'Cancelling operation...'
            }));
            
            await ApiService.cancelCacheClear(cacheOp.operation.data.operationId);
            await cacheOp.clear();
            
            setTimeout(() => {
                setShowCacheClearModal(false);
                setCacheClearProgress(null);
                onSuccess?.('Cache clearing operation cancelled');
            }, 1500);
        } catch (err) {
            console.error('Failed to cancel cache clear:', err);
            setShowCacheClearModal(false);
            setCacheClearProgress(null);
            await cacheOp.clear();
        }
    };

    const isCacheClearingInBackground = cacheOp.operation?.data && 
        !showCacheClearModal && 
        cacheClearProgress && 
        ['Running', 'Preparing'].includes(cacheClearProgress.status);

    return (
        <>
            {/* Background Operation Status */}
            {isCacheClearingInBackground && (
                <div className="bg-blue-900 bg-opacity-30 rounded-lg p-4 border border-blue-700">
                    <div className="flex items-center justify-between">
                        <div className="flex items-center space-x-3 flex-1">
                            <Loader className="w-5 h-5 text-blue-500 animate-spin" />
                            <div className="flex-1">
                                <p className="font-medium text-blue-400">Cache clearing in progress...</p>
                                {cacheClearProgress.bytesDeleted > 0 && (
                                    <p className="text-sm text-gray-300 mt-1">
                                        {formatBytes(cacheClearProgress.bytesDeleted)} cleared
                                    </p>
                                )}
                                <div className="flex items-center space-x-4 mt-1">
                                    <span className="text-sm text-gray-300">
                                        {cacheClearProgress.percentComplete?.toFixed(0)}% complete
                                    </span>
                                </div>
                            </div>
                        </div>
                        <button
                            onClick={() => setShowCacheClearModal(true)}
                            className="flex items-center space-x-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded text-white font-medium ml-4"
                        >
                            <Eye className="w-4 h-4" />
                            <span>View Details</span>
                        </button>
                    </div>
                </div>
            )}

            {/* Main Cache Management Section */}
            <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
                <div className="flex items-center space-x-2 mb-4">
                    <HardDrive className="w-5 h-5 text-blue-400" />
                    <h3 className="text-lg font-semibold text-white">Disk Cache Management</h3>
                </div>
                <p className="text-gray-400 text-sm mb-4">
                    Manage cached game files in <code className="bg-gray-700 px-2 py-1 rounded">{config.cachePath}</code>
                </p>
                <button
                    onClick={handleClearAllCache}
                    disabled={actionLoading || mockMode || isCacheClearingInBackground || cacheOp.loading || !isAuthenticated}
                    className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 transition-colors"
                >
                    {actionLoading || cacheOp.loading ? (
                        <Loader className="w-4 h-4 animate-spin" />
                    ) : (
                        <Trash2 className="w-4 h-4" />
                    )}
                    <span>
                        {isCacheClearingInBackground ? 'Cache Clearing in Progress...' : 'Clear All Cached Files'}
                    </span>
                </button>
                <p className="text-xs text-gray-500 mt-2">
                    ⚠️ This deletes ALL cached game files from disk
                </p>
            </div>

            {/* Cache Clear Modal */}
            {showCacheClearModal && cacheClearProgress && (
                <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
                    <div className="bg-gray-800 rounded-lg p-6 max-w-lg w-full mx-4 border border-gray-700">
                        <h3 className="text-lg font-semibold text-white mb-4">Clearing Cache</h3>
                        
                        <div className="space-y-4">
                            <div className="flex items-center space-x-2">
                                {cacheClearProgress.status === 'Completed' ? (
                                    <CheckCircle className="w-5 h-5 text-green-500" />
                                ) : cacheClearProgress.status === 'Failed' ? (
                                    <AlertCircle className="w-5 h-5 text-red-500" />
                                ) : (
                                    <Loader className="w-5 h-5 text-blue-500 animate-spin" />
                                )}
                                <div className="flex-1">
                                    <span className="text-white">{cacheClearProgress.status}</span>
                                    {cacheClearProgress.statusMessage && (
                                        <p className="text-sm text-gray-400">{cacheClearProgress.statusMessage}</p>
                                    )}
                                </div>
                            </div>
                            
                            {['Running', 'Preparing'].includes(cacheClearProgress.status) && (
                                <>
                                    <div className="w-full bg-gray-700 rounded-full h-4 relative overflow-hidden">
                                        <div 
                                            className="bg-gradient-to-r from-blue-500 to-blue-600 h-4 rounded-full transition-all duration-500"
                                            style={{ width: `${Math.min(100, cacheClearProgress.percentComplete || 0)}%` }}
                                        />
                                        <div className="absolute inset-0 flex items-center justify-center">
                                            <span className="text-xs text-white font-medium">
                                                {(cacheClearProgress.percentComplete || 0).toFixed(0)}%
                                            </span>
                                        </div>
                                    </div>
                                    
                                    {cacheClearProgress.totalBytesToDelete > 0 && (
                                        <div className="text-sm text-gray-400 text-center">
                                            <span className="text-green-400 font-semibold">
                                                {formatBytes(cacheClearProgress.bytesDeleted || 0)}
                                            </span>
                                            {' / '}
                                            <span className="text-white">
                                                {formatBytes(cacheClearProgress.totalBytesToDelete)}
                                            </span>
                                            {' cleared'}
                                        </div>
                                    )}
                                </>
                            )}
                            
                            {cacheClearProgress.error && (
                                <div className="p-3 bg-red-900 bg-opacity-30 rounded border border-red-700">
                                    <p className="text-sm text-red-400">{cacheClearProgress.error}</p>
                                </div>
                            )}
                            
                            <div className="flex justify-end space-x-3 pt-4 border-t border-gray-700">
                                {['Running', 'Preparing'].includes(cacheClearProgress.status) ? (
                                    <>
                                        <button
                                            onClick={handleCancelCacheClear}
                                            className="px-4 py-2 bg-red-600 hover:bg-red-700 rounded text-white flex items-center space-x-2"
                                        >
                                            <StopCircle className="w-4 h-4" />
                                            <span>Cancel</span>
                                        </button>
                                        <button
                                            onClick={() => setShowCacheClearModal(false)}
                                            className="px-4 py-2 bg-gray-600 hover:bg-gray-700 rounded text-white"
                                        >
                                            Run in Background
                                        </button>
                                    </>
                                ) : (
                                    <button
                                        onClick={() => {
                                            setShowCacheClearModal(false);
                                            setCacheClearProgress(null);
                                        }}
                                        className="px-4 py-2 bg-gray-600 hover:bg-gray-700 rounded text-white"
                                    >
                                        Close
                                    </button>
                                )}
                            </div>
                        </div>
                    </div>
                </div>
            )}

            {/* Backend Operation Error */}
            {cacheOp.error && (
                <div className="bg-orange-900 bg-opacity-30 rounded-lg p-4 border border-orange-700">
                    <p className="text-sm text-orange-400">
                        Backend storage error: {cacheOp.error}
                    </p>
                </div>
            )}
        </>
    );
};

export default CacheManager;