import React, { useState, useEffect, useCallback } from 'react';
import {
  AlertTriangle,
  Loader2,
  ChevronDown,
  ChevronUp
} from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import type {
  CorruptionRemovalCompleteEvent
} from '@contexts/SignalRContext/types';
import { useNotifications } from '@contexts/NotificationsContext';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import {
  ManagerCardHeader,
  LoadingState,
  EmptyState,
  ReadOnlyBadge,
  ScanningState
} from '@components/ui/ManagerCard';
import { useFormattedDateTime } from '@/hooks/useFormattedDateTime';
import type { CorruptedChunkDetail } from '@/types';

interface CorruptionManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onReloadRef?: React.MutableRefObject<(() => Promise<void>) | null>;
}

const CorruptionManager: React.FC<CorruptionManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onReloadRef
}) => {
  const { notifications, addNotification } = useNotifications();
  const signalR = useSignalR();

  // Derive corruption detection scan state from notifications (standardized pattern like GameCacheDetector)
  const activeCorruptionDetectionNotification = notifications.find(
    n => n.type === 'corruption_detection' && n.status === 'running'
  );
  const isScanningFromNotification = !!activeCorruptionDetectionNotification;

  // Track local starting state for immediate UI feedback before SignalR events arrive
  const [isStartingScan, setIsStartingScan] = useState(false);

  // Combined scanning state: either notification says running OR we're in starting phase
  const isScanning = isScanningFromNotification || isStartingScan;

  // State
  const [corruptionSummary, setCorruptionSummary] = useState<Record<string, number>>({});
  const [pendingCorruptionRemoval, setPendingCorruptionRemoval] = useState<string | null>(null);
  const [expandedCorruptionService, setExpandedCorruptionService] = useState<string | null>(null);
  const [corruptionDetails, setCorruptionDetails] = useState<Record<string, CorruptedChunkDetail[]>>({});
  const [loadingDetails, setLoadingDetails] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);
  const [logsReadOnly, setLogsReadOnly] = useState(false);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [dockerSocketAvailable, setDockerSocketAvailable] = useState(true);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [startingCorruptionRemoval, setStartingCorruptionRemoval] = useState<string | null>(null);
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [hasCachedResults, setHasCachedResults] = useState(false);

  const formattedLastDetection = useFormattedDateTime(lastDetectionTime);

  // Derive active corruption removal from notifications
  const activeCorruptionRemovalNotification = notifications.find(
    n => n.type === 'corruption_removal' && n.status === 'running'
  );
  const removingCorruption = activeCorruptionRemovalNotification
    ? (activeCorruptionRemovalNotification.id.replace('corruption_removal-', '') as string)
    : null;

  // Load cached data from database
  const loadCachedData = useCallback(async (showNotification: boolean = false) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const cached = await ApiService.getCachedCorruptionDetection();
      if (cached.hasCachedResults && cached.corruptionCounts) {
        setCorruptionSummary(cached.corruptionCounts);
        setLastDetectionTime(cached.lastDetectionTime || null);
        setHasCachedResults(true);

        // Show notification only when explicitly requested or once per session
        const sessionKey = 'corruptionManager_loadedNotificationShown';
        const alreadyShownThisSession = sessionStorage.getItem(sessionKey) === 'true';
        const totalCorrupted = Object.values(cached.corruptionCounts).reduce((a, b) => a + b, 0);
        const serviceCount = Object.keys(cached.corruptionCounts).filter(k => cached.corruptionCounts![k] > 0).length;

        if (showNotification || !alreadyShownThisSession) {
          if (totalCorrupted > 0) {
            addNotification({
              type: 'generic',
              status: 'completed',
              message: `Loaded previous results: ${totalCorrupted.toLocaleString()} corrupted chunk${totalCorrupted !== 1 ? 's' : ''} across ${serviceCount} service${serviceCount !== 1 ? 's' : ''}`,
              details: { notificationType: 'info' }
            });
          } else if (showNotification) {
            addNotification({
              type: 'generic',
              status: 'completed',
              message: 'No corrupted chunks found in previous scan',
              details: { notificationType: 'success' }
            });
          }
          sessionStorage.setItem(sessionKey, 'true');
        }
      } else {
        setCorruptionSummary({});
        setLastDetectionTime(null);
        setHasCachedResults(false);

        if (showNotification) {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: 'No previous corruption scan results found',
            details: { notificationType: 'info' }
          });
        }
      }
      setHasInitiallyLoaded(true);
    } catch (err: unknown) {
      console.error('Failed to load cached corruption data:', err);
      setLoadError(
        (err instanceof Error ? err.message : String(err)) || 'Failed to load cached data'
      );
    } finally {
      setIsLoading(false);
    }
  }, [addNotification]);

  // Start a background scan
  const startScan = useCallback(async () => {
    if (isScanning || mockMode) return;

    setIsStartingScan(true);
    setLoadError(null);
    setCorruptionSummary({});
    setLastDetectionTime(null);
    setHasCachedResults(false);

    try {
      // Start background detection - SignalR will send CorruptionDetectionStarted event
      await ApiService.startCorruptionDetection();
      // Note: NotificationsContext will create a notification via SignalR (CorruptionDetectionStarted event)
    } catch (err: unknown) {
      console.error('Failed to start corruption scan:', err);
      setLoadError(
        (err instanceof Error ? err.message : String(err)) || 'Failed to start corruption scan'
      );
      setIsStartingScan(false);
    }
  }, [isScanning, mockMode]);

  // Listen for corruption detection completion via notifications
  useEffect(() => {
    // Handle corruption detection completion - ONLY if we were starting a scan
    if (isStartingScan) {
      const corruptionDetectionCompleteNotifs = notifications.filter(
        n => n.type === 'corruption_detection' && n.status === 'completed'
      );
      if (corruptionDetectionCompleteNotifs.length > 0) {
        console.log('[CorruptionManager] Corruption detection completed, loading results from database');
        setIsStartingScan(false);

        // Load fresh results from the database (backend already saved them)
        const loadResults = async () => {
          try {
            const result = await ApiService.getCachedCorruptionDetection();
            if (result.hasCachedResults && result.corruptionCounts) {
              setCorruptionSummary(result.corruptionCounts);
              setLastDetectionTime(result.lastDetectionTime || null);
              setHasCachedResults(true);
            }
          } catch (err) {
            console.error('[CorruptionManager] Failed to load detection results:', err);
          }
        };
        loadResults();
      }

      // Handle corruption detection failure - ONLY if we were starting a scan
      const corruptionDetectionFailedNotifs = notifications.filter(
        n => n.type === 'corruption_detection' && n.status === 'failed'
      );
      if (corruptionDetectionFailedNotifs.length > 0) {
        console.error('[CorruptionManager] Corruption detection failed');
        setIsStartingScan(false);
        const failedNotif = corruptionDetectionFailedNotifs[0];
        setLoadError(failedNotif.error || 'Corruption scan failed');
      }
    }
  }, [notifications, isStartingScan]);

  // Listen for CorruptionRemovalComplete event
  useEffect(() => {
    if (!signalR) return;

    const handleCorruptionRemovalComplete = async (result: CorruptionRemovalCompleteEvent) => {
      if (result.success) {
        // Start a new scan after removal to refresh data
        await startScan();
      } else {
        onError?.(result.error || 'Corruption removal failed');
      }
    };

    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);

    return () => {
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    };
  }, [signalR, onError, startScan]);

  // Initial load - load cached data without auto-scanning (matches GameCacheDetector pattern)
  useEffect(() => {
    if (!hasInitiallyLoaded) {
      loadDirectoryPermissions();
      // Only load cached data - don't auto-start scan
      loadCachedData();
    }

    if (onReloadRef) {
      onReloadRef.current = () => startScan();
    }
  }, [hasInitiallyLoaded, onReloadRef, loadCachedData, startScan]);

  const loadDirectoryPermissions = async () => {
    try {
      setCheckingPermissions(true);
      const data = await ApiService.getDirectoryPermissions();
      setLogsReadOnly(data.logs.readOnly);
      setCacheReadOnly(data.cache.readOnly);
      setDockerSocketAvailable(data.dockerSocket?.available ?? true);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setLogsReadOnly(false);
      setCacheReadOnly(false);
      setDockerSocketAvailable(true);
    } finally {
      setCheckingPermissions(false);
    }
  };

  const handleRemoveCorruption = (service: string) => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }
    setPendingCorruptionRemoval(service);
  };

  const confirmRemoveCorruption = async () => {
    if (!pendingCorruptionRemoval || authMode !== 'authenticated') return;

    const service = pendingCorruptionRemoval;
    setPendingCorruptionRemoval(null);
    setStartingCorruptionRemoval(service);

    try {
      await ApiService.removeCorruptedChunks(service);
    } catch (err: unknown) {
      console.error('Removal failed:', err);
      onError?.(
        (err instanceof Error ? err.message : String(err)) ||
          `Failed to remove corrupted chunks for ${service}`
      );
    } finally {
      setStartingCorruptionRemoval(null);
    }
  };

  const toggleCorruptionDetails = async (service: string) => {
    if (expandedCorruptionService === service) {
      setExpandedCorruptionService(null);
      return;
    }

    setExpandedCorruptionService(service);

    if (!corruptionDetails[service]) {
      setLoadingDetails(service);
      try {
        const details = await ApiService.getCorruptionDetails(service);
        setCorruptionDetails((prev) => ({ ...prev, [service]: details }));
      } catch (err: unknown) {
        onError?.(
          (err instanceof Error ? err.message : String(err)) ||
            `Failed to load corruption details for ${service}`
        );
        setExpandedCorruptionService(null);
      } finally {
        setLoadingDetails(null);
      }
    }
  };

  const corruptionList = Object.entries(corruptionSummary)
    .filter(([_, count]) => count > 0)
    .sort((a, b) => b[1] - a[1]);

  const isReadOnly = logsReadOnly || cacheReadOnly;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title="What This Does">
        Identifies cache chunks with 3+ repeated MISS requests, indicating the cached file
        is corrupted and needs to be redownloaded.
      </HelpSection>

      <HelpSection title="What Removal Deletes" variant="subtle">
        <ul className="list-disc list-inside text-sm space-y-1">
          <li><strong>Cache files</strong> - corrupted chunks from disk</li>
          <li><strong>Log entries</strong> - related entries from access.log</li>
          <li><strong>Database records</strong> - download sessions with corruption</li>
        </ul>
      </HelpSection>

      <HelpNote type="warning">
        Removal affects both cache and logs - requires write access to both.
      </HelpNote>
    </HelpPopover>
  );

  // Action buttons for header
  const headerActions = (
    <div className="flex items-center gap-2">
      <Tooltip content="Load previous scan results from database" position="top">
        <Button
          onClick={() => loadCachedData(true)}
          disabled={isLoading || isScanning || !!removingCorruption}
          variant="subtle"
          size="sm"
        >
          {isLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Load'}
        </Button>
      </Tooltip>
      <Tooltip content="Scan for corrupted cache chunks" position="top">
        <Button
          onClick={() => startScan()}
          disabled={isLoading || isScanning || !!removingCorruption}
          variant="filled"
          color="blue"
          size="sm"
        >
          {isScanning ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Scan'}
        </Button>
      </Tooltip>
    </div>
  );

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={AlertTriangle}
          iconColor="red"
          title="Corruption Detection"
          subtitle="Find and fix corrupted cache files"
          helpContent={helpContent}
          permissions={{
            logsReadOnly,
            cacheReadOnly,
            checkingPermissions
          }}
          actions={headerActions}
        />

        {/* Previous Results Badge - matches GameCacheDetector pattern */}
        {hasCachedResults && lastDetectionTime && !isScanning && !isLoading && (
          <Alert color="blue" className="mb-4">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium">
                Results from previous scan
              </span>
              <span className="text-xs text-themed-muted">
                {formattedLastDetection}
              </span>
            </div>
          </Alert>
        )}

        {/* Scanning Status */}
        {isScanning && (
          <ScanningState message="Scanning logs for corrupted chunks... This may take several minutes for large log files." />
        )}

        {/* Read-Only Warning */}
        {isReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">
                {logsReadOnly && cacheReadOnly
                  ? 'Logs and cache directories are read-only'
                  : logsReadOnly
                    ? 'Logs directory is read-only'
                    : 'Cache directory is read-only'}
              </p>
              <p className="text-sm mt-1">
                Corruption removal requires write access to both logs and cache. Remove{' '}
                <code className="bg-themed-tertiary px-1 rounded">:ro</code> from your docker-compose
                volume mounts.
              </p>
            </div>
          </Alert>
        )}

        {/* Docker Socket Warning */}
        {!dockerSocketAvailable && !isReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">Docker socket not available</p>
              <p className="text-sm mt-1">
                Corruption removal requires signaling nginx to reopen logs afterward.
              </p>
              <p className="text-sm mt-2">Add to your docker-compose.yml volumes:</p>
              <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1">
                - /var/run/docker.sock:/var/run/docker.sock:ro
              </code>
            </div>
          </Alert>
        )}

        {/* Content */}
        {isReadOnly || !dockerSocketAvailable ? (
          <ReadOnlyBadge message={isReadOnly ? 'Read-only' : 'Docker socket required'} />
        ) : (
          <>
            {loadError && (
              <Alert color="red" className="mb-4">
                <div>
                  <p className="text-sm font-medium mb-1">Failed to detect corrupted chunks</p>
                  <p className="text-xs opacity-75">{loadError}</p>
                  <Button
                    variant="default"
                    size="sm"
                    onClick={() => startScan()}
                    className="mt-2"
                  >
                    Try Again
                  </Button>
                </div>
              </Alert>
            )}

            {isLoading && !isScanning ? (
              <LoadingState message="Loading cached data..." />
            ) : !loadError && hasCachedResults && corruptionList.length > 0 ? (
              <div className="space-y-3">
                {corruptionList.map(([service, count]) => (
                  <div
                    key={`corruption-${service}`}
                    className="rounded-lg border"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      borderColor: 'var(--theme-border-secondary)'
                    }}
                  >
                    <div className="flex items-center gap-2 p-3">
                      <Button
                        onClick={() => toggleCorruptionDetails(service)}
                        variant="subtle"
                        size="sm"
                        className="flex-shrink-0"
                        disabled={!!removingCorruption}
                      >
                        {expandedCorruptionService === service ? (
                          <ChevronUp className="w-4 h-4" />
                        ) : (
                          <ChevronDown className="w-4 h-4" />
                        )}
                      </Button>
                      <div className="flex-1">
                        <div className="flex items-center gap-2">
                          <span className="capitalize font-medium text-themed-primary">
                            {service}
                          </span>
                          <span className="text-xs text-themed-muted">
                            ({count.toLocaleString()} corrupted chunk{count !== 1 ? 's' : ''})
                          </span>
                        </div>
                      </div>
                      <Tooltip content="Delete cache files and remove log entries for corrupted chunks">
                        <Button
                          onClick={() => handleRemoveCorruption(service)}
                          disabled={
                            mockMode ||
                            !!removingCorruption ||
                            !!startingCorruptionRemoval ||
                            authMode !== 'authenticated' ||
                            logsReadOnly ||
                            cacheReadOnly ||
                            !dockerSocketAvailable ||
                            checkingPermissions
                          }
                          variant="filled"
                          color="red"
                          size="sm"
                          loading={
                            removingCorruption === service || startingCorruptionRemoval === service
                          }
                        >
                          {removingCorruption !== service && startingCorruptionRemoval !== service
                            ? 'Remove All'
                            : 'Removing...'}
                        </Button>
                      </Tooltip>
                    </div>

                    {/* Expandable Details Section */}
                    {expandedCorruptionService === service && (
                      <div
                        className="border-t px-3 py-3"
                        style={{ borderColor: 'var(--theme-border-secondary)' }}
                      >
                        {loadingDetails === service ? (
                          <div className="flex items-center justify-center py-4 gap-2">
                            <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
                            <span className="text-sm text-themed-secondary">
                              Loading corruption details...
                            </span>
                          </div>
                        ) : corruptionDetails[service] && corruptionDetails[service].length > 0 ? (
                          <div className="space-y-2 max-h-96 overflow-y-auto">
                            {corruptionDetails[service].map((chunk, idx) => (
                              <div
                                key={idx}
                                className="p-2 rounded border"
                                style={{
                                  backgroundColor: 'var(--theme-bg-secondary)',
                                  borderColor: 'var(--theme-border-primary)'
                                }}
                              >
                                <div className="flex items-start gap-2">
                                  <AlertTriangle className="w-4 h-4 text-themed-warning flex-shrink-0 mt-0.5" />
                                  <div className="flex-1 min-w-0">
                                    <div className="mb-1">
                                      <Tooltip content={chunk.url}>
                                        <span className="text-xs font-mono text-themed-primary truncate block">
                                          {chunk.url}
                                        </span>
                                      </Tooltip>
                                    </div>
                                    <div className="flex items-center gap-3 text-xs text-themed-muted">
                                      <span>
                                        Miss count:{' '}
                                        <strong className="text-themed-error">
                                          {chunk.miss_count || 0}
                                        </strong>
                                      </span>
                                      {chunk.cache_file_path && (
                                        <Tooltip content={chunk.cache_file_path}>
                                          <span className="truncate">
                                            Cache:{' '}
                                            <code className="text-xs">
                                              {chunk.cache_file_path.split('/').pop() ||
                                                chunk.cache_file_path.split('\\').pop()}
                                            </code>
                                          </span>
                                        </Tooltip>
                                      )}
                                    </div>
                                  </div>
                                </div>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <div className="text-center py-4 text-themed-muted text-sm">
                            No details available
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            ) : !loadError && hasCachedResults && corruptionList.length === 0 ? (
              <EmptyState
                icon={AlertTriangle}
                title="No corrupted chunks detected"
                subtitle="Cache appears healthy - all chunks are being served successfully"
              />
            ) : !loadError && !hasCachedResults && !isScanning && !isLoading ? (
              <EmptyState
                icon={AlertTriangle}
                title="No cached data available"
                subtitle="Click the Scan button to detect corrupted cache chunks"
              />
            ) : null}
          </>
        )}
      </Card>

      {/* Corruption Removal Confirmation Modal */}
      <Modal
        opened={pendingCorruptionRemoval !== null}
        onClose={() => setPendingCorruptionRemoval(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Remove Corrupted Chunks</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Remove all corrupted cache chunks for <strong>{pendingCorruptionRemoval}</strong>?
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">This will DELETE:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li><strong>Cache files</strong> from disk for corrupted chunks</li>
                <li><strong>Log entries</strong> from access.log for these chunks</li>
                <li><strong>Database records</strong> for download sessions with corruption</li>
              </ul>
            </div>
          </Alert>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>May take several minutes for large cache directories</li>
                <li>Valid {pendingCorruptionRemoval} cache files will remain intact</li>
                <li>
                  Removes approximately {corruptionSummary[pendingCorruptionRemoval || ''] || 0}{' '}
                  corrupted chunks
                </li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingCorruptionRemoval(null)}>
              Cancel
            </Button>
            <Button variant="filled" color="red" onClick={confirmRemoveCorruption}>
              Delete Cache & Logs
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CorruptionManager;
