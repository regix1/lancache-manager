import React, { useState, useEffect, useCallback, useMemo } from 'react';
import {
  FileText,
  AlertTriangle,
  Loader,
  RefreshCw,
  ChevronDown,
  ChevronUp
} from 'lucide-react';
import ApiService from '@services/api.service';
import { AuthMode } from '@services/auth.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import type { CorruptedChunkDetail } from '@/types';

// Main services that should always be shown first
const MAIN_SERVICES = [
  'steam', 'epic', 'riot', 'blizzard', 'origin', 'uplay',
  'gog', 'wsus', 'microsoft', 'sony', 'nintendo', 'apple'
];

// Memoized Service Button Component
const ServiceButton = React.memo<{
  service: string;
  count: number;
  isRemoving: boolean;
  isDisabled: boolean;
  onClick: () => void;
}>(({ service, count, isRemoving, isDisabled, onClick }) => {
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
          <span className="text-xs text-themed-muted mt-1">
            ({count.toLocaleString()} entries)
          </span>
        </>
      ) : (
        <span className="capitalize font-medium text-sm sm:text-base">Removing...</span>
      )}
    </Button>
  );
});

ServiceButton.displayName = 'ServiceButton';

interface LogAndCorruptionManagerProps {
  isAuthenticated: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onBackgroundOperation?: (service: string | null) => void;
  onReloadRef?: React.MutableRefObject<(() => Promise<void>) | null>;
  onClearOperationRef?: React.MutableRefObject<(() => Promise<void>) | null>;
}

const LogAndCorruptionManager: React.FC<LogAndCorruptionManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh,
  onBackgroundOperation,
  onReloadRef,
  onClearOperationRef
}) => {
  // Log File Management State
  const [serviceCounts, setServiceCounts] = useState<Record<string, number>>({});
  const [config, setConfig] = useState({
    logPath: 'Loading...',
    services: [] as string[]
  });
  const [activeServiceRemoval, setActiveServiceRemoval] = useState<string | null>(null);
  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<string | null>(null);
  const [showMoreServices, setShowMoreServices] = useState(false);

  // Corruption Detection State
  const [corruptionSummary, setCorruptionSummary] = useState<Record<string, number>>({});
  const [removingCorruption, setRemovingCorruption] = useState<string | null>(null);
  const [pendingCorruptionRemoval, setPendingCorruptionRemoval] = useState<string | null>(null);
  const [expandedCorruptionService, setExpandedCorruptionService] = useState<string | null>(null);
  const [corruptionDetails, setCorruptionDetails] = useState<Record<string, CorruptedChunkDetail[]>>({});
  const [loadingDetails, setLoadingDetails] = useState<string | null>(null);

  // Shared State
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);

  const serviceRemovalOp = useBackendOperation('activeServiceRemoval', 'serviceRemoval', 30);

  const clearOperationState = async () => {
    await serviceRemovalOp.clear();
    setActiveServiceRemoval(null);
    onBackgroundOperation?.(null);
  };

  useEffect(() => {
    // Only load on initial mount
    if (!hasInitiallyLoaded) {
      loadAllData();
      restoreServiceRemoval();
    }

    // Expose reload function to parent via ref
    if (onReloadRef) {
      onReloadRef.current = loadAllData;
    }

    // Expose clear operation function to parent via ref
    if (onClearOperationRef) {
      onClearOperationRef.current = clearOperationState;
    }
  }, [hasInitiallyLoaded, onReloadRef, onClearOperationRef]);

  useEffect(() => {
    onBackgroundOperation?.(activeServiceRemoval);
  }, [activeServiceRemoval]);

  const loadAllData = async (forceRefresh: boolean = false) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const [configData, counts, corruption] = await Promise.all([
        ApiService.getConfig(),
        ApiService.getServiceLogCounts(forceRefresh),
        ApiService.getCorruptionSummary(forceRefresh)
      ]);
      setConfig(configData);
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

  const restoreServiceRemoval = async () => {
    const serviceOp = await serviceRemovalOp.load();
    if (serviceOp?.data?.service) {
      setActiveServiceRemoval(serviceOp.data.service);
      onSuccess?.(`Removing ${serviceOp.data.service} entries from logs (operation resumed)...`);
    }
  };

  const executeRemoveServiceLogs = async (serviceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setPendingServiceRemoval(null);

    try {
      setActiveServiceRemoval(serviceName);
      await serviceRemovalOp.save({ service: serviceName });

      const result = await ApiService.removeServiceFromLogs(serviceName);

      if (result && result.status === 'started') {
        onSuccess?.(`Removing ${serviceName} entries from logs...`);
      } else {
        setActiveServiceRemoval(null);
        await serviceRemovalOp.clear();
        onError?.(`Unexpected response when starting log removal for ${serviceName}`);
      }
    } catch (err: any) {
      await serviceRemovalOp.clear();
      setActiveServiceRemoval(null);

      const errorMessage = err.message?.includes('read-only')
        ? 'Logs directory is read-only. Remove :ro from docker-compose volume mount.'
        : err.message || 'Action failed';
      onError?.(errorMessage);
    }
  };

  const handleRemoveServiceLogs = useCallback((serviceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }
    setPendingServiceRemoval(serviceName);
  }, [authMode, onError]);

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
    setRemovingCorruption(service);

    try {
      const result = await ApiService.removeCorruptedChunks(service);
      onSuccess?.(result.message || `Corrupted chunks removed for ${service}`);

      await loadAllData();
      onDataRefresh?.();
    } catch (err: any) {
      console.error('[CorruptionDetection] Removal failed:', err);
      onError?.(err.message || `Failed to remove corrupted chunks for ${service}`);
    } finally {
      setRemovingCorruption(null);
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
        setCorruptionDetails(prev => ({ ...prev, [service]: details }));
      } catch (err: any) {
        onError?.(err.message || `Failed to load corruption details for ${service}`);
        setExpandedCorruptionService(null);
      } finally {
        setLoadingDetails(null);
      }
    }
  };

  const { mainServices, otherServices, servicesWithData } = useMemo(() => {
    const allServices = Object.keys(serviceCounts).filter(service => serviceCounts[service] > 0);

    const main = allServices
      .filter(service => MAIN_SERVICES.includes(service.toLowerCase()))
      .sort();

    const other = allServices
      .filter(service => !MAIN_SERVICES.includes(service.toLowerCase()))
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
          <div className="flex items-center gap-2">
            <FileText className="w-5 h-5 icon-orange flex-shrink-0" />
            <h3 className="text-lg font-semibold text-themed-primary">Log & Cache Management</h3>
          </div>
          <Button
            variant="default"
            size="sm"
            onClick={() => loadAllData(true)}
            disabled={isLoading || !!activeServiceRemoval || !!removingCorruption}
            leftSection={<RefreshCw className={`w-4 h-4 ${isLoading ? 'animate-spin' : ''}`} />}
          >
            Refresh All
          </Button>
        </div>

        {/* Log File Management Section */}
        <div className="mb-8">
          <p className="text-themed-muted text-sm mb-4 break-words">
            Remove service log entries from{' '}
            <code className="bg-themed-tertiary px-2 py-1 rounded text-xs break-all">{config.logPath}</code>
          </p>

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
              <Loader className="w-6 h-6 animate-spin text-themed-accent" />
              <p className="text-sm text-themed-secondary">Scanning log file for services...</p>
              <p className="text-xs text-themed-muted">This may take up to 5 minutes for large log files</p>
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
                      isRemoving={activeServiceRemoval === service}
                      isDisabled={
                        mockMode || !!activeServiceRemoval || !!removingCorruption || serviceRemovalOp.loading || authMode !== 'authenticated'
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
        </div>

        {/* Divider */}
        <div className="border-t mb-8" style={{ borderColor: 'var(--theme-border-primary)' }}></div>

        {/* Corruption Detection Section */}
        <div className="mb-6">
          <div className="flex items-start gap-2 mb-4">
            <AlertTriangle className="w-5 h-5 text-themed-warning flex-shrink-0 mt-0.5" />
            <div>
              <p className="text-themed-primary text-sm font-medium mb-1">
                Corrupted Cache Detection & Removal
              </p>
              <p className="text-themed-muted text-sm">
                Detects corrupted cache chunks by analyzing repeated MISS/UNKNOWN requests (3+ occurrences) in access logs.
                Removal will <strong>delete cache files</strong> from disk AND <strong>remove log entries</strong> for these chunks.
              </p>
            </div>
          </div>

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
              <Loader className="w-6 h-6 animate-spin text-themed-accent" />
              <p className="text-sm text-themed-secondary">Scanning logs for corrupted chunks...</p>
              <p className="text-xs text-themed-muted">This may take several minutes for large log files</p>
            </div>
          ) : !loadError && corruptionList.length > 0 ? (
            <>
              <div className="space-y-3">
                {corruptionList.map(([service, count]) => (
                  <div key={`corruption-${service}`} className="rounded-lg border" style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    borderColor: 'var(--theme-border-secondary)'
                  }}>
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
                          <span className="capitalize font-medium text-themed-primary">{service}</span>
                          <span className="text-xs text-themed-muted">
                            ({count.toLocaleString()} corrupted chunk{count !== 1 ? 's' : ''})
                          </span>
                        </div>
                      </div>
                      <Tooltip content="Delete cache files and remove log entries for corrupted chunks">
                        <Button
                          onClick={() => handleRemoveCorruption(service)}
                          disabled={mockMode || !!removingCorruption || !!activeServiceRemoval || authMode !== 'authenticated'}
                          variant="filled"
                          color="red"
                          size="sm"
                          loading={removingCorruption === service}
                        >
                          {removingCorruption !== service ? 'Remove All' : 'Removing...'}
                        </Button>
                      </Tooltip>
                    </div>

                    {/* Expandable Details Section */}
                    {expandedCorruptionService === service && (
                      <div className="border-t px-3 py-3" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                        {loadingDetails === service ? (
                          <div className="flex items-center justify-center py-4 gap-2">
                            <Loader className="w-4 h-4 animate-spin text-themed-accent" />
                            <span className="text-sm text-themed-secondary">Loading corruption details...</span>
                          </div>
                        ) : corruptionDetails[service] && corruptionDetails[service].length > 0 ? (
                          <div className="space-y-2 max-h-96 overflow-y-auto">
                            {corruptionDetails[service].map((chunk, idx) => (
                              <div key={idx} className="p-2 rounded border" style={{
                                backgroundColor: 'var(--theme-bg-secondary)',
                                borderColor: 'var(--theme-border-primary)'
                              }}>
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
                                      <span>Miss count: <strong className="text-themed-error">{chunk.miss_count || 0}</strong></span>
                                      {chunk.cache_file_path && (
                                        <Tooltip content={chunk.cache_file_path}>
                                          <span className="truncate">Cache: <code className="text-xs">{chunk.cache_file_path.split('/').pop() || chunk.cache_file_path.split('\\').pop()}</code></span>
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
        </div>

        {/* Combined Warning for Both Sections */}
        <Alert color="yellow">
          <div>
            <p className="text-xs font-medium mb-2">Important:</p>
            <ul className="list-disc list-inside text-xs space-y-1 ml-2">
              <li><strong>Log removal:</strong> Removes entries from access.log (cache files remain intact)</li>
              <li><strong>Corruption removal:</strong> Deletes BOTH cache files AND log entries for corrupted chunks</li>
              <li>Both require write permissions to logs/cache directories</li>
              <li>These actions cannot be undone</li>
            </ul>
          </div>
        </Alert>
      </Card>

      {/* Log Removal Confirmation Modal */}
      <Modal
        opened={pendingServiceRemoval !== null}
        onClose={() => {
          if (!serviceRemovalOp.loading) {
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
            Remove all <strong>{pendingServiceRemoval}</strong> entries from the log file? This will reduce log size and improve performance.
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
              disabled={serviceRemovalOp.loading}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => pendingServiceRemoval && executeRemoveServiceLogs(pendingServiceRemoval)}
              loading={serviceRemovalOp.loading}
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
                <li><strong>Cache files</strong> from disk for corrupted chunks (3+ MISS/UNKNOWN)</li>
                <li><strong>Log entries</strong> from access.log for these chunks</li>
                <li><strong>Empty directories</strong> left after file removal</li>
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
                <li>Removes approximately {corruptionSummary[pendingCorruptionRemoval || ''] || 0} corrupted chunks</li>
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
              Delete Cache Files & Log Entries
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default LogAndCorruptionManager;
