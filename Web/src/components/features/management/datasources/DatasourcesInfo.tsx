import React, { useState, useEffect } from 'react';
import { Logs, PlayCircle, RefreshCw } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { useSignalR } from '@contexts/SignalRContext';
import type { FastProcessingCompleteEvent } from '@contexts/SignalRContext/types';
import { useNotifications } from '@contexts/NotificationsContext';
import {
  ManagerCardHeader,
  LoadingState
} from '@components/ui/ManagerCard';
import type { Config, DatasourceInfo, DatasourceLogPosition } from '../../../../types';

interface DatasourcesManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

// Fetch datasource configuration
const fetchConfig = async (): Promise<Config> => {
  return await ApiService.getConfig();
};

// Fetch log positions
const fetchLogPositions = async (): Promise<DatasourceLogPosition[]> => {
  return await ApiService.getLogPositions();
};

const DatasourcesManager: React.FC<DatasourcesManagerProps> = ({
  isAuthenticated,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const [config, setConfig] = useState<Config | null>(null);
  const [logPositions, setLogPositions] = useState<DatasourceLogPosition[]>([]);
  const [loading, setLoading] = useState(true);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [resetModal, setResetModal] = useState<{ datasource: string | null; all: boolean } | null>(null);

  const { notifications } = useNotifications();
  const signalR = useSignalR();

  // Check if processing is running
  const isProcessing = notifications.some(n => n.type === 'log_processing' && n.status === 'running');

  // Load data
  useEffect(() => {
    const loadData = async () => {
      try {
        const [configData, positionsData] = await Promise.all([
          fetchConfig(),
          fetchLogPositions()
        ]);
        setConfig(configData);
        setLogPositions(positionsData);
      } catch (err) {
        console.error('Failed to load datasource data:', err);
      } finally {
        setLoading(false);
      }
    };

    if (!mockMode) {
      loadData();
    } else {
      setLoading(false);
    }
  }, [mockMode]);

  // Refresh positions periodically
  useEffect(() => {
    if (mockMode || !signalR.isConnected) return;

    const refreshPositions = async () => {
      try {
        const positions = await fetchLogPositions();
        setLogPositions(positions);
      } catch (err) {
        console.error('Failed to refresh log positions:', err);
      }
    };

    // Refresh every 30 seconds
    const interval = setInterval(refreshPositions, 30000);
    return () => clearInterval(interval);
  }, [mockMode, signalR.isConnected]);

  // Listen for processing complete events to refresh positions
  useEffect(() => {
    const handleProcessingComplete = async (_result: FastProcessingCompleteEvent) => {
      console.log('[DatasourcesManager] Processing complete, refreshing positions');
      try {
        const positions = await fetchLogPositions();
        setLogPositions(positions);
      } catch (err) {
        console.error('Failed to refresh log positions after processing:', err);
      }
    };

    signalR.on('FastProcessingComplete', handleProcessingComplete);

    return () => {
      signalR.off('FastProcessingComplete', handleProcessingComplete);
    };
  }, [signalR]);

  const toggleExpanded = (name: string) => {
    setExpandedDatasources(prev => {
      const next = new Set(prev);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  };

  const handleProcessAll = async () => {
    if (!isAuthenticated || isProcessing) return;

    setActionLoading('all');
    try {
      await ApiService.processAllLogs();
      // Note: Progress/completion notifications are handled via SignalR in NotificationsContext
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to start processing');
    } finally {
      setActionLoading(null);
    }
  };

  const handleProcessDatasource = async (datasourceName: string) => {
    if (!isAuthenticated || isProcessing) return;

    setActionLoading(`access-${datasourceName}`);
    try {
      await ApiService.processDatasourceLogs(datasourceName);
      // Note: Progress/completion notifications are handled via SignalR in NotificationsContext
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to start processing');
    } finally {
      setActionLoading(null);
    }
  };

  const handleResetPosition = async (datasourceName: string | null, position: 'top' | 'bottom') => {
    if (!isAuthenticated) return;

    const targetName = datasourceName || 'all';
    setActionLoading(`reset-${targetName}`);
    try {
      if (datasourceName) {
        await ApiService.resetDatasourceLogPosition(datasourceName, position);
        onSuccess?.(`Log position reset for ${datasourceName}`);
      } else {
        await ApiService.resetLogPosition(position);
        onSuccess?.('Log position reset for all datasources');
      }
      // Refresh positions
      const positions = await fetchLogPositions();
      setLogPositions(positions);
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to reset position');
    } finally {
      setActionLoading(null);
      setResetModal(null);
    }
  };

  const getPositionForDatasource = (name: string): DatasourceLogPosition | undefined => {
    return logPositions.find(p => p.datasource === name);
  };

  const formatPosition = (pos: DatasourceLogPosition | undefined): string => {
    if (!pos) return 'Unknown';
    if (pos.totalLines === 0) return 'No log file';

    // When position equals or exceeds totalLines, we're "caught up" to where the log was
    // when last checked. The log may have grown since, so avoid showing misleading 100%.
    if (pos.position >= pos.totalLines) {
      return `${pos.position.toLocaleString()} (caught up)`;
    }

    // Cap at 99% when not fully caught up to avoid misleading 100% display
    const rawPercent = (pos.position / pos.totalLines) * 100;
    const percent = Math.min(Math.round(rawPercent), 99);
    return `${pos.position.toLocaleString()} / ${pos.totalLines.toLocaleString()} (${percent}%)`;
  };

  // Get datasources - ensure at least one exists
  const datasources = config?.dataSources && config.dataSources.length > 0
    ? config.dataSources
    : [{
        name: 'default',
        cachePath: config?.cachePath || '/cache',
        logsPath: config?.logsPath || '/logs',
        cacheWritable: config?.cacheWritable ?? false,
        logsWritable: config?.logsWritable ?? false,
        enabled: true
      } as DatasourceInfo];

  const hasMultiple = datasources.length > 1;

  // Check if any datasource has read-only logs
  const logsReadOnly = datasources.some(ds => !ds.logsWritable);

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title="Log Processing">
        <div className="space-y-1.5">
          <HelpDefinition term="Process" termColor="green">
            Import log entries into the database from current position
          </HelpDefinition>
          <HelpDefinition term="Reset Position" termColor="blue">
            Choose to start from beginning or end of log file
          </HelpDefinition>
          {hasMultiple && (
            <HelpDefinition term="Datasource" termColor="purple">
              A named cache/logs directory pair for separate LANCache instances
            </HelpDefinition>
          )}
        </div>
      </HelpSection>

      <HelpNote type="info">
        Rust processor includes duplicate detection to avoid reimporting entries.
      </HelpNote>
    </HelpPopover>
  );

  // Header actions - main action buttons
  const headerActions = (
    <div className="flex items-center gap-2">
      <Button
        variant="subtle"
        size="sm"
        onClick={() => setResetModal({ datasource: null, all: true })}
        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated}
      >
        Reset
      </Button>
      <Button
        variant="filled"
        color="green"
        size="sm"
        onClick={handleProcessAll}
        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated}
        loading={actionLoading === 'all'}
      >
        Process All
      </Button>
    </div>
  );

  if (loading) {
    return (
      <Card>
        <ManagerCardHeader
          icon={Logs}
          iconColor="purple"
          title="Log Processing"
          subtitle="Loading..."
          helpContent={helpContent}
          permissions={{ logsReadOnly, checkingPermissions: true }}
        />
        <LoadingState message="Loading datasources..." />
      </Card>
    );
  }

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={Logs}
          iconColor="purple"
          title="Log Processing"
          subtitle={hasMultiple
            ? `${datasources.length} datasources configured`
            : 'Import historical data or monitor new downloads'}
          helpContent={helpContent}
          permissions={{ logsReadOnly, checkingPermissions: false }}
          actions={headerActions}
        />

        {/* Datasource list */}
        <div className="space-y-3">
          {datasources.map((ds) => {
            const position = getPositionForDatasource(ds.name);
            const isExpanded = expandedDatasources.has(ds.name);

            return (
              <DatasourceListItem
                key={ds.name}
                name={ds.name}
                path={ds.logsPath}
                isExpanded={isExpanded}
                onToggle={() => toggleExpanded(ds.name)}
                enabled={ds.enabled}
              >
                {/* Expanded content - Position info */}
                <div className="pt-3 space-y-4">
                  {/* Access Log Section */}
                  <div className="flex items-center justify-between p-3 rounded-lg bg-themed-tertiary">
                    <div className="flex items-center gap-3">
                      <Logs className="w-4 h-4 text-themed-muted" />
                      <div>
                        <div className="text-sm font-medium text-themed-primary">access.log</div>
                        <div className="text-xs text-themed-muted">{formatPosition(position)}</div>
                      </div>
                    </div>
                    <div className="flex gap-2">
                      <Button
                        variant="outline"
                        size="sm"
                        leftSection={<RefreshCw className="w-3 h-3" />}
                        onClick={(e) => {
                          e.stopPropagation();
                          setResetModal({ datasource: ds.name, all: false });
                        }}
                        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated || !ds.enabled}
                      >
                        Reset
                      </Button>
                      <Button
                        variant="filled"
                        color="green"
                        size="sm"
                        leftSection={<PlayCircle className="w-3 h-3" />}
                        onClick={(e) => {
                          e.stopPropagation();
                          handleProcessDatasource(ds.name);
                        }}
                        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated || !ds.enabled || position?.totalLines === 0}
                        loading={actionLoading === `access-${ds.name}`}
                      >
                        Process
                      </Button>
                    </div>
                  </div>
                </div>
              </DatasourceListItem>
            );
          })}
        </div>
      </Card>

      {/* Reset Position Modal */}
      <Modal
        opened={resetModal !== null}
        onClose={() => setResetModal(null)}
        title={
          resetModal?.all
            ? 'Reset All Log Positions'
            : `Reset Log: ${resetModal?.datasource}`
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Choose where to start processing logs from:
          </p>

          <div className="p-3 bg-themed-tertiary rounded-lg">
            <p className="text-xs text-themed-muted leading-relaxed">
              <strong>Start from Beginning:</strong> Process entire log history (duplicate detection prevents reimporting)
              <br />
              <strong>Start from End:</strong> Monitor only new downloads going forward
            </p>
          </div>

          <div className="flex flex-col gap-3">
            <Button
              variant="filled"
              color="blue"
              onClick={() => handleResetPosition(resetModal?.datasource || null, 'top')}
              loading={actionLoading?.startsWith('reset-')}
              fullWidth
            >
              Start from Beginning
            </Button>
            <Button
              variant="default"
              onClick={() => handleResetPosition(resetModal?.datasource || null, 'bottom')}
              loading={actionLoading?.startsWith('reset-')}
              fullWidth
            >
              Start from End
            </Button>
            <Button
              variant="outline"
              onClick={() => setResetModal(null)}
              disabled={actionLoading !== null}
              fullWidth
            >
              Cancel
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default DatasourcesManager;
