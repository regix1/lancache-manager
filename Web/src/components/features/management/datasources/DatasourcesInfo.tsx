import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Logs, PlayCircle, RotateCcw } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuItem } from '@components/ui/ActionMenu';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { LogProcessingCompleteEvent } from '@contexts/SignalRContext/types';
import { useConfig } from '@contexts/useConfig';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { getErrorMessage } from '@utils/error';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { LoadingState } from '@components/ui/ManagerCard';
import { AccordionSection } from '@components/ui/AccordionSection';
import { formatCount } from '@utils/formatters';
import type { DatasourceInfo, DatasourceLogPosition } from '../../../../types';

interface DatasourcesManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

// Fetch log positions
const fetchLogPositions = async (): Promise<DatasourceLogPosition[]> => {
  return await ApiService.getLogPositions();
};

const DatasourcesManager: React.FC<DatasourcesManagerProps> = ({
  isAdmin,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const { t } = useTranslation();
  const { config } = useConfig();
  const { checkingPermissions } = useDirectoryPermissionsContext();
  const [logPositions, setLogPositions] = useState<DatasourceLogPosition[]>([]);
  const [loading, setLoading] = useState(true);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [resetModal, setResetModal] = useState<{ datasource: string | null; all: boolean } | null>(
    null
  );
  const [isExpanded, setIsExpanded] = useState<boolean>(() => {
    const saved = localStorage.getItem('management-datasources-expanded-v2');
    return saved !== null ? saved === 'true' : false;
  });

  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const signalR = useSignalR();

  // Check if processing is running or queued behind another operation
  const isProcessing = useOperationBusy({
    types: ['log_processing'],
    status: ['running', 'waiting']
  });

  // Load log positions
  useEffect(() => {
    const loadData = async () => {
      try {
        const positionsData = await fetchLogPositions();
        setLogPositions(positionsData);
      } catch (err) {
        notifyError(
          t('management.datasources.errors.loadFailed', 'Failed to load datasource data'),
          err,
          { logLabel: 'Failed to load datasource data' }
        );
      } finally {
        setLoading(false);
      }
    };

    if (!mockMode) {
      loadData();
    } else {
      setLoading(false);
    }
  }, [mockMode, notifyError, t]);

  // Listen for processing complete events to refresh positions
  useEffect(() => {
    const handleProcessingComplete = async (_result: LogProcessingCompleteEvent) => {
      try {
        const positions = await fetchLogPositions();
        setLogPositions(positions);
      } catch (err) {
        // Background auto-refresh after a completed processing run; the position display simply
        // stays stale until the next successful refresh, so this is explicit background noise.
        notifyError(
          t('management.datasources.errors.loadFailed', 'Failed to load datasource data'),
          err,
          { silent: true, logLabel: 'Failed to refresh log positions after processing' }
        );
      }
    };

    signalR.on('LogProcessingComplete', handleProcessingComplete);

    return () => {
      signalR.off('LogProcessingComplete', handleProcessingComplete);
    };
  }, [signalR, notifyError, t]);

  useEffect(() => {
    localStorage.setItem('management-datasources-expanded-v2', String(isExpanded));
  }, [isExpanded]);

  const toggleExpanded = (name: string) => {
    setExpandedDatasources((prev) => {
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
    if (!isAdmin || isProcessing) return;

    setActionLoading('all');
    try {
      const result = await ApiService.processAllLogs();
      // Wait-queue model: queued/deduplicated responses must not seed a running card.
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'log_processing',
            result.operationId,
            t('signalr.logProcessing.starting')
          )
        );
      }
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.(getErrorMessage(err) || t('management.datasources.errors.processingFailed'));
    } finally {
      setActionLoading(null);
    }
  };

  const handleProcessDatasource = async (datasourceName: string) => {
    if (!isAdmin || isProcessing) return;

    setActionLoading(`access-${datasourceName}`);
    try {
      const result = await ApiService.processDatasourceLogs(datasourceName);
      // Wait-queue model: queued/deduplicated responses must not seed a running card.
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'log_processing',
            result.operationId,
            t('signalr.logProcessing.starting')
          )
        );
      }
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.(getErrorMessage(err) || t('management.datasources.errors.processingFailed'));
    } finally {
      setActionLoading(null);
    }
  };

  const handleResetPosition = async (datasourceName: string | null, position: 'top' | 'bottom') => {
    if (!isAdmin) return;

    const targetName = datasourceName || 'all';
    setActionLoading(`reset-${targetName}`);
    try {
      if (datasourceName) {
        await ApiService.resetDatasourceLogPosition(datasourceName, position);
        onSuccess?.(
          t('management.datasources.messages.positionReset', { datasource: datasourceName })
        );
      } else {
        await ApiService.resetLogPosition(position);
        onSuccess?.(t('management.datasources.messages.positionResetAll'));
      }
      // Refresh positions
      const positions = await fetchLogPositions();
      setLogPositions(positions);
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.(getErrorMessage(err) || t('management.datasources.errors.resetFailed'));
    } finally {
      setActionLoading(null);
      setResetModal(null);
    }
  };

  const getPositionForDatasource = (name: string): DatasourceLogPosition | undefined => {
    return logPositions.find((p) => p.datasource === name);
  };

  const formatPosition = (pos: DatasourceLogPosition | undefined): string => {
    if (!pos) return t('management.datasources.position.unknown');
    if (pos.totalLines === 0) return t('management.datasources.position.noLogFile');

    // When position equals or exceeds totalLines, we're "caught up" to where the log was
    // when last checked. The log may have grown since, so avoid showing misleading 100%.
    if (pos.position >= pos.totalLines) {
      return t('management.datasources.position.caughtUp', {
        position: formatCount(pos.position)
      });
    }

    // Cap at 99% when not fully caught up to avoid misleading 100% display
    const rawPercent = (pos.position / pos.totalLines) * 100;
    const percent = Math.min(Math.round(rawPercent), 99);
    return t('management.datasources.position.progress', {
      position: formatCount(pos.position),
      total: formatCount(pos.totalLines),
      percent
    });
  };

  // Get datasources - ensure at least one exists
  const datasources =
    config.dataSources && config.dataSources.length > 0
      ? config.dataSources
      : [
          {
            name: 'default',
            cachePath: config.cachePath,
            logsPath: config.logsPath,
            cacheWritable: config.cacheWritable,
            logsWritable: config.logsWritable,
            enabled: true
          } as DatasourceInfo
        ];

  const hasMultiple = datasources.length > 1;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.datasources.help.title')} variant="subtle">
        <HelpDefinition
          items={[
            {
              term: t('management.datasources.help.process.term'),
              description: t('management.datasources.help.process.description')
            },
            {
              term: t('management.datasources.help.reposition.term'),
              description: t('management.datasources.help.reposition.description')
            },
            ...(hasMultiple
              ? [
                  {
                    term: t('management.datasources.help.datasource.term'),
                    description: t('management.datasources.help.datasource.description')
                  }
                ]
              : [])
          ]}
        />
      </HelpSection>

      <HelpNote type="info">{t('management.datasources.help.note')}</HelpNote>
    </HelpPopover>
  );

  const headerActions = (
    <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
      <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
        {(close) => (
          <>
            <ActionMenuItem
              icon={<RotateCcw className="w-3.5 h-3.5" />}
              disabled={
                loading ||
                actionLoading !== null ||
                isProcessing ||
                mockMode ||
                !isAdmin ||
                checkingPermissions
              }
              onClick={() => {
                setResetModal({ datasource: null, all: true });
                close();
              }}
            >
              {t('management.datasources.reposition')}
            </ActionMenuItem>
            <ActionMenuItem
              icon={<PlayCircle className="w-3.5 h-3.5" />}
              disabled={
                loading ||
                actionLoading !== null ||
                isProcessing ||
                mockMode ||
                !isAdmin ||
                checkingPermissions
              }
              onClick={() => {
                handleProcessAll();
                close();
              }}
            >
              {t('common.processAll')}
            </ActionMenuItem>
          </>
        )}
      </SectionActionsMenu>
      {helpContent}
    </div>
  );

  return (
    <>
      <Card>
        <AccordionSection
          title={t('management.datasources.title')}
          icon={Logs}
          iconColor="var(--theme-icon-purple)"
          isExpanded={isExpanded}
          onToggle={() => setIsExpanded((prev) => !prev)}
          badge={headerActions}
        >
          {loading ? (
            <LoadingState message={t('management.datasources.loadingDatasources')} />
          ) : (
            <div className="space-y-3">
              {datasources.map((ds) => {
                const position = getPositionForDatasource(ds.name);
                const isDatasourceExpanded = expandedDatasources.has(ds.name);

                return (
                  <DatasourceListItem
                    key={ds.name}
                    name={ds.name}
                    path={ds.logsPath}
                    isExpanded={isDatasourceExpanded}
                    onToggle={() => toggleExpanded(ds.name)}
                    enabled={ds.enabled}
                  >
                    {/* Expanded content - Position info */}
                    <div className="space-y-3">
                      {/* Access Log Section */}
                      <div className="flex flex-wrap items-center justify-between gap-3 p-3 bg-themed-tertiary rounded-lg">
                        <div className="min-w-0">
                          <div className="font-mono text-sm text-themed-primary">access.log</div>
                          <div className="text-xs text-themed-muted">
                            {formatPosition(position)}
                          </div>
                        </div>
                        <div className="flex items-center gap-2 flex-shrink-0">
                          <Button
                            variant="filled"
                            color="gray"
                            size="sm"
                            onClick={(e) => {
                              e.stopPropagation();
                              setResetModal({ datasource: ds.name, all: false });
                            }}
                            awaitPermissions
                            disabled={
                              actionLoading !== null ||
                              isProcessing ||
                              mockMode ||
                              !isAdmin ||
                              !ds.enabled
                            }
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
                            awaitPermissions
                            disabled={
                              actionLoading !== null ||
                              isProcessing ||
                              mockMode ||
                              !isAdmin ||
                              !ds.enabled ||
                              position?.totalLines === 0
                            }
                            loading={actionLoading === `access-${ds.name}`}
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
          )}
        </AccordionSection>
      </Card>

      {/* Reposition Log Modal */}
      <Modal
        opened={resetModal !== null}
        onClose={() => setResetModal(null)}
        title={
          resetModal?.all
            ? t('management.datasources.modal.repositionAll')
            : t('management.datasources.modal.repositionSingle', {
                datasource: resetModal?.datasource
              })
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.datasources.modal.choosePosition')}
          </p>

          <div className="p-3 bg-themed-tertiary rounded-lg">
            <p className="text-xs text-themed-muted leading-relaxed">
              <strong>{t('management.datasources.modal.startFromBeginning')}:</strong>{' '}
              {t('management.datasources.modal.beginningDescription')}
              <br />
              <strong>{t('management.datasources.modal.startFromEnd')}:</strong>{' '}
              {t('management.datasources.modal.endDescription')}
            </p>
          </div>

          <div className="flex flex-col gap-3">
            <Button
              variant="filled"
              color="blue"
              onClick={() => handleResetPosition(resetModal?.datasource || null, 'top')}
              awaitPermissions
              loading={actionLoading?.startsWith('reset-')}
              fullWidth
            >
              {t('management.datasources.modal.startFromBeginning')}
            </Button>
            <Button
              variant="default"
              onClick={() => handleResetPosition(resetModal?.datasource || null, 'bottom')}
              awaitPermissions
              loading={actionLoading?.startsWith('reset-')}
              fullWidth
            >
              {t('management.datasources.modal.startFromEnd')}
            </Button>
            <Button
              variant="filled"
              color="gray"
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
