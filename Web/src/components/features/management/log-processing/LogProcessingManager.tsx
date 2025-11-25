import React, { useState, useEffect, useRef } from 'react';
import { Zap, RefreshCw, PlayCircle, AlertTriangle } from 'lucide-react';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import { useSignalR } from '@contexts/SignalRContext';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import type { ProcessingStatus as ApiProcessingStatus } from '../../../../types';
import DepotMappingManager from '../depot/DepotMappingManager';

interface LogProcessingManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
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
  onDataRefresh
}) => {
  const { steamAuthMode } = useSteamAuth();
  const [isProcessingLogs, setIsProcessingLogs] = useState(false);
  // Local state for tracking processing UI (notifications handled by NotificationsContext)
  // @ts-ignore - processingStatus is set but notifications are handled by NotificationsContext
  const [processingStatus, setProcessingStatus] = useState<ProcessingUIStatus | null>(null);
  const [actionLoading, setActionLoading] = useState(false);
  const [confirmModal, setConfirmModal] = useState<{
    title: string;
    message: string;
    confirmLabel?: string;
    onConfirm: () => Promise<void> | void;
  } | null>(null);

  const logProcessingOp = useBackendOperation('activeLogProcessing', 'logProcessing', 120);
  const signalR = useSignalR();
  const pollingInterval = useRef<NodeJS.Timeout | null>(null);
  const onDataRefreshRef = useRef(onDataRefresh);
  const mockModeRef = useRef(mockMode);

  // Keep the refs up to date
  useEffect(() => {
    onDataRefreshRef.current = onDataRefresh;
    mockModeRef.current = mockMode;
  }, [onDataRefresh, mockMode]);

  // Note: Log processing notifications are now handled automatically by SignalR
  // in NotificationsContext, so we don't need to manually manage them here

  const parseMetric = (value: unknown) => {
    const numeric = Number(value ?? 0);
    return Number.isFinite(numeric) ? numeric : 0;
  };

  const formatProgressDetail = (processed: number, totalLines: number) => {
    const safeProcessed = Math.max(processed, 0);
    const safeTotalLines = Math.max(totalLines, 0);

    if (safeProcessed === 0 && safeTotalLines === 0) {
      return '';
    }

    // If 0 entries but lines were scanned, all were duplicates
    if (safeProcessed === 0 && safeTotalLines > 0) {
      return `0 entries from ${safeTotalLines.toLocaleString()} total lines (all duplicates already processed)`;
    }

    // When complete, show entries saved vs total lines scanned
    // totalLines = all lines scanned across all log files
    // processed = valid entries actually saved (excludes duplicates, invalid lines)
    if (safeProcessed === safeTotalLines) {
      return `${safeProcessed.toLocaleString()} entries processed`;
    }
    return `${safeProcessed.toLocaleString()} entries from ${safeTotalLines.toLocaleString()} total lines`;
  };

  useEffect(() => {
    if (mockMode) {
      // Don't subscribe in mock mode
      return;
    }

    restoreLogProcessing();

    return () => {
      if (pollingInterval.current) {
        clearInterval(pollingInterval.current);
        pollingInterval.current = null;
      }
    };
  }, [mockMode]);

  const restoreLogProcessing = async () => {
    const logOp = await logProcessingOp.load();
    if (logOp?.data) {
      const status = await ApiService.getProcessingStatus().catch(() => null);
      if (status?.isProcessing) {
        setIsProcessingLogs(true);
        const processedEntries = parseMetric(status.entriesProcessed);
        const totalLines = parseMetric(status.totalLines);
        const detailSegments = [
          formatProgressDetail(processedEntries, totalLines),
          status.processingRate ? `Speed: ${status.processingRate.toFixed(1)} MB/s` : ''
        ].filter(Boolean);

        // Cap progress at 99.9% if still processing
        const progressValue = Math.min(99.9, status.percentComplete || status.progress || 0);

        setProcessingStatus({
          message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
          detailMessage: detailSegments.join(' • '),
          progress: progressValue,
          estimatedTime: status.estimatedTime,
          status: status.status || 'processing'
        });
        startProcessingPolling();
      } else {
        await logProcessingOp.clear();
        if (status) {
          const processedEntries = parseMetric(status.entriesProcessed);
          const totalLines = parseMetric(status.totalLines);
          const detailSegments = [
            `Processed ${status.mbTotal?.toFixed(1) || 0} MB`,
            formatProgressDetail(processedEntries, totalLines)
          ].filter(Boolean);

          setProcessingStatus({
            message: 'Processing Complete!',
            detailMessage: detailSegments.join(' • '),
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

  // Subscribe to SignalR events for log processing
  // Note: signalR.on/off are stable functions, so we only need to subscribe once
  useEffect(() => {
    if (mockMode) {
      return;
    }

    // Handler for ProcessingProgress event
    const handleProcessingProgress = async (progress: any) => {
      const currentProgress = progress.percentComplete || progress.progress || 0;
      const status = progress.status || 'processing';

      const processedEntries = parseMetric(progress.entriesProcessed);
      const totalLines = parseMetric(progress.totalLines);

      // Always set isProcessingLogs to true when we receive progress updates (unless complete)
      if (status !== 'complete') {
        setIsProcessingLogs(true);
      }

      setProcessingStatus(() => {
        // Only mark as complete when status is explicitly 'complete', not just based on percentage
        if (status === 'complete') {
          if (pollingInterval.current) {
            clearInterval(pollingInterval.current);
            pollingInterval.current = null;
          }
          return {
            message: 'Processing Complete!',
            detailMessage: formatProgressDetail(processedEntries, totalLines),
            progress: 100,
            status: 'complete'
          };
        }

        if (status === 'finalizing') {
          return {
            message: progress.message || 'Finalizing log processing...',
            detailMessage: formatProgressDetail(processedEntries, totalLines),
            progress: currentProgress,
            status: 'finalizing'
          };
        }

        return {
          message: `Processing: ${progress.mbProcessed?.toFixed(1) || 0} MB of ${progress.mbTotal?.toFixed(1) || 0} MB`,
          detailMessage: formatProgressDetail(processedEntries, totalLines),
          progress: Math.min(99.9, currentProgress), // Cap at 99.9% until truly complete
          status: 'processing'
        };
      });

      await logProcessingOp.update({
        lastProgress: progress.percentComplete || progress.progress || 0,
        mbProcessed: progress.mbProcessed,
        mbTotal: progress.mbTotal,
        entriesProcessed: processedEntries,
        linesProcessed: totalLines,
        status
      });
    };

    // Handler for FastProcessingComplete event
    const handleFastProcessingComplete = async (result: any) => {
      console.log('SignalR FastProcessingComplete received:', result);
      // Stop polling immediately when we receive completion signal
      if (pollingInterval.current) {
        clearInterval(pollingInterval.current);
        pollingInterval.current = null;
      }

      setProcessingStatus({
        message: 'Processing Complete!',
        detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString()} entries from ${result.linesProcessed?.toLocaleString()} lines in ${result.elapsed?.toFixed(1)} minutes.`,
        progress: 100,
        status: 'complete'
      });

      // Don't set isProcessingLogs to false here - keep modal visible
      // The timeout below will handle clearing everything after 3 seconds
      await logProcessingOp.clear();

      // Mark setup as completed (persistent flag for guest mode eligibility)
      try {
        await fetch('/api/system/setup', {
          method: 'PATCH',
          headers: ApiService.getHeaders({ 'Content-Type': 'application/json' }),
          body: JSON.stringify({ completed: true })
        });
      } catch (error) {
        console.warn('Failed to mark setup as completed:', error);
      }

      // Show completion for 3 seconds, then stop completely
      setTimeout(async () => {
        setProcessingStatus(null);
        setIsProcessingLogs(false); // Now set to false to hide modal and re-enable buttons
        onDataRefreshRef.current?.();
      }, 3000);
    };

    // Handler for DownloadsRefresh event (silent background processing)
    const handleDownloadsRefresh = async () => {
      // Silently refresh data without showing progress bars or notifications
      // This is triggered by the LiveLogMonitorService background processing
      onDataRefreshRef.current?.();
    };

    // Subscribe to events
    signalR.on('ProcessingProgress', handleProcessingProgress);
    signalR.on('FastProcessingComplete', handleFastProcessingComplete);
    signalR.on('DownloadsRefresh', handleDownloadsRefresh);

    // console.log('[LogProcessingManager] Subscribed to SignalR events');

    // If SignalR disconnects and we're processing, fall back to polling
    // Note: This is handled by monitoring signalR.isConnected in another effect

    // Cleanup: unsubscribe from events
    return () => {
      signalR.off('ProcessingProgress', handleProcessingProgress);
      signalR.off('FastProcessingComplete', handleFastProcessingComplete);
      signalR.off('DownloadsRefresh', handleDownloadsRefresh);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode]); // signalR.on/off are stable, don't need signalR as dependency

  // Monitor SignalR connection and use polling as fallback when disconnected
  useEffect(() => {
    if (mockMode || !isProcessingLogs) {
      return;
    }

    // If SignalR is not connected and we're processing, use polling
    if (!signalR.isConnected) {
      // console.log('[LogProcessingManager] SignalR disconnected, starting polling fallback');
      startProcessingPolling();
    } else {
      // SignalR is connected, stop polling if active
      if (pollingInterval.current) {
        // console.log('[LogProcessingManager] SignalR connected, stopping polling fallback');
        clearInterval(pollingInterval.current);
        pollingInterval.current = null;
      }
    }

    return () => {
      if (pollingInterval.current) {
        clearInterval(pollingInterval.current);
        pollingInterval.current = null;
      }
    };
  }, [mockMode, isProcessingLogs, signalR.isConnected]);

  const startProcessingPolling = () => {
    if (pollingInterval.current) {
      clearInterval(pollingInterval.current);
    }

    const checkStatus = async () => {
      try {
        const status: ApiProcessingStatus = await ApiService.getProcessingStatus();
        if (status?.isProcessing) {
          setIsProcessingLogs(true);
          // Cap progress at 99.9% while still processing
          const progressValue = Math.min(99.9, status.percentComplete || status.progress || 0);

          setProcessingStatus({
            message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
            detailMessage: status.processingRate
              ? `Speed: ${status.processingRate.toFixed(1)} MB/s`
              : '',
            progress: progressValue,
            estimatedTime: status.estimatedTime,
            status: status.status || 'processing'
          });
          await logProcessingOp.update({
            lastProgress: progressValue,
            mbProcessed: status.mbProcessed,
            mbTotal: status.mbTotal
          });
        } else {
          // Processing is complete - stop polling immediately
          if (pollingInterval.current) {
            clearInterval(pollingInterval.current);
            pollingInterval.current = null;
          }

          const finalProgress = status?.percentComplete || status?.progress || 0;
          const reachedEnd =
            status?.currentPosition &&
            status?.totalSize &&
            status.currentPosition >= status.totalSize;
          // Only consider complete when status is explicitly 'complete'
          const isComplete = status?.status === 'complete' || reachedEnd;
          const isAlmostComplete = false; // Don't auto-complete based on percentage alone

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

            setIsProcessingLogs(false); // Set to false when complete
            await logProcessingOp.clear();

            // Show completion for 3 seconds, then stop
            setTimeout(() => {
              setProcessingStatus(null);
              setIsProcessingLogs(false); // Ensure buttons are re-enabled
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
        // Stop polling on error
        if (pollingInterval.current) {
          clearInterval(pollingInterval.current);
          pollingInterval.current = null;
        }
      }
    };

    checkStatus();
    pollingInterval.current = setInterval(checkStatus, 3000);
  };

  const handleResetLogs = () => {
    setConfirmModal({
      title: 'Reset Log Position',
      message: 'Choose where to start processing logs from:',
      confirmLabel: 'Confirm',
      onConfirm: () => {} // Will be handled by custom modal
    });
  };

  const executeResetFromTop = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      const result = await ApiService.resetLogPosition('top');
      if (result) {
        onSuccess?.('Log position reset to beginning of file');
        setTimeout(() => onDataRefresh?.(), 2000);
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to reset log position');
    } finally {
      setActionLoading(false);
      setConfirmModal(null);
    }
  };

  const executeResetFromBottom = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      const result = await ApiService.resetLogPosition('bottom');
      if (result) {
        onSuccess?.('Log position reset to end of file');
        setTimeout(() => onDataRefresh?.(), 2000);
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to reset log position');
    } finally {
      setActionLoading(false);
      setConfirmModal(null);
    }
  };

  const executeProcessAllLogs = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      const result = await ApiService.processAllLogs();

      if (result) {
        if (
          result.status === 'empty_file' ||
          result.status === 'no_log_file' ||
          result.status === 'insufficient_data' ||
          result.status === 'already_processed'
        ) {
          if (result.status === 'already_processed') {
            onSuccess?.(result.message);
          } else {
            onError?.(result.message);
          }
          setActionLoading(false);
          return;
        }

        if (result.logSizeMB !== undefined && result.logSizeMB > 0) {
          await logProcessingOp.save({ type: 'processAll', resume: result.resume });
          const remainingMBRaw =
            typeof result.remainingMB === 'number' ? result.remainingMB : result.logSizeMB || 0;
          const initialProgress = 0;

          setIsProcessingLogs(true);
          setProcessingStatus({
            message: result.resume ? 'Resuming log processing...' : 'Starting log processing...',
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

          if (!signalR.isConnected) {
            // Start polling immediately when SignalR is not connected
            startProcessingPolling();
          }
        } else if (result.logSizeMB === 0) {
          // Log file exists but no new data to process (already at end of file)
          onSuccess?.('No new log entries to process. All logs are up to date.');
          await logProcessingOp.clear();
        } else {
          // Processing started without size info (backend will handle it)
          await logProcessingOp.save({ type: 'processAll', resume: false });
          setIsProcessingLogs(true);

          if (!signalR.isConnected) {
            startProcessingPolling();
          }
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

  const handleProcessAllLogs = () => {
    setConfirmModal({
      title: 'Process All Logs',
      message:
        'Process the entire access log? This may take several minutes and will re-import all entries.',
      confirmLabel: 'Process Logs',
      onConfirm: executeProcessAllLogs
    });
  };

  const handleConfirmAction = async () => {
    if (!confirmModal) {
      return;
    }

    const { onConfirm } = confirmModal;
    setConfirmModal(null);
    await onConfirm();
  };

  return (
    <>
      <Card>
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
            <Zap className="w-5 h-5 icon-purple" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Log Processing</h3>
        </div>
        <p className="text-themed-muted text-sm mb-4">
          Import historical data or reset to monitor only new downloads
        </p>
        <div className="space-y-3">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Button
              variant="default"
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
        </div>

        <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
          <p className="text-xs text-themed-muted leading-relaxed">
            <strong>Reset Log Position:</strong> Choose to start from beginning or end of log file
            <br />
            <strong>Process All Logs:</strong> Process logs based on reset position (rust service
            always starts from top with duplicate detection)
          </p>
        </div>

        {logProcessingOp.error && (
          <Alert color="orange">Backend storage error: {logProcessingOp.error}</Alert>
        )}
      </Card>

      {/* Depot Mapping Section */}
      <DepotMappingManager
        isAuthenticated={isAuthenticated}
        mockMode={mockMode}
        steamAuthMode={steamAuthMode}
        actionLoading={actionLoading}
        setActionLoading={setActionLoading}
        isProcessingLogs={isProcessingLogs}
        onError={onError}
        onSuccess={onSuccess}
        onDataRefresh={onDataRefresh}
      />

      <Modal
        opened={confirmModal !== null}
        onClose={() => {
          if (!actionLoading) {
            setConfirmModal(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{confirmModal?.title}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">{confirmModal?.message}</p>

          {confirmModal?.title === 'Reset Log Position' ? (
            <>
              <div className="space-y-3">
                <div className="p-3 bg-themed-tertiary rounded-lg">
                  <p className="text-xs text-themed-muted leading-relaxed">
                    <strong>Start from Beginning:</strong> Process entire log history (rust
                    processor has duplicate detection)
                    <br />
                    <strong>Start from End:</strong> Monitor only new downloads going forward
                  </p>
                </div>
              </div>

              <div className="flex flex-col gap-3 pt-2">
                <Button
                  variant="filled"
                  color="blue"
                  onClick={executeResetFromTop}
                  loading={actionLoading}
                  fullWidth
                >
                  Start from Beginning
                </Button>
                <Button
                  variant="default"
                  onClick={executeResetFromBottom}
                  loading={actionLoading}
                  fullWidth
                >
                  Start from End
                </Button>
                <Button
                  variant="outline"
                  onClick={() => setConfirmModal(null)}
                  disabled={actionLoading}
                  fullWidth
                >
                  Cancel
                </Button>
              </div>
            </>
          ) : (
            <>
              <Alert color="yellow">
                <p className="text-sm">
                  <strong>Important:</strong> Ensure no other maintenance tasks are running before
                  continuing.
                </p>
              </Alert>

              <div className="flex justify-end space-x-3 pt-2">
                <Button
                  variant="default"
                  onClick={() => setConfirmModal(null)}
                  disabled={actionLoading}
                >
                  Cancel
                </Button>
                <Button
                  variant="filled"
                  color="red"
                  onClick={handleConfirmAction}
                  loading={actionLoading}
                >
                  {confirmModal?.confirmLabel || 'Confirm'}
                </Button>
              </div>
            </>
          )}
        </div>
      </Modal>
    </>
  );
};

export default LogProcessingManager;
