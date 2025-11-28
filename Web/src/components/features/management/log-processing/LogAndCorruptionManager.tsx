import React, { useState, useEffect, useCallback, useMemo } from 'react';
import {
  FileText,
  AlertTriangle,
  Loader2,
  RefreshCw,
  ChevronDown,
  ChevronUp,
  Lock
} from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import { useNotifications } from '@contexts/NotificationsContext';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import type { CorruptedChunkDetail } from '@/types';

// Main services that should always be shown first
const MAIN_SERVICES = [
  'steam',
  'epic',
  'riot',
  'blizzard',
  'origin',
  'uplay',
  'gog',
  'wsus',
  'microsoft',
  'sony',
  'nintendo',
  'apple'
];

const ServiceButton: React.FC<{
  service: string;
  count: number;
  isRemoving: boolean;
  isDisabled: boolean;
  onClick: () => void;
}> = ({ service, count, isRemoving, isDisabled, onClick }) => {
  return (
    <Button
      onClick={onClick}
      disabled={isDisabled}
      variant="default"
      loading={isRemoving}
      className="flex flex-col items-center min-h-[60px] justify-center"
      fullWidth
    >
      {!isRemoving ? (
        <>
          <span className="capitalize font-medium text-sm sm:text-base">Clear {service}</span>
          <span className="text-xs text-themed-muted mt-1">({count.toLocaleString()} entries)</span>
        </>
      ) : (
        <span className="capitalize font-medium text-sm sm:text-base">Removing...</span>
      )}
    </Button>
  );
};

interface LogAndCorruptionManagerProps {
  isAuthenticated: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onReloadRef?: React.MutableRefObject<(() => Promise<void>) | null>;
  onClearOperationRef?: React.MutableRefObject<(() => Promise<void>) | null>;
}

const LogAndCorruptionManager: React.FC<LogAndCorruptionManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess,
  onReloadRef,
  onClearOperationRef
}) => {
  // Get notifications to check for running operations
  const { notifications } = useNotifications();

  // Log File Management State
  const [serviceCounts, setServiceCounts] = useState<Record<string, number>>({});
  const [config, setConfig] = useState({
    logPath: 'Loading...',
    services: [] as string[]
  });
  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<string | null>(null);
  const [showMoreServices, setShowMoreServices] = useState(false);

  // Corruption Detection State
  const [corruptionSummary, setCorruptionSummary] = useState<Record<string, number>>({});
  const [pendingCorruptionRemoval, setPendingCorruptionRemoval] = useState<string | null>(null);
  const [expandedCorruptionService, setExpandedCorruptionService] = useState<string | null>(null);
  const [corruptionDetails, setCorruptionDetails] = useState<
    Record<string, CorruptedChunkDetail[]>
  >({});
  const [loadingDetails, setLoadingDetails] = useState<string | null>(null);

  // Shared State
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);
  const [logsReadOnly, setLogsReadOnly] = useState(false);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(true);

  const signalR = useSignalR();

  // Derive active operations from notifications (standardized pattern)
  // Log entry removal: notification ID is 'service_removal-{service}', details.service contains the service name
  const activeServiceRemovalNotification = notifications.find(
    n => n.type === 'service_removal' && n.id.startsWith('service_removal-') && n.status === 'running'
  );
  const activeServiceRemoval = activeServiceRemovalNotification?.details?.service as string | null ?? null;

  // Corruption removal: notification ID is 'corruption_removal-{service}'
  const activeCorruptionRemovalNotification = notifications.find(
    n => n.type === 'corruption_removal' && n.status === 'running'
  );
  const removingCorruption = activeCorruptionRemovalNotification
    ? (activeCorruptionRemovalNotification.id.replace('corruption_removal-', '') as string)
    : null;

  // Track local loading states for button feedback before SignalR events arrive
  const [startingServiceRemoval, setStartingServiceRemoval] = useState<string | null>(null);
  const [startingCorruptionRemoval, setStartingCorruptionRemoval] = useState<string | null>(null);

  // Clear operation state is now a no-op since state is derived from notifications
  const clearOperationState = async () => {
    // State is derived from notifications, nothing to clear locally
  };

  // Listen for CorruptionRemovalComplete event and refresh data
  useEffect(() => {
    if (!signalR) return;

    const handleCorruptionRemovalComplete = async (payload: any) => {
      console.log('[LogAndCorruptionManager] CorruptionRemovalComplete received, refreshing data');

      // State is derived from notifications - NotificationsContext handles the notification update
      // We just need to refresh the data if successful
      if (payload.success) {
        try {
          const [configData, counts, corruption] = await Promise.all([
            ApiService.getConfig(),
            ApiService.getServiceLogCounts(true),
            ApiService.getCorruptionSummary(true)
          ]);
          setConfig({
            logPath: configData.logsPath,
            services: Object.keys(counts)
          });
          setServiceCounts(counts);
          setCorruptionSummary(corruption);
        } catch (err) {
          console.error('[LogAndCorruptionManager] Failed to refresh after corruption removal:', err);
        }
      } else {
        // Removal failed - show error
        onError?.(payload.error || 'Corruption removal failed');
      }
    };

    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);

    return () => {
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    };
  }, [signalR, onError]);

  useEffect(() => {
    // Only load on initial mount
    if (!hasInitiallyLoaded) {
      // Defer heavy data loading to not block initial render
      // Show UI first, then load data in background after 100ms
      setTimeout(() => {
        loadAllData();
      }, 100);

      // Load directory permissions
      loadDirectoryPermissions();

      // Note: Operation state is now derived from notifications
      // NotificationsContext handles recovery via backend status endpoints
    }

    // Expose reload function to parent via ref
    // Always force refresh when called from parent (after operations complete)
    if (onReloadRef) {
      onReloadRef.current = () => loadAllData(true);
    }

    // Expose clear operation function to parent via ref
    if (onClearOperationRef) {
      onClearOperationRef.current = clearOperationState;
    }
  }, [hasInitiallyLoaded, onReloadRef, onClearOperationRef]);

  // Note: Service removal progress is now reported via SignalR in ManagementTab
  // activeServiceRemoval is only used for local UI state (button disabling, etc.)

  const loadAllData = async (forceRefresh = false) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const [configData, counts, corruption] = await Promise.all([
        ApiService.getConfig(),
        ApiService.getServiceLogCounts(forceRefresh),
        ApiService.getCorruptionSummary(forceRefresh)
      ]);
      setConfig({
        logPath: configData.logsPath,
        services: Object.keys(counts)
      });
      setServiceCounts(counts);
      setCorruptionSummary(corruption);
      setLoadError(null);
      setHasInitiallyLoaded(true);
    } catch (err: any) {
      console.error('Failed to load log and corruption data:', err);
      setLoadError(err.message || 'Failed to load service data');
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
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setLogsReadOnly(false); // Assume writable on error
      setCacheReadOnly(false);
    } finally {
      setCheckingPermissions(false);
    }
  };

  const executeRemoveServiceLogs = async (serviceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setPendingServiceRemoval(null);
    setStartingServiceRemoval(serviceName);

    try {
      const result = await ApiService.removeServiceFromLogs(serviceName);

      if (result && result.status === 'started') {
        // SignalR will send LogRemovalProgress which creates the notification
        // The UI will update automatically when the notification is added
        onSuccess?.(`Removing ${serviceName} entries from logs...`);
      } else {
        onError?.(`Unexpected response when starting log removal for ${serviceName}`);
      }
    } catch (err: any) {
      const errorMessage = err.message?.includes('read-only')
        ? 'Logs directory is read-only. Remove :ro from docker-compose volume mount.'
        : err.message || 'Action failed';
      onError?.(errorMessage);
    } finally {
      setStartingServiceRemoval(null);
    }
  };

  const handleRemoveServiceLogs = useCallback(
    (serviceName: string) => {
      if (authMode !== 'authenticated') {
        onError?.('Full authentication required for management operations');
        return;
      }
      setPendingServiceRemoval(serviceName);
    },
    [authMode, onError]
  );

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
      // Backend will send CorruptionRemovalStarted via SignalR which creates the notification
      // Then CorruptionRemovalComplete when done
      // The UI will update automatically via derived state from notifications
    } catch (err: any) {
      console.error('[CorruptionDetection] Removal failed:', err);
      onError?.(err.message || `Failed to remove corrupted chunks for ${service}`);
    } finally {
      setStartingCorruptionRemoval(null);
    }
  };

  const toggleCorruptionDetails = async (service: string) => {
    if (expandedCorruptionService === service) {
      // Collapse if already expanded
      setExpandedCorruptionService(null);
      return;
    }

    // Expand and load details if not already loaded
    setExpandedCorruptionService(service);

    if (!corruptionDetails[service]) {
      setLoadingDetails(service);
      try {
        const details = await ApiService.getCorruptionDetails(service);
        setCorruptionDetails((prev) => ({ ...prev, [service]: details }));
      } catch (err: any) {
        onError?.(err.message || `Failed to load corruption details for ${service}`);
        setExpandedCorruptionService(null);
      } finally {
        setLoadingDetails(null);
      }
    }
  };

  const { mainServices, otherServices, servicesWithData } = useMemo(() => {
    const allServices = Object.keys(serviceCounts).filter((service) => serviceCounts[service] > 0);

    const main = allServices
      .filter((service) => MAIN_SERVICES.includes(service.toLowerCase()))
      .sort();

    const other = allServices
      .filter((service) => !MAIN_SERVICES.includes(service.toLowerCase()))
      .sort();

    const displayed = showMoreServices ? [...main, ...other] : main;

    return { mainServices: main, otherServices: other, servicesWithData: displayed };
  }, [serviceCounts, showMoreServices]);

  const corruptionList = Object.entries(corruptionSummary)
    .filter(([_, count]) => count > 0)
    .sort((a, b) => b[1] - a[1]);

  return (
    <>
      <Card>
        {/* Shared Header with Single Refresh Button */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-orange">
              <FileText className="w-5 h-5 icon-orange" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">Log & Cache Management</h3>
              <p className="text-xs text-themed-muted">Remove log entries and fix corrupted cache files</p>
            </div>
            <HelpPopover position="left" width={320}>
              <HelpSection title="Operations">
                <div className="space-y-1.5">
                  <HelpDefinition term="Log Removal" termColor="blue">
                    Removes entries from access.log only — cache files remain intact
                  </HelpDefinition>
                  <HelpDefinition term="Corruption Fix" termColor="purple">
                    Deletes cache files, log entries, and database records for corrupted chunks
                  </HelpDefinition>
                </div>
              </HelpSection>

              <HelpSection title="Corruption Detection" variant="subtle">
                Identifies chunks with 3+ repeated MISS requests,
                indicating the cache file is broken and needs redownload.
              </HelpSection>

              <HelpNote type="warning">
                Both operations require write permissions and cannot be undone.
              </HelpNote>
            </HelpPopover>
          </div>
          <button
            onClick={() => loadAllData(true)}
            disabled={isLoading || !!activeServiceRemoval || !!removingCorruption}
            className="p-2 rounded-lg transition-colors disabled:opacity-50 flex items-center justify-center"
            style={{
              color: 'var(--theme-text-muted)',
              backgroundColor: 'transparent'
            }}
            onMouseEnter={(e) =>
              !isLoading &&
              !activeServiceRemoval &&
              !removingCorruption &&
              (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
            }
            onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
            title="Refresh all data"
          >
            {isLoading ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <RefreshCw className="w-4 h-4" />
            )}
          </button>
        </div>

        {/* Read-Only Warning - Show once at the top */}
        {(logsReadOnly || cacheReadOnly) && (
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
                {logsReadOnly && cacheReadOnly
                  ? 'Both directories are mounted in read-only mode. All log and corruption management features are disabled.'
                  : logsReadOnly
                    ? 'The logs directory is mounted in read-only mode. Log removal features are disabled.'
                    : 'The cache directory is mounted in read-only mode. Corruption removal features are disabled.'}{' '}
                Remove <code className="bg-themed-tertiary px-1 rounded">:ro</code> from your
                docker-compose volume mounts to enable these features.
              </p>
            </div>
          </Alert>
        )}

        {/* Log File Management Section */}
        <div className="mb-8">
          <div className="flex items-center justify-between mb-3">
            <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Log Entries</h4>
            {logsReadOnly && (
              <span
                className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
                style={{
                  backgroundColor: 'var(--theme-warning-bg)',
                  color: 'var(--theme-warning)',
                  borderColor: 'var(--theme-warning)'
                }}
              >
                <Lock className="w-3 h-3" />
                Read-only
              </span>
            )}
          </div>
          {!logsReadOnly && (
            <p className="text-themed-muted text-sm mb-4 break-words">
              Remove service entries from{' '}
              <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-xs">
                {config.logPath}
              </code>
            </p>
          )}

          {!logsReadOnly && (
            <>
              {loadError && (
                <Alert color="red" className="mb-4">
                  <div>
                    <p className="text-sm font-medium mb-1">Failed to load service log counts</p>
                    <p className="text-xs opacity-75">{loadError}</p>
                    <Button
                      variant="default"
                      size="sm"
                      onClick={() => loadAllData()}
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
                    Scanning log files for services...
                  </p>
                  <p className="text-xs text-themed-muted">
                    This may take several minutes for large log files
                  </p>
                </div>
              ) : !loadError && (mainServices.length > 0 || otherServices.length > 0) ? (
                <>
                  <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
                    {servicesWithData.map((service) => {
                      const handleClick = () => handleRemoveServiceLogs(service);
                      return (
                        <ServiceButton
                          key={service}
                          service={service}
                          count={serviceCounts[service] || 0}
                          isRemoving={activeServiceRemoval === service || startingServiceRemoval === service}
                          isDisabled={
                            mockMode ||
                            !!activeServiceRemoval ||
                            !!removingCorruption ||
                            !!startingServiceRemoval ||
                            !!startingCorruptionRemoval ||
                            authMode !== 'authenticated' ||
                            logsReadOnly ||
                            checkingPermissions
                          }
                          onClick={handleClick}
                        />
                      );
                    })}
                  </div>

                  {otherServices.length > 0 && (
                    <div className="mt-4 text-center">
                      <Button
                        variant="default"
                        size="sm"
                        onClick={() => setShowMoreServices(!showMoreServices)}
                      >
                        {showMoreServices ? (
                          <>Show Less ({otherServices.length} hidden)</>
                        ) : (
                          <>Show More ({otherServices.length} more)</>
                        )}
                      </Button>
                    </div>
                  )}
                </>
              ) : (
                <div className="text-center py-8 text-themed-muted">
                  <div className="mb-2">No services with log entries found</div>
                  <div className="text-xs">
                    Services appear here when they have downloadable content in the logs
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {/* Divider */}
        <div className="border-t mb-8" style={{ borderColor: 'var(--theme-border-primary)' }}></div>

        {/* Corruption Detection Section */}
        <div className="mb-6">
          <div className="flex items-center justify-between mb-3">
            <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Corrupted Cache</h4>
            {(logsReadOnly || cacheReadOnly) && (
              <span
                className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
                style={{
                  backgroundColor: 'var(--theme-warning-bg)',
                  color: 'var(--theme-warning)',
                  borderColor: 'var(--theme-warning)'
                }}
              >
                <Lock className="w-3 h-3" />
                Read-only
              </span>
            )}
          </div>
          {!(logsReadOnly || cacheReadOnly) && (
            <p className="text-themed-muted text-sm mb-4">
              Detects chunks with 3+ repeated MISS requests. Removal deletes cache files, log entries, and database records.
            </p>
          )}

          {!(logsReadOnly || cacheReadOnly) && (
            <>
              {loadError && (
                <Alert color="red" className="mb-4">
                  <div>
                    <p className="text-sm font-medium mb-1">Failed to detect corrupted chunks</p>
                    <p className="text-xs opacity-75">{loadError}</p>
                    <Button
                      variant="default"
                      size="sm"
                      onClick={() => loadAllData()}
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
                <>
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
                            disabled={!!removingCorruption || !!activeServiceRemoval}
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
                                !!activeServiceRemoval ||
                                !!startingCorruptionRemoval ||
                                !!startingServiceRemoval ||
                                authMode !== 'authenticated' ||
                                logsReadOnly ||
                                cacheReadOnly ||
                                checkingPermissions
                              }
                              variant="filled"
                              color="red"
                              size="sm"
                              loading={removingCorruption === service || startingCorruptionRemoval === service}
                              title={
                                logsReadOnly || cacheReadOnly
                                  ? 'Directories are read-only'
                                  : undefined
                              }
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
                            ) : corruptionDetails[service] &&
                              corruptionDetails[service].length > 0 ? (
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
                </>
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
        </div>

      </Card>

      {/* Log Removal Confirmation Modal */}
      <Modal
        opened={pendingServiceRemoval !== null}
        onClose={() => {
          if (!startingServiceRemoval) {
            setPendingServiceRemoval(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Remove Service Logs</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Remove all <strong>{pendingServiceRemoval}</strong> entries from the log file? This will
            reduce log size and improve performance.
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>May take several minutes for large log files</li>
                <li>Cached {pendingServiceRemoval} game files will remain intact</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingServiceRemoval(null)}
              disabled={!!startingServiceRemoval}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() =>
                pendingServiceRemoval && executeRemoveServiceLogs(pendingServiceRemoval)
              }
              loading={!!startingServiceRemoval}
            >
              Remove Logs
            </Button>
          </div>
        </div>
      </Modal>

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
                  <strong>Cache files</strong> from disk for corrupted chunks (3+ MISS/UNKNOWN)
                </li>
                <li>
                  <strong>Log entries</strong> from access.log for these chunks
                </li>
                <li>
                  <strong>Database records</strong> for entire download sessions with corrupted chunks
                </li>
                <li>
                  <strong>Empty directories</strong> left after file removal
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
              leftSection={<AlertTriangle className="w-4 h-4" />}
            >
              Delete Cache, Logs & Database Entries
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default LogAndCorruptionManager;
