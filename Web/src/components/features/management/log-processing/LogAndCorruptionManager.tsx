import React, { useState, useEffect, useCallback } from 'react';
import {
  FileText,
  AlertTriangle,
  Loader2,
  RefreshCw,
  ChevronDown,
  ChevronUp,
  Lock,
  FolderOpen
} from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import type { CorruptionRemovalCompletePayload } from '@contexts/SignalRContext/types';
import { useNotifications } from '@contexts/NotificationsContext';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import DepotMappingManager from '../depot/DepotMappingManager';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import type { CorruptedChunkDetail, DatasourceServiceCounts } from '@/types';

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
      variant="outline"
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
  isAuthenticated,
  authMode,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh,
  onReloadRef,
  onClearOperationRef
}) => {
  // Get steam auth mode for depot mapping
  const { steamAuthMode } = useSteamAuth();

  // Get notifications to check for running operations
  const { notifications } = useNotifications();

  // Log File Management State - per datasource
  const [datasourceCounts, setDatasourceCounts] = useState<DatasourceServiceCounts[]>([]);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<{ datasource: string; service: string } | null>(null);
  const [showMoreServices, setShowMoreServices] = useState<Record<string, boolean>>({});

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
  const [dockerSocketAvailable, setDockerSocketAvailable] = useState(true);
  const [checkingPermissions, setCheckingPermissions] = useState(true);

  const signalR = useSignalR();

  // Derive active operations from notifications (standardized pattern)
  // Log entry removal: notification ID is 'log_removal-{service}', details.service contains the service name
  const activeLogRemovalNotification = notifications.find(
    n => n.type === 'log_removal' && n.id.startsWith('log_removal-') && n.status === 'running'
  );
  const activeLogRemoval = activeLogRemovalNotification?.details?.service as string | null ?? null;

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

  // Action loading state for depot mapping
  const [actionLoading, setActionLoading] = useState(false);

  // Derive log processing state from notifications
  const activeProcessingNotification = notifications.find(
    n => n.type === 'log_processing' && n.status === 'running'
  );
  const isProcessingLogs = !!activeProcessingNotification;

  // Clear operation state is now a no-op since state is derived from notifications
  const clearOperationState = async () => {
    // State is derived from notifications, nothing to clear locally
  };

  // Listen for CorruptionRemovalComplete event and refresh data
  useEffect(() => {
    if (!signalR) return;

    const handleCorruptionRemovalComplete = async (payload: CorruptionRemovalCompletePayload) => {
      console.log('[LogAndCorruptionManager] CorruptionRemovalComplete received, refreshing data');

      // State is derived from notifications - NotificationsContext handles the notification update
      // We just need to refresh the data if successful
      if (payload.success) {
        try {
          const [dsCounts, corruption] = await Promise.all([
            ApiService.getServiceLogCountsByDatasource(),
            ApiService.getCorruptionSummary(true)
          ]);
          setDatasourceCounts(dsCounts);
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

  // Note: Log removal progress is now reported via SignalR in ManagementTab
  // activeLogRemoval is only used for local UI state (button disabling, etc.)

  const loadAllData = async (forceRefresh = false) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const [dsCounts, corruption] = await Promise.all([
        ApiService.getServiceLogCountsByDatasource(),
        ApiService.getCorruptionSummary(forceRefresh)
      ]);
      setDatasourceCounts(dsCounts);
      setCorruptionSummary(corruption);
      // Auto-expand single datasource
      if (dsCounts.length === 1) {
        setExpandedDatasources(new Set([dsCounts[0].datasource]));
      }
      setLoadError(null);
      setHasInitiallyLoaded(true);
    } catch (err: unknown) {
      console.error('Failed to load log and corruption data:', err);
      setLoadError((err instanceof Error ? err.message : String(err)) || 'Failed to load service data');
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
      setLogsReadOnly(false); // Assume writable on error
      setCacheReadOnly(false);
      setDockerSocketAvailable(true); // Assume available on error
    } finally {
      setCheckingPermissions(false);
    }
  };

  const executeRemoveServiceLogs = async (datasourceName: string, serviceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setPendingServiceRemoval(null);
    setStartingServiceRemoval(`${datasourceName}:${serviceName}`);

    try {
      const result = await ApiService.removeServiceFromDatasourceLogs(datasourceName, serviceName);

      if (result && result.status === 'started') {
        // SignalR will send LogRemovalProgress which creates the notification
        // The UI will update automatically when the notification is added
      } else {
        onError?.(`Unexpected response when starting log removal for ${serviceName}`);
      }
    } catch (err: unknown) {
      const errMsg = err instanceof Error ? err.message : String(err);
      const errorMessage = errMsg?.includes('read-only')
        ? 'Logs directory is read-only. Remove :ro from docker-compose volume mount.'
        : errMsg || 'Action failed';
      onError?.(errorMessage);
    } finally {
      setStartingServiceRemoval(null);
    }
  };

  const handleRemoveServiceLogs = useCallback(
    (datasourceName: string, serviceName: string) => {
      if (authMode !== 'authenticated') {
        onError?.('Full authentication required for management operations');
        return;
      }
      setPendingServiceRemoval({ datasource: datasourceName, service: serviceName });
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
    } catch (err: unknown) {
      console.error('[CorruptionDetection] Removal failed:', err);
      onError?.((err instanceof Error ? err.message : String(err)) || `Failed to remove corrupted chunks for ${service}`);
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
      } catch (err: unknown) {
        onError?.((err instanceof Error ? err.message : String(err)) || `Failed to load corruption details for ${service}`);
        setExpandedCorruptionService(null);
      } finally {
        setLoadingDetails(null);
      }
    }
  };

  // Helper to get services for a datasource
  const getServicesForDatasource = useCallback((ds: DatasourceServiceCounts) => {
    const allServices = Object.keys(ds.serviceCounts).filter((s) => ds.serviceCounts[s] > 0);
    const main = allServices.filter((s) => MAIN_SERVICES.includes(s.toLowerCase())).sort();
    const other = allServices.filter((s) => !MAIN_SERVICES.includes(s.toLowerCase())).sort();
    const showMore = showMoreServices[ds.datasource] ?? false;
    const displayed = showMore ? [...main, ...other] : main;
    return { main, other, displayed };
  }, [showMoreServices]);

  const toggleDatasourceExpanded = (name: string) => {
    setExpandedDatasources(prev => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  };

  const hasAnyLogEntries = datasourceCounts.some(ds =>
    Object.values(ds.serviceCounts).some(count => count > 0)
  );

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
            disabled={isLoading || !!activeLogRemoval || !!removingCorruption}
            className="p-2 rounded-lg transition-colors disabled:opacity-50 flex items-center justify-center"
            style={{
              color: 'var(--theme-text-muted)',
              backgroundColor: 'transparent'
            }}
            onMouseEnter={(e) =>
              !isLoading &&
              !activeLogRemoval &&
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

        {/* Docker Socket Warning */}
        {!dockerSocketAvailable && !logsReadOnly && !cacheReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">Docker socket not available</p>
              <p className="text-sm mt-1">
                Log removal and corruption management features are disabled because the Docker socket
                is not mounted. These operations modify the access.log file and require signaling nginx
                to reopen logs afterward.
              </p>
              <p className="text-sm mt-2">
                Add the following to your docker-compose.yml volumes:
              </p>
              <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1">
                - /var/run/docker.sock:/var/run/docker.sock:ro
              </code>
            </div>
          </Alert>
        )}

        {/* Log File Management Section */}
        <div className="mb-8">
          <div className="flex items-center justify-between mb-3">
            <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Log Entries</h4>
            {(logsReadOnly || !dockerSocketAvailable) && (
              <span
                className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
                style={{
                  backgroundColor: 'var(--theme-warning-bg)',
                  color: 'var(--theme-warning)',
                  borderColor: 'var(--theme-warning)'
                }}
              >
                <Lock className="w-3 h-3" />
                {logsReadOnly ? 'Read-only' : 'Docker socket required'}
              </span>
            )}
          </div>
          {!logsReadOnly && dockerSocketAvailable && (
            <p className="text-themed-muted text-sm mb-4 break-words">
              Remove service entries from log files
            </p>
          )}

          {!logsReadOnly && dockerSocketAvailable && (
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
              ) : !loadError && hasAnyLogEntries ? (
                <div className="space-y-3">
                  {datasourceCounts.map((ds) => {
                    const { other, displayed } = getServicesForDatasource(ds);
                    const isExpanded = expandedDatasources.has(ds.datasource);
                    const totalEntries = Object.values(ds.serviceCounts).reduce((a, b) => a + b, 0);
                    const hasEntries = totalEntries > 0;

                    return (
                      <div
                        key={ds.datasource}
                        className="rounded-lg border"
                        style={{
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: ds.logsWritable ? 'var(--theme-border-primary)' : 'var(--theme-border-secondary)',
                          opacity: ds.enabled && ds.logsWritable ? 1 : 0.7
                        }}
                      >
                        {/* Datasource header */}
                        <div
                          className="p-3 cursor-pointer"
                          onClick={() => toggleDatasourceExpanded(ds.datasource)}
                        >
                          <div className="flex items-center justify-between">
                            <div className="flex items-center gap-2">
                              <FolderOpen className="w-4 h-4 text-themed-muted" />
                              <span className="font-semibold text-themed-primary">{ds.datasource}</span>
                              {!ds.logsWritable && (
                                <Tooltip content="Logs are read-only">
                                  <Lock className="w-3.5 h-3.5 text-themed-warning" />
                                </Tooltip>
                              )}
                            </div>
                            <div className="flex items-center gap-3">
                              <span className="text-xs text-themed-muted">
                                {totalEntries.toLocaleString()} entries
                              </span>
                              {isExpanded ? (
                                <ChevronUp className="w-4 h-4 text-themed-muted" />
                              ) : (
                                <ChevronDown className="w-4 h-4 text-themed-muted" />
                              )}
                            </div>
                          </div>
                          <div className="flex items-center gap-2 text-xs text-themed-muted mt-1">
                            <code className="bg-themed-tertiary px-1.5 py-0.5 rounded truncate">
                              {ds.logsPath}
                            </code>
                          </div>
                        </div>

                        {/* Expanded content with services */}
                        {isExpanded && (
                          <div className="px-3 pb-3 border-t" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                            {hasEntries ? (
                              <>
                                <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3 pt-3">
                                  {displayed.map((service) => {
                                    const key = `${ds.datasource}:${service}`;
                                    return (
                                      <ServiceButton
                                        key={key}
                                        service={service}
                                        count={ds.serviceCounts[service] || 0}
                                        isRemoving={activeLogRemoval === service || startingServiceRemoval === key}
                                        isDisabled={
                                          mockMode ||
                                          !!activeLogRemoval ||
                                          !!removingCorruption ||
                                          !!startingServiceRemoval ||
                                          !!startingCorruptionRemoval ||
                                          authMode !== 'authenticated' ||
                                          !ds.logsWritable ||
                                          !dockerSocketAvailable ||
                                          checkingPermissions
                                        }
                                        onClick={() => handleRemoveServiceLogs(ds.datasource, service)}
                                      />
                                    );
                                  })}
                                </div>

                                {other.length > 0 && (
                                  <div className="mt-3 text-center">
                                    <Button
                                      variant="default"
                                      size="sm"
                                      onClick={(e) => {
                                        e.stopPropagation();
                                        setShowMoreServices(prev => ({
                                          ...prev,
                                          [ds.datasource]: !prev[ds.datasource]
                                        }));
                                      }}
                                    >
                                      {showMoreServices[ds.datasource] ? (
                                        <>Show Less ({other.length} hidden)</>
                                      ) : (
                                        <>Show More ({other.length} more)</>
                                      )}
                                    </Button>
                                  </div>
                                )}
                              </>
                            ) : (
                              <div className="text-center py-4 text-themed-muted text-sm">
                                No services with log entries
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
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
            {(logsReadOnly || cacheReadOnly || !dockerSocketAvailable) && (
              <span
                className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
                style={{
                  backgroundColor: 'var(--theme-warning-bg)',
                  color: 'var(--theme-warning)',
                  borderColor: 'var(--theme-warning)'
                }}
              >
                <Lock className="w-3 h-3" />
                {logsReadOnly || cacheReadOnly ? 'Read-only' : 'Docker socket required'}
              </span>
            )}
          </div>
          {!(logsReadOnly || cacheReadOnly) && dockerSocketAvailable && (
            <p className="text-themed-muted text-sm mb-4">
              Detects chunks with 3+ repeated MISS requests. Removal deletes cache files, log entries, and database records.
            </p>
          )}

          {!(logsReadOnly || cacheReadOnly) && dockerSocketAvailable && (
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
                            disabled={!!removingCorruption || !!activeLogRemoval}
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
                                !!activeLogRemoval ||
                                !!startingCorruptionRemoval ||
                                !!startingServiceRemoval ||
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
                              title={
                                logsReadOnly || cacheReadOnly
                                  ? 'Directories are read-only'
                                  : !dockerSocketAvailable
                                    ? 'Docker socket required'
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
            Remove all <strong>{pendingServiceRemoval?.service}</strong> entries from{' '}
            <strong>{pendingServiceRemoval?.datasource}</strong> logs? This will
            reduce log size and improve performance.
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>May take several minutes for large log files</li>
                <li>Cached {pendingServiceRemoval?.service} game files will remain intact</li>
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
                pendingServiceRemoval && executeRemoveServiceLogs(pendingServiceRemoval.datasource, pendingServiceRemoval.service)
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

      {/* Depot Mapping Manager */}
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
    </>
  );
};

export default LogAndCorruptionManager;
