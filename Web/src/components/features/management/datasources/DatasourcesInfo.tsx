import { noAutofill } from '@utils/autofill';
import React, { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Logs, PlayCircle, RotateCcw } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { Alert } from '@components/ui/Alert';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionErrorChip, SectionHeaderActions } from '@components/ui/SectionHeaderActions';
import { ActionMenuItem } from '@components/ui/ActionMenu';
import { DisabledActionGate } from '@components/features/management/DisabledActionGate';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { API_BASE } from '@utils/constants';
import type { LogProcessingCompleteEvent } from '@contexts/SignalRContext/types';
import { useConfig } from '@contexts/useConfig';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useSelectionSet } from '@hooks/useSelectionSet';
import { useHeldValue } from '@hooks/useHeldValue';
import { getErrorMessage } from '@utils/error';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useReconnectRefetch } from '@/hooks/useReconnectRefetch';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { formatBytes, formatCount } from '@utils/formatters';
import type { DatasourceInfo, DatasourceLogPosition } from '../../../../types';
import { useSectionExpanded } from '@hooks/useSectionExpanded';

interface DatasourcesManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string, error?: unknown) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

// Which row is persisting a cache-size change, and through which button, so
// only the pressed button shows its spinner while everything else disables.
interface CacheSizeSaveState {
  name: string;
  action: 'save' | 'reset';
  cachePath: string;
  logsPath: string;
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
  const [loading, setLoading] = useState(!mockMode);
  const [loadError, setLoadError] = useState<string | null>(null);
  // The first load, Retry, the processing-complete refresh and the reconnect refresh can overlap;
  // only the newest one writes the positions and the section error.
  const positionsRequestRef = useRef(0);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const { selected: expandedDatasources, toggle: toggleExpanded } = useSelectionSet<string>();
  const [resetModal, setResetModal] = useState<{ datasource: string | null; all: boolean } | null>(
    null
  );
  const shownResetModal = useHeldValue(resetModal);
  // Read when a reset answers, to learn whether the person closed the dialog while it waited.
  const resetModalRef = useRef(resetModal);
  resetModalRef.current = resetModal;
  // Resets in flight as `<target>:<position>`, the target being a datasource name or 'all'. Each
  // target busies only its own opener, so a reset waiting on the log lock locks nothing else.
  const runningResets = useSelectionSet<string>();
  const isResetRunning = (target: string): boolean =>
    runningResets.isSelected(`${target}:top`) || runningResets.isSelected(`${target}:bottom`);
  const shownResetTarget = shownResetModal?.datasource || 'all';
  const [resetError, setResetError] = useState<string | null>(null);
  // The server refuses a log action for the whole time an unmapped removal runs or is being stopped.
  const isUnmappedRemovalRunning = useOperationBusy({
    types: ['unmapped_file_removal'],
    status: ['running', 'cancelling']
  });
  const unmappedRemovalReason = isUnmappedRemovalRunning
    ? t('management.storage.unmappedRemovalRunning')
    : '';
  const [isExpanded, setIsExpanded] = useSectionExpanded(
    'management-datasources-expanded-v2',
    false
  );
  useAccordionGroupItem('storage-datasources', isExpanded, () => setIsExpanded((prev) => !prev));

  // Per-datasource manual cache-size limit. A blank value clears the override and falls
  // back to auto-detect, so the input drives an explicit save rather than editing state live.
  const [cacheSizeDraft, setCacheSizeDraft] = useState<Record<string, string>>({});
  const [cacheSizeSaving, setCacheSizeSaving] = useState<CacheSizeSaveState | null>(null);
  const [cacheSizeError, setCacheSizeError] = useState<Record<string, string | undefined>>({});
  const cacheSizeSaveRequestRef = useRef(0);
  const cacheSizeSaveAllowedRef = useRef(isAdmin && !mockMode);
  const configRef = useRef(config);
  cacheSizeSaveAllowedRef.current = isAdmin && !mockMode;
  configRef.current = config;

  useEffect(
    () => () => {
      cacheSizeSaveRequestRef.current += 1;
    },
    []
  );
  useEffect(() => {
    if (cacheSizeSaving === null) return;
    const currentSource = config.dataSources.find(
      (datasource) => datasource.name === cacheSizeSaving.name
    );
    if (
      isAdmin &&
      !mockMode &&
      currentSource?.cachePath === cacheSizeSaving.cachePath &&
      currentSource?.logsPath === cacheSizeSaving.logsPath
    ) {
      return;
    }
    cacheSizeSaveRequestRef.current += 1;
    setCacheSizeSaving(null);
  }, [cacheSizeSaving, config.dataSources, isAdmin, mockMode]);

  const saveCacheSize = async (name: string, raw: string, action: CacheSizeSaveState['action']) => {
    // Single save at a time: a second row's Enter must not steal the saving owner.
    if (cacheSizeSaving !== null || !isAdmin || mockMode) return;
    const source = config.dataSources.find((datasource) => datasource.name === name);
    if (!source) return;

    const request = ++cacheSizeSaveRequestRef.current;
    const cachePath = source.cachePath;
    const logsPath = source.logsPath;
    const saveCurrent = (): boolean => {
      const currentSource = configRef.current.dataSources.find(
        (datasource) => datasource.name === name
      );
      return (
        request === cacheSizeSaveRequestRef.current &&
        cacheSizeSaveAllowedRef.current &&
        currentSource?.cachePath === cachePath &&
        currentSource?.logsPath === logsPath
      );
    };

    setCacheSizeSaving({ name, action, cachePath, logsPath });
    setCacheSizeError((prev) => ({ ...prev, [name]: undefined }));
    try {
      const result = await ApiService.setDatasourceCacheSize(name, raw.length > 0 ? raw : null);
      if (!saveCurrent()) return;
      updateConfig((current) => {
        const currentSource = current.dataSources.find((datasource) => datasource.name === name);
        if (
          request !== cacheSizeSaveRequestRef.current ||
          !cacheSizeSaveAllowedRef.current ||
          currentSource?.cachePath !== cachePath ||
          currentSource?.logsPath !== logsPath
        ) {
          return null;
        }

        return {
          dataSources: current.dataSources.map((datasource) =>
            datasource.name === name
              ? {
                  ...datasource,
                  cacheSizeOverrideBytes: result.cacheSizeOverrideBytes,
                  resolvedCacheSizeBytes: result.resolvedCacheSizeBytes,
                  cacheSizeSource: result.cacheSizeSource
                }
              : datasource
          )
        };
      });
      if (!saveCurrent()) return;
      setCacheSizeDraft((prev) => ({ ...prev, [name]: '' }));
      onSuccess?.(
        action === 'reset'
          ? t('management.datasources.cacheSize.resetDone')
          : t('management.datasources.cacheSize.saved')
      );
      // Reconcile with the server: refresh the datasource config and the dashboard cache total.
      if (!saveCurrent()) return;
      await refreshConfig();
      if (!saveCurrent()) return;
      onDataRefresh?.();
    } catch (err: unknown) {
      if (!saveCurrent()) return;
      setCacheSizeError((prev) => ({
        ...prev,
        [name]: getErrorMessage(err)
      }));
    } finally {
      if (saveCurrent()) {
        setCacheSizeSaving(null);
      }
    }
  };

  const handleSaveCacheSize = async (name: string) => {
    await saveCacheSize(name, (cacheSizeDraft[name] ?? '').trim(), 'save');
  };

  const handleResetCacheSize = async (name: string) => {
    await saveCacheSize(name, '', 'reset');
  };
  const signalR = useSignalR();
  const [serverLogResetRunning, setServerLogResetRunning] = useState(false);
  const [logResetRunningKnown, setLogResetRunningKnown] = useState(mockMode);
  const logResetRunningPollRef = useRef(0);
  const logResetRunningAppliedRef = useRef(0);

  // Check if processing is running or queued behind another operation
  const isProcessing = useOperationBusy({
    types: ['log_processing'],
    status: ['running', 'waiting']
  });
  // A Start from End count holds the processing gate as a hidden log processing run, so this
  // reason covers it as well as a processing pass.
  const logsInUseReason = isProcessing ? t('management.datasources.logsInUse') : '';
  const resetAllRunningReason = isResetRunning('all')
    ? t('management.datasources.resetRunning')
    : '';
  const repositionRowReason = isUnmappedRemovalRunning ? unmappedRemovalReason : logsInUseReason;
  const repositionAllReason = isUnmappedRemovalRunning
    ? unmappedRemovalReason
    : resetAllRunningReason ||
      (logResetRunningKnown && serverLogResetRunning
        ? t('management.datasources.resetRunning')
        : '') ||
      logsInUseReason ||
      (!logResetRunningKnown ? t('management.datasources.resetRunningUnknown') : '');
  const repositionAllDisabled =
    loading ||
    isResetRunning('all') ||
    serverLogResetRunning ||
    (!logResetRunningKnown && !mockMode) ||
    isProcessing ||
    isUnmappedRemovalRunning ||
    mockMode ||
    !isAdmin ||
    checkingPermissions;
  const processAllDisabled =
    loading ||
    actionLoading !== null ||
    isProcessing ||
    mockMode ||
    !isAdmin ||
    checkingPermissions;

  const refreshLogResetRunning = useCallback(async () => {
    if (mockMode) {
      setServerLogResetRunning(false);
      setLogResetRunningKnown(true);
      return;
    }
    const request = ++logResetRunningPollRef.current;
    try {
      const res = await fetch(
        `${API_BASE}/logs/reset-running`,
        ApiService.getFetchOptions({ signal: AbortSignal.timeout(10000) })
      );
      if (!res.ok) return;
      const body = (await res.json()) as { running?: boolean };
      if (request <= logResetRunningAppliedRef.current) return;
      logResetRunningAppliedRef.current = request;
      setLogResetRunningKnown(true);
      setServerLogResetRunning(body.running === true);
    } catch {
      // A failed or non-OK read does not retire an older success.
    }
  }, [mockMode]);

  const loadData = useCallback(async () => {
    const request = ++positionsRequestRef.current;
    setLoading(true);
    setLoadError(null);
    try {
      const [positionsData] = await Promise.all([fetchLogPositions(), refreshLogResetRunning()]);
      if (request !== positionsRequestRef.current) return;
      setLogPositions(positionsData);
    } catch (err) {
      if (request !== positionsRequestRef.current) return;
      setLoadError(getErrorMessage(err));
    } finally {
      // Background refreshes never raise the skeleton, so a superseded load still lowers it.
      setLoading(false);
    }
  }, [refreshLogResetRunning]);

  // Load log positions
  useEffect(() => {
    if (!mockMode) {
      void loadData();
    } else {
      setLoading(false);
      setLoadError(null);
    }
  }, [loadData, mockMode]);

  // Re-reads positions with the rows kept on screen. It never throws: a failure lands in the
  // section's own box, so an action that calls it reports only its own failure.
  const refreshPositions = useCallback(async () => {
    const request = ++positionsRequestRef.current;
    try {
      const [positions] = await Promise.all([fetchLogPositions(), refreshLogResetRunning()]);
      if (request !== positionsRequestRef.current) return;
      setLogPositions(positions);
      setLoadError(null);
    } catch (err) {
      if (request !== positionsRequestRef.current) return;
      setLoadError(getErrorMessage(err));
    }
  }, [refreshLogResetRunning]);

  // Listen for processing complete events to refresh positions
  useEffect(() => {
    // The socket stays connected in mock mode, so a real processing run would otherwise pull real
    // log positions into the mock view.
    if (mockMode) return;

    const handleProcessingComplete = (_result: LogProcessingCompleteEvent) => {
      void refreshPositions();
    };
    // A log delete, removal or purge moves the positions too, and each of them sends
    // ServiceCountsChanged.
    const handleLogsChanged = () => {
      void refreshPositions();
    };

    signalR.on('LogProcessingComplete', handleProcessingComplete);
    signalR.on('ServiceCountsChanged', handleLogsChanged);

    return () => {
      signalR.off('LogProcessingComplete', handleProcessingComplete);
      signalR.off('ServiceCountsChanged', handleLogsChanged);
    };
  }, [mockMode, signalR, refreshPositions]);

  // Recover a stale snapshot after a reconnect: a processing-complete event can be missed while
  // the socket is down, so resync log positions whenever the connection returns.
  useReconnectRefetch(signalR.isConnected, () => {
    if (mockMode) return;
    void refreshPositions();
  });

  // The header action is on screen for the whole time this section is mounted, collapsed or not.
  // A reset started on another page sets no event this section listens for, so the flag has to
  // be read on a timer before it has ever been true. A failed read leaves the last answer.
  useEffect(() => {
    if (mockMode) return;
    void refreshLogResetRunning();
    const id = setInterval(() => {
      void refreshLogResetRunning();
    }, 2000);
    return () => clearInterval(id);
  }, [mockMode, refreshLogResetRunning]);

  const handleProcessAll = async () => {
    if (!isAdmin || isProcessing) return;

    setActionLoading('all');
    try {
      // The server's run row opens the card.
      await ApiService.processAllLogs();
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.(t('management.datasources.errors.processingFailed'), err);
    } finally {
      setActionLoading(null);
    }
  };

  const handleProcessDatasource = async (datasourceName: string) => {
    if (!isAdmin || isProcessing) return;

    setActionLoading(`access-${datasourceName}`);
    try {
      await ApiService.processDatasourceLogs(datasourceName);
      onDataRefresh?.();
    } catch (err: unknown) {
      onError?.(t('management.datasources.errors.processingFailed'), err);
    } finally {
      setActionLoading(null);
    }
  };

  // A press during the dialog's fade has no datasource to read (resetModal is already null) and
  // must not fall through to resetting every datasource. A repeat press for a target whose reset
  // runs is ignored. A reset of another target is sent, and the server's refusal shows like any
  // other.
  const handleResetPosition = async (position: 'top' | 'bottom') => {
    if (!isAdmin || resetModal === null) return;

    const datasourceName = resetModal.datasource;
    const targetName = datasourceName || 'all';
    if (isResetRunning(targetName)) return;
    const resetKey = `${targetName}:${position}`;
    runningResets.setMany([resetKey], true);
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
      // Only this reset's own dialog closes; one opened for another target stays.
      setResetModal((current) => (current?.datasource === datasourceName ? null : current));
      // Refresh positions
      await refreshPositions();
      onDataRefresh?.();
    } catch (err: unknown) {
      // This reset's dialog, still open, shows the refusal beside the choice the person made; once
      // closed, or replaced by another target's dialog, it is reported the way the success is.
      if (resetModalRef.current?.datasource === datasourceName) {
        setResetError(getErrorMessage(err));
      } else {
        onError?.(t('management.datasources.errors.resetFailed'), err);
      }
    } finally {
      runningResets.setMany([resetKey], false);
    }
  };

  // Closing never waits on the request: the server can hold it on the log lock for a whole
  // import pass, and the opener for that target shows the reset busy until it answers.
  const closeResetModal = () => {
    setResetModal(null);
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

  const datasources = config.dataSources;

  const hasMultiple = datasources.length > 1;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.datasources.help.aboutTitle')}>
        {t('management.datasources.summary')}
      </HelpSection>

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
    <SectionHeaderActions>
      {loadError !== null && !isExpanded && <SectionErrorChip />}
      <SectionActionsMenu label={t('management.actions.menuLabel')}>
        {(close) => (
          <>
            <DisabledActionGate
              disabled={repositionAllDisabled}
              reason={repositionAllReason}
              label={t('management.datasources.reposition')}
              position="left"
              className="block w-full"
            >
              <ActionMenuItem
                icon={
                  isResetRunning('all') ? (
                    <LoadingSpinner inline size="xs" />
                  ) : (
                    <RotateCcw className="w-3.5 h-3.5" />
                  )
                }
                disabled={repositionAllDisabled}
                onClick={() => {
                  setResetError(null);
                  setResetModal({ datasource: null, all: true });
                  close();
                }}
              >
                {t('management.datasources.reposition')}
              </ActionMenuItem>
            </DisabledActionGate>
            <DisabledActionGate
              disabled={processAllDisabled}
              reason={logsInUseReason}
              label={t('common.processAll')}
              position="left"
              className="block w-full"
            >
              <ActionMenuItem
                icon={<PlayCircle className="w-3.5 h-3.5" />}
                disabled={processAllDisabled}
                onClick={() => {
                  handleProcessAll();
                  close();
                }}
              >
                {t('common.processAll')}
              </ActionMenuItem>
            </DisabledActionGate>
          </>
        )}
      </SectionActionsMenu>
    </SectionHeaderActions>
  );

  return (
    <>
      <AccordionSection
        title={t('management.datasources.title')}
        titleAccessory={helpContent}
        icon={Logs}
        isExpanded={isExpanded}
        onToggle={() => setIsExpanded((prev) => !prev)}
        badge={headerActions}
      >
        {loading ? (
          <LoadingState message={t('management.datasources.loadingDatasources')} shape="rows" />
        ) : (
          <div className="space-y-3">
            {loadError && (
              <ErrorBlock
                title={t('management.datasources.errors.loadFailed')}
                message={loadError}
                retryLabel={t('common.retry')}
                onRetry={() => void loadData()}
              />
            )}

            {datasources.length === 0 && !loadError && (
              <EmptyState variant="text" title={t('management.datasources.noActiveDatasources')} />
            )}

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
              const repositionDisabled =
                isProcessing || isUnmappedRemovalRunning || mockMode || !isAdmin || !ds.enabled;
              const processDisabled =
                actionLoading !== null ||
                isProcessing ||
                mockMode ||
                !isAdmin ||
                !ds.enabled ||
                position?.totalLines === 0;

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
                  <div className="mgmt-list divided-list mt-3 datasource-list">
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
                        <Badge variant="neutral">
                          {t('management.datasources.scheme.effective', {
                            scheme: cacheKeySchemeLabel
                          })}
                        </Badge>
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
                            {...noAutofill}
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
                            color="primary"
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
                              color="secondary"
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
                        <DisabledActionGate
                          disabled={repositionDisabled}
                          reason={repositionRowReason}
                          label={t('management.datasources.reposition')}
                        >
                          <Button
                            variant="filled"
                            color="run"
                            size="sm"
                            className="datasource-row-btn"
                            onClick={(e) => {
                              e.stopPropagation();
                              setResetError(null);
                              setResetModal({ datasource: ds.name, all: false });
                            }}
                            awaitPermissions
                            loading={isResetRunning(ds.name)}
                            disabled={repositionDisabled}
                          >
                            {t('management.datasources.reposition')}
                          </Button>
                        </DisabledActionGate>
                        <DisabledActionGate
                          disabled={processDisabled}
                          reason={
                            logsInUseReason ||
                            (position?.totalLines === 0
                              ? t('management.datasources.noLogLines')
                              : '')
                          }
                          label={t('common.process')}
                        >
                          <Button
                            variant="filled"
                            color="run"
                            size="sm"
                            className="datasource-row-btn"
                            onClick={(e) => {
                              e.stopPropagation();
                              handleProcessDatasource(ds.name);
                            }}
                            awaitPermissions
                            disabled={processDisabled}
                            loading={actionLoading === `access-${ds.name}`}
                          >
                            {t('common.process')}
                          </Button>
                        </DisabledActionGate>
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
        onClose={closeResetModal}
        title={
          shownResetModal?.all
            ? t('management.datasources.modal.repositionAll')
            : t('management.datasources.modal.repositionSingle', {
                datasource: shownResetModal?.datasource
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

          {resetError !== null && (
            <Alert color="error" title={t('management.datasources.errors.resetFailed')}>
              {resetError}
            </Alert>
          )}

          {/* A removal that starts while the dialog is already open. */}
          {isUnmappedRemovalRunning && (
            <Alert color="yellow">
              <p className="text-sm">{unmappedRemovalReason}</p>
            </Alert>
          )}

          <div className="flex flex-col gap-3">
            <Button
              variant="filled"
              color="run"
              onClick={() => handleResetPosition('top')}
              awaitPermissions
              loading={runningResets.isSelected(`${shownResetTarget}:top`)}
              disabled={isUnmappedRemovalRunning}
              fullWidth
            >
              {t('management.datasources.modal.startFromBeginning')}
            </Button>
            <Button
              variant="default"
              onClick={() => handleResetPosition('bottom')}
              awaitPermissions
              loading={runningResets.isSelected(`${shownResetTarget}:bottom`)}
              disabled={isUnmappedRemovalRunning}
              fullWidth
            >
              {t('management.datasources.modal.startFromEnd')}
            </Button>
            <Button variant="filled" color="secondary" onClick={closeResetModal} fullWidth>
              {t('common.cancel')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default DatasourcesManager;
