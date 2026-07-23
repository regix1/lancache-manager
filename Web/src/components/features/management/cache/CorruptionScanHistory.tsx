import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp, CircleCheck, History } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { useFormattedDateTime } from '@/hooks/useFormattedDateTime';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { formatCount } from '@utils/formatters';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import Badge from '@components/ui/Badge';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import CorruptionChunkList from './CorruptionChunkList';
import { projectCorruptionCounts } from './corruptionCountProjection';
import {
  validateCorruptionHistoryDetails,
  validateCorruptionScanHistory
} from './corruptionHistoryValidation';
import type {
  CorruptedChunkDetail,
  CorruptionDetectionMethod,
  CorruptionScanHistoryEntry
} from '@/types';

interface CorruptionScanHistoryProps {
  authMode: AuthMode;
  mockMode: boolean;
  /** Bump to refetch the list (scan/removal terminal events). */
  refreshKey: number;
  /** True while corruption scan/removal work is active; deletion stays disabled. */
  deletionLocked: boolean;
  onCurrentSnapshotDeleted: (scanId: string, method: CorruptionDetectionMethod) => void;
}

const methodLabelKey = (method: CorruptionDetectionMethod) =>
  method === 'structural'
    ? 'management.corruption.methods.structural.label'
    : 'management.corruption.methods.repeatedMiss.label';

const compareNewestFirst = (a: CorruptionScanHistoryEntry, b: CorruptionScanHistoryEntry) =>
  Date.parse(b.completedAtUtc) - Date.parse(a.completedAtUtc) || b.scanId.localeCompare(a.scanId);

interface HistoryRowProps {
  entry: CorruptionScanHistoryEntry;
  deleteDisabled: boolean;
  deleting: boolean;
  onView: () => void;
  onDelete: () => void;
}

const HistoryRow: React.FC<HistoryRowProps> = ({
  entry,
  deleteDisabled,
  deleting,
  onView,
  onDelete
}) => {
  const { t } = useTranslation();
  const formattedDate = useFormattedDateTime(entry.completedAtUtc, true);

  const metaParts: string[] = [];
  if (entry.detectionMethod === 'repeated_miss') {
    if (typeof entry.settings.threshold === 'number') {
      metaParts.push(
        t('management.corruption.history.thresholdMeta', { threshold: entry.settings.threshold })
      );
    }
    if (typeof entry.settings.lookbackDays === 'number') {
      metaParts.push(
        t('management.corruption.evidenceWindow', { days: entry.settings.lookbackDays })
      );
    }
  } else if (entry.scanMode) {
    metaParts.push(
      t(
        entry.scanMode === 'incremental'
          ? 'management.corruption.incrementalScan'
          : 'management.corruption.fullScan'
      )
    );
  }
  metaParts.push(
    t('management.corruption.history.serviceCount', { count: entry.totalServicesWithCorruption })
  );
  metaParts.push(
    t('management.corruption.flaggedCount', {
      count: entry.totalCorruptedChunks,
      formattedCount: formatCount(entry.totalCorruptedChunks)
    })
  );

  return (
    <div className="mgmt-row flex-wrap">
      <div className="mgmt-row__body">
        <p className="mgmt-row__title tabular-nums">{formattedDate}</p>
        <p className="mgmt-row__meta">{metaParts.join(' · ')}</p>
      </div>
      <div className="mgmt-row__actions mgmt-corruption-actions flex-wrap justify-end">
        <Badge variant={entry.isCurrent ? 'info' : 'neutral'}>
          {t(
            entry.isCurrent
              ? 'management.corruption.history.currentBadge'
              : 'management.corruption.history.historicalBadge'
          )}
        </Badge>
        <Button variant="filled" color="gray" size="xs" onClick={onView}>
          {t('management.corruption.history.view')}
        </Button>
        <Button
          variant="filled"
          color="red"
          size="xs"
          stableWidth
          loading={deleting}
          disabled={deleteDisabled}
          onClick={onDelete}
        >
          {t('management.corruption.history.deleteSavedScan')}
        </Button>
      </div>
    </div>
  );
};

interface HistoryEntrySummaryProps {
  entry: CorruptionScanHistoryEntry;
  withViewOnlyBadge?: boolean;
}

const HistoryEntrySummary: React.FC<HistoryEntrySummaryProps> = ({
  entry,
  withViewOnlyBadge = false
}) => {
  const { t } = useTranslation();
  const formattedDate = useFormattedDateTime(entry.completedAtUtc, true);
  return (
    <div className="flex flex-wrap items-center gap-2">
      <p className="mgmt-scanmeta">
        {t(methodLabelKey(entry.detectionMethod))} · {formattedDate} ·{' '}
        {t('management.corruption.history.serviceCount', {
          count: entry.totalServicesWithCorruption
        })}{' '}
        ·{' '}
        {t('management.corruption.flaggedCount', {
          count: entry.totalCorruptedChunks,
          formattedCount: formatCount(entry.totalCorruptedChunks)
        })}
      </p>
      {withViewOnlyBadge && (
        <Badge variant="neutral">{t('management.corruption.history.viewOnlyBadge')}</Badge>
      )}
    </div>
  );
};

const CorruptionScanHistory: React.FC<CorruptionScanHistoryProps> = ({
  authMode,
  mockMode,
  refreshKey,
  deletionLocked,
  onCurrentSnapshotDeleted
}) => {
  const { t } = useTranslation();
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();

  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('storage-corruption-history', expanded, () =>
    setExpanded((current) => !current)
  );
  const [entries, setEntries] = useState<CorruptionScanHistoryEntry[] | null>(null);
  const [listLoading, setListLoading] = useState(false);
  const [listError, setListError] = useState(false);
  const listRequestSeqRef = useRef(0);

  const [viewEntry, setViewEntry] = useState<CorruptionScanHistoryEntry | null>(null);
  const [expandedDetailService, setExpandedDetailService] = useState<string | null>(null);
  const [detailChunks, setDetailChunks] = useState<Record<string, CorruptedChunkDetail[]>>({});
  const [detailLoadingService, setDetailLoadingService] = useState<string | null>(null);
  const [detailErrorServices, setDetailErrorServices] = useState<Set<string>>(new Set());
  const detailRequestSeqRef = useRef(0);

  const [pendingDelete, setPendingDelete] = useState<CorruptionScanHistoryEntry | null>(null);
  const [deletingScanId, setDeletingScanId] = useState<string | null>(null);

  const loadHistory = useCallback(async () => {
    const seq = ++listRequestSeqRef.current;
    setListLoading(true);
    setListError(false);
    try {
      const response = await ApiService.getCorruptionScanHistory();
      if (seq !== listRequestSeqRef.current) return;
      const validated = validateCorruptionScanHistory(response);
      if (!validated) {
        setEntries(null);
        setListError(true);
        notifyError(t('management.corruption.history.loadError'), undefined, {
          silent: true,
          logLabel: '[CorruptionScanHistory] History response failed contract validation'
        });
        return;
      }
      setEntries(validated);
    } catch (error: unknown) {
      if (seq !== listRequestSeqRef.current) return;
      setEntries(null);
      setListError(true);
      notifyError(t('management.corruption.history.loadError'), error, {
        silent: true,
        logLabel: '[CorruptionScanHistory] Failed to load scan history'
      });
    } finally {
      if (seq === listRequestSeqRef.current) setListLoading(false);
    }
  }, [notifyError, t]);

  useEffect(() => {
    void loadHistory();
  }, [loadHistory, refreshKey]);

  const grouped = useMemo(() => {
    const byMethod = (method: CorruptionDetectionMethod) =>
      (entries ?? []).filter((entry) => entry.detectionMethod === method).sort(compareNewestFirst);
    return { repeatedMiss: byMethod('repeated_miss'), structural: byMethod('structural') };
  }, [entries]);

  const viewProjection = useMemo(
    () => projectCorruptionCounts(viewEntry?.corruptionCounts ?? {}),
    [viewEntry]
  );

  const openView = (entry: CorruptionScanHistoryEntry) => {
    detailRequestSeqRef.current += 1;
    setViewEntry(entry);
    setExpandedDetailService(null);
    setDetailChunks({});
    setDetailErrorServices(new Set());
    setDetailLoadingService(null);
  };

  const closeView = () => {
    detailRequestSeqRef.current += 1;
    setViewEntry(null);
    setExpandedDetailService(null);
    setDetailChunks({});
    setDetailErrorServices(new Set());
    setDetailLoadingService(null);
  };

  const loadHistoryDetails = useCallback(
    async (entry: CorruptionScanHistoryEntry, service: string) => {
      const seq = ++detailRequestSeqRef.current;
      setDetailErrorServices((current) => {
        const next = new Set(current);
        next.delete(service);
        return next;
      });
      setDetailLoadingService(service);
      try {
        const details = await ApiService.getCorruptionHistoryDetails(entry.scanId, service);
        if (seq !== detailRequestSeqRef.current) return;
        const validated = validateCorruptionHistoryDetails(details, entry.detectionMethod);
        if (!validated || validated.length === 0) {
          setDetailErrorServices((current) => new Set(current).add(service));
          notifyError(t('management.corruption.errors.unsafeDetails'), undefined, {
            silent: true,
            logLabel: '[CorruptionScanHistory] History detail response failed validation'
          });
          return;
        }
        setDetailChunks((current) => ({ ...current, [service]: validated }));
      } catch (error: unknown) {
        if (seq !== detailRequestSeqRef.current) return;
        setDetailErrorServices((current) => new Set(current).add(service));
        notifyError(
          t('management.corruption.errors.loadDetails', {
            service: getServiceDisplayName(service)
          }),
          error,
          { silent: true, logLabel: '[CorruptionScanHistory] Failed to load history details' }
        );
      } finally {
        if (seq === detailRequestSeqRef.current) setDetailLoadingService(null);
      }
    },
    [notifyError, t]
  );

  const toggleDetailService = (service: string) => {
    if (!viewEntry) return;
    if (expandedDetailService === service) {
      setExpandedDetailService(null);
      return;
    }
    setExpandedDetailService(service);
    if (!detailChunks[service]) void loadHistoryDetails(viewEntry, service);
  };

  // History deletion is a database-only mutation: it needs full auth but never
  // cache/log write permissions. It stays locked while scan/removal work runs so
  // it cannot race an active operation's scan identity.
  const deleteBlocked = mockMode || authMode !== 'authenticated' || deletionLocked;
  const deleteDisabled = deleteBlocked || deletingScanId !== null;

  const confirmDelete = async () => {
    if (!pendingDelete || deletingScanId !== null) return;
    const entry = pendingDelete;
    setDeletingScanId(entry.scanId);
    try {
      await ApiService.deleteCorruptionScanHistory(entry.scanId);
      setPendingDelete(null);
      setEntries((current) =>
        current ? current.filter((candidate) => candidate.scanId !== entry.scanId) : current
      );
      if (viewEntry?.scanId === entry.scanId) closeView();
      addNotification({
        type: 'generic',
        status: 'completed',
        message: t('management.corruption.history.deleteSuccess'),
        details: { notificationType: 'success' }
      });
      if (entry.isCurrent) onCurrentSnapshotDeleted(entry.scanId, entry.detectionMethod);
    } catch (error: unknown) {
      // Keep the confirmation open with the row intact so the user can retry.
      notifyError(t('management.corruption.history.deleteError'), error, {
        logLabel: '[CorruptionScanHistory] Failed to delete saved scan'
      });
    } finally {
      setDeletingScanId(null);
    }
  };

  const renderGroup = (labelKey: string, groupEntries: CorruptionScanHistoryEntry[]) => (
    <div className="space-y-2">
      <p className="mgmt-subhead caps-label">{t(labelKey)}</p>
      {groupEntries.length === 0 ? (
        <p className="mgmt-scanmeta">{t('management.corruption.history.methodEmpty')}</p>
      ) : (
        <div className="mgmt-list divided-list">
          {groupEntries.map((entry) => (
            <HistoryRow
              key={entry.scanId}
              entry={entry}
              deleteDisabled={deleteDisabled}
              deleting={deletingScanId === entry.scanId}
              onView={() => openView(entry)}
              onDelete={() => setPendingDelete(entry)}
            />
          ))}
        </div>
      )}
    </div>
  );

  return (
    <>
      <AccordionSection
        title={t('management.corruption.history.title')}
        description={t('management.corruption.history.description')}
        icon={History}
        count={entries?.length}
        isExpanded={expanded}
        onToggle={() => setExpanded((current) => !current)}
        surface="well"
      >
        {listLoading && entries === null && !listError ? (
          <LoadingState message={t('management.corruption.history.loading')} />
        ) : listError ? (
          <Alert color="red">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <p className="font-medium">{t('management.corruption.history.loadError')}</p>
                <p className="text-sm mt-1">{t('management.corruption.history.loadErrorRetry')}</p>
              </div>
              <Button size="sm" onClick={() => void loadHistory()}>
                {t('common.retry')}
              </Button>
            </div>
          </Alert>
        ) : entries !== null && entries.length === 0 ? (
          <p className="py-4 text-center text-sm text-themed-muted">
            {t('management.corruption.history.empty')}
          </p>
        ) : entries !== null ? (
          <div className="space-y-4">
            {renderGroup('management.corruption.methods.repeatedMiss.label', grouped.repeatedMiss)}
            {renderGroup('management.corruption.methods.structural.label', grouped.structural)}
          </div>
        ) : null}
      </AccordionSection>

      <Modal
        opened={viewEntry !== null}
        onClose={closeView}
        size="xl"
        title={t('management.corruption.history.detailTitle')}
      >
        {viewEntry && (
          <div className="space-y-4">
            <HistoryEntrySummary entry={viewEntry} withViewOnlyBadge />
            <Alert color="blue">
              <p className="text-sm">{t('management.corruption.history.viewOnlyNotice')}</p>
            </Alert>
            {viewProjection.rows.length === 0 ? (
              <EmptyState
                icon={CircleCheck}
                title={t('management.corruption.history.zeroResultTitle')}
                subtitle={t('management.corruption.history.zeroResultSubtitle')}
              />
            ) : (
              <div className="mgmt-list divided-list">
                {viewProjection.rows.map(({ service, count }) => {
                  const isDetailExpanded = expandedDetailService === service;
                  return (
                    <div key={`history-${service}`}>
                      <div className="mgmt-row flex-wrap">
                        <div className="mgmt-row__body">
                          <p className="mgmt-row__title mgmt-row__title--service truncate">
                            {getServiceDisplayName(service)}
                          </p>
                        </div>
                        <div className="mgmt-row__actions mgmt-corruption-actions flex-wrap justify-end">
                          <Badge variant="neutral" className="badge-count badge-count-warning">
                            {t('management.corruption.flaggedCount', {
                              count,
                              formattedCount: formatCount(count)
                            })}
                          </Badge>
                          <Button
                            variant="filled"
                            color="gray"
                            size="xs"
                            className="mgmt-row__toggle"
                            onClick={() => toggleDetailService(service)}
                            aria-label={
                              isDetailExpanded
                                ? t('management.corruption.collapseDetails', {
                                    service: getServiceDisplayName(service)
                                  })
                                : t('management.corruption.expandDetails', {
                                    service: getServiceDisplayName(service)
                                  })
                            }
                            aria-expanded={isDetailExpanded}
                          >
                            {isDetailExpanded ? (
                              <ChevronUp className="w-4 h-4" />
                            ) : (
                              <ChevronDown className="w-4 h-4" />
                            )}
                          </Button>
                        </div>
                      </div>
                      <CollapsibleRegion open={isDetailExpanded} contentClassName="mgmt-row-detail">
                        {detailLoadingService === service ? (
                          <LoadingState message={t('management.corruption.loadingDetails')} />
                        ) : detailErrorServices.has(service) ? (
                          <Alert color="red">
                            <div className="flex flex-wrap items-center justify-between gap-3">
                              <p className="text-sm">
                                {t('management.corruption.errors.loadDetails', {
                                  service: getServiceDisplayName(service)
                                })}
                              </p>
                              <Button
                                size="sm"
                                onClick={() => void loadHistoryDetails(viewEntry, service)}
                              >
                                {t('common.retry')}
                              </Button>
                            </div>
                          </Alert>
                        ) : detailChunks[service]?.length > 0 ? (
                          <CorruptionChunkList chunks={detailChunks[service]} />
                        ) : (
                          <p className="py-4 text-center text-sm text-themed-muted">
                            {t('management.corruption.noDetailsAvailable')}
                          </p>
                        )}
                      </CollapsibleRegion>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        )}
      </Modal>

      <ConfirmationModal
        opened={pendingDelete !== null}
        onClose={() => setPendingDelete(null)}
        onConfirm={() => void confirmDelete()}
        title={t('management.corruption.history.deleteTitle')}
        confirmLabel={t('management.corruption.history.deleteSavedScan')}
        confirmColor="red"
        loading={deletingScanId !== null}
        confirmDisabled={deleteBlocked}
      >
        {pendingDelete && (
          <div className="space-y-3 text-themed-secondary">
            <p>
              {t('management.corruption.history.deleteConfirmBody', {
                method: t(methodLabelKey(pendingDelete.detectionMethod))
              })}
            </p>
            <HistoryEntrySummary entry={pendingDelete} />
            <p>{t('management.corruption.history.deleteScope')}</p>
            {pendingDelete.isCurrent && (
              <p>{t('management.corruption.history.deleteCurrentNote')}</p>
            )}
          </div>
        )}
      </ConfirmationModal>
    </>
  );
};

export default CorruptionScanHistory;
