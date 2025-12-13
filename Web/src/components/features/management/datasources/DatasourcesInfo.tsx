import React, { useState, useEffect } from 'react';
import { Database, FolderOpen, FileText, CheckCircle, XCircle, PlayCircle, RefreshCw, ChevronDown, ChevronUp } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { useSignalR } from '@contexts/SignalRContext';
import { useNotifications } from '@contexts/NotificationsContext';
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
      onSuccess?.('Started processing all datasources');
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to start processing');
    } finally {
      setActionLoading(null);
    }
  };

  const handleProcessDatasource = async (datasourceName: string) => {
    if (!isAuthenticated || isProcessing) return;

    setActionLoading(datasourceName);
    try {
      await ApiService.processDatasourceLogs(datasourceName);
      onSuccess?.(`Started processing ${datasourceName}`);
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
    const percent = pos.totalLines > 0 ? Math.round((pos.position / pos.totalLines) * 100) : 0;
    return `${pos.position.toLocaleString()} / ${pos.totalLines.toLocaleString()} (${percent}%)`;
  };

  if (loading) {
    return (
      <Card>
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
            <Database className="w-5 h-5 icon-purple" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Log Processing</h3>
        </div>
        <p className="text-themed-muted">Loading...</p>
      </Card>
    );
  }

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

  return (
    <>
      <Card>
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
            <Database className="w-5 h-5 icon-purple" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Log Processing</h3>
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
        </div>

        <p className="text-themed-muted text-sm mb-4">
          {hasMultiple
            ? `${datasources.length} datasources configured. Import historical data or reset to monitor only new downloads.`
            : 'Import historical data or reset to monitor only new downloads.'}
        </p>

        {/* Process All button */}
        <div className="mb-4">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Button
              variant="default"
              leftSection={<RefreshCw className="w-4 h-4" />}
              onClick={() => setResetModal({ datasource: null, all: true })}
              disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated}
              fullWidth
            >
              Reset All Positions
            </Button>
            <Button
              variant="filled"
              color="green"
              leftSection={<PlayCircle className="w-4 h-4" />}
              onClick={handleProcessAll}
              disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated}
              loading={actionLoading === 'all'}
              fullWidth
            >
              Process All Logs
            </Button>
          </div>
        </div>

        {/* Datasource list */}
        <div className="space-y-3">
          {datasources.map((ds) => {
            const position = getPositionForDatasource(ds.name);
            const isExpanded = expandedDatasources.has(ds.name);

            return (
              <div
                key={ds.name}
                className="rounded-lg border"
                style={{
                  backgroundColor: 'var(--theme-bg-secondary)',
                  borderColor: ds.enabled ? 'var(--theme-border-primary)' : 'var(--theme-border-secondary)',
                  opacity: ds.enabled ? 1 : 0.6
                }}
              >
                {/* Header - clickable to expand */}
                <div
                  className="p-3 cursor-pointer"
                  onClick={() => toggleExpanded(ds.name)}
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <span className="font-semibold text-themed-primary">{ds.name}</span>
                      {!ds.enabled && (
                        <span
                          className="px-2 py-0.5 text-xs rounded font-medium"
                          style={{
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            color: 'var(--theme-text-muted)'
                          }}
                        >
                          Disabled
                        </span>
                      )}
                    </div>
                    <div className="flex items-center gap-3">
                      {/* Log position summary */}
                      <span className="text-xs text-themed-muted hidden sm:inline">
                        Position: {formatPosition(position)}
                      </span>
                      {/* Writable status icons */}
                      <div className="flex items-center gap-2">
                        <span
                          className="flex items-center gap-1 text-xs"
                          title={ds.cacheWritable ? 'Cache is writable' : 'Cache is read-only'}
                        >
                          {ds.cacheWritable ? (
                            <CheckCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-success-text)' }} />
                          ) : (
                            <XCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-warning)' }} />
                          )}
                        </span>
                        <span
                          className="flex items-center gap-1 text-xs"
                          title={ds.logsWritable ? 'Logs are writable' : 'Logs are read-only'}
                        >
                          {ds.logsWritable ? (
                            <CheckCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-success-text)' }} />
                          ) : (
                            <XCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-warning)' }} />
                          )}
                        </span>
                      </div>
                      {/* Expand/collapse icon */}
                      {isExpanded ? (
                        <ChevronUp className="w-4 h-4 text-themed-muted" />
                      ) : (
                        <ChevronDown className="w-4 h-4 text-themed-muted" />
                      )}
                    </div>
                  </div>
                </div>

                {/* Expanded details */}
                {isExpanded && (
                  <div className="px-3 pb-3 border-t" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                    {/* Paths */}
                    <div className="py-2 space-y-1">
                      <div className="flex items-center gap-2 text-xs">
                        <FolderOpen className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                        <span className="text-themed-muted">Cache:</span>
                        <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
                          {ds.cachePath}
                        </code>
                      </div>
                      <div className="flex items-center gap-2 text-xs">
                        <FileText className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                        <span className="text-themed-muted">Logs:</span>
                        <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
                          {ds.logsPath}
                        </code>
                      </div>
                      {/* Position info on mobile */}
                      <div className="sm:hidden text-xs text-themed-muted pt-1">
                        Position: {formatPosition(position)}
                      </div>
                    </div>

                    {/* Actions */}
                    <div className="grid grid-cols-2 gap-2 pt-2">
                      <Button
                        variant="outline"
                        size="sm"
                        leftSection={<RefreshCw className="w-3.5 h-3.5" />}
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
                        leftSection={<PlayCircle className="w-3.5 h-3.5" />}
                        onClick={(e) => {
                          e.stopPropagation();
                          handleProcessDatasource(ds.name);
                        }}
                        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated || !ds.enabled}
                        loading={actionLoading === ds.name}
                      >
                        Process
                      </Button>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </Card>

      {/* Reset Position Modal */}
      <Modal
        opened={resetModal !== null}
        onClose={() => setResetModal(null)}
        title={resetModal?.all ? 'Reset All Log Positions' : `Reset Position: ${resetModal?.datasource}`}
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

// Export function to invalidate cache when needed (kept for compatibility)
export const invalidateDatasourcesCache = () => {
  // No longer using promise cache, but keep export for any existing imports
};
