import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Logs, PlayCircle } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { useSignalR } from '@contexts/SignalRContext';
import type { FastProcessingCompleteEvent } from '@contexts/SignalRContext/types';
import { useNotifications } from '@contexts/notifications';
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
  const { t } = useTranslation();
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
      onError?.((err instanceof Error ? err.message : String(err)) || t('management.datasources.errors.processingFailed'));
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
      onError?.((err instanceof Error ? err.message : String(err)) || t('management.datasources.errors.processingFailed'));
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
        onSuccess?.(t('management.datasources.messages.positionReset', { datasource: datasourceName }));
      } else {
        await ApiService.resetLogPosition(position);
        onSuccess?.(t('management.datasources.messages.positionResetAll'));
      }
      // Refresh positions
      const positions = await fetchLogPositions();
      setLogPositions(positions);
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || t('management.datasources.errors.resetFailed'));
    } finally {
      setActionLoading(null);
      setResetModal(null);
    }
  };

  const getPositionForDatasource = (name: string): DatasourceLogPosition | undefined => {
    return logPositions.find(p => p.datasource === name);
  };

  const formatPosition = (pos: DatasourceLogPosition | undefined): string => {
    if (!pos) return t('management.datasources.position.unknown');
    if (pos.totalLines === 0) return t('management.datasources.position.noLogFile');

    // When position equals or exceeds totalLines, we're "caught up" to where the log was
    // when last checked. The log may have grown since, so avoid showing misleading 100%.
    if (pos.position >= pos.totalLines) {
      return t('management.datasources.position.caughtUp', { position: pos.position.toLocaleString() });
    }

    // Cap at 99% when not fully caught up to avoid misleading 100% display
    const rawPercent = (pos.position / pos.totalLines) * 100;
    const percent = Math.min(Math.round(rawPercent), 99);
    return t('management.datasources.position.progress', {
      position: pos.position.toLocaleString(),
      total: pos.totalLines.toLocaleString(),
      percent
    });
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
      <HelpSection title={t('management.datasources.help.title')}>
        <div className="space-y-1.5">
          <HelpDefinition term={t('management.datasources.help.process.term')} termColor="green">
            {t('management.datasources.help.process.description')}
          </HelpDefinition>
          <HelpDefinition term={t('management.datasources.help.reposition.term')} termColor="blue">
            {t('management.datasources.help.reposition.description')}
          </HelpDefinition>
          {hasMultiple && (
            <HelpDefinition term={t('management.datasources.help.datasource.term')} termColor="purple">
              {t('management.datasources.help.datasource.description')}
            </HelpDefinition>
          )}
        </div>
      </HelpSection>

      <HelpNote type="info">
        {t('management.datasources.help.note')}
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
        {t('management.datasources.reposition')}
      </Button>
      <Button
        variant="filled"
        color="green"
        size="sm"
        onClick={handleProcessAll}
        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated}
        loading={actionLoading === 'all'}
      >
        {t('common.processAll')}
      </Button>
    </div>
  );

  if (loading) {
    return (
      <Card>
        <ManagerCardHeader
          icon={Logs}
          iconColor="purple"
          title={t('management.datasources.title')}
          subtitle={t('management.datasources.subtitleLoading')}
          helpContent={helpContent}
          permissions={{ logsReadOnly, checkingPermissions: true }}
        />
        <LoadingState message={t('management.datasources.loadingDatasources')} />
      </Card>
    );
  }

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={Logs}
          iconColor="purple"
          title={t('management.datasources.title')}
          subtitle={hasMultiple
            ? t('management.datasources.subtitleMultiple', { count: datasources.length })
            : t('management.datasources.subtitleSingle')}
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
                  <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 p-3 rounded-lg bg-themed-tertiary">
                    <div className="flex items-center gap-3">
                      <Logs className="w-4 h-4 text-themed-muted flex-shrink-0" />
                      <div className="min-w-0">
                        <div className="text-sm font-medium text-themed-primary">access.log</div>
                        <div className="text-xs text-themed-muted truncate">{formatPosition(position)}</div>
                      </div>
                    </div>
                    <div className="flex gap-2 flex-shrink-0">
                      <Button
                        variant="outline"
                        size="sm"
                        onClick={(e) => {
                          e.stopPropagation();
                          setResetModal({ datasource: ds.name, all: false });
                        }}
                        disabled={actionLoading !== null || isProcessing || mockMode || !isAuthenticated || !ds.enabled}
                        className="flex-1 sm:flex-initial"
                      >
                        {t('management.datasources.reposition')}
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
                        className="flex-1 sm:flex-initial"
                      >
                        {t('common.process')}
                      </Button>
                    </div>
                  </div>
                </div>
              </DatasourceListItem>
            );
          })}
        </div>
      </Card>

      {/* Reposition Log Modal */}
      <Modal
        opened={resetModal !== null}
        onClose={() => setResetModal(null)}
        title={
          resetModal?.all
            ? t('management.datasources.modal.repositionAll')
            : t('management.datasources.modal.repositionSingle', { datasource: resetModal?.datasource })
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.datasources.modal.choosePosition')}
          </p>

          <div className="p-3 bg-themed-tertiary rounded-lg">
            <p className="text-xs text-themed-muted leading-relaxed">
              <strong>{t('management.datasources.modal.startFromBeginning')}:</strong> {t('management.datasources.modal.beginningDescription')}
              <br />
              <strong>{t('management.datasources.modal.startFromEnd')}:</strong> {t('management.datasources.modal.endDescription')}
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
              {t('management.datasources.modal.startFromBeginning')}
            </Button>
            <Button
              variant="default"
              onClick={() => handleResetPosition(resetModal?.datasource || null, 'bottom')}
              loading={actionLoading?.startsWith('reset-')}
              fullWidth
            >
              {t('management.datasources.modal.startFromEnd')}
            </Button>
            <Button
              variant="outline"
              onClick={() => setResetModal(null)}
              disabled={actionLoading !== null}
              fullWidth
            >
              {t('common.cancel')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default DatasourcesManager;
