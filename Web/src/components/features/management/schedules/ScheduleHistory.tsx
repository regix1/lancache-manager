import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { Pagination } from '@components/ui/Pagination';
import { FormattedTimestamp } from '@components/common/FormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import ApiService from '@services/api.service';
import { getErrorMessage, isAbortError } from '@utils/error';
import { formatCount } from '@utils/formatters';
import { rowToggleHandlers } from '@utils/rowToggle';
import { VARIANT_BY_STATUS } from '@utils/statusVariant';
import { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } from './scheduled-prefill/constants';
import {
  SCHEDULE_HISTORY_PAGE_SIZE,
  type ScheduleExecutionResponse,
  type ServiceScheduleInfo
} from './types';

export function ScheduleHistory() {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const [sectionOpen, setSectionOpen] = useState(false);
  const [confirmed, setConfirmed] = useState<ScheduleExecutionResponse | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expandedIds, setExpandedIds] = useState<Set<number>>(() => new Set());
  const mountedRef = useRef(false);
  const pageRef = useRef(1);
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
          const next = await ApiService.getScheduleHistory(
            requestedPage,
            SCHEDULE_HISTORY_PAGE_SIZE,
            controller.signal
          );
          if (mountedRef.current && owner === request.owner && requestedPage === pageRef.current) {
            setConfirmed(next);
            setError(null);
            setLoaded(true);
          }
        } catch (requestError: unknown) {
          if (
            mountedRef.current &&
            owner === request.owner &&
            requestedPage === pageRef.current &&
            !isAbortError(requestError)
          ) {
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

  const changePage = useCallback(
    (nextPage: number): void => {
      if (nextPage === pageRef.current) return;
      pageRef.current = nextPage;
      setExpandedIds(new Set());
      requestRef.current.controller?.abort();
      void fetchHistory();
    },
    [fetchHistory]
  );

  return (
    <AccordionSection
      title={t('management.schedules.history.title')}
      count={confirmed?.totalCount ?? 0}
      isExpanded={sectionOpen}
      onToggle={() => setSectionOpen((open) => !open)}
    >
      <p className="schedule-history-note">{t('management.schedules.history.waitingNote')}</p>

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
        <EmptyState variant="text" title={t('management.schedules.history.empty')} />
      )}

      {confirmed && confirmed.items.length > 0 && (
        <div className="schedule-history-list divided-list">
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
            const serviceName =
              execution.scheduleName !== null
                ? execution.scheduleName
                : t(`management.schedules.services.${execution.serviceKey}.displayName`);
            const trigger =
              execution.trigger === null
                ? t('management.schedules.history.unknown')
                : t(`management.schedules.history.triggers.${execution.trigger}`);

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
                    variant={VARIANT_BY_STATUS[execution.status]}
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

                <CollapsibleRegion open={expanded} contentClassName="schedule-history-detail">
                  <dl className="schedule-history-fields">
                    <div>
                      <dt>{t('management.schedules.history.trigger')}</dt>
                      <dd>{trigger}</dd>
                    </div>
                    {execution.platform !== null && (
                      <div>
                        <dt>{t('management.schedules.history.platform')}</dt>
                        <dd>
                          {t(
                            `management.schedules.services.scheduledPrefill.config.services.${SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[execution.platform]}`
                          )}
                        </dd>
                      </div>
                    )}
                    <div>
                      <dt>{t('management.schedules.history.completed')}</dt>
                      <dd>
                        <FormattedTimestamp timestamp={execution.completedAt} />
                      </dd>
                    </div>
                  </dl>
                  {execution.detail !== null && (
                    <p className="schedule-history-terminal-detail">{execution.detail}</p>
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
    </AccordionSection>
  );
}
