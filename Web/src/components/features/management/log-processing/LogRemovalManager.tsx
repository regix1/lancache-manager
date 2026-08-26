import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useOptimisticPending } from '@/hooks/useOptimisticPending';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useSelectionSet } from '@/hooks/useSelectionSet';
import { useTranslation } from 'react-i18next';
import { FileText, RefreshCw, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { getErrorMessage } from '@utils/error';
import { useNotifications } from '@contexts/notifications';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { useBulkRemoval, type LogBatchEntry } from '@contexts/BulkRemovalContext';
import { useConfig } from '@contexts/useConfig';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useManagerLoading } from '@/hooks/useManagerLoading';
import { useReconnectRefetch } from '@/hooks/useReconnectRefetch';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { Alert } from '@components/ui/Alert';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Tooltip } from '@components/ui/Tooltip';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionHeaderActions, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { formatCount } from '@utils/formatters';
import { LoadingState, EmptyState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import CardDirectoryNotice from '@components/features/management/CardDirectoryNotice';
import { NginxReopenActionGate } from '@components/features/management/NginxReopenActionGate';
import type { DatasourceInfo, DatasourceServiceCounts } from '@/types';
import { resolveCardNotice } from '@utils/cardDirectoryNotice';
import { resolveDatasources } from '@utils/datasources';
import { getNginxReopenGate } from '@utils/nginxReopenAvailability';
import { useSectionExpanded } from '@hooks/useSectionExpanded';

// Main services that should always be shown first
const MAIN_SERVICES = [
  'steam',
  'epicgames',
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

const ServiceRow: React.FC<{
  service: string;
  count: number;
  isRemoving: boolean;
  isDisabled: boolean;
  onClick: () => void;
  clearLabel: string;
  entriesLabel: string;
  removingLabel: string;
  selectable: boolean;
  selected: boolean;
  onSelectToggle: () => void;
  selectLabel: string;
  selectDisabled: boolean;
  clearTooltip?: string;
}> = ({
  service,
  count,
  isRemoving,
  isDisabled,
  onClick,
  clearLabel,
  entriesLabel,
  removingLabel,
  selectable,
  selected,
  onSelectToggle,
  selectLabel,
  selectDisabled,
  clearTooltip
}) => {
  const clearButton = (
    <Button
      onClick={onClick}
      awaitPermissions
      disabled={isDisabled}
      variant="filled"
      color="destructive"
      size="sm"
      loading={isRemoving}
    >
      {isRemoving ? removingLabel : clearLabel}
    </Button>
  );

  // No row-level click or keyboard action exists here - only the nested Checkbox and the
  // clear Button are interactive - so the row carries no hover/focus affordance suggesting
  // otherwise. [28]
  return (
    <div className="mgmt-row">
      {selectable && (
        <Checkbox
          checked={selected}
          onChange={onSelectToggle}
          disabled={selectDisabled}
          aria-label={selectLabel}
          className="flex-shrink-0"
        />
      )}
      <div className="mgmt-row__body">
        {/* Display-only fold (xboxlive -> Xbox): the raw LogEntries.Service tag stays on keys
            and API calls - on-disk cache filenames are md5(tag+url), so the tag itself must
            never be relabeled. */}
        <p className="mgmt-row__title capitalize truncate">{getServiceDisplayName(service)}</p>
        <p className="mgmt-row__meta">
          {formatCount(count)} {entriesLabel}
        </p>
      </div>
      <div className="mgmt-row__actions">
        {clearTooltip ? (
          <Tooltip content={clearTooltip} position="top">
            {clearButton}
          </Tooltip>
        ) : (
          clearButton
        )}
      </div>
    </div>
  );
};

interface LogRemovalManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const LogRemovalManager: React.FC<LogRemovalManagerProps> = ({ authMode, mockMode, onError }) => {
  const { t } = useTranslation();
  const { notifications, isAnyRemovalRunning, addNotification } = useNotifications();
  const { runLogRemoval, isLogRemovalRunning: isBatchRunning } = useBulkRemoval();
  const { on, off, isConnected } = useSignalR();
  const { config } = useConfig();
  const { cacheReadOnly, logsReadOnly, cacheExist, logsExist, checkingPermissions } =
    useDirectoryPermissionsContext();

  // The per-datasource service-count endpoint does not carry the source layout, so join it
  // from the config datasource list by name to drive the bare-metal displays below.
  const configuredDatasources = useMemo<DatasourceInfo[]>(
    () => resolveDatasources(config),
    [config]
  );
  const cardNginxReopenGate = getNginxReopenGate(configuredDatasources);
  const datasourceInfoByName = useMemo<Map<string, DatasourceInfo>>(
    () => new Map(configuredDatasources.map((ds) => [ds.name, ds])),
    [configuredDatasources]
  );

  // State
  const [datasourceCounts, setDatasourceCounts] = useState<DatasourceServiceCounts[]>([]);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<{
    datasource: string;
    service: string;
  } | null>(null);
  const [pendingLogFileDeletion, setPendingLogFileDeletion] = useState<string | null>(null);
  const [deletingLogFile, setDeletingLogFile] = useState<string | null>(null);
  const [showMoreServices, setShowMoreServices] = useState<Record<string, boolean>>({});
  const [showBatchConfirm, setShowBatchConfirm] = useState(false);

  // Client-only selection of (datasource::service) pairs for the "Remove Selected"
  // batch. Toggling a checkbox never hits the network - the batch runs only on confirm.
  const selection = useSelectionSet<string>();
  const selectionRef = useRef(selection);
  selectionRef.current = selection;
  const { isLoading, isRefreshing, hasInitiallyLoaded, beginLoad, markLoaded, markFailed } =
    useManagerLoading(true);
  const {
    isPending: isServiceRemovalPending,
    anyPending: anyServiceRemovalPending,
    markStarting: markServiceRemovalStarting,
    clearPending: clearServiceRemovalPending,
    clearOnNotification: clearServiceRemovalOnNotification
  } = useOptimisticPending<string>();
  const [sectionExpanded, setSectionExpanded] = useSectionExpanded(
    'management-log-removal-expanded',
    false
  );
  useAccordionGroupItem('storage-log-removal', sectionExpanded, () =>
    setSectionExpanded((prev) => !prev)
  );

  // Track the last processed completion notification ID to prevent duplicate reloads
  const lastProcessedCompletionRef = useRef<string | null>(null);

  // Derive active log removal from notifications
  const activeLogRemovalNotification = notifications.find(
    (n) => n.type === 'log_removal' && n.status === 'running'
  );
  const activeLogRemoval =
    (activeLogRemovalNotification?.details?.service as string | null) ?? null;
  // Own-card gate: any running OR queued log removal disables every remove button
  // in this card (per-service rows and log-file deletion gate together).
  const isLogRemovalActive = useOperationBusy({
    types: ['log_removal'],
    status: ['running', 'waiting']
  });

  useEffect(() => {
    if (!hasInitiallyLoaded) {
      void loadData();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hasInitiallyLoaded]);

  // Refetch whenever the backend invalidates the per-service counts cache. This is the
  // single live-update signal covering every log writer (manual clear, eviction log
  // purge, partial cache removals) - the backend broadcasts it from the one invalidation
  // choke point, so this panel never needs a manual refresh after a removal.
  useEffect(() => {
    const handleServiceCountsChanged = () => {
      void loadData(true);
    };
    on('ServiceCountsChanged', handleServiceCountsChanged);
    return () => off('ServiceCountsChanged', handleServiceCountsChanged);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [on, off]);

  // A ServiceCountsChanged broadcast during a socket drop is missed, leaving the per-service
  // counts stale until remount. Refetch once the connection re-establishes.
  useReconnectRefetch(isConnected, () => void loadData(true));

  // Listen for log removal completion via notifications to trigger reload
  // Use ref to prevent duplicate processing of the same completion notification
  useEffect(() => {
    // A cancelled removal is terminal too, and it has usually already deleted some entries,
    // so it must reload the list exactly like a completed or failed one.
    const completedLogRemoval = notifications.find(
      (n) => n.type === 'log_removal' && isTerminalNotificationStatus(n.status)
    );

    if (completedLogRemoval && hasInitiallyLoaded) {
      // Only reload if we haven't already processed this completion. Key on the per-run
      // operationId - the notification id is the stable per-type 'log_removal', so keying
      // on it would block every clear after the first one.
      const completionKey =
        (completedLogRemoval.details?.operationId as string | undefined) ?? completedLogRemoval.id;
      if (lastProcessedCompletionRef.current !== completionKey) {
        lastProcessedCompletionRef.current = completionKey;
        void loadData(true);
      }
    }

    // Clear optimistic pending as soon as the matching running notification appears
    if (anyServiceRemovalPending && activeLogRemoval) {
      datasourceCounts.forEach((ds) => {
        const key = `${ds.datasource}:${activeLogRemoval}`;
        clearServiceRemovalOnNotification(key, notifications, (n, k) => {
          const [, svc] = k.split(':');
          return n.type === 'log_removal' && n.status === 'running' && n.details?.service === svc;
        });
      });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [notifications, hasInitiallyLoaded]);

  const loadData = async (forceRefresh = false) => {
    beginLoad(forceRefresh);
    try {
      const dsCounts = await ApiService.getServiceLogCountsByDatasource();
      setDatasourceCounts(dsCounts);
      markLoaded();
    } catch (err: unknown) {
      console.error('Failed to load log data:', err);
      markFailed();
    }
  };

  const executeRemoveServiceLogs = async (datasourceName: string, serviceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }

    const key = `${datasourceName}:${serviceName}`;
    setPendingServiceRemoval(null);
    markServiceRemovalStarting(key);

    try {
      const result = await ApiService.removeServiceFromDatasourceLogs(datasourceName, serviceName);
      if (result?.queued || result?.alreadyRunning || result?.status === 'waiting') {
        // Wait-queue model: a queued/deduplicated response is a SUCCESS, not an error - the
        // OperationWaiting purple card owns the UI until promotion. Release the button's
        // optimistic pending now: the waiting card carries no per-service details, so the
        // running-notification matcher would never clear it.
        clearServiceRemovalPending(key);
      } else if (result?.status === 'running' && result.operationId) {
        addNotification(
          buildSeededRunningNotification(
            'log_removal',
            result.operationId,
            t('signalr.logRemoval.starting.default', {
              service: getServiceDisplayName(serviceName)
            }),
            {
              // Raw tag: notification matching and the backend operate on LogEntries.Service.
              service: serviceName
            }
          )
        );
      } else if (result && (result.status === 'running' || result.operationId)) {
        // Accepted without a seedable shape (e.g. the queue's immediate-start path):
        // SignalR Started/progress events own the card from here.
      } else {
        onError?.(
          t('management.logRemoval.errors.unexpectedResponse', {
            service: getServiceDisplayName(serviceName)
          })
        );
        clearServiceRemovalPending(key);
      }
    } catch (err: unknown) {
      const errMsg = getErrorMessage(err);
      const errorMessage = errMsg?.includes('read-only')
        ? t('management.logRemoval.errors.readOnly')
        : errMsg || t('management.logRemoval.errors.actionFailed');
      onError?.(errorMessage);
      clearServiceRemovalPending(key);
    }
  };

  const handleRemoveServiceLogs = useCallback(
    (datasourceName: string, serviceName: string) => {
      if (authMode !== 'authenticated') {
        onError?.(t('common.fullAuthRequired'));
        return;
      }
      setPendingServiceRemoval({ datasource: datasourceName, service: serviceName });
    },
    [authMode, onError, t]
  );

  const executeDeleteLogFile = async (datasourceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }

    setPendingLogFileDeletion(null);
    setDeletingLogFile(datasourceName);

    try {
      await ApiService.deleteLogFile(datasourceName);
      // Refresh data after deletion
      await loadData(true);
    } catch (err: unknown) {
      const errMsg = getErrorMessage(err);
      const errorMessage = errMsg?.includes('read-only')
        ? t('management.logRemoval.errors.readOnly')
        : errMsg || t('management.logRemoval.errors.deleteFailed');
      onError?.(errorMessage);
    } finally {
      setDeletingLogFile(null);
    }
  };

  const getServicesForDatasource = useCallback(
    (ds: DatasourceServiceCounts) => {
      const allServices = Object.keys(ds.serviceCounts).filter((s) => ds.serviceCounts[s] > 0);
      const main = allServices.filter((s) => MAIN_SERVICES.includes(s.toLowerCase())).sort();
      const other = allServices.filter((s) => !MAIN_SERVICES.includes(s.toLowerCase())).sort();
      const showMore = showMoreServices[ds.datasource] ?? false;
      const displayed = showMore ? [...main, ...other] : main;
      return { main, other, displayed };
    },
    [showMoreServices]
  );

  // All currently visible + writable (datasource::service) pairs - the scope of both
  // the select-all checkbox and prune-on-reload. Mirrors "Remove All" semantics: only
  // rows the user can actually see and act on.
  const selectableKeys = useMemo<string[]>(() => {
    const keys: string[] = [];
    datasourceCounts.forEach((ds) => {
      if (!ds.logsWritable) return;
      // Scope is EVERY writable service with entries, independent of the per-datasource
      // show-more/less UI state. Keying off `displayed` would drop a selected "other"
      // service the moment its datasource collapsed (prune effect) and would leave
      // select-all unable to reach the hidden rows.
      Object.keys(ds.serviceCounts).forEach((service) => {
        if ((ds.serviceCounts[service] || 0) > 0) {
          keys.push(`${ds.datasource}::${service}`);
        }
      });
    });
    return keys;
  }, [datasourceCounts]);

  const allVisibleSelected = selectableKeys.length > 0 && selection.allSelected(selectableKeys);

  // Prune selection keys that disappear from the visible list on refresh so a stale
  // (datasource, service) pair can never survive a reload into a batch.
  useEffect(() => {
    const valid = new Set(selectableKeys);
    const sel = selectionRef.current;
    const stale = [...sel.selected].filter((key) => !valid.has(key));
    if (stale.length > 0) {
      sel.setMany(stale, false);
    }
  }, [selectableKeys]);

  const runBatchRemoval = useCallback(async () => {
    setShowBatchConfirm(false);
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }

    // Snapshot the selection, dropping any pair that is no longer removable.
    const valid = new Set(selectableKeys);
    const items: LogBatchEntry[] = [...selection.selected]
      .filter((key) => valid.has(key))
      .map((key) => {
        const sep = key.indexOf('::');
        return { datasource: key.slice(0, sep), service: key.slice(sep + 2) };
      });
    if (items.length === 0) return;

    await runLogRemoval(items, {
      // Counts refresh via the existing ServiceCountsChanged subscription; here we
      // only drop the selection so the next batch starts clean.
      onSettled: () => selectionRef.current.clear()
    });
  }, [authMode, onError, t, selectableKeys, selection, runLogRemoval]);

  const toggleDatasourceExpanded = (name: string) => {
    setExpandedDatasources((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  };

  const hasAnyLogEntries = datasourceCounts.some((ds) =>
    Object.values(ds.serviceCounts).some((count) => count > 0)
  );

  const directoryNotice = resolveCardNotice(
    { cacheWrite: false, cacheRead: false, logsWrite: true, nginx: true },
    {
      cacheReadOnly,
      logsReadOnly,
      cacheExist,
      logsExist,
      checkingPermissions,
      nginxReopenGate: cardNginxReopenGate
    }
  );
  const selectedDatasourceNames = [...selection.selected].map((key) =>
    key.slice(0, key.indexOf('::'))
  );
  const selectedNginxReopenGate = getNginxReopenGate(
    configuredDatasources,
    selectedDatasourceNames
  );
  const selectedNginxReopenMessage = selectedNginxReopenGate.messageKey
    ? t(selectedNginxReopenGate.messageKey)
    : '';

  // Header action cluster: everything lives in one overflow menu now (the count
  // still shows on the "Remove Selected" item's own label). flex-wrap keeps the
  // trigger from overflowing at 390px.
  const headerBadge = (
    <SectionHeaderActions>
      {selection.count > 0 && (
        <SectionHeaderChip variant="neutral" className="badge-count">
          {selection.count}
        </SectionHeaderChip>
      )}
      <SectionActionsMenu label={t('management.actions.menuLabel')}>
        {(close) => (
          <>
            <ActionMenuItem
              icon={<RefreshCw className="w-3.5 h-3.5" />}
              disabled={isRefreshing || isAnyRemovalRunning}
              onClick={() => {
                loadData(true);
                close();
              }}
            >
              {t('common.refresh')}
            </ActionMenuItem>
            <ActionMenuDivider />
            <NginxReopenActionGate
              available={selectedNginxReopenGate.available}
              tooltip={selectedNginxReopenMessage}
              position="left"
              className="block w-full"
            >
              <ActionMenuDangerItem
                icon={<Trash2 className="w-3.5 h-3.5" />}
                disabled={
                  selection.count === 0 ||
                  mockMode ||
                  authMode !== 'authenticated' ||
                  !selectedNginxReopenGate.available ||
                  isLogRemovalActive ||
                  anyServiceRemovalPending ||
                  isBatchRunning
                }
                onClick={() => {
                  setShowBatchConfirm(true);
                  close();
                }}
              >
                {t('management.batchSelect.removeSelectedLabel')}
              </ActionMenuDangerItem>
            </NginxReopenActionGate>
          </>
        )}
      </SectionActionsMenu>
    </SectionHeaderActions>
  );

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.logRemoval.help.aboutTitle')}>
        {t('management.logRemoval.summary')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <>
      <AccordionSection
        title={t('management.logRemoval.title')}
        titleAccessory={helpAccessory}
        icon={FileText}
        isExpanded={sectionExpanded}
        onToggle={() => setSectionExpanded((prev) => !prev)}
        badge={headerBadge}
      >
        <div className="space-y-4">
          <CardDirectoryNotice notice={directoryNotice} />

          {/* Content */}
          <>
            {isLoading ? (
              <LoadingState
                variant="spinner"
                message={t('management.logRemoval.loading.scanning')}
                submessage={t('management.logRemoval.loading.mayTakeMinutes')}
              />
            ) : hasAnyLogEntries ? (
              <div className="space-y-3">
                {selectableKeys.length > 0 && (
                  <div className="flex flex-wrap items-center gap-2">
                    {/* Select-all only. The selected count shows once in the section
                            header badge, so it is not repeated here. */}
                    <Checkbox
                      checked={allVisibleSelected}
                      onChange={() => selection.setMany(selectableKeys, !allVisibleSelected)}
                      disabled={
                        mockMode ||
                        authMode !== 'authenticated' ||
                        isLogRemovalActive ||
                        anyServiceRemovalPending ||
                        isBatchRunning
                      }
                      label={t(
                        allVisibleSelected
                          ? 'management.batchSelect.deselectAll'
                          : 'management.batchSelect.selectAll'
                      )}
                    />
                  </div>
                )}
                {datasourceCounts.map((ds) => {
                  const { other, displayed } = getServicesForDatasource(ds);
                  const isExpanded = expandedDatasources.has(ds.datasource);
                  const totalEntries = Object.values(ds.serviceCounts).reduce((a, b) => a + b, 0);
                  const hasEntries = totalEntries > 0;
                  const layout = datasourceInfoByName.get(ds.datasource)?.layout;
                  const nginxReopenGate = getNginxReopenGate(configuredDatasources, [
                    ds.datasource
                  ]);
                  const nginxReopenMessage = nginxReopenGate.messageKey
                    ? t(nginxReopenGate.messageKey)
                    : '';
                  const isBareMetalLayout = layout === 'bare_metal' || layout === 'mixed';
                  const layoutLabel =
                    layout === 'bare_metal'
                      ? t('management.datasources.layout.bareMetal')
                      : layout === 'mixed'
                        ? t('management.datasources.layout.mixed')
                        : null;

                  return (
                    <DatasourceListItem
                      key={ds.datasource}
                      name={ds.datasource}
                      path={ds.logsPath}
                      isExpanded={isExpanded}
                      onToggle={() => toggleDatasourceExpanded(ds.datasource)}
                      enabled={ds.enabled && ds.logsWritable}
                      statusBadge={`${formatCount(totalEntries)} ${t('management.logRemoval.labels.entries')}`}
                      statusIcons={
                        layoutLabel ? (
                          <span
                            className={`text-xs inline-flex items-center px-2.5 py-1 rounded-full transition duration-300 ${
                              isExpanded
                                ? 'bg-[var(--theme-accent-subtle)] text-themed-accent'
                                : 'bg-themed-tertiary text-themed-muted'
                            }`}
                          >
                            {layoutLabel}
                          </span>
                        ) : undefined
                      }
                    >
                      {hasEntries ? (
                        <div className="space-y-3 pt-3">
                          {!nginxReopenGate.available && (
                            <ReadOnlyBadge message={nginxReopenMessage} />
                          )}
                          {isBareMetalLayout && (
                            <Alert color="blue">
                              <p className="text-sm">{t('management.logRemoval.bareMetal.note')}</p>
                            </Alert>
                          )}
                          <div className="mgmt-list divided-list">
                            {displayed.map((service) => {
                              const key = `${ds.datasource}:${service}`;
                              const selectKey = `${ds.datasource}::${service}`;
                              const selectionDisabled =
                                mockMode ||
                                anyServiceRemovalPending ||
                                isLogRemovalActive ||
                                authMode !== 'authenticated' ||
                                !ds.logsWritable ||
                                isBatchRunning;
                              const rowDisabled = selectionDisabled || !nginxReopenGate.available;
                              return (
                                <ServiceRow
                                  key={key}
                                  service={service}
                                  count={ds.serviceCounts[service] || 0}
                                  isRemoving={
                                    activeLogRemoval === service || isServiceRemovalPending(key)
                                  }
                                  isDisabled={rowDisabled}
                                  onClick={() => handleRemoveServiceLogs(ds.datasource, service)}
                                  clearLabel={t('management.logRemoval.buttons.clear')}
                                  entriesLabel={t('management.logRemoval.labels.entries')}
                                  removingLabel={t('management.logRemoval.labels.removing', {
                                    service
                                  })}
                                  selectable={ds.logsWritable}
                                  selected={selection.isSelected(selectKey)}
                                  onSelectToggle={() => selection.toggle(selectKey)}
                                  selectLabel={t('management.batchSelect.selectItem', {
                                    name: getServiceDisplayName(service)
                                  })}
                                  selectDisabled={selectionDisabled}
                                  clearTooltip={
                                    !nginxReopenGate.available
                                      ? nginxReopenMessage
                                      : isBareMetalLayout
                                        ? t('management.logRemoval.bareMetal.clearTooltip')
                                        : undefined
                                  }
                                />
                              );
                            })}
                          </div>

                          {other.length > 0 && (
                            <div>
                              <Button
                                variant="filled"
                                color="secondary"
                                size="sm"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  setShowMoreServices((prev) => ({
                                    ...prev,
                                    [ds.datasource]: !prev[ds.datasource]
                                  }));
                                }}
                              >
                                {showMoreServices[ds.datasource] ? (
                                  <>
                                    {t('management.logRemoval.buttons.showLess', {
                                      count: other.length
                                    })}
                                  </>
                                ) : (
                                  <>
                                    {t('management.logRemoval.buttons.showMore', {
                                      count: other.length
                                    })}
                                  </>
                                )}
                              </Button>
                            </div>
                          )}

                          {/* Delete entire log file button */}
                          <div className="flex justify-end pt-3 mt-3 border-t border-themed-secondary">
                            <NginxReopenActionGate
                              available={nginxReopenGate.available}
                              tooltip={nginxReopenMessage}
                            >
                              <Button
                                variant="filled"
                                size="sm"
                                color="destructive"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  setPendingLogFileDeletion(ds.datasource);
                                }}
                                awaitPermissions
                                loading={deletingLogFile === ds.datasource}
                                disabled={
                                  mockMode ||
                                  isAnyRemovalRunning ||
                                  isLogRemovalActive ||
                                  anyServiceRemovalPending ||
                                  !!deletingLogFile ||
                                  authMode !== 'authenticated' ||
                                  !ds.logsWritable ||
                                  !nginxReopenGate.available
                                }
                                className="w-full sm:w-auto"
                              >
                                {t('management.logRemoval.buttons.deleteLogFile')}
                              </Button>
                            </NginxReopenActionGate>
                          </div>
                        </div>
                      ) : (
                        <div className="py-6 text-center text-sm text-themed-muted">
                          {t('management.logRemoval.noEntriesForDatasource')}
                        </div>
                      )}
                    </DatasourceListItem>
                  );
                })}
              </div>
            ) : (
              <EmptyState
                title={t('management.logRemoval.emptyState.title')}
                subtitle={t('management.logRemoval.emptyState.subtitle')}
              />
            )}
          </>
        </div>
      </AccordionSection>

      {/* Log Removal Confirmation Modal */}
      <ConfirmationModal
        opened={pendingServiceRemoval !== null}
        onClose={() => setPendingServiceRemoval(null)}
        onConfirm={() => {
          if (pendingServiceRemoval) {
            void executeRemoveServiceLogs(
              pendingServiceRemoval.datasource,
              pendingServiceRemoval.service
            );
          }
        }}
        title={t('management.logRemoval.modal.removeServiceLogs')}
        confirmLabel={t('management.logRemoval.buttons.removeLogs')}
        loading={anyServiceRemovalPending}
      >
        <p className="text-themed-secondary">
          {t('management.logRemoval.modal.removeQuestion', {
            service: pendingServiceRemoval
              ? getServiceDisplayName(pendingServiceRemoval.service)
              : undefined,
            datasource: pendingServiceRemoval?.datasource
          })}
        </p>

        <Alert color="yellow">
          <p className="text-sm">
            {t('management.logRemoval.modal.serviceSummary', {
              service: pendingServiceRemoval
                ? getServiceDisplayName(pendingServiceRemoval.service)
                : undefined
            })}
          </p>
        </Alert>
      </ConfirmationModal>

      {/* Delete Log File Confirmation Modal - a red trash marks this as the harsher of the two
          removals: it drops the whole log file, not one service's entries. */}
      <ConfirmationModal
        opened={pendingLogFileDeletion !== null}
        onClose={() => setPendingLogFileDeletion(null)}
        onConfirm={() => {
          if (pendingLogFileDeletion) {
            void executeDeleteLogFile(pendingLogFileDeletion);
          }
        }}
        title={t('management.logRemoval.modal.deleteEntireLogFile')}
        icon={<Trash2 className="w-6 h-6 text-themed-error" />}
        confirmLabel={t('management.logRemoval.buttons.deleteLogFile')}
        loading={!!deletingLogFile}
      >
        <p className="text-themed-secondary">
          {t('management.logRemoval.modal.deleteQuestion', {
            datasource: pendingLogFileDeletion
          })}
        </p>

        <Alert color="red">
          <p className="text-sm">{t('management.logRemoval.modal.fileSummary')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Batch Remove Selected Confirmation Modal */}
      <ConfirmationModal
        opened={showBatchConfirm}
        onClose={() => setShowBatchConfirm(false)}
        onConfirm={() => {
          void runBatchRemoval();
        }}
        title={t('management.batchSelect.confirmTitle')}
        confirmLabel={t('management.batchSelect.removeSelected', { count: selection.count })}
        loading={isBatchRunning}
      >
        <p className="text-themed-secondary">
          {t('management.batchSelect.confirmBody', { count: selection.count })}
        </p>

        <Alert color="yellow">
          <p className="text-sm">{t('management.logRemoval.modal.batchSummary')}</p>
        </Alert>
      </ConfirmationModal>
    </>
  );
};

export default LogRemovalManager;
