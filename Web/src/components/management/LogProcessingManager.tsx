import React, { useState, useEffect, useRef } from 'react';
import { FileText, RefreshCw, PlayCircle, Database, AlertTriangle, CheckCircle, Clock, Zap, RotateCcw } from 'lucide-react';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import * as signalR from '@microsoft/signalr';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
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


interface PicsProgress {
  isRunning: boolean;
  status: string;
  totalApps: number;
  processedApps: number;
  totalBatches: number;
  processedBatches: number;
  progressPercent: number;
  depotMappingsFound: number;
  depotMappingsFoundInSession: number;
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
  const [showPostDepotPopup, setShowPostDepotPopup] = useState(false);
  const [postDepotTimer, setPostDepotTimer] = useState(60);
  const [confirmModal, setConfirmModal] = useState<
    | {
        title: string;
        message: string;
        confirmLabel?: string;
        onConfirm: () => Promise<void> | void;
      }
    | null
  >(null);

  // Depot mapping schedule state
  const [depotSchedule, setDepotSchedule] = useState({
    enabled: true,
    intervalHours: 24,
    lastRun: null as Date | null,
    nextRun: new Date(Date.now() + 24 * 60 * 60 * 1000) as Date | null // 24 hours from now by default
  });

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

  const parseMetric = (value: unknown) => {
    const numeric = Number(value ?? 0);
    return Number.isFinite(numeric) ? numeric : 0;
  };

  const formatProgressDetail = (queued: number, processed: number, lines: number, pending: number) => {
    const safeProcessed = Math.max(processed, 0);
    const safeQueued = Math.max(queued, safeProcessed);
    const safeLines = Math.max(lines, 0);
    const safePending = Math.max(pending, safeQueued - safeProcessed, 0);

    if (safeQueued === 0 && safeProcessed === 0 && safeLines === 0) {
      return '';
    }

    if (safePending > 0) {
      return (
        safeProcessed.toLocaleString() +
        ' saved / ' +
        safeQueued.toLocaleString() +
        ' queued (' +
        safePending.toLocaleString() +
        ' pending)'
      );
    }

    return (
      safeProcessed.toLocaleString() +
      ' entries from ' +
      safeLines.toLocaleString() +
      ' lines'
    );
  };


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
        const queued = parseMetric(status.entriesQueued ?? status.entriesProcessed);
        const processedEntries = parseMetric(status.entriesProcessed);
        const pendingEntries = parseMetric(status.pendingEntries ?? Math.max(queued - processedEntries, 0));
        const lines = parseMetric(status.linesProcessed);
        const detailSegments = [
          formatProgressDetail(queued, processedEntries, lines, pendingEntries),
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
          const queued = parseMetric(status.entriesQueued ?? status.entriesProcessed);
          const processedEntries = parseMetric(status.entriesProcessed);
          const pendingEntries = parseMetric(status.pendingEntries ?? Math.max(queued - processedEntries, 0));
          const lines = parseMetric(status.linesProcessed);
          const detailSegments = [
            `Processed ${status.mbTotal?.toFixed(1) || 0} MB`,
            formatProgressDetail(queued, processedEntries, lines, pendingEntries)
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

        const queued = parseMetric(progress.entriesQueued ?? progress.entriesProcessed);
        const processedEntries = parseMetric(progress.entriesProcessed);
        const pendingEntries = parseMetric(progress.pendingEntries ?? Math.max(queued - processedEntries, 0));
        const lines = parseMetric(progress.linesProcessed);

        // Always set isProcessingLogs to true when we receive progress updates (unless complete)
        if (status !== 'complete') {
          setIsProcessingLogs(true);
        }

        setProcessingStatus(() => {
          // Only mark as complete when status is explicitly 'complete', not just based on percentage
          if (status === 'complete') {
            if (pollingInterval.current) {
              clearInterval(pollingInterval.current);
            }
            return {
              message: 'Processing Complete!',
              detailMessage: formatProgressDetail(queued, processedEntries, lines, pendingEntries),
              progress: 100,
              status: 'complete'
            };
          }

          if (status === 'finalizing') {
            return {
              message: progress.message || 'Finalizing log processing...',
              detailMessage: formatProgressDetail(queued, processedEntries, lines, pendingEntries),
              progress: currentProgress,
              status: 'finalizing'
            };
          }

          return {
            message: `Processing: ${progress.mbProcessed?.toFixed(1) || 0} MB of ${progress.mbTotal?.toFixed(1) || 0} MB`,
            detailMessage: formatProgressDetail(queued, processedEntries, lines, pendingEntries),
            progress: Math.min(99.9, currentProgress), // Cap at 99.9% until truly complete
            status: 'processing'
          };
        });

        await logProcessingOp.update({
          lastProgress: progress.percentComplete || progress.progress || 0,
          mbProcessed: progress.mbProcessed,
          mbTotal: progress.mbTotal,
          entriesProcessed: processedEntries,
          entriesQueued: queued,
          pendingEntries,
          linesProcessed: lines,
          status
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

        setIsProcessingLogs(false);  // Set to false when complete
        await logProcessingOp.clear();
        if (depotMappingsProcessed > 0) {
          onSuccess?.(`Depot mappings applied to ${depotMappingsProcessed.toLocaleString()} downloads.`);
        }

        // Show completion for 3 seconds instead of 10, then stop completely
        setTimeout(async () => {
          setProcessingStatus(null);
          setIsProcessingLogs(false);  // Ensure buttons are re-enabled
          onDataRefresh?.();
        }, 3000);
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
          }

          const finalProgress = status?.percentComplete || status?.progress || 0;
          const reachedEnd = status?.currentPosition && status?.totalSize &&
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

            setIsProcessingLogs(false);  // Set to false when complete
            await logProcessingOp.clear();

            // Show completion for 3 seconds, then stop
            setTimeout(() => {
              setProcessingStatus(null);
              setIsProcessingLogs(false);  // Ensure buttons are re-enabled
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

        if (result.logSizeMB > 0) {
          await logProcessingOp.save({ type: 'processAll', resume: result.resume });
          const remainingMBRaw = typeof result.remainingMB === 'number' ? result.remainingMB : result.logSizeMB || 0;
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

  const handleProcessAllLogs = () => {
    setConfirmModal({
      title: 'Process All Logs',
      message:
        'Process the entire access log? This may take several minutes and will re-import all entries.',
      confirmLabel: 'Process Logs',
      onConfirm: executeProcessAllLogs
    });
  };

  const executeCancelProcessing = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

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

  const handleCancelProcessing = () => {
    setConfirmModal({
      title: 'Cancel Log Processing',
      message: 'Cancel the current log processing job? Any progress made so far will be preserved.',
      confirmLabel: 'Cancel Processing',
      onConfirm: executeCancelProcessing
    });
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

  const executePostProcessDepotMappings = async (mode: 'incremental' | 'full') => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      if (mode === 'incremental') {
        // For incremental mode: trigger crawl, wait for completion, then apply mappings
        onSuccess?.('Starting incremental depot crawl - check progress at top of page');

        // Trigger PICS crawl (incremental)
        await ApiService.triggerSteamKitRebuild(true);

        // Wait for PICS crawl to complete by polling
        let attempts = 0;
        const maxAttempts = 600; // 10 minutes max (600 * 1 second)

        while (attempts < maxAttempts) {
          await new Promise(resolve => setTimeout(resolve, 1000)); // Wait 1 second
          attempts++;

          try {
            const progress = await fetch('/api/gameinfo/steamkit/progress');
            if (progress.ok) {
              const data = await progress.json();

              // Check if crawl is complete (not running and status is Complete)
              if (!data.isRunning && data.status === 'Complete') {
                onSuccess?.('Depot crawl complete! Now applying mappings to downloads...');

                // Apply the new depot mappings to existing downloads
                const applyResult = await ApiService.postProcessDepotMappings();
                onSuccess?.(applyResult.message || `Applied depot mappings to ${applyResult.mappingsProcessed || 0} downloads`);
                setTimeout(() => onDataRefresh?.(), 2000);
                break;
              }
            }
          } catch (pollErr) {
            console.warn('Error polling PICS progress:', pollErr);
          }
        }

        if (attempts >= maxAttempts) {
          onError?.('Depot crawl timed out. Mappings may not have been applied.');
        }
      } else {
        // For full rebuild: just trigger it, don't auto-apply
        await ApiService.triggerSteamKitRebuild(false);
        onSuccess?.('Full depot rebuild initiated - check progress at top of page');
        setTimeout(() => onDataRefresh?.(), 2000);
      }
    } catch (err: any) {
      onError?.(err.message || 'Failed to process depot mappings');
    } finally {
      setActionLoading(false);
    }
  };

  const handlePostProcessDepotMappings = (mode: 'incremental' | 'full') => {
    setConfirmModal({
      title: mode === 'full' ? 'Regenerate All Mappings' : 'Apply Depot Mappings',
      message:
        mode === 'full'
          ? 'Regenerate depot mappings from scratch? This will re-map all depot IDs to Steam game names.'
          : 'Apply depot mappings now? This will attempt to map depot IDs to Steam game names for all unidentified downloads.',
      confirmLabel: mode === 'full' ? 'Regenerate All' : 'Apply Mappings',
      onConfirm: () => executePostProcessDepotMappings(mode)
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

  const formatNextRun = () => {
    if (!depotSchedule.nextRun) return 'Not scheduled';
    const now = new Date();
    const diff = depotSchedule.nextRun.getTime() - now.getTime();
    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
    if (hours > 24) {
      return `${Math.floor(hours / 24)} days`;
    }
    return `${hours}h ${minutes}m`;
  };

  return (
    <>
      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <FileText className="w-5 h-5 cache-hit" />
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

        <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
          <p className="text-xs text-themed-muted leading-relaxed">
            <strong>Reset:</strong> Start monitoring from current end of log file
            <br />
            <strong>Process All:</strong> Import entire log history into database
          </p>
        </div>

        {logProcessingOp.error && (
          <Alert color="orange">Backend storage error: {logProcessingOp.error}</Alert>
        )}
      </Card>

      {/* Depot Mapping Section */}
      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <Database className="w-5 h-5 text-themed-primary" />
          <h3 className="text-lg font-semibold text-themed-primary">Depot Mapping</h3>
        </div>

        <p className="text-themed-secondary mb-4">
          Automatically identifies Steam games from depot IDs in download history
        </p>

        {/* Schedule Status */}
        <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
          <div className="flex items-start justify-between gap-4">
            <div className="flex-1">
              <div className="flex items-center gap-2 mb-2">
                <Clock className="w-4 h-4 text-themed-primary" />
                <span className="text-sm font-medium text-themed-secondary">Automatic Schedule</span>
              </div>
              <div className="text-xs text-themed-muted space-y-1">
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Runs every:</span>
                  <span className="font-medium text-themed-primary">{depotSchedule.intervalHours} hours</span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Next run:</span>
                  <span className="font-medium text-themed-primary">{formatNextRun()}</span>
                </div>
                {depotSchedule.lastRun && (
                  <div className="flex items-center gap-2">
                    <span style={{ opacity: 0.6 }}>Last run:</span>
                    <span className="font-medium text-themed-primary">
                      {new Date(depotSchedule.lastRun).toLocaleString()}
                    </span>
                  </div>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2 min-w-[120px]">
              <select
                className="px-3 py-1.5 text-sm rounded border themed-input"
                style={{
                  borderColor: 'var(--theme-input-border)',
                  backgroundColor: 'var(--theme-input-bg)',
                  color: 'var(--theme-text-primary)'
                }}
                value={depotSchedule.intervalHours}
                onChange={(e) => {
                  const newInterval = Number(e.target.value);
                  setDepotSchedule({
                    ...depotSchedule,
                    intervalHours: newInterval,
                    nextRun: new Date(Date.now() + newInterval * 60 * 60 * 1000)
                  });
                }}
                disabled={mockMode || !isAuthenticated}
              >
                <option value={1}>Every hour</option>
                <option value={6}>Every 6 hours</option>
                <option value={12}>Every 12 hours</option>
                <option value={24}>Every 24 hours</option>
                <option value={48}>Every 2 days</option>
                <option value={168}>Weekly</option>
              </select>
            </div>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Button
            variant="default"
            leftSection={<Zap className="w-4 h-4" />}
            onClick={() => handlePostProcessDepotMappings('incremental')}
            disabled={
              actionLoading ||
              isProcessingLogs ||
              depotProcessing?.isRunning ||
              mockMode ||
              !isAuthenticated
            }
            fullWidth
          >
            Apply Now
          </Button>
          <Button
            variant="default"
            leftSection={<RotateCcw className="w-4 h-4" />}
            onClick={() => handlePostProcessDepotMappings('full')}
            disabled={
              actionLoading ||
              isProcessingLogs ||
              depotProcessing?.isRunning ||
              mockMode ||
              !isAuthenticated
            }
            fullWidth
          >
            Regenerate All
          </Button>
        </div>

        <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
          <p className="text-xs text-themed-muted leading-relaxed">
            <strong>Apply Now:</strong> Maps unmapped downloads using existing depot data
            <br />
            <strong>Regenerate All:</strong> Re-fetches depot data from Steam and re-maps all downloads
          </p>
        </div>
      </Card>

      {/* Post-Depot Processing Modal */}
      <Modal
        opened={showPostDepotPopup}
        onClose={handleClosePostDepotPopup}
        title={
          <div className="flex items-center space-x-3">
            <CheckCircle className="w-6 h-6 text-themed-success" />
            <span>Depot Processing Complete</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Steam depot mapping has finished! Would you like to process all logs now to update download statistics with the new depot mappings?
          </p>

          <Alert color="green">
            <p className="text-sm">
              New depot mappings are ready to be applied to your download history.
            </p>
          </Alert>

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

          <div className="text-center">
            <span className="text-xs text-themed-muted">
              This dialog will close in {postDepotTimer} seconds
            </span>
          </div>
        </div>
      </Modal>

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

          <Alert color="yellow">
            <p className="text-sm">
              <strong>Important:</strong> Ensure no other maintenance tasks are running before continuing.
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
        </div>
      </Modal>
    </>
  );
};

export default LogProcessingManager;