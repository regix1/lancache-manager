import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useOptimisticPending } from '@/hooks/useOptimisticPending';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useSelectionSet } from '@/hooks/useSelectionSet';
import { useCancellableQueue } from '@/hooks/useCancellableQueue';
import { useTranslation } from 'react-i18next';
import { FileText, AlertTriangle, RefreshCw, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { getErrorMessage } from '@utils/error';
import { useNotifications } from '@contexts/notifications';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { waitForSignalRCompletion } from '@contexts/notifications/waitForSignalRCompletion';
import { useConfig } from '@contexts/useConfig';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  LogRemovalStartedEvent,
  LogRemovalProgressEvent,
  LogRemovalCompleteEvent
} from '@contexts/SignalRContext/types';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useManagerLoading } from '@/hooks/useManagerLoading';
import { finalizeBulkRemovalNotification } from '@components/features/management/game-detection/cacheRemovalHelpers';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { Checkbox } from '@components/ui/Checkbox';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { formatCount } from '@utils/formatters';
import { LoadingState, EmptyState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import CardDirectoryNotice from '@components/features/management/CardDirectoryNotice';
import { NginxReopenActionGate } from '@components/features/management/NginxReopenActionGate';
import type { DatasourceInfo, DatasourceServiceCounts } from '@/types';
import { resolveCardNotice } from '@utils/cardDirectoryNotice';
import { getNginxReopenGate } from '@utils/nginxReopenAvailability';

/** One (datasource, service) pair queued for sequential log removal. */
interface LogBatchEntry {
  datasource: string;
  service: string;
}

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
      color="red"
      size="sm"
      loading={isRemoving}
    >
      {isRemoving ? removingLabel : clearLabel}
    </Button>
  );

  return (
    <div className="mgmt-row mgmt-row--interactive focus-ring--inset">
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
  const { notifications, isAnyRemovalRunning, addNotification, updateNotification } =
    useNotifications();
  const { on, off } = useSignalR();
  const { config } = useConfig();
  const { cacheReadOnly, logsReadOnly, cacheExist, logsExist, checkingPermissions } =
    useDirectoryPermissionsContext();

  // The per-datasource service-count endpoint does not carry the source layout, so join it
  // from the config datasource list by name to drive the bare-metal displays below.
  const configuredDatasources = useMemo<DatasourceInfo[]>(
    () =>
      config.dataSources && config.dataSources.length > 0
        ? config.dataSources
        : [
            {
              name: 'default',
              cachePath: config.cachePath,
              logsPath: config.logsPath,
              cacheWritable: config.cacheWritable,
              logsWritable: config.logsWritable,
              enabled: true,
              layout: 'monolithic',
              nginxReopenAvailable: false
            }
          ],
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
  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem('management-log-removal-expanded');
    return saved !== null ? saved === 'true' : false;
  });
  useAccordionGroupItem('storage-log-removal', sectionExpanded, () =>
    setSectionExpanded((prev) => !prev)
  );

  useEffect(() => {
    localStorage.setItem('management-log-removal-expanded', String(sectionExpanded));
  }, [sectionExpanded]);

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

  const { run: runLogBatch, state: logBatchState } = useCancellableQueue<LogBatchEntry>({
    onSettled: () => {
      // Counts refresh via the existing ServiceCountsChanged subscription; here we
      // only drop the selection so the next batch starts clean.
      selectionRef.current.clear();
    }
  });
  const isBatchRunning =
    logBatchState.status === 'running' || logBatchState.status === 'cancelling';

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
    const total = items.length;
    if (total === 0) return;

    let bulkNotifId: string | null = null;
    let currentIndex = 0;

    await runLogBatch({
      items,
      openNotification: () => {
        // Mirror BulkRemovalContext's cache card exactly: a bulk_removal card with no
        // operationId, so handleCancel routes cancellation through the client-queue
        // branch (flips details.cancelling) rather than a server-side cancel.
        const id = addNotification({
          type: 'bulk_removal',
          status: 'running',
          message: t('management.batchSelect.removeSelected', { count: total }),
          progress: 0,
          details: {}
        });
        bulkNotifId = id;
        return id;
      },
      onItemStart: (entry, index, tot, notifId) => {
        currentIndex = index;
        updateNotification(notifId, {
          message: t('signalr.logRemoval.removing', {
            service: getServiceDisplayName(entry.service)
          }),
          progress: Math.floor(((index - 1) / tot) * 100)
        });
      },
      processItem: async (entry, ctx) => {
        const { datasource, service } = entry;
        let operationId: string | null = null;
        // Register the SignalR listeners BEFORE the DELETE so the Started/Complete
        // events are never missed in a race. Log removal is single-flight
        // server-side and this queue runs one item at a time, so matching on the
        // captured operationId (else the service name on the terminal event) is
        // unambiguous within the batch.
        const waitPromise = waitForSignalRCompletion<
          LogRemovalStartedEvent,
          LogRemovalCompleteEvent,
          LogRemovalProgressEvent
        >({
          signalR: { on, off },
          completeEvent: 'LogRemovalComplete',
          startedEvent: 'LogRemovalStarted',
          match: (payload) =>
            operationId ? payload?.operationId === operationId : payload?.service === service,
          onStartedCapture: (payload) => {
            const startedService = payload?.context?.service;
            return typeof payload?.operationId === 'string' &&
              (startedService === undefined || startedService === service)
              ? { opId: payload.operationId }
              : null;
          },
          onOperationIdCaptured: (opId) => {
            operationId = opId;
            ctx.setOperationId(opId);
          },
          progressEvent: 'LogRemovalProgress',
          onProgress: (payload) => {
            if (!bulkNotifId || !operationId || payload?.operationId !== operationId) return;
            const inner = Math.min(100, Math.max(0, payload.percentComplete ?? 0));
            const overall = Math.min(100, ((currentIndex - 1 + inner / 100) / total) * 100);
            updateNotification(bulkNotifId, { progress: Math.floor(overall) });
          },
          signal: ctx.signal,
          // Large log files can take several minutes to rewrite; give each item a
          // generous window so a legitimately-slow removal is not misreported.
          timeoutMs: 600_000
        });

        const result = await ApiService.removeServiceFromDatasourceLogs(datasource, service);
        if (result?.operationId) {
          operationId = result.operationId;
          ctx.setOperationId(result.operationId);
        }
        const outcome = await waitPromise;
        if (outcome.timedOut) {
          // No completion within the window: count as a failure rather than a silent
          // success so the batch tally and progress stay honest.
          throw new Error(`Log removal timed out for ${service}`);
        }
        // A completion that reports failure (e.g. locked files) must count as failed,
        // not succeeded. Exclude server-side cancels, which the queue's abort path owns.
        if (outcome.event && outcome.event.success === false && !outcome.event.cancelled) {
          throw new Error(outcome.event.message || `Log removal failed for ${service}`);
        }
      },
      finalize: ({ id, succeeded, failed, cancelled, total: finalizeTotal }) => {
        finalizeBulkRemovalNotification({
          id,
          succeeded,
          failed,
          total: finalizeTotal,
          cancelled,
          t,
          updateNotification,
          text: {
            completeKey: 'management.batchSelect.batchComplete',
            completeDefaultValue: 'Removed {{count}} of {{total}} service logs',
            partialFailureKey: 'management.batchSelect.batchCompleteWithFailures',
            partialFailureDefaultValue: 'Removed {{count}} service logs, but {{failed}} failed',
            cancelledKey: 'management.batchSelect.batchCancelled',
            cancelledDefaultValue: 'Log removal cancelled after {{count}} service logs',
            cancelledWithFailuresKey: 'management.batchSelect.batchCancelledWithFailures',
            cancelledWithFailuresDefaultValue:
              'Log removal cancelled after {{count}} service logs, with {{failed}} failures'
          }
        });
      }
    });
  }, [
    authMode,
    onError,
    t,
    selectableKeys,
    selection,
    runLogBatch,
    addNotification,
    updateNotification,
    on,
    off
  ]);

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
    <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
      {selection.count > 0 && (
        <Badge variant="neutral" className="badge-count">
          {selection.count}
        </Badge>
      )}
      <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
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
                {t('management.batchSelect.removeSelectedLabel', 'Remove Selected')}
              </ActionMenuDangerItem>
            </NginxReopenActionGate>
          </>
        )}
      </SectionActionsMenu>
    </div>
  );

  return (
    <>
      <AccordionSection
        title={t('management.logRemoval.title')}
        description={t('management.logRemoval.summary')}
        icon={FileText}
        iconColor="var(--theme-icon-red)"
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
                      statusBadge={`${formatCount(totalEntries)} entries`}
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
                                color="gray"
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
                                color="red"
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
      <Modal
        opened={pendingServiceRemoval !== null}
        onClose={() => {
          if (!anyServiceRemovalPending) {
            setPendingServiceRemoval(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.logRemoval.modal.removeServiceLogs')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.logRemoval.modal.removeQuestion', {
              service: pendingServiceRemoval
                ? getServiceDisplayName(pendingServiceRemoval.service)
                : undefined,
              datasource: pendingServiceRemoval?.datasource
            })}
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.logRemoval.modal.important')}:
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.logRemoval.modal.cannotUndo')}</li>
                <li>{t('management.logRemoval.modal.mayTakeMinutes')}</li>
                <li>
                  {t('management.logRemoval.modal.cachedFilesRemain', {
                    service: pendingServiceRemoval
                      ? getServiceDisplayName(pendingServiceRemoval.service)
                      : undefined
                  })}
                </li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingServiceRemoval(null)}
              disabled={anyServiceRemovalPending}
            >
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() =>
                pendingServiceRemoval &&
                executeRemoveServiceLogs(
                  pendingServiceRemoval.datasource,
                  pendingServiceRemoval.service
                )
              }
              loading={anyServiceRemovalPending}
            >
              {t('management.logRemoval.buttons.removeLogs')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Log File Confirmation Modal */}
      <Modal
        opened={pendingLogFileDeletion !== null}
        onClose={() => {
          if (!deletingLogFile) {
            setPendingLogFileDeletion(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Trash2 className="w-6 h-6 text-themed-error" />
            <span>{t('management.logRemoval.modal.deleteEntireLogFile')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.logRemoval.modal.deleteQuestion', {
              datasource: pendingLogFileDeletion
            })}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.logRemoval.modal.warningDestructive')}:
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.logRemoval.modal.permanentlyDelete')}</li>
                <li>{t('management.logRemoval.modal.historyLost')}</li>
                <li>{t('management.logRemoval.modal.cannotUndo')}</li>
                <li>{t('management.logRemoval.modal.cachedGamesRemain')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingLogFileDeletion(null)}
              disabled={!!deletingLogFile}
            >
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => pendingLogFileDeletion && executeDeleteLogFile(pendingLogFileDeletion)}
              loading={!!deletingLogFile}
            >
              {t('management.logRemoval.buttons.deleteLogFile')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Batch Remove Selected Confirmation Modal */}
      <Modal
        opened={showBatchConfirm}
        onClose={() => {
          if (!isBatchRunning) {
            setShowBatchConfirm(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.batchSelect.confirmTitle')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.batchSelect.confirmBody', { count: selection.count })}
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.logRemoval.modal.important')}:
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.logRemoval.modal.cannotUndo')}</li>
                <li>{t('management.logRemoval.modal.mayTakeMinutes')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowBatchConfirm(false)}
              disabled={isBatchRunning}
            >
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => {
                void runBatchRemoval();
              }}
              loading={isBatchRunning}
            >
              {t('management.batchSelect.removeSelected', { count: selection.count })}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default LogRemovalManager;
