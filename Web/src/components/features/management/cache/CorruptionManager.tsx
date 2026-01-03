import React, { useState, useEffect } from 'react';
import {
  AlertTriangle,
  Loader2,
  RefreshCw,
  ChevronDown,
  ChevronUp,
  Lock,
  CheckCircle,
  XCircle,
  ScrollText,
  HardDrive
} from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import type { CorruptionRemovalCompleteEvent } from '@contexts/SignalRContext/types';
import { useNotifications } from '@contexts/NotificationsContext';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
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
  const { notifications } = useNotifications();
  const signalR = useSignalR();

  // State
  const [corruptionSummary, setCorruptionSummary] = useState<Record<string, number>>({});
  const [pendingCorruptionRemoval, setPendingCorruptionRemoval] = useState<string | null>(null);
  const [expandedCorruptionService, setExpandedCorruptionService] = useState<string | null>(null);
  const [corruptionDetails, setCorruptionDetails] = useState<Record<string, CorruptedChunkDetail[]>>({});
  const [loadingDetails, setLoadingDetails] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);
  const [logsReadOnly, setLogsReadOnly] = useState(false);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [dockerSocketAvailable, setDockerSocketAvailable] = useState(true);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [startingCorruptionRemoval, setStartingCorruptionRemoval] = useState<string | null>(null);

  // Derive active corruption removal from notifications
  const activeCorruptionRemovalNotification = notifications.find(
    n => n.type === 'corruption_removal' && n.status === 'running'
  );
  const removingCorruption = activeCorruptionRemovalNotification
    ? (activeCorruptionRemovalNotification.id.replace('corruption_removal-', '') as string)
    : null;

  // Listen for CorruptionRemovalComplete event
  useEffect(() => {
    if (!signalR) return;

    const handleCorruptionRemovalComplete = async (result: CorruptionRemovalCompleteEvent) => {
      if (result.success) {
        try {
          const corruption = await ApiService.getCorruptionSummary(true);
          setCorruptionSummary(corruption);
        } catch (err) {
          console.error('Failed to refresh after corruption removal:', err);
        }
      } else {
        onError?.(result.error || 'Corruption removal failed');
      }
    };

    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);

    return () => {
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    };
  }, [signalR, onError]);

  useEffect(() => {
    if (!hasInitiallyLoaded) {
      setTimeout(() => {
        loadData();
      }, 100);
      loadDirectoryPermissions();
    }

    if (onReloadRef) {
      onReloadRef.current = () => loadData(true);
    }
  }, [hasInitiallyLoaded, onReloadRef]);

  const loadData = async (forceRefresh = false) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const corruption = await ApiService.getCorruptionSummary(forceRefresh);
      setCorruptionSummary(corruption);
      setLoadError(null);
      setHasInitiallyLoaded(true);
    } catch (err: unknown) {
      console.error('Failed to load corruption data:', err);
      setLoadError((err instanceof Error ? err.message : String(err)) || 'Failed to load corruption data');
    } finally {
      setIsLoading(false);
    }
  };

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
      onError?.((err instanceof Error ? err.message : String(err)) || `Failed to remove corrupted chunks for ${service}`);
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
        onError?.((err instanceof Error ? err.message : String(err)) || `Failed to load corruption details for ${service}`);
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

  return (
    <>
      <Card>
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-red flex-shrink-0">
              <AlertTriangle className="w-5 h-5 icon-red" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">Corruption Detection</h3>
              <p className="text-xs text-themed-muted">Find and fix corrupted cache files</p>
            </div>
            <HelpPopover position="left" width={320}>
              <HelpSection title="What This Does">
                Identifies cache chunks with 3+ repeated MISS requests, indicating
                the cached file is corrupted and needs to be redownloaded.
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
          </div>
          <div className="flex items-center gap-3">
            {!checkingPermissions && (
              <div className="flex items-center gap-2">
                <Tooltip content={logsReadOnly ? 'Logs are read-only' : 'Logs are writable'} position="top">
                  <span className="flex items-center gap-0.5">
                    <ScrollText className="w-3.5 h-3.5 text-themed-muted" />
                    {logsReadOnly ? (
                      <XCircle className="w-4 h-4" style={{ color: 'var(--theme-warning)' }} />
                    ) : (
                      <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />
                    )}
                  </span>
                </Tooltip>
                <Tooltip content={cacheReadOnly ? 'Cache is read-only' : 'Cache is writable'} position="top">
                  <span className="flex items-center gap-0.5">
                    <HardDrive className="w-3.5 h-3.5 text-themed-muted" />
                    {cacheReadOnly ? (
                      <XCircle className="w-4 h-4" style={{ color: 'var(--theme-warning)' }} />
                    ) : (
                      <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />
                    )}
                  </span>
                </Tooltip>
              </div>
            )}
            <button
              onClick={() => loadData(true)}
              disabled={isLoading || !!removingCorruption}
              className="hover-btn p-2 rounded-lg disabled:opacity-50 flex items-center justify-center"
              style={{
                color: 'var(--theme-text-muted)',
                backgroundColor: 'transparent'
              }}
              title="Refresh data"
            >
              {isLoading ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <RefreshCw className="w-4 h-4" />
              )}
            </button>
          </div>
        </div>

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
                Corruption removal requires write access to both logs and cache.
                Remove <code className="bg-themed-tertiary px-1 rounded">:ro</code> from your
                docker-compose volume mounts.
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
              <p className="text-sm mt-2">
                Add to your docker-compose.yml volumes:
              </p>
              <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1">
                - /var/run/docker.sock:/var/run/docker.sock:ro
              </code>
            </div>
          </Alert>
        )}

        {/* Content */}
        {isReadOnly || !dockerSocketAvailable ? (
          <div className="flex items-center justify-center py-4">
            <span
              className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
              style={{
                backgroundColor: 'var(--theme-warning-bg)',
                color: 'var(--theme-warning)',
                borderColor: 'var(--theme-warning)'
              }}
            >
              <Lock className="w-3 h-3" />
              {isReadOnly ? 'Read-only' : 'Docker socket required'}
            </span>
          </div>
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
                    onClick={() => loadData()}
                    className="mt-2"
                    leftSection={<RefreshCw className="w-3 h-3" />}
                  >
                    Try Again
                  </Button>
                </div>
              </Alert>
            )}

            {isLoading ? (
              <div className="flex flex-col items-center justify-center py-8 gap-3">
                <Loader2 className="w-6 h-6 animate-spin text-themed-accent" />
                <p className="text-sm text-themed-secondary">
                  Scanning logs for corrupted chunks...
                </p>
                <p className="text-xs text-themed-muted">
                  This may take several minutes for large log files
                </p>
              </div>
            ) : !loadError && corruptionList.length > 0 ? (
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
                          loading={removingCorruption === service || startingCorruptionRemoval === service}
                        >
                          {removingCorruption !== service && startingCorruptionRemoval !== service ? 'Remove All' : 'Removing...'}
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
            ) : (
              <div className="text-center py-8 text-themed-muted">
                <div className="mb-2">No corrupted chunks detected</div>
                <div className="text-xs">
                  Cache appears healthy - all chunks are being served successfully
                </div>
              </div>
            )}
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
                <li>
                  <strong>Cache files</strong> from disk for corrupted chunks
                </li>
                <li>
                  <strong>Log entries</strong> from access.log for these chunks
                </li>
                <li>
                  <strong>Database records</strong> for download sessions with corruption
                </li>
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
            <Button
              variant="filled"
              color="red"
              onClick={confirmRemoveCorruption}
            >
              Delete Cache & Logs
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CorruptionManager;
