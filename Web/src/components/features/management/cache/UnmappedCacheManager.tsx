import React, { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, CircleCheck, FileQuestion, RefreshCw, Search, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useNotifications } from '@contexts/notifications';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { shouldPinOperationIdFromResponse } from '@components/features/management/game-detection/gameRemovalEntity';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { UnmappedRemovalCompleteEvent } from '@contexts/SignalRContext/types';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useOptimisticPending } from '@/hooks/useOptimisticPending';
import { useSelectionSet } from '@/hooks/useSelectionSet';
import { useFormattedDateTime } from '@/hooks/useFormattedDateTime';
import { useManagerLoading } from '@/hooks/useManagerLoading';
import { useDiskObjectCapability } from '@hooks/useDiskObjectCapability';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSectionExpanded } from '@hooks/useSectionExpanded';
import { usePaginatedList } from '@hooks/usePaginatedList';
import CardDirectoryNotice from '@components/features/management/CardDirectoryNotice';
import { DiskObjectActionGate } from '@components/features/management/DiskObjectActionGate';
import { isCardDiskActionBlocked, resolveCardNotice } from '@utils/cardDirectoryNotice';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { formatBytes, formatCount } from '@utils/formatters';
import { getErrorMessage } from '@utils/error';
import { rowToggleHandlers } from '@utils/rowToggle';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpNote, HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { Alert } from '@components/ui/Alert';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionHeaderActions, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { ActionMenuDangerItem, ActionMenuDivider, ActionMenuItem } from '@components/ui/ActionMenu';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { Pagination } from '@components/ui/Pagination';
import { SearchInput } from '@components/ui/SearchInput';
import Badge from '@components/ui/Badge';
import CacheEntityList from '../game-detection/CacheEntityList';
import {
  UNMAPPED_CONTRACT_VERSION,
  type CachedUnmappedScanResponse,
  type UnmappedCacheFile,
  type UnmappedServiceRow
} from './unmappedCacheTypes';

interface UnmappedCacheManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

// One service can hold tens of thousands of orphans; cap what is mounted per page so an
// expanded row never renders the whole list, and give search and pagination something to
// do only once the list outgrows a single page.
const FILES_PER_PAGE = 25;

interface UnmappedFileListProps {
  files: UnmappedCacheFile[];
}

/**
 * The files under one expanded service row. A cache file is named by an md5 digest, so the
 * upstream address recovered from its own stored key is the only thing that says what it holds.
 */
const UnmappedFileList: React.FC<UnmappedFileListProps> = ({ files }) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');
  // Unique per instance: two services can be expanded across a re-render, so a fixed DOM
  // id would duplicate the label/input pair.
  const searchInputId = useId();

  const filteredFiles = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    if (!query) return files;
    return files.filter((file) => (file.url ?? file.path).toLowerCase().includes(query));
  }, [files, searchQuery]);

  const { page, setPage, totalPages, paginatedItems } = usePaginatedList<UnmappedCacheFile>({
    items: filteredFiles,
    pageSize: FILES_PER_PAGE,
    resetKey: searchQuery
  });

  const enableControls = files.length > FILES_PER_PAGE;
  const visibleFiles = filteredFiles.length > FILES_PER_PAGE ? paginatedItems : filteredFiles;

  return (
    <div className="space-y-3">
      {enableControls && (
        <div className="space-y-1">
          <label htmlFor={searchInputId} className="block text-xs text-themed-secondary">
            {t('management.unmapped.searchLabel')}
          </label>
          <SearchInput
            id={searchInputId}
            placeholder={t('management.unmapped.searchPlaceholder')}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            onClear={() => setSearchQuery('')}
          />
          <p className="text-xs text-themed-muted tabular-nums">
            {t('management.unmapped.searchResultCount', {
              count: filteredFiles.length,
              total: files.length
            })}
          </p>
        </div>
      )}

      {filteredFiles.length === 0 ? (
        <div className="py-6 text-center text-sm text-themed-muted space-y-3">
          <p>{t('management.unmapped.noSearchMatch', { query: searchQuery.trim() })}</p>
          <Button size="sm" onClick={() => setSearchQuery('')}>
            {t('management.unmapped.clearSearch')}
          </Button>
        </div>
      ) : (
        <>
          <CustomScrollbar maxHeight="24rem" radius="none" paddingMode="compact">
            <div className="mgmt-evidence-list divided-list">
              {visibleFiles.map((file) => (
                <div key={file.id} className="mgmt-evidence">
                  <div className="mgmt-evidence__head">
                    <div className="mgmt-evidence__ident">
                      {/* The stored key is unreadable on a truncated or headerless file, and
                          then its path on disk is the only thing left that identifies it. */}
                      <code className="mgmt-evidence__exact-value mgmt-evidence__url text-themed-primary">
                        {file.url ?? file.path}
                      </code>
                    </div>
                    <div className="mgmt-evidence__status">
                      <span className="mgmt-evidence__count">{formatBytes(file.sizeBytes)}</span>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </CustomScrollbar>
          {filteredFiles.length > FILES_PER_PAGE && (
            <Pagination
              currentPage={page}
              totalPages={totalPages}
              totalItems={filteredFiles.length}
              itemsPerPage={FILES_PER_PAGE}
              onPageChange={setPage}
              itemLabel={t('management.unmapped.fileLabel')}
              variant="compact"
              showCard={false}
            />
          )}
        </>
      )}
    </div>
  );
};

/**
 * Cache files on disk that no game or service detection run claims. The scan finds them,
 * names each one by the upstream address stored inside it, and groups them by service.
 */
const UnmappedCacheManager: React.FC<UnmappedCacheManagerProps> = ({
  authMode,
  mockMode,
  onError
}) => {
  const { t } = useTranslation();
  const { notifications, addNotification, isAnyRemovalRunning } = useNotifications();
  const { on, off, isConnected } = useSignalR();
  const { logsReadOnly, cacheReadOnly, logsExist, cacheExist, checkingPermissions } =
    useDirectoryPermissionsContext();
  // The scan maps every cache file back to its digest, so each enabled datasource needs one
  // resolved cache-key scheme. Disable the scan and the deletes with the backend's reason.
  const { available: diskObjectsAvailable, denialReason: diskObjectDenialReason } =
    useDiskObjectCapability();

  const isScanRunning = useOperationBusy({ types: ['unmapped_scan'], status: 'running' });
  const isScanWaiting = useOperationBusy({ types: ['unmapped_scan'], status: 'waiting' });
  const isRemovalActive = useOperationBusy({
    types: ['unmapped_removal'],
    status: ['running', 'waiting']
  });

  const [startingScan, setStartingScan] = useState(false);
  const scanRequestInFlightRef = useRef(false);
  const isScanning = isScanRunning || startingScan;
  const isScanBusy = isScanning || isScanWaiting;

  const [scanId, setScanId] = useState<string | null>(null);
  const [lastScanTime, setLastScanTime] = useState<string | null>(null);
  const [totalFiles, setTotalFiles] = useState(0);
  const [totalBytes, setTotalBytes] = useState(0);
  const [services, setServices] = useState<UnmappedServiceRow[]>([]);
  const [hasCachedResults, setHasCachedResults] = useState(false);
  const [filesByService, setFilesByService] = useState<Record<string, UnmappedCacheFile[]>>({});
  const [loadingFileServices, setLoadingFileServices] = useState<Set<string>>(new Set());
  const [fileErrors, setFileErrors] = useState<Set<string>>(new Set());
  const [loadFailed, setLoadFailed] = useState(false);
  const [pendingRemoveAll, setPendingRemoveAll] = useState(false);
  const [pendingRemoveSelected, setPendingRemoveSelected] = useState(false);
  // Bumped whenever the displayed result is replaced, so a response belonging to the
  // previous scan can never repaint the section after a newer one has landed.
  const resultEpochRef = useRef(0);

  const [sectionExpanded, setSectionExpanded] = useSectionExpanded(
    'management-unmapped-expanded',
    false
  );
  useAccordionGroupItem('storage-unmapped', sectionExpanded, () =>
    setSectionExpanded((expanded) => !expanded)
  );

  const { isLoading, isRefreshing, hasInitiallyLoaded, beginLoad, markLoaded, markFailed } =
    useManagerLoading();
  const selection = useSelectionSet<string>();
  const clearSelection = selection.clear;
  const {
    isPending: isRemovalPending,
    anyPending: anyRemovalPending,
    markStarting: markRemovalStarting,
    clearPending: clearRemovalPending,
    clearOnNotification: clearRemovalOnNotification
  } = useOptimisticPending<'removeAll' | 'removeSelected'>();

  const formattedLastScan = useFormattedDateTime(lastScanTime);

  const clearLoadedResults = useCallback(() => {
    resultEpochRef.current += 1;
    setScanId(null);
    setLastScanTime(null);
    setTotalFiles(0);
    setTotalBytes(0);
    setServices([]);
    setHasCachedResults(false);
    setFilesByService({});
    setLoadingFileServices(new Set());
    setFileErrors(new Set());
    setPendingRemoveAll(false);
    setPendingRemoveSelected(false);
    clearSelection();
  }, [clearSelection]);

  const applyCachedScan = useCallback(
    (cached: CachedUnmappedScanResponse) => {
      clearLoadedResults();
      if (!cached.hasCachedResults) return;

      // A tab left open across an app upgrade keeps talking to the new API. Refuse a
      // snapshot written to another contract rather than naming the wrong files deletable.
      if (cached.contractVersion !== UNMAPPED_CONTRACT_VERSION || !cached.scanId) {
        setLoadFailed(true);
        return;
      }

      setScanId(cached.scanId);
      setLastScanTime(cached.lastScanTime);
      setTotalFiles(cached.totalFiles);
      setTotalBytes(cached.totalBytes);
      setServices(cached.services);
      setHasCachedResults(true);
      setLoadFailed(false);
    },
    [clearLoadedResults]
  );

  const loadCachedScan = useCallback(
    async (showRefreshing = false) => {
      beginLoad(showRefreshing);
      setLoadFailed(false);
      const requestEpoch = resultEpochRef.current;
      try {
        const cached = await ApiService.getCachedUnmappedScan();
        if (requestEpoch !== resultEpochRef.current) {
          markLoaded();
          return;
        }
        applyCachedScan(cached);
        markLoaded();
      } catch {
        if (requestEpoch !== resultEpochRef.current) return;
        setLoadFailed(true);
        markFailed();
      }
    },
    [applyCachedScan, beginLoad, markFailed, markLoaded]
  );

  useEffect(() => {
    if (!hasInitiallyLoaded) void loadCachedScan();
  }, [hasInitiallyLoaded, loadCachedScan]);

  // A dropped socket can swallow the completion event of a scan or a removal that finished
  // while it was down; resync the saved snapshot on reconnect.
  useReconnectRefetch(isConnected, () => {
    void loadCachedScan();
  });

  useEffect(() => {
    // The backend keeps the previous snapshot when a scan fails or is cancelled, so every
    // terminal event reloads and what is displayed stays the authoritative saved scan.
    const handleScanComplete = () => {
      setStartingScan(false);
      scanRequestInFlightRef.current = false;
      void loadCachedScan(true);
    };

    on('UnmappedScanComplete', handleScanComplete);
    return () => off('UnmappedScanComplete', handleScanComplete);
  }, [loadCachedScan, off, on]);

  useEffect(() => {
    const handleRemovalComplete = (event: UnmappedRemovalCompleteEvent) => {
      if (!event.success) return;
      void loadCachedScan(true);
    };

    on('UnmappedRemovalComplete', handleRemovalComplete);
    return () => off('UnmappedRemovalComplete', handleRemovalComplete);
  }, [loadCachedScan, off, on]);

  useEffect(() => {
    if (!startingScan) return;
    const opened = notifications.some(
      (notification) =>
        notification.type === 'unmapped_scan' &&
        (notification.status === 'running' || notification.status === 'waiting')
    );
    if (opened) {
      setStartingScan(false);
      scanRequestInFlightRef.current = false;
    }
  }, [notifications, startingScan]);

  useEffect(() => {
    if (!anyRemovalPending) return;
    clearRemovalOnNotification(
      'removeAll',
      notifications,
      (notification) =>
        notification.type === 'unmapped_removal' && notification.status === 'running'
    );
    clearRemovalOnNotification(
      'removeSelected',
      notifications,
      (notification) =>
        notification.type === 'unmapped_removal' && notification.status === 'running'
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [notifications]);

  useEffect(() => {
    const validServices = new Set(services.map((row) => row.service));
    const stale = [...selection.selected].filter((service) => !validServices.has(service));
    if (stale.length > 0) selection.setMany(stale, false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [services]);

  const directoryNoticeConditions = {
    cacheWrite: true,
    cacheRead: false,
    logsWrite: false,
    nginx: false
  };
  const directoryNoticeLiveState = {
    cacheReadOnly,
    logsReadOnly,
    cacheExist,
    logsExist,
    checkingPermissions,
    nginxReopenGate: { available: true, messageKey: null }
  };
  const directoryNotice = resolveCardNotice(directoryNoticeConditions, directoryNoticeLiveState);
  const diskActionBlocked = isCardDiskActionBlocked(
    directoryNoticeConditions,
    directoryNoticeLiveState
  );

  const removalBusy = anyRemovalPending || isRemovalActive;
  const scanBlocked =
    isScanBusy ||
    isAnyRemovalRunning ||
    removalBusy ||
    checkingPermissions ||
    diskActionBlocked ||
    mockMode ||
    authMode !== 'authenticated';

  const selectedServices = services.filter((row) => selection.isSelected(row.service));
  const selectedFileTotal = selectedServices.reduce((total, row) => total + row.fileCount, 0);
  const selectedByteTotal = selectedServices.reduce((total, row) => total + row.totalBytes, 0);

  const removalBlocked =
    !scanId ||
    totalFiles === 0 ||
    isScanBusy ||
    removalBusy ||
    checkingPermissions ||
    diskActionBlocked ||
    mockMode ||
    authMode !== 'authenticated';

  const startScan = useCallback(async () => {
    if (scanBlocked || !diskObjectsAvailable || scanRequestInFlightRef.current) return;

    scanRequestInFlightRef.current = true;
    setStartingScan(true);
    resultEpochRef.current += 1;
    setLoadFailed(false);
    try {
      const result = await ApiService.startUnmappedScan();
      if (shouldPinOperationIdFromResponse(result)) {
        addNotification(
          buildSeededRunningNotification(
            'unmapped_scan',
            result.operationId,
            t('signalr.unmappedScan.starting')
          )
        );
      } else {
        setStartingScan(false);
        scanRequestInFlightRef.current = false;
      }
    } catch (error: unknown) {
      onError?.(
        t('management.unmapped.errors.startScan', {
          error: getErrorMessage(error) || t('common.unknownError')
        })
      );
      setStartingScan(false);
      scanRequestInFlightRef.current = false;
    }
  }, [addNotification, diskObjectsAvailable, onError, scanBlocked, t]);

  const loadFiles = useCallback(
    async (service: string) => {
      if (!scanId || loadingFileServices.has(service)) return;
      const requestEpoch = resultEpochRef.current;
      setFileErrors((current) => {
        const next = new Set(current);
        next.delete(service);
        return next;
      });
      setLoadingFileServices((current) => new Set(current).add(service));
      try {
        const files = await ApiService.getUnmappedCacheFiles(service, scanId);
        if (requestEpoch !== resultEpochRef.current) return;
        setFilesByService((current) => ({ ...current, [service]: files }));
      } catch {
        if (requestEpoch !== resultEpochRef.current) return;
        setFileErrors((current) => new Set(current).add(service));
      } finally {
        if (requestEpoch === resultEpochRef.current) {
          setLoadingFileServices((current) => {
            const next = new Set(current);
            next.delete(service);
            return next;
          });
        }
      }
    },
    [loadingFileServices, scanId]
  );

  const removeFiles = useCallback(
    async (key: 'removeAll' | 'removeSelected', scopedServices: string[]) => {
      if (removalBlocked || !scanId || scopedServices.length === 0) return;
      markRemovalStarting(key);
      try {
        await ApiService.removeUnmappedCacheFiles(scanId, scopedServices);
      } catch (error: unknown) {
        onError?.(
          t('management.unmapped.errors.removeFiles', {
            error: getErrorMessage(error) || t('common.unknownError')
          })
        );
        clearRemovalPending(key);
      }
    },
    [clearRemovalPending, markRemovalStarting, onError, removalBlocked, scanId, t]
  );

  const getServiceKey = useCallback((row: UnmappedServiceRow) => row.service, []);

  const filterAndSortServices = useCallback(
    (rows: UnmappedServiceRow[], query: string): UnmappedServiceRow[] => {
      const needle = query.trim().toLowerCase();
      const matched = needle
        ? rows.filter(
            (row) =>
              row.service.toLowerCase().includes(needle) ||
              getServiceDisplayName(row.service).toLowerCase().includes(needle)
          )
        : rows;
      return [...matched].sort((first, second) => second.fileCount - first.fileCount);
    },
    []
  );

  const selectionAdapter = useMemo(
    () => ({
      isSelected: selection.isSelected,
      onToggle: selection.toggle,
      allSelected: selection.allSelected,
      setMany: selection.setMany
    }),
    [selection.isSelected, selection.toggle, selection.allSelected, selection.setMany]
  );

  const headerActions = (
    <SectionHeaderActions>
      {/* totalFiles holds the STORED scan's count and is not cleared when a new scan starts, so
      while one runs this is the previous result. The "results from previous scan" caption that
      says so is hidden during a scan, which would leave a bare number reading as a live total. */}
      {totalFiles > 0 && !isScanBusy && (
        <SectionHeaderChip variant="neutral" className="badge-count badge-count-warning">
          {t('management.unmapped.fileCount', {
            count: totalFiles,
            formattedCount: formatCount(totalFiles)
          })}
        </SectionHeaderChip>
      )}
      {selectedServices.length > 0 && (
        <SectionHeaderChip variant="neutral" className="badge-count">
          {selectedServices.length}
        </SectionHeaderChip>
      )}
      <SectionActionsMenu label={t('management.actions.menuLabel')}>
        {(close) => (
          <>
            <DiskObjectActionGate
              available={diskObjectsAvailable}
              tooltip={diskObjectDenialReason ?? t('management.capability.diskObjectsUnavailable')}
              position="left"
              className="block w-full"
            >
              <ActionMenuItem
                icon={<Search className="w-3.5 h-3.5" />}
                disabled={scanBlocked || !diskObjectsAvailable}
                onClick={() => {
                  void startScan();
                  close();
                }}
              >
                {t('common.scan')}
              </ActionMenuItem>
            </DiskObjectActionGate>
            <ActionMenuItem
              icon={<RefreshCw className="w-3.5 h-3.5" />}
              disabled={isRefreshing || isScanBusy || removalBusy}
              onClick={() => {
                void loadCachedScan(true);
                close();
              }}
            >
              {t('common.load')}
            </ActionMenuItem>
            <ActionMenuDivider />
            <DiskObjectActionGate
              available={diskObjectsAvailable}
              tooltip={diskObjectDenialReason ?? t('management.capability.diskObjectsUnavailable')}
              position="left"
              className="block w-full"
            >
              <ActionMenuDangerItem
                icon={<Trash2 className="w-3.5 h-3.5" />}
                disabled={removalBlocked || selectedServices.length === 0 || !diskObjectsAvailable}
                onClick={() => {
                  setPendingRemoveSelected(true);
                  close();
                }}
              >
                {t('management.unmapped.removeSelected')}
              </ActionMenuDangerItem>
            </DiskObjectActionGate>
            <DiskObjectActionGate
              available={diskObjectsAvailable}
              tooltip={diskObjectDenialReason ?? t('management.capability.diskObjectsUnavailable')}
              position="left"
              className="block w-full"
            >
              <ActionMenuDangerItem
                icon={<Trash2 className="w-3.5 h-3.5" />}
                disabled={removalBlocked || !diskObjectsAvailable}
                onClick={() => {
                  setPendingRemoveAll(true);
                  close();
                }}
              >
                {isRemovalPending('removeAll')
                  ? t('management.unmapped.removing')
                  : t('management.unmapped.removeAll')}
              </ActionMenuDangerItem>
            </DiskObjectActionGate>
          </>
        )}
      </SectionActionsMenu>
    </SectionHeaderActions>
  );

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.unmapped.help.aboutTitle')}>
        {t('management.unmapped.help.about')}
      </HelpSection>
      <HelpSection title={t('management.unmapped.help.causesTitle')} variant="subtle">
        {t('management.unmapped.help.causes')}
      </HelpSection>
      <HelpNote type="warning">{t('management.unmapped.help.removalNote')}</HelpNote>
    </HelpPopover>
  );

  return (
    <>
      <AccordionSection
        title={t('management.unmapped.title')}
        shortTitle={t('management.unmapped.titleShort')}
        titleAccessory={helpAccessory}
        icon={FileQuestion}
        isExpanded={sectionExpanded}
        onToggle={() => setSectionExpanded((expanded) => !expanded)}
        badge={headerActions}
      >
        <div className="space-y-3">
          {hasCachedResults && lastScanTime && !isScanBusy && !isLoading && (
            <p className="mgmt-scanmeta">
              {t('common.resultsFromPreviousScan')} · {formattedLastScan} ·{' '}
              {t('management.unmapped.scanTotals', {
                files: formatCount(totalFiles),
                size: formatBytes(totalBytes)
              })}
            </p>
          )}

          {isScanning && (
            <LoadingState variant="spinner" message={t('management.unmapped.scanningMessage')} />
          )}

          {loadFailed && !isLoading && (
            <Alert color="red">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <p className="font-medium">{t('management.unmapped.errors.loadCachedScan')}</p>
                  <p className="text-sm mt-1">{t('management.unmapped.errors.loadCachedRetry')}</p>
                </div>
                <Button size="sm" onClick={() => void loadCachedScan()}>
                  {t('common.retry')}
                </Button>
              </div>
            </Alert>
          )}

          <CardDirectoryNotice notice={directoryNotice} />

          {isLoading && !isScanning ? (
            <div role="status" aria-live="polite" aria-busy="true">
              <span className="sr-only">{t('management.unmapped.loadingCachedScan')}</span>
              <div className="space-y-3" aria-hidden="true">
                <div className="skeleton-shimmer rounded h-5 w-28" />
                <div className="mgmt-list divided-list">
                  {Array.from({ length: 4 }).map((_, index) => (
                    <div key={index} className="mgmt-row flex-wrap">
                      <div className="skeleton-shimmer rounded h-5 w-5 flex-shrink-0" />
                      <div className="mgmt-row__body">
                        <div
                          className={`skeleton-shimmer rounded h-3.5 ${index % 2 === 0 ? 'w-2/5' : 'w-1/2'}`}
                        />
                      </div>
                      <div className="mgmt-row__actions mgmt-unmapped-actions flex-wrap justify-end">
                        <div className="skeleton-shimmer rounded-full h-5 w-24" />
                        <div className="skeleton-shimmer rounded-full h-5 w-16" />
                        <div className="skeleton-shimmer rounded h-8 w-8" />
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          ) : isScanning ? null : hasCachedResults && services.length > 0 ? (
            <CacheEntityList<UnmappedServiceRow>
              items={services}
              searchPlaceholder={t('management.unmapped.serviceSearchPlaceholder')}
              getEmptyMessage={(query) => t('management.unmapped.noServiceMatch', { query })}
              itemLabel={t('management.unmapped.serviceLabel')}
              getItemKey={getServiceKey}
              filterAndSortItems={filterAndSortServices}
              selection={selectionAdapter}
              renderItem={(row, state) => {
                const files = filesByService[row.service];
                const openDetails = () => {
                  state.onToggleDetails(row.service);
                  if (!state.isExpanded && !files) void loadFiles(row.service);
                };
                return (
                  <div>
                    <div
                      className="mgmt-row mgmt-row--interactive focus-ring--inset flex-wrap cursor-pointer"
                      {...rowToggleHandlers(openDetails)}
                    >
                      <Checkbox
                        checked={state.selected}
                        onChange={state.onSelectToggle}
                        disabled={removalBlocked}
                        aria-label={t('management.batchSelect.selectItem', {
                          name: getServiceDisplayName(row.service)
                        })}
                        className="flex-shrink-0"
                      />
                      <div className="mgmt-row__body">
                        <p className="mgmt-row__title mgmt-row__title--service truncate">
                          {getServiceDisplayName(row.service)}
                        </p>
                      </div>
                      <div className="mgmt-row__actions mgmt-unmapped-actions flex-wrap justify-end">
                        <Badge
                          variant="neutral"
                          className="badge-count badge-count-warning tabular-nums"
                        >
                          {t('management.unmapped.fileCount', {
                            count: row.fileCount,
                            formattedCount: formatCount(row.fileCount)
                          })}
                        </Badge>
                        <Badge variant="neutral" className="badge-count tabular-nums">
                          {formatBytes(row.totalBytes)}
                        </Badge>
                        {/* Ends the row: the expander is the quietest control here, and
                            sitting between the two counts it split a pair that belong
                            together. */}
                        <Button
                          type="button"
                          variant="accordion"
                          size="sm"
                          open={state.isExpanded}
                          className="btn-icon-square btn-icon-square--sm pointer-target-44 flex-shrink-0"
                          onClick={openDetails}
                          aria-label={
                            state.isExpanded
                              ? t('management.unmapped.collapseDetails', {
                                  service: getServiceDisplayName(row.service)
                                })
                              : t('management.unmapped.expandDetails', {
                                  service: getServiceDisplayName(row.service)
                                })
                          }
                          aria-expanded={state.isExpanded}
                        >
                          <ChevronDown
                            className={`w-4 h-4 transition duration-200 ease-out${
                              state.isExpanded
                                ? ' rotate-180 text-themed-accent'
                                : ' rotate-0 text-themed-muted'
                            }`}
                          />
                        </Button>
                      </div>
                    </div>
                    <CollapsibleRegion open={state.isExpanded} contentClassName="mgmt-row-detail">
                      {loadingFileServices.has(row.service) ? (
                        <LoadingState
                          variant="spinner"
                          message={t('management.unmapped.loadingFiles')}
                        />
                      ) : fileErrors.has(row.service) ? (
                        <Alert color="red">
                          <div className="flex flex-wrap items-center justify-between gap-3">
                            <p className="text-sm">
                              {t('management.unmapped.errors.loadFiles', {
                                service: getServiceDisplayName(row.service)
                              })}
                            </p>
                            <Button size="sm" onClick={() => void loadFiles(row.service)}>
                              {t('common.retry')}
                            </Button>
                          </div>
                        </Alert>
                      ) : files && files.length > 0 ? (
                        <UnmappedFileList files={files} />
                      ) : (
                        <EmptyState
                          variant="text"
                          title={t('management.unmapped.noFilesAvailable')}
                        />
                      )}
                    </CollapsibleRegion>
                  </div>
                );
              }}
            />
          ) : hasCachedResults ? (
            <EmptyState
              icon={CircleCheck}
              title={t('management.unmapped.emptyStates.nothingUnmapped.title')}
              subtitle={t('management.unmapped.emptyStates.nothingUnmapped.subtitle')}
            />
          ) : !isScanBusy && !loadFailed ? (
            <EmptyState
              icon={Search}
              title={t('management.unmapped.emptyStates.noScan.title')}
              subtitle={t('management.unmapped.emptyStates.noScan.subtitle')}
            />
          ) : null}
        </div>
      </AccordionSection>

      <ConfirmationModal
        opened={pendingRemoveAll}
        onClose={() => setPendingRemoveAll(false)}
        onConfirm={() => {
          setPendingRemoveAll(false);
          void removeFiles(
            'removeAll',
            services.map((row) => row.service)
          );
        }}
        title={t('management.unmapped.modal.removeAllTitle')}
        confirmLabel={t('management.unmapped.modal.removeAllConfirm')}
        confirmDisabled={removalBlocked}
      >
        <p className="text-themed-secondary">
          {t('management.unmapped.modal.confirmRemoveAll', {
            files: formatCount(totalFiles),
            size: formatBytes(totalBytes)
          })}
        </p>
        <Alert color="red">
          <p className="text-sm">{t('management.unmapped.modal.removalWarning')}</p>
        </Alert>
      </ConfirmationModal>

      <ConfirmationModal
        opened={pendingRemoveSelected}
        onClose={() => setPendingRemoveSelected(false)}
        onConfirm={() => {
          setPendingRemoveSelected(false);
          void removeFiles(
            'removeSelected',
            selectedServices.map((row) => row.service)
          );
        }}
        title={t('management.unmapped.modal.removeSelectedTitle')}
        confirmLabel={t('management.unmapped.modal.removeSelectedConfirm', {
          count: selectedServices.length
        })}
        confirmDisabled={removalBlocked || selectedServices.length === 0}
      >
        <p className="text-themed-secondary">
          {t('management.unmapped.modal.confirmRemoveSelected', {
            count: selectedServices.length,
            files: formatCount(selectedFileTotal),
            size: formatBytes(selectedByteTotal)
          })}
        </p>
        <Alert color="red">
          <p className="text-sm">{t('management.unmapped.modal.removalWarning')}</p>
        </Alert>
      </ConfirmationModal>
    </>
  );
};

export default UnmappedCacheManager;
