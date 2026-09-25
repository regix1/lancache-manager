import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { useReaderClock } from '@hooks/useReaderClock';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { ProgressBar } from '@components/ui/ProgressBar';
import LoadingSpinner from '@components/common/LoadingSpinner';
import StatusDot from '@components/common/StatusDot';
import { FormattedTimestamp } from '@components/common/FormattedDateTime';
import {
  getPrefillProgressStateKey,
  getPrefillRunProgress,
  getPrefillRunReasonKey,
  isPrefillRunActive,
  prefillRunKey,
  supportsConcurrentPrefill,
  type PrefillRun,
  type PrefillRunFailedGame
} from '@components/features/prefill/hooks/prefillTypes';
import { formatDurationFromSeconds, formatEtaShort } from '@components/features/prefill/types';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { getErrorMessage } from '@utils/error';
import { formatTimestamp } from '@utils/dateTimeFormat';
import { formatBytes, formatCount, formatSpeed } from '@utils/formatters';
import { SCHEDULED_PREFILL_SERVICE_RUN_ORDER } from './constants';
import {
  SCHEDULED_PREFILL_PLATFORM_UI,
  getPersistentServiceId
} from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillServiceKey } from './types';
import type { useScheduledPrefillContainers } from './useScheduledPrefillContainers';
import '@components/features/management/managementSectionContent.css';
import './ScheduledPrefillActivityModal.css';

const baseKey = 'management.schedules.services.scheduledPrefill.config';

interface ScheduledPrefillActivityModalProps {
  opened: boolean;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
  disabled: boolean;
  onClose: () => void;
}

interface ScheduledPrefillRecentRunProps {
  run: PrefillRun;
  serviceKey: ScheduledPrefillServiceKey;
}

function ScheduledPrefillRecentRun({ run, serviceKey }: ScheduledPrefillRecentRunProps) {
  const { t } = useTranslation();
  const detailsId = useId();
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [failedGames, setFailedGames] = useState<PrefillRunFailedGame[] | null>(null);
  const [failedGamesError, setFailedGamesError] = useState<unknown | null>(null);
  const [failedGamesLoading, setFailedGamesLoading] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  useEffect(() => () => abortRef.current?.abort(), []);

  const { snapshot } = run;
  const ServiceIcon = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey].icon;
  const name = run.scheduleName
    ? run.scheduleName
    : t('prefill.runs.name', { id: run.runId.slice(0, 8) });
  const serviceName = t(`prefill.persistent.services.${serviceKey}`);
  const reasonKey = getPrefillRunReasonKey(snapshot.reason);
  // FinishRunAsync turns a completed run with failed games into `failed` and keeps the daemon's
  // (empty) reason, so a failed run without a reason finished with only some games failed.
  const result =
    snapshot.state === 'cancelled'
      ? { label: t(`${baseKey}.activity.canceled`), tone: 'idle' as const }
      : snapshot.failedApps > 0 && !snapshot.reason
        ? {
            label: t(`${baseKey}.activity.finishedWithFailures`, { count: snapshot.failedApps }),
            tone: 'warning' as const
          }
        : snapshot.state === 'failed'
          ? { label: t('prefill.runs.failed'), tone: 'error' as const }
          : { label: t('prefill.runs.completed'), tone: 'running' as const };
  const gamesText = [
    t(`${baseKey}.activity.downloaded`, { count: snapshot.completedApps }),
    ...(snapshot.cachedApps > 0
      ? [t(`${baseKey}.activity.cached`, { count: snapshot.cachedApps })]
      : []),
    ...(snapshot.failedApps > 0
      ? [t(`${baseKey}.activity.failed`, { count: snapshot.failedApps })]
      : [])
  ].join(' · ');
  const sizeText = formatBytes(snapshot.bytesTransferred);
  const ranForText = run.completedAtUtc
    ? t(`${baseKey}.activity.ranFor`, {
        duration: formatDurationFromSeconds(
          Math.floor((Date.parse(run.completedAtUtc) - Date.parse(snapshot.startedAt)) / 1000)
        )
      })
    : null;
  const canHaveFailedGames = snapshot.failedApps > 0 || snapshot.state === 'failed';

  const loadFailedGames = () => {
    const controller = new AbortController();
    abortRef.current = controller;
    setFailedGamesError(null);
    setFailedGamesLoading(true);
    ApiService.getPersistentPrefillRunFailedGames(
      getPersistentServiceId(serviceKey),
      run.runId,
      controller.signal
    )
      .then((games) => setFailedGames(games))
      .catch((error: unknown) => setFailedGamesError(error))
      .finally(() => setFailedGamesLoading(false));
  };

  const toggleDetails = () => {
    const opening = !detailsOpen;
    setDetailsOpen(opening);
    if (
      opening &&
      canHaveFailedGames &&
      failedGames === null &&
      failedGamesError === null &&
      !failedGamesLoading
    ) {
      loadFailedGames();
    }
  };

  return (
    <li className={SCHEDULED_PREFILL_PLATFORM_UI[serviceKey].rowClassName}>
      <div className="scheduled-prefill-activity-runs__row scheduled-prefill-activity-runs__recent-row">
        <div className="flex items-center gap-3 min-w-0">
          <span
            className="icon-box scheduled-prefill-schedule-table__service-icon"
            aria-hidden="true"
          >
            <ServiceIcon size={18} />
          </span>
          <div className="min-w-0">
            <p className="font-medium text-themed-primary break-words">{name}</p>
            <p className="scheduled-prefill-activity-runs__meta">{serviceName}</p>
          </div>
        </div>
        <div className="flex items-center gap-2 min-w-0 max-md:col-span-full">
          <StatusDot tone={result.tone} label={result.label} />
          <span className="text-themed-primary">{result.label}</span>
        </div>
        <p className="hidden md:block tabular-nums text-themed-secondary">{gamesText}</p>
        <p className="hidden md:block tabular-nums text-themed-secondary">{sizeText}</p>
        <div className="hidden md:block min-w-0 tabular-nums text-themed-secondary">
          <FormattedTimestamp timestamp={snapshot.startedAt} />
          {ranForText && <p className="scheduled-prefill-activity-runs__meta">{ranForText}</p>}
        </div>
        <Button
          type="button"
          variant="accordion"
          size="md"
          open={detailsOpen}
          className="btn-icon-square pointer-target-44 max-md:row-start-1 max-md:col-start-2"
          onClick={toggleDetails}
          aria-expanded={detailsOpen}
          aria-controls={detailsId}
          aria-label={t('prefill.runs.details')}
        >
          <ChevronDown
            className={`w-4 h-4 transition duration-200 ease-out${
              detailsOpen ? ' rotate-180 text-themed-accent' : ' rotate-0 text-themed-muted'
            }`}
          />
        </Button>
        <p className="md:hidden col-span-full scheduled-prefill-activity-runs__meta tabular-nums">
          {gamesText} · {sizeText} · <FormattedTimestamp timestamp={snapshot.startedAt} />
          {ranForText && ` · ${ranForText}`}
        </p>
      </div>
      <div id={detailsId} inert={!detailsOpen}>
        <CollapsibleRegion open={detailsOpen} contentClassName="mgmt-row-detail">
          <div className="flex flex-col gap-3 text-sm">
            {reasonKey && <p className="text-themed-secondary">{t(reasonKey)}</p>}
            {run.historyIncomplete && (
              <Alert color="yellow">{t('prefill.runs.historyIncomplete')}</Alert>
            )}
            <dl className="prefill-run-detail-grid">
              <div className="prefill-run-detail-grid__item">
                <dt>{t('prefill.runs.startedLabel')}</dt>
                <dd className="tabular-nums">
                  <FormattedTimestamp timestamp={snapshot.startedAt} />
                </dd>
              </div>
              {run.completedAtUtc && (
                <div className="prefill-run-detail-grid__item">
                  <dt>{t(`${baseKey}.activity.finishedLabel`)}</dt>
                  <dd className="tabular-nums">
                    <FormattedTimestamp timestamp={run.completedAtUtc} />
                  </dd>
                </div>
              )}
              <div className="prefill-run-detail-grid__item">
                <dt>{t('prefill.runs.selectionLabel')}</dt>
                <dd className="prefill-run-selection">
                  {t('prefill.runs.selectionValue', { selection: run.options.selection })}
                  <Badge variant="neutral" className="badge-count">
                    {formatCount(snapshot.totalApps)}
                  </Badge>
                </dd>
              </div>
              {run.options.operatingSystems.length > 0 && (
                <div className="prefill-run-detail-grid__item">
                  <dt>{t('prefill.runs.platformsLabel')}</dt>
                  <dd>{run.options.operatingSystems.join(', ')}</dd>
                </div>
              )}
              <div className="prefill-run-detail-grid__item prefill-run-detail-grid__item--wide">
                <dt>{t('prefill.runs.identifierLabel')}</dt>
                <dd className="prefill-run-detail-grid__identifier">{run.runId}</dd>
              </div>
            </dl>
            {canHaveFailedGames && (
              <section className="flex flex-col gap-2">
                <h4 className="font-medium text-themed-primary">
                  {t(`${baseKey}.activity.failedGames`)}
                </h4>
                {failedGamesLoading ? (
                  <p className="flex items-center gap-2 scheduled-prefill-activity-runs__meta">
                    <LoadingSpinner inline size="xs" />
                    {t(`${baseKey}.activity.loadingFailedGames`)}
                  </p>
                ) : failedGamesError instanceof ApiError && failedGamesError.status === 404 ? (
                  // The run's stored record is gone, so a retry cannot bring the list back.
                  <p className="scheduled-prefill-activity-runs__meta">
                    {getErrorMessage(failedGamesError)}
                  </p>
                ) : failedGamesError !== null ? (
                  <ErrorBlock
                    title={t(`${baseKey}.activity.failedGamesLoadFailed`)}
                    message={getErrorMessage(failedGamesError)}
                    retryLabel={t('common.retry')}
                    onRetry={loadFailedGames}
                  />
                ) : failedGames !== null && failedGames.length === 0 ? (
                  <p className="scheduled-prefill-activity-runs__meta">
                    {t(`${baseKey}.activity.noFailedGames`)}
                  </p>
                ) : failedGames !== null ? (
                  <ul className="divided-list">
                    {failedGames.map((game) => (
                      <li key={game.appId} className="py-2">
                        <p className="text-themed-primary break-words">
                          {game.name ? game.name : t('prefill.progress.appId', { id: game.appId })}
                        </p>
                        <p className="scheduled-prefill-activity-runs__meta">{t(game.reasonKey)}</p>
                      </li>
                    ))}
                  </ul>
                ) : null}
              </section>
            )}
          </div>
        </CollapsibleRegion>
      </div>
    </li>
  );
}

export function ScheduledPrefillActivityModal({
  opened,
  containers,
  disabled,
  onClose
}: ScheduledPrefillActivityModalProps) {
  const { t } = useTranslation();
  // While the connection banner is up every read fails for that reason, so the banner speaks for
  // the load alert.
  const connectionLost = useConnectionLost();
  const clock = useReaderClock();
  const [serviceFilter, setServiceFilter] = useState<ScheduledPrefillServiceKey | 'all'>('all');

  const services = SCHEDULED_PREFILL_SERVICE_RUN_ORDER.filter(
    (serviceKey) => serviceFilter === 'all' || serviceFilter === serviceKey
  );
  const runs = services.flatMap((serviceKey) =>
    (containers.containersByServiceKey.get(serviceKey)?.runs ?? []).map((run) => ({
      run,
      serviceKey
    }))
  );
  const activeRuns = runs.filter(({ run }) => isPrefillRunActive(run));
  const recentRuns = runs
    .filter(({ run }) => !isPrefillRunActive(run))
    .sort((a, b) => b.run.snapshot.startedAt.localeCompare(a.run.snapshot.startedAt));
  // A daemon without concurrent runs reports one download on the container and no run list.
  const oldDaemonServices = services.filter((serviceKey) => {
    const container = containers.containersByServiceKey.get(serviceKey);
    return container?.isPrefilling === true && !supportsConcurrentPrefill(container);
  });
  const runningCount = activeRuns.length + oldDaemonServices.length;
  const filterOptions = [
    { value: 'all', label: t(`${baseKey}.activity.allServices`) },
    ...SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => ({
      value: serviceKey,
      label: t(`prefill.persistent.services.${serviceKey}`)
    }))
  ];

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t(`${baseKey}.activityTitle`)}
      size="xl"
      bodyFlexLayout
      className="scheduled-prefill-content-dialog"
    >
      <div className="scheduled-prefill-config-modal">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-themed-muted">{t(`${baseKey}.activityDescription`)}</p>
          <EnhancedDropdown
            className="scheduled-prefill-activity-filter"
            options={filterOptions}
            value={serviceFilter}
            onChange={(value) =>
              setServiceFilter(
                SCHEDULED_PREFILL_SERVICE_RUN_ORDER.find((serviceKey) => serviceKey === value) ??
                  'all'
              )
            }
            variant="button"
          />
        </div>
        <div className="scheduled-prefill-config-modal__scroll-area">
          <CustomScrollbar
            maxHeight="none"
            className="scheduled-prefill-config-modal__viewport"
            railPlacement="outer"
            radius="none"
          >
            <div className="flex flex-col gap-6">
              {containers.persistentContainers !== null &&
                containers.persistentError &&
                !connectionLost && (
                  <Alert color="red">
                    {t(`${baseKey}.summaryError`, { error: containers.persistentError })}
                  </Alert>
                )}
              {containers.persistentContainers === null ? (
                containers.persistentError !== null ? (
                  <ErrorBlock
                    title={t(`${baseKey}.serviceStatus.loadFailed`)}
                    message={containers.persistentError}
                    retryLabel={t('common.retry')}
                    onRetry={() => void containers.loadPersistentContainers()}
                  />
                ) : (
                  !connectionLost && <LoadingSpinner size="sm" />
                )
              ) : (
                <>
                  <section className="flex flex-col gap-3">
                    <h3 className="flex items-center gap-2 font-semibold text-themed-primary">
                      {t(`${baseKey}.activity.runningNow`)}
                      <Badge variant="neutral" className="badge-count">
                        {formatCount(runningCount)}
                      </Badge>
                    </h3>
                    {runningCount === 0 ? (
                      <p className="text-sm text-themed-muted">
                        {t(`${baseKey}.activity.noRunning`)}
                      </p>
                    ) : (
                      <ul className="scheduled-prefill-activity-runs__list">
                        {oldDaemonServices.map((serviceKey) => {
                          const container = containers.containersByServiceKey.get(serviceKey);
                          const platformUi = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
                          const ServiceIcon = platformUi.icon;
                          const serviceName = t(`prefill.persistent.services.${serviceKey}`);
                          // The cancel handler keys an old daemon's download by the container's runId.
                          const runId = container?.runId;
                          const canceling = runId
                            ? containers.cancellingRunIds.includes(runId)
                            : false;
                          const runError = runId ? containers.runErrors[runId] : undefined;
                          return (
                            <li
                              key={serviceKey}
                              className={`scheduled-prefill-activity-runs__row scheduled-prefill-activity-runs__running ${platformUi.rowClassName}`}
                            >
                              <div className="scheduled-prefill-activity-runs__header flex flex-wrap items-start gap-3">
                                <span
                                  className="icon-box scheduled-prefill-schedule-table__service-icon"
                                  aria-hidden="true"
                                >
                                  <ServiceIcon size={18} />
                                </span>
                                <p className="flex-1 min-w-[7rem] font-medium text-themed-primary break-words">
                                  {serviceName}
                                </p>
                              </div>
                              {typeof container?.currentAppName === 'string' &&
                                container.currentAppName !== '' && (
                                  <p className="text-sm text-themed-primary break-words">
                                    {container.currentAppName}
                                  </p>
                                )}
                              <p className="scheduled-prefill-activity-runs__meta tabular-nums">
                                {t(`${baseKey}.persistentContainer.downloadProgressGeneric`, {
                                  bytes: formatBytes(container?.totalBytesTransferred ?? 0)
                                })}
                              </p>
                              {canceling && (
                                <span
                                  className="scheduled-prefill-activity-runs__meta flex items-center gap-1"
                                  role="status"
                                >
                                  <LoadingSpinner inline size="xs" />
                                  {t(`${baseKey}.activity.canceling`)}
                                </span>
                              )}
                              {runError && <Alert color="red">{runError}</Alert>}
                              <Button
                                type="button"
                                variant="filled"
                                color="stop"
                                className="scheduled-prefill-activity-runs__cancel"
                                onClick={() =>
                                  void containers.handleCancelPersistentDownload(serviceKey)
                                }
                                disabled={disabled || canceling}
                                aria-label={t('prefill.runs.cancel', { name: serviceName })}
                              >
                                {t('common.cancel')}
                              </Button>
                            </li>
                          );
                        })}
                        {activeRuns.map(({ run, serviceKey }) => {
                          const container = containers.containersByServiceKey.get(serviceKey);
                          const platformUi = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
                          const ServiceIcon = platformUi.icon;
                          const { snapshot } = run;
                          const progress = getPrefillRunProgress(run);
                          const name = run.scheduleName
                            ? run.scheduleName
                            : t('prefill.runs.name', { id: run.runId.slice(0, 8) });
                          const slot =
                            activeRuns
                              .filter((entry) => entry.serviceKey === serviceKey)
                              .map((entry) => entry.run)
                              .sort((a, b) =>
                                a.snapshot.startedAt.localeCompare(b.snapshot.startedAt)
                              )
                              .indexOf(run) + 1;
                          const canceling =
                            run.cancelRequested || containers.cancellingRunIds.includes(run.runId);
                          const downloading = !run.recovering && progress.state === 'downloading';
                          const appPercent = Math.min(100, Math.max(0, progress.percentComplete));
                          const processedApps =
                            snapshot.completedApps + snapshot.cachedApps + snapshot.failedApps;
                          const etaSeconds =
                            progress.bytesPerSecond > 0
                              ? Math.floor(
                                  Math.max(0, progress.totalBytes - progress.bytesDownloaded) /
                                    progress.bytesPerSecond
                                )
                              : null;
                          const numbers = [
                            ...(downloading
                              ? [
                                  t(`${baseKey}.activity.sizeOf`, {
                                    done: formatBytes(progress.bytesDownloaded),
                                    total: formatBytes(progress.totalBytes)
                                  }),
                                  formatSpeed(progress.bytesPerSecond),
                                  ...(etaSeconds !== null
                                    ? [
                                        t(`${baseKey}.activity.timeLeft`, {
                                          time: formatEtaShort(etaSeconds)
                                        })
                                      ]
                                    : [])
                                ]
                              : []),
                            ...(snapshot.cachedApps > 0
                              ? [
                                  t('prefill.gameSelection.alreadyCachedCount', {
                                    count: snapshot.cachedApps
                                  })
                                ]
                              : [])
                          ];
                          const runError = containers.runErrors[run.runId];
                          return (
                            <li
                              key={prefillRunKey(run)}
                              className={`scheduled-prefill-activity-runs__row scheduled-prefill-activity-runs__running ${platformUi.rowClassName}`}
                            >
                              <div className="scheduled-prefill-activity-runs__header flex flex-wrap items-start gap-3">
                                <span
                                  className="icon-box scheduled-prefill-schedule-table__service-icon"
                                  aria-hidden="true"
                                >
                                  <ServiceIcon size={18} />
                                </span>
                                <div className="flex-1 min-w-[7rem]">
                                  <p className="font-medium text-themed-primary break-words">
                                    {name}
                                  </p>
                                  <p className="scheduled-prefill-activity-runs__meta flex flex-wrap items-center gap-x-2 tabular-nums">
                                    <span>
                                      {t(`prefill.persistent.services.${serviceKey}`)} ·{' '}
                                      {t(`${baseKey}.activity.started`, {
                                        time: formatTimestamp(snapshot.startedAt, {
                                          ...clock,
                                          forceYear: false
                                        })
                                      })}{' '}
                                      ·{' '}
                                      {t(`${baseKey}.activity.slot`, {
                                        slot,
                                        limit: container?.maxConcurrentRuns ?? 1
                                      })}
                                    </span>
                                    {canceling && (
                                      <span className="flex items-center gap-1" role="status">
                                        <LoadingSpinner inline size="xs" />
                                        {t(`${baseKey}.activity.canceling`)}
                                      </span>
                                    )}
                                  </p>
                                </div>
                              </div>
                              {!downloading && (
                                <p className="text-sm text-themed-secondary">
                                  {t(
                                    run.recovering
                                      ? 'prefill.progress.reconnecting'
                                      : getPrefillProgressStateKey(progress.state)
                                  )}
                                </p>
                              )}
                              {progress.currentAppId && progress.currentAppId !== '0' && (
                                <p className="text-sm text-themed-primary break-words">
                                  {progress.currentAppName
                                    ? progress.currentAppName
                                    : t('prefill.progress.appId', { id: progress.currentAppId })}
                                  {snapshot.totalApps > 1 && (
                                    <span className="text-themed-muted tabular-nums">
                                      {' · '}
                                      {t(`${baseKey}.activity.gameOf`, {
                                        current: Math.min(processedApps + 1, snapshot.totalApps),
                                        total: snapshot.totalApps
                                      })}
                                    </span>
                                  )}
                                </p>
                              )}
                              {downloading && (
                                <ProgressBar
                                  value={appPercent}
                                  height="md"
                                  label={t('prefill.runs.gameProgress', { name })}
                                />
                              )}
                              {numbers.length > 0 && (
                                <p className="scheduled-prefill-activity-runs__meta tabular-nums">
                                  {numbers.join(' · ')}
                                </p>
                              )}
                              {runError && <Alert color="red">{runError}</Alert>}
                              <Button
                                type="button"
                                variant="filled"
                                color="stop"
                                className="scheduled-prefill-activity-runs__cancel"
                                onClick={() =>
                                  void containers.handleCancelPersistentDownload(
                                    serviceKey,
                                    run.runId
                                  )
                                }
                                disabled={disabled || canceling}
                                aria-label={t('prefill.runs.cancel', { name })}
                              >
                                {t('common.cancel')}
                              </Button>
                            </li>
                          );
                        })}
                      </ul>
                    )}
                  </section>
                  <section className="flex flex-col gap-3">
                    <h3 className="flex items-center gap-2 font-semibold text-themed-primary">
                      {t(`${baseKey}.activity.recentRuns`)}
                      <Badge variant="neutral" className="badge-count">
                        {formatCount(recentRuns.length)}
                      </Badge>
                    </h3>
                    {recentRuns.length === 0 ? (
                      <p className="text-sm text-themed-muted">
                        {t(`${baseKey}.activity.noRecent`)}
                      </p>
                    ) : (
                      <div className="scheduled-prefill-activity-runs__list">
                        <div className="hidden md:grid scheduled-prefill-activity-runs__recent-row caps-label">
                          <span>{t(`${baseKey}.activity.columns.run`)}</span>
                          <span>{t(`${baseKey}.activity.columns.result`)}</span>
                          <span>{t(`${baseKey}.activity.columns.games`)}</span>
                          <span>{t(`${baseKey}.activity.columns.size`)}</span>
                          <span>{t(`${baseKey}.activity.columns.when`)}</span>
                        </div>
                        <ul>
                          {recentRuns.map(({ run, serviceKey }) => (
                            <ScheduledPrefillRecentRun
                              key={prefillRunKey(run)}
                              run={run}
                              serviceKey={serviceKey}
                            />
                          ))}
                        </ul>
                      </div>
                    )}
                  </section>
                </>
              )}
            </div>
          </CustomScrollbar>
        </div>
        <div className="scheduled-prefill-config-modal__actions">
          <Button onClick={onClose}>{t('common.close')}</Button>
        </div>
      </div>
    </Modal>
  );
}
