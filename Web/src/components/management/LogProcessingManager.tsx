import React, { useState, useEffect, useRef } from 'react';
import { FileText, RefreshCw, PlayCircle, Database, AlertTriangle, Clock, Zap } from 'lucide-react';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import * as signalR from '@microsoft/signalr';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ChangeGapWarningModal } from '@components/shared/ChangeGapWarningModal';
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
  nextCrawlIn: any; // Can be TimeSpan string, object, or number from backend
  crawlIntervalHours: number;
  crawlIncrementalMode: boolean;
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
  const [confirmModal, setConfirmModal] = useState<
    | {
        title: string;
        message: string;
        confirmLabel?: string;
        onConfirm: () => Promise<void> | void;
      }
    | null
  >(null);
  const [changeGapWarning, setChangeGapWarning] = useState<{
    show: boolean;
    changeGap: number;
    estimatedApps: number;
  } | null>(null);

  const logProcessingOp = useBackendOperation('activeLogProcessing', 'logProcessing', 120);
  const signalRConnection = useRef<signalR.HubConnection | null>(null);
  const pollingInterval = useRef<NodeJS.Timeout | null>(null);
  const reconnectTimeout = useRef<NodeJS.Timeout | null>(null);
  const depotPollingInterval = useRef<NodeJS.Timeout | null>(null);
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
              pollingInterval.current = null;
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
          pollingInterval.current = null;
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

        // Mark setup as completed (persistent flag for guest mode eligibility)
        try {
          await fetch('/api/management/mark-setup-completed', {
            method: 'POST',
            headers: ApiService.getHeaders()
          });
        } catch (error) {
          console.warn('Failed to mark setup as completed:', error);
        }

        // Show completion for 3 seconds instead of 10, then stop completely
        setTimeout(async () => {
          setProcessingStatus(null);
          setIsProcessingLogs(false);  // Ensure buttons are re-enabled
          onDataRefresh?.();
        }, 3000);
      });

      // Listen for silent background processing updates (live mode)
      connection.on('DownloadsRefresh', async () => {
        // Silently refresh data without showing progress bars or notifications
        // This is triggered by the LiveLogMonitorService background processing
        onDataRefresh?.();
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
            pollingInterval.current = null;
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
        pollingInterval.current = null;
      }
      setProcessingStatus(null);
      onSuccess?.('Processing cancelled successfully');
      setTimeout(() => {
        onDataRefresh?.();
      }, 1000);
    } catch (err: any) {
      onError?.(err.message || 'Failed to cancel processing');
    } finally {
      setActionLoading(false);
      setConfirmModal(null);
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
          setDepotProcessing(data);
        }
      } catch (error) {
        console.error('Failed to fetch depot status:', error);
      }
    };

    checkDepotStatus();
    depotPollingInterval.current = setInterval(checkDepotStatus, 3000);
  };

  const executePostProcessDepotMappings = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      // Check if JSON file exists and needs to be imported to database
      const picsStatus = await ApiService.getPicsStatus();
      const hasJsonFile = picsStatus?.jsonFile?.exists === true;
      const hasDatabaseMappings = (picsStatus?.database?.totalMappings || 0) > 1000;

      // Import JSON to database if needed (JSON exists but database is empty)
      if (hasJsonFile && !hasDatabaseMappings) {
        console.log('[Management] Importing JSON file to database before scan');
        await fetch('/api/gameinfo/import-pics-data', {
          method: 'POST',
          headers: ApiService.getHeaders()
        });
        onSuccess?.('Imported depot mappings to database - depot count will update after scan completes');
        await new Promise(resolve => setTimeout(resolve, 1000));
      }

      // Use the scan mode from dropdown (incremental or full)
      const useIncrementalScan = depotProcessing?.crawlIncrementalMode ?? true;
      await ApiService.triggerSteamKitRebuild(useIncrementalScan);
      const scanType = useIncrementalScan ? 'Incremental' : 'Full';
      onSuccess?.(`${scanType} depot scan started - mappings will be applied when complete`);
      setTimeout(() => onDataRefresh?.(), 2000);
    } catch (err: any) {
      onError?.(err.message || 'Failed to process depot mappings');
    } finally {
      setActionLoading(false);
    }
  };

  const handleDownloadFromGitHub = async () => {
    setChangeGapWarning(null);
    setActionLoading(true);

    try {
      await ApiService.downloadPrecreatedDepotData();
      onSuccess?.('Pre-created depot data downloaded and imported successfully');
      setTimeout(() => onDataRefresh?.(), 2000);
    } catch (err: any) {
      onError?.(err.message || 'Failed to download from GitHub');
    } finally {
      setActionLoading(false);
    }
  };

  const handlePostProcessDepotMappings = async () => {
    // Check for change gap if incremental mode is selected
    if (depotProcessing?.crawlIncrementalMode) {
      try {
        const viability = await ApiService.checkIncrementalViability();

        if (viability.willTriggerFullScan) {
          // Show warning modal if change gap is too large
          setChangeGapWarning({
            show: true,
            changeGap: viability.changeGap,
            estimatedApps: viability.estimatedAppsToScan
          });
          return;
        }
      } catch (err) {
        console.error('Failed to check incremental viability:', err);
        // If check fails, proceed with normal flow
      }
    }

    // Show normal confirmation modal
    const scanMode = depotProcessing?.crawlIncrementalMode ? 'incremental' : 'full';
    setConfirmModal({
      title: 'Run Depot Scan & Apply',
      message: `Run ${scanMode} PICS scan and apply mappings? This will ${depotProcessing?.crawlIncrementalMode ? 'update only apps that changed since the last scan' : 're-scan all Steam apps'}, then apply all mappings to downloads.`,
      confirmLabel: 'Scan & Apply',
      onConfirm: () => executePostProcessDepotMappings()
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
    // Check if actually running first
    if (depotProcessing?.isRunning) {
      return 'Running now';
    }

    // Use backend data
    if (depotProcessing?.nextCrawlIn !== undefined && depotProcessing?.nextCrawlIn !== null) {
      // Handle TimeSpan object from backend (has hours, minutes, seconds, etc.)
      let totalSeconds: number;

      if (typeof depotProcessing.nextCrawlIn === 'object' && depotProcessing.nextCrawlIn.totalSeconds !== undefined) {
        // Backend sends TimeSpan with totalSeconds property
        totalSeconds = depotProcessing.nextCrawlIn.totalSeconds;
      } else if (typeof depotProcessing.nextCrawlIn === 'object' && depotProcessing.nextCrawlIn.totalHours !== undefined) {
        // Fallback: convert totalHours to seconds
        totalSeconds = depotProcessing.nextCrawlIn.totalHours * 3600;
      } else if (typeof depotProcessing.nextCrawlIn === 'string') {
        // Parse TimeSpan string format "HH:MM:SS" or "D.HH:MM:SS"
        const parts = depotProcessing.nextCrawlIn.split(':');
        if (parts.length >= 3) {
          const dayHourPart = parts[0].split('.');
          let hours = 0;
          let days = 0;

          if (dayHourPart.length === 2) {
            // Format: "D.HH:MM:SS"
            days = parseInt(dayHourPart[0]) || 0;
            hours = parseInt(dayHourPart[1]) || 0;
          } else {
            // Format: "HH:MM:SS"
            hours = parseInt(parts[0]) || 0;
          }

          const minutes = parseInt(parts[1]) || 0;
          const seconds = parseInt(parts[2]) || 0;
          totalSeconds = (days * 86400) + (hours * 3600) + (minutes * 60) + seconds;
        } else {
          return 'Loading...';
        }
      } else if (typeof depotProcessing.nextCrawlIn === 'number') {
        // Direct number - assume it's already in seconds
        totalSeconds = depotProcessing.nextCrawlIn;
      } else {
        return 'Loading...';
      }

      if (!isFinite(totalSeconds) || isNaN(totalSeconds)) return 'Loading...';

      // If time is negative or zero but not running, show "Due now"
      if (totalSeconds <= 0) {
        return 'Due now';
      }

      // Convert seconds to hours and minutes for display
      const totalHours = totalSeconds / 3600;

      if (totalHours > 24) {
        const days = Math.floor(totalHours / 24);
        const hours = Math.floor(totalHours % 24);
        return hours > 0 ? `${days}d ${hours}h` : `${days} days`;
      }
      const hours = Math.floor(totalHours);
      const minutes = Math.floor((totalSeconds % 3600) / 60);
      return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
    }

    // Fallback when backend data not yet loaded
    return 'Loading...';
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
            <strong>Reset Log Position:</strong> Choose to start from beginning or end of log file
            <br />
            <strong>Process All Logs:</strong> Process logs based on reset position (rust service always starts from top with duplicate detection)
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
                  <span className="font-medium text-themed-primary">
                    {depotProcessing?.crawlIntervalHours
                      ? `${depotProcessing.crawlIntervalHours} hour${depotProcessing.crawlIntervalHours !== 1 ? 's' : ''}`
                      : 'Loading...'}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Scan mode:</span>
                  <span className="font-medium text-themed-primary">
                    {depotProcessing?.crawlIncrementalMode !== undefined
                      ? depotProcessing.crawlIncrementalMode ? 'Incremental' : 'Full'
                      : 'Loading...'}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Next run:</span>
                  <span className="font-medium text-themed-primary">{formatNextRun()}</span>
                </div>
                {depotProcessing?.lastCrawlTime && (
                  <div className="flex items-center gap-2">
                    <span style={{ opacity: 0.6 }}>Last run:</span>
                    <span className="font-medium text-themed-primary">
                      {new Date(depotProcessing.lastCrawlTime).toLocaleString()}
                    </span>
                  </div>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2 min-w-[120px]">
              <EnhancedDropdown
                options={[
                  { value: '1', label: 'Every hour' },
                  { value: '6', label: 'Every 6 hours' },
                  { value: '12', label: 'Every 12 hours' },
                  { value: '24', label: 'Every 24 hours' },
                  { value: '48', label: 'Every 2 days' },
                  { value: '168', label: 'Weekly' }
                ]}
                value={String(depotProcessing?.crawlIntervalHours || 1)}
                onChange={async (value) => {
                  const newInterval = Number(value);
                  try {
                    await fetch('/api/gameinfo/steamkit/interval', {
                      method: 'POST',
                      headers: {
                        ...ApiService.getHeaders(),
                        'Content-Type': 'application/json'
                      },
                      body: JSON.stringify(newInterval)
                    });

                    // Immediately fetch updated status to show new next run time
                    const response = await fetch('/api/gameinfo/steamkit/progress');
                    if (response.ok) {
                      const data: PicsProgress = await response.json();
                      setDepotProcessing(data);
                    }
                  } catch (error) {
                    console.error('Failed to update crawl interval:', error);
                  }
                }}
                disabled={!isAuthenticated || mockMode}
                className="w-full"
              />
              <EnhancedDropdown
                options={[
                  { value: 'true', label: 'Incremental' },
                  { value: 'false', label: 'Full scan' }
                ]}
                value={String(depotProcessing?.crawlIncrementalMode ?? true)}
                onChange={async (value) => {
                  const incremental = value === 'true';
                  try {
                    await fetch('/api/gameinfo/steamkit/scan-mode', {
                      method: 'POST',
                      headers: {
                        ...ApiService.getHeaders(),
                        'Content-Type': 'application/json'
                      },
                      body: JSON.stringify(incremental)
                    });

                    // Immediately fetch updated status to show new scan mode
                    const response = await fetch('/api/gameinfo/steamkit/progress');
                    if (response.ok) {
                      const data: PicsProgress = await response.json();
                      setDepotProcessing(data);
                    }
                  } catch (error) {
                    console.error('Failed to update scan mode:', error);
                  }
                }}
                disabled={!isAuthenticated || mockMode}
                className="w-full"
              />
            </div>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="flex">
          <Button
            variant="filled"
            color="blue"
            leftSection={<Zap className="w-4 h-4" />}
            onClick={() => handlePostProcessDepotMappings()}
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
        </div>

        <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
          <p className="text-xs text-themed-muted leading-relaxed">
            <strong>Scan mode dropdown:</strong> Controls whether "Apply Now" and automatic runs use incremental or full scans
            <br />
            <strong>Incremental:</strong> Only scans apps that changed since last run (faster, recommended)
            <br />
            <strong>Full scan:</strong> Re-scans all Steam apps from scratch (slower, but ensures complete data)
            <br />
            <strong>Apply Now:</strong> Runs a scan using the dropdown setting (incremental or full), then applies mappings to all downloads
          </p>
        </div>
      </Card>

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
                    <strong>Start from Beginning:</strong> Process entire log history (rust processor has duplicate detection)
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
            </>
          )}
        </div>
      </Modal>

      {/* Change Gap Warning Modal */}
      {changeGapWarning?.show && (
        <ChangeGapWarningModal
          changeGap={changeGapWarning.changeGap}
          estimatedApps={changeGapWarning.estimatedApps}
          onConfirm={() => {
            setChangeGapWarning(null);
            executePostProcessDepotMappings();
          }}
          onCancel={() => setChangeGapWarning(null)}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
        />
      )}
    </>
  );
};

export default LogProcessingManager;