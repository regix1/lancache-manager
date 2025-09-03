import React, { useState, useEffect, useRef } from 'react';
import { FileText, RefreshCw, PlayCircle, StopCircle, Loader, CheckCircle } from 'lucide-react';
import ApiService from '../../services/api.service';
import { useBackendOperation } from '../../hooks/useBackendOperation';
import * as signalR from '@microsoft/signalr';

const LogProcessingManager = ({ 
    isAuthenticated, 
    mockMode, 
    onError, 
    onSuccess, 
    onDataRefresh 
}) => {
    const [isProcessingLogs, setIsProcessingLogs] = useState(false);
    const [processingStatus, setProcessingStatus] = useState(null);
    const [actionLoading, setActionLoading] = useState(false);
    
    const logProcessingOp = useBackendOperation('activeLogProcessing', 'logProcessing', 120);
    const signalRConnection = useRef(null);
    const pollingInterval = useRef(null);

    useEffect(() => {
        restoreLogProcessing();
        setupSignalR();
        
        return () => {
            if (pollingInterval.current) {
                clearInterval(pollingInterval.current);
            }
            if (signalRConnection.current) {
                signalRConnection.current.stop();
            }
        };
    }, []);

    const restoreLogProcessing = async () => {
        const logOp = await logProcessingOp.load();
        if (logOp?.data) {
            const status = await ApiService.getProcessingStatus().catch(() => null);
            if (status?.isProcessing) {
                setIsProcessingLogs(true);
                setProcessingStatus({
                    message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
                    detailMessage: status.processingRate ? `Speed: ${status.processingRate.toFixed(1)} MB/s` : '',
                    progress: status.percentComplete || 0,
                    estimatedTime: status.estimatedTime,
                    status: status.status
                });
                startProcessingPolling();
            } else {
                await logProcessingOp.clear();
            }
        }
    };

    const setupSignalR = async () => {
        try {
            const apiUrl = import.meta.env.VITE_API_URL || '';
            const connection = new signalR.HubConnectionBuilder()
                .withUrl(`${apiUrl}/hubs/downloads`)
                .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                .build();

            connection.on('ProcessingProgress', async (progress) => {
                console.log('Processing progress received:', progress);
                
                setProcessingStatus(prev => {
                    if (prev?.status === 'complete') {
                        return prev;
                    }
                    
                    return {
                        message: `Processing: ${progress.mbProcessed?.toFixed(1) || 0} MB of ${progress.mbTotal?.toFixed(1) || 0} MB`,
                        detailMessage: `${progress.entriesProcessed || 0} entries from ${progress.linesProcessed || 0} lines`,
                        progress: progress.percentComplete || 0,
                        status: 'processing'
                    };
                });
                
                setIsProcessingLogs(true);
                
                await logProcessingOp.update({ 
                    lastProgress: progress.percentComplete || 0,
                    mbProcessed: progress.mbProcessed,
                    mbTotal: progress.mbTotal,
                    entriesProcessed: progress.entriesProcessed,
                    linesProcessed: progress.linesProcessed
                });
            });

            connection.on('BulkProcessingComplete', async (result) => {
                console.log('Bulk processing complete:', result);
                
                if (pollingInterval.current) {
                    clearInterval(pollingInterval.current);
                }
                
                setProcessingStatus({
                    message: 'Processing Complete!',
                    detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString()} entries from ${result.linesProcessed?.toLocaleString()} lines in ${result.elapsed?.toFixed(1)} minutes`,
                    progress: 100,
                    status: 'complete'
                });
                
                setIsProcessingLogs(true);
                await logProcessingOp.clear();
                
                setTimeout(async () => {
                    setIsProcessingLogs(false);
                    setProcessingStatus(null);
                    onDataRefresh?.();
                }, 10000);
            });

            await connection.start();
            signalRConnection.current = connection;
            console.log('SignalR connected');
        } catch (err) {
            console.error('SignalR connection failed, falling back to polling:', err);
        }
    };

    const startProcessingPolling = () => {
        const checkStatus = async () => {
            try {
                const currentStatus = processingStatus;
                if (currentStatus?.status === 'complete') {
                    return;
                }
                
                const status = await ApiService.getProcessingStatus();
                if (status?.isProcessing) {
                    setIsProcessingLogs(true);
                    setProcessingStatus({
                        message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
                        detailMessage: status.processingRate ? `Speed: ${status.processingRate.toFixed(1)} MB/s` : '',
                        progress: status.percentComplete || 0,
                        estimatedTime: status.estimatedTime,
                        status: status.status
                    });
                    await logProcessingOp.update({ 
                        lastProgress: status.percentComplete || 0,
                        mbProcessed: status.mbProcessed,
                        mbTotal: status.mbTotal
                    });
                } else {
                    if (status?.percentComplete >= 100) {
                        if (pollingInterval.current) {
                            clearInterval(pollingInterval.current);
                        }
                        
                        setProcessingStatus({
                            message: 'Processing Complete!',
                            detailMessage: `Processed ${status.mbTotal?.toFixed(1) || 0} MB`,
                            progress: 100,
                            status: 'complete'
                        });
                        
                        setIsProcessingLogs(true);
                        await logProcessingOp.clear();
                        
                        setTimeout(() => {
                            setIsProcessingLogs(false);
                            setProcessingStatus(null);
                            onDataRefresh?.();
                        }, 10000);
                    } else {
                        setIsProcessingLogs(false);
                        await logProcessingOp.clear();
                        if (pollingInterval.current) {
                            clearInterval(pollingInterval.current);
                        }
                    }
                }
            } catch (err) {
                console.error('Error checking processing status:', err);
            }
        };
        
        checkStatus();
        pollingInterval.current = setInterval(checkStatus, 3000);
    };

    const handleResetLogs = async () => {
        if (!isAuthenticated) {
            onError?.('Authentication required');
            return;
        }

        setActionLoading(true);
        try {
            const result = await ApiService.resetLogPosition();
            if (result) {
                onSuccess?.(result.message || 'Log position reset successfully');
                setTimeout(() => onDataRefresh?.(), 2000);
            }
        } catch (err) {
            onError?.(err.message || 'Failed to reset log position');
        } finally {
            setActionLoading(false);
        }
    };

    const handleProcessAllLogs = async () => {
        if (!isAuthenticated) {
            onError?.('Authentication required');
            return;
        }

        if (!confirm('Process entire log file?')) return;

        setActionLoading(true);
        try {
            const result = await ApiService.processAllLogs();
            
            if (result) {
                if (result.status === 'empty_file' || result.status === 'no_log_file' || result.status === 'insufficient_data') {
                    onError?.(result.message);
                    setActionLoading(false);
                    return;
                }
                
                if (result.logSizeMB > 0) {
                    await logProcessingOp.save({ type: 'processAll' });
                    setIsProcessingLogs(true);
                    setProcessingStatus({
                        message: 'Preparing to process logs...',
                        detailMessage: `${result.logSizeMB?.toFixed(1) || 0} MB to process`,
                        progress: 0,
                        estimatedTime: `Estimated: ${result.estimatedTimeMinutes} minutes`,
                        status: 'starting'
                    });
                    setTimeout(() => startProcessingPolling(), 5000);
                } else {
                    onError?.('Log file appears to be empty or invalid');
                    await logProcessingOp.clear();
                }
            } else {
                await logProcessingOp.clear();
            }
        } catch (err) {
            await logProcessingOp.clear();
            onError?.(err.message || 'Failed to process logs');
        } finally {
            setActionLoading(false);
        }
    };

    const handleCancelProcessing = async () => {
        if (!isAuthenticated) {
            onError?.('Authentication required');
            return;
        }

        if (!confirm('Cancel log processing?')) return;
        
        setActionLoading(true);
        try {
            await ApiService.cancelProcessing();
            setIsProcessingLogs(false);
            await logProcessingOp.clear();
            if (pollingInterval.current) {
                clearInterval(pollingInterval.current);
            }
            onSuccess?.('Processing cancelled');
            setTimeout(() => {
                setProcessingStatus(null);
                onDataRefresh?.();
            }, 5000);
        } catch (err) {
            onError?.('Failed to cancel processing');
        } finally {
            setActionLoading(false);
        }
    };

    return (
        <>
            {/* Processing Status */}
            {isProcessingLogs && processingStatus && (
                <div className={`rounded-lg p-4 border ${
                    processingStatus.status === 'complete' 
                        ? 'bg-green-900 bg-opacity-30 border-green-700'
                        : 'bg-yellow-900 bg-opacity-30 border-yellow-700'
                }`}>
                    <div className="flex items-center justify-between">
                        <div className="flex items-center space-x-3 flex-1">
                            {processingStatus.status === 'complete' ? (
                                <CheckCircle className="w-5 h-5 text-green-500" />
                            ) : (
                                <Loader className="w-5 h-5 text-yellow-500 animate-spin" />
                            )}
                            <div className="flex-1">
                                <p className={`font-medium ${
                                    processingStatus.status === 'complete' ? 'text-green-400' : 'text-yellow-400'
                                }`}>
                                    {processingStatus.message}
                                </p>
                                {processingStatus.detailMessage && (
                                    <p className="text-sm text-gray-300 mt-1">{processingStatus.detailMessage}</p>
                                )}
                                {processingStatus.progress > 0 && processingStatus.status !== 'complete' && (
                                    <div className="mt-2">
                                        <div className="w-full bg-gray-700 rounded-full h-2">
                                            <div 
                                                className="bg-yellow-500 h-2 rounded-full transition-all duration-500"
                                                style={{ width: `${Math.min(processingStatus.progress, 100)}%` }}
                                            />
                                        </div>
                                        <p className="text-xs text-gray-400 mt-1">
                                            {processingStatus.progress.toFixed(1)}% complete
                                            {processingStatus.estimatedTime && ` • ${processingStatus.estimatedTime} remaining`}
                                        </p>
                                    </div>
                                )}
                            </div>
                        </div>
                        {processingStatus.status !== 'complete' && (
                            <button
                                onClick={handleCancelProcessing}
                                disabled={actionLoading}
                                className="flex items-center space-x-2 px-4 py-2 bg-red-600 hover:bg-red-700 rounded text-white font-medium disabled:opacity-50 ml-4"
                            >
                                <StopCircle className="w-4 h-4" />
                                <span>Cancel</span>
                            </button>
                        )}
                    </div>
                </div>
            )}

            {/* Log Processing Management */}
            <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
                <div className="flex items-center space-x-2 mb-4">
                    <FileText className="w-5 h-5 text-green-400" />
                    <h3 className="text-lg font-semibold text-white">Log Processing</h3>
                </div>
                <p className="text-gray-400 text-sm mb-4">
                    Control how access.log is processed for statistics
                </p>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                    <button
                        onClick={handleResetLogs}
                        disabled={actionLoading || isProcessingLogs || mockMode || logProcessingOp.loading || !isAuthenticated}
                        className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-yellow-600 hover:bg-yellow-700 disabled:opacity-50 transition-colors"
                    >
                        <RefreshCw className="w-4 h-4" />
                        <span>Reset Log Position</span>
                    </button>
                    <button
                        onClick={handleProcessAllLogs}
                        disabled={actionLoading || isProcessingLogs || mockMode || logProcessingOp.loading || !isAuthenticated}
                        className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-green-600 hover:bg-green-700 disabled:opacity-50 transition-colors"
                    >
                        {logProcessingOp.loading ? (
                            <Loader className="w-4 h-4 animate-spin" />
                        ) : (
                            <PlayCircle className="w-4 h-4" />
                        )}
                        <span>Process All Logs</span>
                    </button>
                </div>
                <div className="mt-4 p-3 bg-gray-700 rounded-lg">
                    <p className="text-xs text-gray-400">
                        <strong>Reset:</strong> Start from current end of log<br/>
                        <strong>Process All:</strong> Import entire log history
                    </p>
                </div>
            </div>

            {/* Backend Operation Error */}
            {logProcessingOp.error && (
                <div className="bg-orange-900 bg-opacity-30 rounded-lg p-4 border border-orange-700">
                    <p className="text-sm text-orange-400">
                        Backend storage error: {logProcessingOp.error}
                    </p>
                </div>
            )}
        </>
    );
};

export default LogProcessingManager;