import React, { useState, useEffect, useRef } from 'react';
import { FileText, RefreshCw, PlayCircle } from 'lucide-react';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import * as signalR from '@microsoft/signalr';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
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

  const logProcessingOp = useBackendOperation('activeLogProcessing', 'logProcessing', 120);
  const signalRConnection = useRef<signalR.HubConnection | null>(null);
  const pollingInterval = useRef<NodeJS.Timeout | null>(null);
  const reconnectTimeout = useRef<NodeJS.Timeout | null>(null);
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

    return () => {
      if (pollingInterval.current) {
        clearInterval(pollingInterval.current);
      }
      if (reconnectTimeout.current) {
        clearTimeout(reconnectTimeout.current);
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
      }
    }
  };

  const setupSignalR = async () => {
    try {
      const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/downloads')
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

      connection.on('ProcessingProgress', async (progress: any) => {

        setProcessingStatus((prev) => {
          if (prev?.status === 'complete') {
            return prev;
          }

          return {
            message: `Processing: ${progress.mbProcessed?.toFixed(1) || 0} MB of ${progress.mbTotal?.toFixed(1) || 0} MB`,
            detailMessage: `${progress.entriesProcessed || 0} entries from ${progress.linesProcessed || 0} lines`,
            progress: progress.percentComplete || progress.progress || 0,
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

      connection.onreconnecting(() => {
        setSignalRConnected(false);
      });

      connection.onreconnected(() => {
        setSignalRConnected(true);
      });

      connection.onclose((error) => {
        console.error('SignalR disconnected:', error);
        setSignalRConnected(false);

        if (isProcessingLogs) {
          startProcessingPolling();
        }

        reconnectTimeout.current = setTimeout(() => {
          setupSignalR();
        }, 5000);
      });

      await connection.start();
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
        const currentStatus = processingStatus;
        if (currentStatus?.status === 'complete') {
          return;
        }

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
          const finalProgress = status?.percentComplete || status?.progress || 0;
          if (finalProgress >= 100) {
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
          await logProcessingOp.save({ type: 'processAll' });
          setIsProcessingLogs(true);
          setProcessingStatus({
            message: 'Preparing to process logs...',
            detailMessage: `${result.logSizeMB?.toFixed(1) || 0} MB to process`,
            progress: 0,
            estimatedTime: `Estimated: ${result.estimatedTimeMinutes} minutes`,
            status: 'starting'
          });

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

  return (
    <Card>
      <div className="flex items-center space-x-2 mb-4">
        <FileText className="w-5 h-5 cache-hit" />
        <h3 className="text-lg font-semibold text-themed-primary">Log Processing</h3>
      </div>
      <p className="text-themed-muted text-sm mb-4">
        Control how access.log is processed for statistics
      </p>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Button
          variant="filled"
          color="yellow"
          leftSection={<RefreshCw className="w-4 h-4" />}
          onClick={handleResetLogs}
          disabled={
            actionLoading ||
            isProcessingLogs ||
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
      <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
        <p className="text-xs text-themed-muted">
          <strong>Reset:</strong> Start from current end of log
          <br />
          <strong>Process All:</strong> Import entire log history
        </p>
      </div>

      {logProcessingOp.error && (
        <Alert color="orange">Backend storage error: {logProcessingOp.error}</Alert>
      )}
    </Card>
  );
};

export default LogProcessingManager;
