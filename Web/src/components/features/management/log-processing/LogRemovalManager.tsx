import React, { useState, useEffect, useCallback } from 'react';
import {
  FileText,
  AlertTriangle,
  Loader2,
  RefreshCw,
  Lock,
  CheckCircle,
  XCircle,
  ScrollText
} from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useNotifications } from '@contexts/NotificationsContext';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import type { DatasourceServiceCounts } from '@/types';

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

interface LogRemovalManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onReloadRef?: React.MutableRefObject<(() => Promise<void>) | null>;
}

const LogRemovalManager: React.FC<LogRemovalManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onReloadRef
}) => {
  const { notifications } = useNotifications();

  // State
  const [datasourceCounts, setDatasourceCounts] = useState<DatasourceServiceCounts[]>([]);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<{ datasource: string; service: string } | null>(null);
  const [showMoreServices, setShowMoreServices] = useState<Record<string, boolean>>({});
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);
  const [logsReadOnly, setLogsReadOnly] = useState(false);
  const [dockerSocketAvailable, setDockerSocketAvailable] = useState(true);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [startingServiceRemoval, setStartingServiceRemoval] = useState<string | null>(null);

  // Derive active log removal from notifications
  const activeLogRemovalNotification = notifications.find(
    n => n.type === 'log_removal' && n.id.startsWith('log_removal-') && n.status === 'running'
  );
  const activeLogRemoval = activeLogRemovalNotification?.details?.service as string | null ?? null;

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

  const loadData = async (_forceRefresh = false) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const dsCounts = await ApiService.getServiceLogCountsByDatasource();
      setDatasourceCounts(dsCounts);
      setLoadError(null);
      setHasInitiallyLoaded(true);
    } catch (err: unknown) {
      console.error('Failed to load log data:', err);
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
      setDockerSocketAvailable(data.dockerSocket?.available ?? true);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setLogsReadOnly(false);
      setDockerSocketAvailable(true);
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
        // SignalR will handle progress
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

  return (
    <>
      <Card>
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-orange">
              <FileText className="w-5 h-5 icon-orange" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">Log Removal</h3>
              <p className="text-xs text-themed-muted">Remove service entries from log files</p>
            </div>
            <HelpPopover position="left" width={320}>
              <HelpSection title="What This Does">
                Removes entries for a specific service from your access.log files.
                This reduces log file size and can improve processing performance.
              </HelpSection>

              <HelpSection title="What It Affects" variant="subtle">
                <ul className="list-disc list-inside text-sm space-y-1">
                  <li>Log files (entries removed)</li>
                  <li>Database records (cleaned up)</li>
                </ul>
              </HelpSection>

              <HelpNote type="info">
                Cache files remain intact - only log entries are removed.
              </HelpNote>
            </HelpPopover>
          </div>
          <div className="flex items-center gap-3">
            {/* Permission status */}
            {!checkingPermissions && (
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
            )}
            <button
              onClick={() => loadData(true)}
              disabled={isLoading || !!activeLogRemoval}
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
        {logsReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">Logs directory is read-only</p>
              <p className="text-sm mt-1">
                Remove <code className="bg-themed-tertiary px-1 rounded">:ro</code> from your
                docker-compose volume mounts to enable log removal.
              </p>
            </div>
          </Alert>
        )}

        {/* Docker Socket Warning */}
        {!dockerSocketAvailable && !logsReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">Docker socket not available</p>
              <p className="text-sm mt-1">
                Log removal requires signaling nginx to reopen logs afterward.
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
        {logsReadOnly || !dockerSocketAvailable ? (
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
              {logsReadOnly ? 'Read-only' : 'Docker socket required'}
            </span>
          </div>
        ) : (
          <>
            {loadError && (
              <Alert color="red" className="mb-4">
                <div>
                  <p className="text-sm font-medium mb-1">Failed to load service log counts</p>
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
                    <DatasourceListItem
                      key={ds.datasource}
                      name={ds.datasource}
                      path={ds.logsPath}
                      isExpanded={isExpanded}
                      onToggle={() => toggleDatasourceExpanded(ds.datasource)}
                      enabled={ds.enabled && ds.logsWritable}
                      statusBadge={`${totalEntries.toLocaleString()} entries`}
                    >
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
                                    !!startingServiceRemoval ||
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
                    </DatasourceListItem>
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
    </>
  );
};

export default LogRemovalManager;
