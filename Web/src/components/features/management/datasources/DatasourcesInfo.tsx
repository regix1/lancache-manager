import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Logs, PlayCircle, RotateCcw } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
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
import { formatBytes, formatCount } from '@utils/formatters';
import type { DatasourceInfo, DatasourceLogPosition } from '../../../../types';

interface DatasourcesManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

// Which row is persisting a cache-size change, and through which button, so
// only the pressed button shows its spinner while everything else disables.
interface CacheSizeSaveState {
  name: string;
  action: 'save' | 'reset';
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
  const { config, refreshConfig, updateConfig } = useConfig();
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

  // Per-datasource manual cache-size limit. A blank value clears the override and falls
  // back to auto-detect, so the input drives an explicit save rather than editing state live.
  const [cacheSizeDraft, setCacheSizeDraft] = useState<Record<string, string>>({});
  const [cacheSizeSaving, setCacheSizeSaving] = useState<CacheSizeSaveState | null>(null);
  const [cacheSizeError, setCacheSizeError] = useState<Record<string, string | undefined>>({});

  const saveCacheSize = async (name: string, raw: string, action: CacheSizeSaveState['action']) => {
    // Single save at a time: a second row's Enter must not steal the saving owner.
    if (cacheSizeSaving !== null) return;
    setCacheSizeSaving({ name, action });
    setCacheSizeError((prev) => ({ ...prev, [name]: undefined }));
    try {
      const result = await ApiService.setDatasourceCacheSize(name, raw.length > 0 ? raw : null);
      setCacheSizeDraft((prev) => ({ ...prev, [name]: '' }));
      // Apply the endpoint's authoritative result to this row immediately so it updates even if the
      // config refresh below fails (the config provider keeps its last-good config silently on error).
      if (config?.dataSources) {
        updateConfig({
          dataSources: config.dataSources.map((ds) =>
            ds.name === name
              ? {
                  ...ds,
                  cacheSizeOverrideBytes: result.cacheSizeOverrideBytes,
                  resolvedCacheSizeBytes: result.resolvedCacheSizeBytes,
                  cacheSizeSource: result.cacheSizeSource
                }
              : ds
          )
        });
      }
      onSuccess?.(
        action === 'reset'
          ? t('management.datasources.cacheSize.resetDone')
          : t('management.datasources.cacheSize.saved')
      );
      // Reconcile with the server: refresh the datasource config and the dashboard cache total.
      await refreshConfig();
      onDataRefresh?.();
    } catch (err: unknown) {
      setCacheSizeError((prev) => ({
        ...prev,
        [name]: getErrorMessage(err) || t('management.datasources.cacheSize.invalid')
      }));
    } finally {
      setCacheSizeSaving(null);
    }
  };

  const handleSaveCacheSize = async (name: string) => {
    await saveCacheSize(name, (cacheSizeDraft[name] ?? '').trim(), 'save');
  };

  const handleResetCacheSize = async (name: string) => {
    await saveCacheSize(name, '', 'reset');
  };
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
    if (pos.totalLines === 0) {
      // Distinguish "there are no log sources on disk" from "sources exist but haven't
      // produced any lines yet" so the empty state reflects reality instead of implying a
      // missing file.
      return (pos.sourceCount ?? 0) === 0
        ? t('management.datasources.position.noSources')
        : t('management.datasources.position.noData');
    }

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
    </div>
  );

  return (
    <>
      <AccordionSection
        title={t('management.datasources.title')}
        description={t('management.datasources.summary')}
        titleAccessory={helpContent}
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
            {datasources.map((ds: DatasourceInfo) => {
              const position = getPositionForDatasource(ds.name);
              const isDatasourceExpanded = expandedDatasources.has(ds.name);
              const schemeOverride = ds.schemeOverride ?? 'auto';
              const cacheKeyScheme =
                ds.cacheKeyScheme ??
                (ds.layout === 'bare_metal'
                  ? 'bare_metal'
                  : ds.layout === 'mixed'
                    ? 'mixed'
                    : 'monolithic');
              const schemeOverrideLabel = t(
                'management.datasources.scheme.values.' + schemeOverride
              );
              const cacheKeySchemeLabel = t(
                'management.datasources.scheme.values.' + cacheKeyScheme
              );
              const layoutBadge =
                ds.layout === 'bare_metal'
                  ? t('management.datasources.layout.bareMetal')
                  : ds.layout === 'mixed'
                    ? t('management.datasources.layout.mixed')
                    : undefined;
              const unparsed = position?.unparsedLines ?? 0;
              const hintless = position?.hintlessHttpDetailedLines ?? 0;
              const hasDiagnostics =
                Boolean(position?.missingSourcesMessage) || unparsed > 0 || hintless > 0;
              // Bare-metal and mixed datasources read several per-service logs, so a single
              // "access.log" title is inaccurate. Show the source-set count instead.
              const isMultiSource = ds.layout === 'bare_metal' || ds.layout === 'mixed';
              const sourceCount = position?.sourceCount ?? ds.sourceCount ?? 0;

              return (
                <DatasourceListItem
                  key={ds.name}
                  name={ds.name}
                  path={ds.logsPath}
                  isExpanded={isDatasourceExpanded}
                  onToggle={() => toggleExpanded(ds.name)}
                  enabled={ds.enabled}
                  statusBadge={layoutBadge}
                >
                  {/* Expanded content - Position info */}
                  <div className="mgmt-list mt-3">
                    <div className="mgmt-row flex-wrap">
                      <div className="mgmt-row__body">
                        <div className="flex items-center gap-1.5">
                          <p className="mgmt-row__title">
                            {t('management.datasources.scheme.title')}
                          </p>
                          <Tooltip
                            content={t('management.datasources.scheme.tooltip')}
                            position="top"
                          />
                        </div>
                        <p className="mgmt-row__meta">
                          {t('management.datasources.scheme.configured', {
                            scheme: schemeOverrideLabel
                          })}
                        </p>
                      </div>
                      <div className="mgmt-row__actions">
                        <span className="text-xs font-medium px-2.5 py-1 rounded-full bg-themed-tertiary text-themed-secondary">
                          {t('management.datasources.scheme.effective', {
                            scheme: cacheKeySchemeLabel
                          })}
                        </span>
                      </div>
                    </div>

                    {/* Cache size limit row */}
                    <div className="mgmt-row flex-wrap">
                      <div className="mgmt-row__body">
                        <div className="flex items-center gap-1.5">
                          <p className="mgmt-row__title">
                            {t('management.datasources.cacheSize.title')}
                          </p>
                          <Tooltip
                            content={t('management.datasources.cacheSize.tooltip')}
                            position="top"
                          />
                        </div>
                        <p className="mgmt-row__meta">
                          {(ds.cacheSizeSource ?? 'fullDisk') === 'fullDisk'
                            ? t('management.datasources.cacheSize.currentFullDisk')
                            : t('management.datasources.cacheSize.current', {
                                value: formatBytes(ds.resolvedCacheSizeBytes ?? 0),
                                source: t(
                                  'management.datasources.cacheSize.source.' +
                                    (ds.cacheSizeSource ?? 'fullDisk')
                                )
                              })}
                        </p>
                        {cacheSizeError[ds.name] && (
                          <p className="mgmt-row__meta text-themed-error">
                            {cacheSizeError[ds.name]}
                          </p>
                        )}
                      </div>
                      {isAdmin && !mockMode && (
                        <div className="mgmt-row__actions">
                          <input
                            type="text"
                            className="themed-input themed-border-radius-sm min-h-8 w-28 px-3 py-1.5 text-sm"
                            placeholder={t('management.datasources.cacheSize.placeholder')}
                            aria-label={t('management.datasources.cacheSize.title')}
                            value={cacheSizeDraft[ds.name] ?? ''}
                            disabled={cacheSizeSaving?.name === ds.name}
                            onClick={(e) => e.stopPropagation()}
                            onChange={(e) =>
                              setCacheSizeDraft((prev) => ({
                                ...prev,
                                [ds.name]: e.target.value
                              }))
                            }
                            onKeyDown={(e) => {
                              if (e.key === 'Enter') {
                                e.preventDefault();
                                handleSaveCacheSize(ds.name);
                              }
                            }}
                          />
                          <Button
                            variant="filled"
                            color="gray"
                            size="sm"
                            className="datasource-row-btn"
                            onClick={(e) => {
                              e.stopPropagation();
                              handleSaveCacheSize(ds.name);
                            }}
                            disabled={
                              cacheSizeSaving !== null &&
                              (cacheSizeSaving.name !== ds.name ||
                                cacheSizeSaving.action !== 'save')
                            }
                            loading={
                              cacheSizeSaving?.name === ds.name && cacheSizeSaving.action === 'save'
                            }
                          >
                            {t('common.save')}
                          </Button>
                          {ds.cacheSizeSource === 'manual' && (
                            <Button
                              variant="filled"
                              color="gray"
                              size="sm"
                              className="datasource-row-btn"
                              onClick={(e) => {
                                e.stopPropagation();
                                handleResetCacheSize(ds.name);
                              }}
                              disabled={
                                cacheSizeSaving !== null &&
                                (cacheSizeSaving.name !== ds.name ||
                                  cacheSizeSaving.action !== 'reset')
                              }
                              loading={
                                cacheSizeSaving?.name === ds.name &&
                                cacheSizeSaving.action === 'reset'
                              }
                            >
                              {t('common.reset')}
                            </Button>
                          )}
                        </div>
                      )}
                    </div>

                    {/* Access Log row */}
                    <div className="mgmt-row flex-wrap">
                      <div className="mgmt-row__body">
                        {isMultiSource ? (
                          <p className="mgmt-row__title">
                            {t('management.datasources.sourceSet', { count: sourceCount })}
                          </p>
                        ) : (
                          <p className="mgmt-row__title font-mono">access.log</p>
                        )}
                        <p className="mgmt-row__meta">{formatPosition(position)}</p>
                      </div>
                      <div className="mgmt-row__actions">
                        <Button
                          variant="filled"
                          color="gray"
                          size="sm"
                          className="datasource-row-btn"
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
                          className="datasource-row-btn"
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

                  {ds.capabilityDenialReason && (
                    <Alert color="yellow" className="mt-3">
                      <p>
                        <strong>{t('management.datasources.scheme.unavailable')}:</strong>{' '}
                        {ds.capabilityDenialReason}
                      </p>
                    </Alert>
                  )}

                  {hasDiagnostics && (
                    <Alert color="yellow" className="mt-3">
                      <div className="space-y-1">
                        {position?.missingSourcesMessage && <p>{position.missingSourcesMessage}</p>}
                        {hintless > 0 && (
                          <p>
                            {t('management.datasources.diagnostics.hintless', { count: hintless })}
                          </p>
                        )}
                        {unparsed > 0 && (
                          <p>
                            {t('management.datasources.diagnostics.unparsed', { count: unparsed })}
                          </p>
                        )}
                      </div>
                    </Alert>
                  )}
                </DatasourceListItem>
              );
            })}
          </div>
        )}
      </AccordionSection>

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
