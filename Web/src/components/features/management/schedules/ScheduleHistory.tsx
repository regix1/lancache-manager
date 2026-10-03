import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { Pagination } from '@components/ui/Pagination';
import { SearchInput } from '@components/ui/SearchInput';
import { FormattedTimestamp } from '@components/common/FormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY } from '@contexts/notifications/constants';
import { NOTIFICATION_TITLE_KEYS } from '@contexts/notifications/notificationTitleKeys';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import ApiService from '@services/api.service';
import { getErrorMessage, isAbortError } from '@utils/error';
import { formatCount, formatWarningCounts } from '@utils/formatters';
import { rowToggleHandlers } from '@utils/rowToggle';
import { formatServiceLabel } from '@utils/serviceDisplayName';
import { VARIANT_BY_STATUS } from '@utils/statusVariant';
import { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } from './scheduled-prefill/constants';
import {
  SCHEDULE_HISTORY_PAGE_SIZE,
  type ScheduleExecutionResponse,
  type ScheduleHistoryQuery,
  type ServiceScheduleInfo
} from './types';
import '../managementSectionContent.css';

export function ScheduleHistory() {
  const { t, i18n } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const initialQuery: ScheduleHistoryQuery = {
    page: 1,
    pageSize: SCHEDULE_HISTORY_PAGE_SIZE,
    search: '',
    serviceKey: '',
    status: ''
  };
  const [sectionOpen, setSectionOpen] = useState(false);
  const [confirmed, setConfirmed] = useState<ScheduleExecutionResponse | null>(null);
  const [confirmedQuery, setConfirmedQuery] = useState<ScheduleHistoryQuery | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expandedIds, setExpandedIds] = useState<Set<number>>(() => new Set());
  const [query, setQuery] = useState<ScheduleHistoryQuery>(initialQuery);
  const mountedRef = useRef(false);
  const pageRef = useRef<ScheduleHistoryQuery>(initialQuery);
  const requestRef = useRef({
    running: false,
    pending: false,
    owner: 0,
    controller: null as AbortController | null
  });

  const fetchHistory = useCallback(async (): Promise<void> => {
    const request = requestRef.current;
    if (request.running) {
      request.pending = true;
      request.owner++;
      request.controller?.abort();
      return;
    }

    request.running = true;
    try {
      do {
        request.pending = false;
        const owner = ++request.owner;
        const requestedPage = pageRef.current;
        const controller = new AbortController();
        request.controller = controller;

        try {
          const next = await ApiService.getScheduleHistory(requestedPage, controller.signal);
          if (mountedRef.current && owner === request.owner) {
            const acceptedQuery =
              next.page === requestedPage.page
                ? requestedPage
                : { ...requestedPage, page: next.page };
            const itemIds = new Set(next.items.map((item) => item.id));
            pageRef.current = acceptedQuery;
            setQuery(acceptedQuery);
            setConfirmed(next);
            setConfirmedQuery(acceptedQuery);
            setExpandedIds((current) => {
              const kept = new Set([...current].filter((id) => itemIds.has(id)));
              return kept.size === current.size ? current : kept;
            });
            setError(null);
            setLoaded(true);
          }
        } catch (requestError: unknown) {
          if (mountedRef.current && owner === request.owner && !isAbortError(requestError)) {
            setError(getErrorMessage(requestError));
            setLoaded(true);
          }
        } finally {
          if (owner === request.owner) {
            request.controller = null;
          }
        }
      } while (request.pending && mountedRef.current);
    } finally {
      request.running = false;
    }
  }, []);

  useEffect(() => {
    const request = requestRef.current;
    mountedRef.current = true;
    void fetchHistory();
    return () => {
      mountedRef.current = false;
      request.pending = false;
      request.controller?.abort();
    };
  }, [fetchHistory]);

  useEffect(() => {
    const handleSchedulesUpdated = (_schedules: ServiceScheduleInfo[]) => {
      void fetchHistory();
    };
    on('SchedulesUpdated', handleSchedulesUpdated);
    return () => off('SchedulesUpdated', handleSchedulesUpdated);
  }, [fetchHistory, off, on]);

  useReconnectRefetch(isConnected, fetchHistory);

  const toggleRow = (id: number): void => {
    setExpandedIds((current) => {
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const changeQuery = useCallback(
    (nextQuery: ScheduleHistoryQuery): void => {
      const current = pageRef.current;
      if (
        nextQuery.page === current.page &&
        nextQuery.pageSize === current.pageSize &&
        nextQuery.search === current.search &&
        nextQuery.serviceKey === current.serviceKey &&
        nextQuery.status === current.status
      ) {
        return;
      }

      pageRef.current = nextQuery;
      setQuery(nextQuery);
      setExpandedIds(new Set());
      setError(null);
      requestRef.current.controller?.abort();
      void fetchHistory();
    },
    [fetchHistory]
  );

  const changePage = useCallback(
    (nextPage: number): void => {
      if (nextPage === pageRef.current.page) return;
      changeQuery({ ...pageRef.current, page: nextPage });
    },
    [changeQuery]
  );

  const getServiceLabel = (serviceKey: string): string => {
    const displayNameKey = `management.schedules.services.${serviceKey}.displayName`;
    if (i18n.exists(displayNameKey)) {
      return t(displayNameKey);
    }

    const scheduledNotification = Object.entries(SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY).find(
      ([, scheduledServiceKey]) => scheduledServiceKey === serviceKey
    );
    const titleKey =
      scheduledNotification === undefined
        ? null
        : NOTIFICATION_TITLE_KEYS[scheduledNotification[0] as keyof typeof NOTIFICATION_TITLE_KEYS];
    if (typeof titleKey === 'string' && i18n.exists(titleKey)) {
      return t(titleKey);
    }

    return formatServiceLabel(serviceKey);
  };

  const serviceOptions = [
    { value: '', label: t('downloads.tab.filters.allServices') },
    ...Array.from(
      new Set(
        Object.values(SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY).filter(
          (serviceKey): serviceKey is string => typeof serviceKey === 'string'
        )
      )
    ).map((serviceKey) => ({
      value: serviceKey,
      label: getServiceLabel(serviceKey)
    }))
  ];
  const statusOptions = [
    { value: '', label: t('management.prefillSessions.statusFilters.all') },
    { value: 'completed', label: t('management.schedules.history.status.completed') },
    { value: 'failed', label: t('management.schedules.history.status.failed') },
    { value: 'cancelled', label: t('management.schedules.history.status.cancelled') },
    { value: 'skipped', label: t('management.schedules.history.status.skipped') }
  ];
  const pageSizeOptions = ([20, 50, 100] as const).map((pageSize) => ({
    value: pageSize.toString(),
    label: pageSize.toString()
  }));
  const confirmedFiltered =
    confirmedQuery !== null &&
    (confirmedQuery.search.trim().length > 0 ||
      confirmedQuery.serviceKey.length > 0 ||
      confirmedQuery.status.length > 0);

  return (
    <AccordionSection
      title={t('management.schedules.history.title')}
      count={confirmed?.totalCount ?? 0}
      isExpanded={sectionOpen}
      onToggle={() => setSectionOpen((open) => !open)}
    >
      <div className="schedule-history-content">
        <p className="schedule-history-note">{t('management.schedules.history.waitingNote')}</p>

        <div className="schedule-history-controls">
          <div className="schedule-history-search-row">
            <SearchInput
              size="md"
              value={query.search}
              placeholder={t('management.schedules.history.searchPlaceholder')}
              aria-label={t('common.search')}
              onChange={(event) =>
                changeQuery({ ...pageRef.current, page: 1, search: event.target.value })
              }
              onClear={() => changeQuery({ ...pageRef.current, page: 1, search: '' })}
            />
          </div>
          <div className="schedule-history-filter-row">
            <EnhancedDropdown
              options={serviceOptions}
              value={query.serviceKey}
              onChange={(serviceKey) => changeQuery({ ...pageRef.current, page: 1, serviceKey })}
              className="schedule-history-filter schedule-history-filter--service"
              prefix={t('downloads.tab.filters.showPrefix')}
              variant="button"
              size="md"
            />
            <EnhancedDropdown
              options={statusOptions}
              value={query.status}
              onChange={(status) => {
                if (
                  status === '' ||
                  status === 'completed' ||
                  status === 'failed' ||
                  status === 'cancelled' ||
                  status === 'skipped'
                ) {
                  changeQuery({ ...pageRef.current, page: 1, status });
                }
              }}
              className="schedule-history-filter schedule-history-filter--status"
              prefix={t('downloads.tab.filters.showPrefix')}
              variant="button"
              size="md"
            />
            <EnhancedDropdown
              options={pageSizeOptions}
              value={query.pageSize.toString()}
              onChange={(value) => {
                const pageSize = Number(value);
                if (pageSize === 20 || pageSize === 50 || pageSize === 100) {
                  changeQuery({ ...pageRef.current, page: 1, pageSize });
                }
              }}
              className="schedule-history-filter schedule-history-filter--size"
              prefix={t('management.schedules.history.pageSize')}
              variant="button"
              size="md"
            />
          </div>
        </div>

        {error && (
          <ErrorBlock
            title={t('management.schedules.history.fetchError')}
            message={error}
            retryLabel={t('common.retry')}
            onRetry={() => void fetchHistory()}
          />
        )}

        {!loaded && <LoadingState shape="rows" rows={5} />}

        {loaded && confirmed?.items.length === 0 && (
          <EmptyState
            variant="text"
            title={t(
              confirmedFiltered
                ? 'management.schedules.history.noMatches'
                : 'management.schedules.history.empty'
            )}
          />
        )}

        {confirmed && confirmed.items.length > 0 && (
          <div className="schedule-history-list mgmt-list divided-list">
            {confirmed.items.map((execution) => {
              const expanded = expandedIds.has(execution.id);
              const elapsedSeconds = Math.max(
                0,
                Math.round(
                  (new Date(execution.completedAt).getTime() -
                    new Date(execution.startedAt).getTime()) /
                    1000
                )
              );
              const capturedName =
                typeof execution.scheduleName === 'string' ? execution.scheduleName.trim() : '';
              const serviceName =
                capturedName.length > 0 ? capturedName : getServiceLabel(execution.serviceKey);
              const triggerKey =
                typeof execution.trigger === 'string' && execution.trigger.trim().length > 0
                  ? `management.schedules.history.triggers.${execution.trigger}`
                  : null;
              const trigger =
                triggerKey !== null && i18n.exists(triggerKey)
                  ? t(triggerKey)
                  : t('management.schedules.history.unknown');
              const platformServiceKey = Object.entries(
                SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY
              ).find(([platform]) => platform === execution.platform)?.[1];
              const platformKey =
                platformServiceKey === undefined
                  ? null
                  : `management.schedules.services.scheduledPrefill.config.services.${platformServiceKey}`;
              const platform =
                platformKey !== null && i18n.exists(platformKey) ? t(platformKey) : null;
              const detail = execution.warning
                ? t(execution.warning.stageKey, formatWarningCounts(execution.warning.context))
                : typeof execution.detail === 'string' && execution.detail.trim().length > 0
                  ? execution.detail
                  : null;

              return (
                <div key={execution.id} className="schedule-history-row">
                  <div
                    className="schedule-history-summary"
                    aria-expanded={expanded}
                    {...rowToggleHandlers(() => toggleRow(execution.id))}
                  >
                    <div className="schedule-history-identity">
                      <span className="schedule-history-name">{serviceName}</span>
                      <span className="schedule-history-actor">
                        {execution.actorKind === 'account'
                          ? execution.username
                          : execution.actorKind === 'server'
                            ? t('management.schedules.history.actors.server')
                            : t('management.schedules.history.unknown')}
                      </span>
                    </div>
                    <Badge
                      variant={
                        execution.status === 'completed' && execution.warning
                          ? 'warning'
                          : VARIANT_BY_STATUS[execution.status]
                      }
                      ariaLabel={t(`management.schedules.history.status.${execution.status}`)}
                    >
                      {t(`management.schedules.history.status.${execution.status}`)}
                    </Badge>
                    <span className="schedule-history-time">
                      <FormattedTimestamp timestamp={execution.startedAt} />
                    </span>
                    <span className="schedule-history-duration tabular-nums">
                      {t('management.schedules.history.durationSeconds', {
                        value: formatCount(elapsedSeconds)
                      })}
                    </span>
                  </div>

                  <CollapsibleRegion
                    open={expanded}
                    contentClassName="schedule-history-detail mgmt-row-detail"
                  >
                    <dl className="schedule-history-fields">
                      <div>
                        <dt>{t('management.schedules.history.trigger')}</dt>
                        <dd>{trigger}</dd>
                      </div>
                      {platform !== null && (
                        <div>
                          <dt>{t('management.schedules.history.platform')}</dt>
                          <dd>{platform}</dd>
                        </div>
                      )}
                      <div>
                        <dt>{t('management.schedules.history.completed')}</dt>
                        <dd>
                          <FormattedTimestamp timestamp={execution.completedAt} />
                        </dd>
                      </div>
                    </dl>
                    {detail !== null && (
                      <p className="schedule-history-terminal-detail">{detail}</p>
                    )}
                  </CollapsibleRegion>
                </div>
              );
            })}
          </div>
        )}

        {confirmed && confirmed.totalPages > 1 && (
          <Pagination
            currentPage={confirmed.page}
            totalPages={confirmed.totalPages}
            totalItems={confirmed.totalCount}
            itemsPerPage={confirmed.pageSize}
            itemLabel={t('management.schedules.history.items')}
            onPageChange={changePage}
            showCard={false}
            parentPadding="none"
            className="schedule-history-pagination"
          />
        )}
      </div>
    </AccordionSection>
  );
}
