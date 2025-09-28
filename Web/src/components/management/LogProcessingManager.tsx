import React, { useState, useEffect, useRef } from 'react';
import { FileText, RefreshCw, PlayCircle, X, Database } from 'lucide-react';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import * as signalR from '@microsoft/signalr';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { SIGNALR_BASE } from '@utils/constants';
import type { ProcessingStatus as ApiProcessingStatus } from '../../types';

interface LogProcessingManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onBackgroundOperation?: (operation: any) => void;
}

interface ProcessingUIStatus {
  message: string;
  detailMessage?: string;
  progress: number;
  estimatedTime?: string;
  status: string;
}

interface DepotMappingProgress {
  isProcessing: boolean;
  totalMappings: number;
  processedMappings: number;
  mappingsApplied?: number;
  percentComplete: number;
  status: string;
  message: string;
}

interface PicsProgress {
  isRunning: boolean;
  status: string;
  totalApps: number;
  processedApps: number;
  totalBatches: number;
  processedBatches: number;
  progressPercent: number;
  depotMappingsFound: number;
  isReady: boolean;
  lastCrawlTime?: string;
  nextCrawlIn: { totalHours: number };
  isConnected: boolean;
  isLoggedOn: boolean;
}

const LogProcessingManager: React.FC<LogProcessingManagerProps> = ({
  isAuthenticated,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh,
  onBackgroundOperation
}) => {
  const [isProcessingLogs, setIsProcessingLogs] = useState(false);
  const [processingStatus, setProcessingStatus] = useState<ProcessingUIStatus | null>(null);
  const [actionLoading, setActionLoading] = useState(false);
  const [signalRConnected, setSignalRConnected] = useState(false);
  const [depotProcessing, setDepotProcessing] = useState<PicsProgress | null>(null);
  const [depotMappingProgress, setDepotMappingProgress] = useState<DepotMappingProgress | null>(null);
  const [showPostDepotPopup, setShowPostDepotPopup] = useState(false);
  const [postDepotTimer, setPostDepotTimer] = useState(60);

  const logProcessingOp = useBackendOperation('activeLogProcessing', 'logProcessing', 120);
  const signalRConnection = useRef<signalR.HubConnection | null>(null);
  const pollingInterval = useRef<NodeJS.Timeout | null>(null);
  const reconnectTimeout = useRef<NodeJS.Timeout | null>(null);
  const depotPollingInterval = useRef<NodeJS.Timeout | null>(null);
  const postDepotPopupInterval = useRef<NodeJS.Timeout | null>(null);
  const onBackgroundOperationRef = useRef(onBackgroundOperation);

  // Keep the ref up to date
  useEffect(() => {
    onBackgroundOperationRef.current = onBackgroundOperation;
  });

  // Report processing status to parent
  useEffect(() => {
    if (isProcessingLogs && processingStatus && onBackgroundOperationRef.current) {
      onBackgroundOperationRef.current({
        message: processingStatus.message,
        detailMessage: processingStatus.detailMessage,
        progress: processingStatus.progress,
        estimatedTime: processingStatus.estimatedTime,
        status: processingStatus.status,
        onCancel: handleCancelProcessing
      });
    } else if (onBackgroundOperationRef.current) {
      onBackgroundOperationRef.current(null);
    }
  }, [isProcessingLogs, processingStatus]);

  useEffect(() => {
    restoreLogProcessing();
    setupSignalR();
    startDepotPolling();

    return () => {
      if (pollingInterval.current) {
        clearInterval(pollingInterval.current);
      }
      if (reconnectTimeout.current) {
        clearTimeout(reconnectTimeout.current);
      }
      if (depotPollingInterval.current) {
        clearInterval(depotPollingInterval.current);
      }
      if (postDepotPopupInterval.current) {
        clearInterval(postDepotPopupInterval.current);
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
          detailMessage: status.processingRate
            ? `Speed: ${status.processingRate.toFixed(1)} MB/s`
            : '',
          progress: status.percentComplete || status.progress || 0,
          estimatedTime: status.estimatedTime,
          status: status.status || 'processing'
        });
        startProcessingPolling();
      } else {
        await logProcessingOp.clear();
        if (status) {
          setProcessingStatus({
            message: 'Processing Complete!',
            detailMessage: `Processed ${status.mbTotal?.toFixed(1) || 0} MB`,
            progress: 100,
            status: 'complete'
          });
          setTimeout(() => {
            setIsProcessingLogs(false);
            setProcessingStatus(null);
            onDataRefresh?.();
          }, 3000);
        } else {
          setProcessingStatus(null);
          setIsProcessingLogs(false);
        }
      }
    }
  };

  const setupSignalR = async () => {
    try {
      const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${SIGNALR_BASE}/downloads`)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Information)
        .build();

      // IMPORTANT: Add all event handlers BEFORE starting the connection
      connection.on('ProcessingProgress', async (progress: any) => {
        console.log('SignalR ProcessingProgress received:', progress);
        const currentProgress = progress.percentComplete || progress.progress || 0;
        const status = progress.status || 'processing';

        setProcessingStatus(() => {
          // If progress is 100% or status is complete/finalizing, mark as complete
          if (currentProgress >= 100 || status === 'complete') {
            // Stop polling when we hit 100% via progress update
            if (pollingInterval.current) {
              clearInterval(pollingInterval.current);
            }
            return {
              message: 'Processing Complete!',
              detailMessage: `Successfully processed ${progress.entriesProcessed || 0} entries from ${progress.linesProcessed || 0} lines`,
              progress: 100,
              status: 'complete'
            };
          }

          // Handle finalizing status
          if (status === 'finalizing') {
            return {
              message: progress.message || 'Finalizing log processing...',
              detailMessage: `${progress.entriesProcessed || 0} entries from ${progress.linesProcessed || 0} lines`,
              progress: currentProgress,
              status: 'finalizing'
            };
          }

          return {
            message: `Processing: ${progress.mbProcessed?.toFixed(1) || 0} MB of ${progress.mbTotal?.toFixed(1) || 0} MB`,
            detailMessage: `${progress.entriesProcessed || 0} entries from ${progress.linesProcessed || 0} lines`,
            progress: currentProgress,
            status: 'processing'
          };
        });

        setIsProcessingLogs(true);

        await logProcessingOp.update({
          lastProgress: progress.percentComplete || progress.progress || 0,
          mbProcessed: progress.mbProcessed,
          mbTotal: progress.mbTotal,
          entriesProcessed: progress.entriesProcessed,
          linesProcessed: progress.linesProcessed
        });
      });

      connection.on('BulkProcessingComplete', async (result: any) => {
        console.log('SignalR BulkProcessingComplete received:', result);
        // Stop polling immediately when we receive completion signal
        if (pollingInterval.current) {
          clearInterval(pollingInterval.current);
        }

        const depotMappingsProcessed = result.depotMappingsProcessed ?? 0;
        setProcessingStatus({
          message: 'Processing Complete!',
          detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString()} entries from ${result.linesProcessed?.toLocaleString()} lines in ${result.elapsed?.toFixed(1)} minutes. Applied ${depotMappingsProcessed.toLocaleString()} depot mappings automatically.`,
          progress: 100,
          status: 'complete'
        });

        setIsProcessingLogs(true);
        await logProcessingOp.clear();
        if (depotMappingsProcessed > 0) {
          onSuccess?.(`Depot mappings applied to ${depotMappingsProcessed.toLocaleString()} downloads.`);
        }

        // Show completion for 3 seconds instead of 10, then stop completely
        setTimeout(async () => {
          setIsProcessingLogs(false);
          setProcessingStatus(null);
          onDataRefresh?.();
        }, 3000);
      });


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
            onDataRefresh?.();
          }, 5000);
        }
      });

      connection.on('DepotPostProcessingFailed', (payload: any) => {
        setDepotMappingProgress(null);
        onError?.(payload?.error
          ? `Depot mapping post-processing failed: ${payload.error}`
          : 'Depot mapping post-processing failed.');
      });

      connection.onreconnecting(() => {
        console.log('SignalR reconnecting...');
        setSignalRConnected(false);
      });

      connection.onreconnected(() => {
        console.log('SignalR reconnected successfully');
        setSignalRConnected(true);
      });

      connection.onclose((error) => {
        console.error('SignalR disconnected:', error);
        setSignalRConnected(false);

        if (isProcessingLogs) {
          console.log('Starting polling fallback due to SignalR disconnect');
          startProcessingPolling();
        }

        reconnectTimeout.current = setTimeout(() => {
          console.log('Attempting to reconnect SignalR...');
          setupSignalR();
        }, 5000);
      });

      console.log('Starting SignalR connection...');
      await connection.start();
      console.log('SignalR connection started successfully, connection ID:', connection.connectionId);
      signalRConnection.current = connection;
      setSignalRConnected(true);
    } catch (err) {
      console.error('SignalR connection failed, falling back to polling:', err);
      setSignalRConnected(false);

      if (isProcessingLogs) {
        startProcessingPolling();
      }
    }
  };

  const startProcessingPolling = () => {
    if (pollingInterval.current) {
      clearInterval(pollingInterval.current);
    }

    const checkStatus = async () => {
      try {

        const status: ApiProcessingStatus = await ApiService.getProcessingStatus();
        if (status?.isProcessing) {
          setIsProcessingLogs(true);
          setProcessingStatus({
            message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
            detailMessage: status.processingRate
              ? `Speed: ${status.processingRate.toFixed(1)} MB/s`
              : '',
            progress: status.percentComplete || status.progress || 0,
            estimatedTime: status.estimatedTime,
            status: status.status || 'processing'
          });
          await logProcessingOp.update({
            lastProgress: status.percentComplete || status.progress || 0,
            mbProcessed: status.mbProcessed,
            mbTotal: status.mbTotal
          });
        } else {
          // Processing is complete - stop polling immediately
          if (pollingInterval.current) {
            clearInterval(pollingInterval.current);
          }

          const finalProgress = status?.percentComplete || status?.progress || 0;
          const reachedEnd = status?.currentPosition && status?.totalSize &&
            status.currentPosition >= status.totalSize;
          const isComplete = finalProgress >= 100 ||
                            status?.status === 'complete' ||
                            reachedEnd;
          const isAlmostComplete = !status?.isProcessing && finalProgress >= 99;

          console.log('Processing complete detected via polling:', {
            finalProgress,
            isComplete,
            isAlmostComplete,
            status: status?.status,
            isProcessing: status?.isProcessing
          });

          if (isComplete || isAlmostComplete) {
            console.log('Forcing completion via polling fallback');
            setProcessingStatus({
              message: 'Processing Complete!',
              detailMessage: `Successfully processed ${status.entriesProcessed?.toLocaleString() || 0} entries from ${status.linesProcessed?.toLocaleString() || 0} lines`,
              progress: 100,
              status: 'complete'
            });

            setIsProcessingLogs(true);
            await logProcessingOp.clear();

            // Show completion for 3 seconds, then stop
            setTimeout(() => {
              setIsProcessingLogs(false);
              setProcessingStatus(null);
              onDataRefresh?.();
            }, 3000);
          } else {
            // Not processing and not complete - just stop
            setIsProcessingLogs(false);
            setProcessingStatus(null);
            await logProcessingOp.clear();
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
    } catch (err: any) {
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

    if (!window.confirm('Process entire log file?')) return;

    setActionLoading(true);
    try {
      const result = await ApiService.processAllLogs();

      if (result) {
        if (
          result.status === 'empty_file' ||
          result.status === 'no_log_file' ||
          result.status === 'insufficient_data'
        ) {
          onError?.(result.message);
          setActionLoading(false);
          return;
        }

        if (result.logSizeMB > 0) {
          await logProcessingOp.save({ type: 'processAll', resume: result.resume });
          const remainingMBRaw = typeof result.remainingMB === 'number' ? result.remainingMB : result.logSizeMB || 0;
          const processedMB = Math.max(0, (result.logSizeMB || 0) - remainingMBRaw);
          const initialProgress = result.resume && (result.logSizeMB || 0) > 0
            ? Math.min(99, Math.max(0, (processedMB / (result.logSizeMB || 1)) * 100))
            : 0;

          setIsProcessingLogs(true);
          setProcessingStatus({
            message: result.resume ? 'Resuming log processing...' : 'Preparing to process logs...',
            detailMessage: result.resume
              ? `${remainingMBRaw.toFixed(1)} MB remaining to import`
              : `${result.logSizeMB?.toFixed(1) || 0} MB to process`,
            progress: initialProgress,
            estimatedTime: result.estimatedTimeMinutes
              ? `Estimated: ${result.estimatedTimeMinutes} minutes`
              : undefined,
            status: 'starting'
          });

          if (result.message) {
            onSuccess?.(result.message);
          }

          if (!signalRConnected) {
            setTimeout(() => startProcessingPolling(), 5000);
          }
        } else {
          onError?.('Log file appears to be empty or invalid');
          await logProcessingOp.clear();
        }
      } else {
        await logProcessingOp.clear();
      }
    } catch (err: any) {
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

    if (!window.confirm('Cancel log processing?')) return;

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

  const startDepotPolling = () => {
    const checkDepotStatus = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data: PicsProgress = await response.json();
          const wasRunning = depotProcessing?.isRunning;
          setDepotProcessing(data);

          // If depot just finished, show popup for 1 minute
          if (wasRunning && !data.isRunning && data.status === 'Complete') {
            setShowPostDepotPopup(true);
            setPostDepotTimer(60);
            startPostDepotTimer();
          }
        }
      } catch (error) {
        console.error('Failed to fetch depot status:', error);
      }
    };

    checkDepotStatus();
    depotPollingInterval.current = setInterval(checkDepotStatus, 3000);
  };

  const startPostDepotTimer = () => {
    if (postDepotPopupInterval.current) {
      clearInterval(postDepotPopupInterval.current);
    }

    postDepotPopupInterval.current = setInterval(() => {
      setPostDepotTimer((prev) => {
        if (prev <= 1) {
          setShowPostDepotPopup(false);
          if (postDepotPopupInterval.current) {
            clearInterval(postDepotPopupInterval.current);
          }
          return 0;
        }
        return prev - 1;
      });
    }, 1000);
  };

  const handleClosePostDepotPopup = () => {
    setShowPostDepotPopup(false);
    if (postDepotPopupInterval.current) {
      clearInterval(postDepotPopupInterval.current);
    }
  };

  const handleProcessLogsFromPopup = () => {
    handleClosePostDepotPopup();
    handleProcessAllLogs();
  };

  const handlePostProcessDepotMappings = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    if (!window.confirm('Reapply Depot Mappings to existing download data? This will update all downloads with the latest depot information.')) return;

    setActionLoading(true);
    try {
      const response = await fetch('/api/management/post-process-depot-mappings', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
      });

      if (response.ok) {
        const result = await response.json();
        onSuccess?.(result.message || `Successfully processed ${result.mappingsProcessed} downloads`);
        setTimeout(() => onDataRefresh?.(), 2000);
      } else {
        const error = await response.json();
        onError?.(error.error || 'Failed to process depot mappings');
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to process depot mappings');
    } finally {
      setActionLoading(false);
    }
  };

  return (
    <>
      <Card>
      <div className="flex items-center space-x-2 mb-4">
        <FileText className="w-5 h-5 cache-hit" />
        <h3 className="text-lg font-semibold text-themed-primary">Log Processing</h3>
      </div>
      <p className="text-themed-muted text-sm mb-4">
        Control how access.log is processed for statistics
      </p>
      <div className="space-y-4">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <Button
            variant="filled"
            color="yellow"
            leftSection={<RefreshCw className="w-4 h-4" />}
            onClick={handleResetLogs}
            disabled={
              actionLoading ||
              isProcessingLogs ||
              depotProcessing?.isRunning ||
              mockMode ||
              logProcessingOp.loading ||
              !isAuthenticated
            }
            fullWidth
          >
            Reset Log Position
          </Button>
          <Button
            variant="filled"
            color="green"
            leftSection={<PlayCircle className="w-4 h-4" />}
            onClick={handleProcessAllLogs}
            disabled={
              actionLoading ||
              isProcessingLogs ||
              depotProcessing?.isRunning ||
              mockMode ||
              logProcessingOp.loading ||
              !isAuthenticated
            }
            loading={logProcessingOp.loading}
            fullWidth
          >
            Process All Logs
          </Button>
        </div>
        <Button
          variant="filled"
          color="blue"
          leftSection={<Database className="w-4 h-4" />}
          onClick={handlePostProcessDepotMappings}
          disabled={
            actionLoading ||
            isProcessingLogs ||
            depotProcessing?.isRunning ||
            mockMode ||
            !isAuthenticated
          }
          fullWidth
        >
          Reapply Depot Mappings
        </Button>
      </div>
      {depotProcessing?.isRunning && (
        <div
          className="mt-4 p-3 rounded-lg border"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderColor: 'var(--theme-info)',
            color: 'var(--theme-info-text)'
          }}
        >
          <p className="text-xs">
            <strong>Depot Processing Active:</strong> Log processing is disabled while Steam PICS depot mapping is running.
            Progress: {Math.round(depotProcessing.progressPercent)}% ({depotProcessing.processedBatches}/{depotProcessing.totalBatches} batches)
          </p>
        </div>
      )}

      {depotMappingProgress && (
        <div
          className="mt-4 p-4 rounded-lg border"
          style={{
            backgroundColor: depotMappingProgress.status === 'complete'
              ? 'var(--theme-success-bg)'
              : 'var(--theme-warning-bg)',
            borderColor: depotMappingProgress.status === 'complete'
              ? 'var(--theme-success)'
              : 'var(--theme-warning)',
            color: depotMappingProgress.status === 'complete'
              ? 'var(--theme-success-text)'
              : 'var(--theme-warning-text)'
          }}
        >
          <div className="mb-2">
            <p className="font-semibold text-sm">{depotMappingProgress.message}</p>
            {depotMappingProgress.totalMappings > 0 && (
              <p className="text-xs mt-1">
                {depotMappingProgress.processedMappings} / {depotMappingProgress.totalMappings} downloads processed
                {depotMappingProgress.mappingsApplied !== undefined && (
                  <span> • {depotMappingProgress.mappingsApplied} mappings applied</span>
                )}
              </p>
            )}
          </div>
          {depotMappingProgress.isProcessing && depotMappingProgress.percentComplete > 0 && (
            <div className="w-full progress-track rounded-full h-3 relative overflow-hidden">
              <div
                className="progress-bar-low h-3 rounded-full smooth-transition"
                style={{ width: `${Math.min(100, depotMappingProgress.percentComplete)}%` }}
              />
              <div className="absolute inset-0 flex items-center justify-center">
                <span className="text-xs font-medium">
                  {depotMappingProgress.percentComplete.toFixed(0)}%
                </span>
              </div>
            </div>
          )}
        </div>
      )}

      <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
        <p className="text-xs text-themed-muted">
          <strong>Reset:</strong> Start from current end of log
          <br />
          <strong>Process All:</strong> Import entire log history
          <br />
          <strong>Reapply Depot Mappings:</strong> Manually rerun depot mapping if needed. Log processing now applies mappings automatically after completion.
        </p>
      </div>

      {logProcessingOp.error && (
        <Alert color="orange">Backend storage error: {logProcessingOp.error}</Alert>
      )}
      </Card>

    {/* Post-Depot Processing Popup */}
    {showPostDepotPopup && (
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
        <div
          className="rounded-lg shadow-xl p-6 max-w-md w-full mx-4"
          style={{
            backgroundColor: 'var(--theme-bg-primary)',
            border: '1px solid var(--theme-border-primary)'
          }}
        >
          <div className="flex items-center justify-between mb-4">
            <h3 className="text-lg font-semibold text-themed-primary">
              Depot Processing Complete
            </h3>
            <button
              onClick={handleClosePostDepotPopup}
              className="text-themed-muted hover:text-themed-primary transition-colors"
            >
              <X className="w-5 h-5" />
            </button>
          </div>

          <p className="text-themed-secondary mb-6">
            Steam depot mapping has finished! Would you like to process all logs now to update download statistics with the new depot mappings?
          </p>

          <div className="flex flex-col gap-3">
            <Button
              variant="filled"
              color="green"
              leftSection={<PlayCircle className="w-4 h-4" />}
              onClick={handleProcessLogsFromPopup}
              disabled={!isAuthenticated || mockMode}
              fullWidth
            >
              Process All Logs
            </Button>
            <Button
              variant="outline"
              onClick={handleClosePostDepotPopup}
              fullWidth
            >
              Maybe Later
            </Button>
          </div>

          <div className="mt-4 text-center">
            <span className="text-xs text-themed-muted">
              This popup will close in {postDepotTimer} seconds
            </span>
          </div>
        </div>
      </div>
    )}
    </>
  );
};

export default LogProcessingManager;
