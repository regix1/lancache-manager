import { useTranslation } from 'react-i18next';
import { useContext, useEffect, useId, useState } from 'react';
import { PrefillContext } from '@contexts/PrefillContext.types';
import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { Tooltip } from '../../ui/Tooltip';
import { ChevronDown, Download } from 'lucide-react';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes, formatSpeed, formatPercent } from '@utils/formatters';
import { formatTimeRemaining, formatEtaShort } from './types';
import { isPrefillRunActive, type PrefillRun, type PrefillProgress } from './hooks/prefillTypes';
import { Alert } from '../../ui/Alert';
import { CollapsibleRegion } from '../../ui/CollapsibleRegion';

interface PrefillProgressCardProps {
  run?: PrefillRun;
  error?: string;
  disabled?: boolean;
  progress: PrefillProgress;
  onCancel?: () => void;
  /** When true the Cancel button shows a disabled "Cancelling..." state. */
  isCancelling?: boolean;
}

export function PrefillProgressCard({
  progress,
  onCancel,
  isCancelling = false,
  run,
  error,
  disabled = false
}: PrefillProgressCardProps) {
  const { t } = useTranslation();
  const [detailsOpen, setDetailsOpen] = useState(false);
  const detailsId = useId();
  const completionContext = useContext(PrefillContext);
  const recordRunCompletion = completionContext?.recordRunCompletion;
  useEffect(() => {
    if (run && !isPrefillRunActive(run)) recordRunCompletion?.(run);
  }, [run, recordRunCompletion]);

  const getStateLabel = () => {
    switch (progress.state) {
      case 'reconnecting':
        return t('prefill.progress.reconnecting');
      case 'loading-metadata':
        return t('prefill.progress.loadingGameData');
      case 'metadata-loaded':
        return t('prefill.progress.preparingDownload');
      case 'starting':
        return t('prefill.progress.starting');
      case 'preparing':
        return t('prefill.progress.preparing');
      case 'app_completed':
        return t('prefill.progress.loadingNextGame');
      case 'already_cached':
        return t('prefill.progress.alreadyCached');
      case 'downloading':
        return t('prefill.progress.downloading');
      default:
        // Unknown/transitional state must NOT assert an active download — use a neutral label.
        return t('prefill.progress.preparing');
    }
  };

  const showAppInfo =
    progress.state === 'downloading' ||
    progress.state === 'app_completed' ||
    progress.state === 'already_cached';
  const isReconnecting = progress.state === 'reconnecting';

  // Per-app percent (the daemon reports current-app progress in percentComplete).
  const appPercent = Math.min(100, Math.max(0, progress.percentComplete));

  // ---- Two-tier "overall" progress (Game X of N + aggregate %) ----
  const expectedAppCount = progress.expectedAppCount ?? progress.totalApps ?? 0;
  const failedApps = progress.failedApps ?? 0;
  // PROCESSED (not "completed"): includes failed apps so the position count ("Game X of N") and the
  // monotonic overall bar advance past every app the job has finished with — success OR failure.
  const processedApps = (progress.updatedApps ?? 0) + (progress.alreadyUpToDate ?? 0) + failedApps;
  const showOverall = expectedAppCount > 1;
  // 1-based number of the game currently being worked on, capped to the total.
  const currentGameNumber = Math.min(processedApps + 1, expectedAppCount);
  const overallPercent =
    expectedAppCount > 0
      ? Math.min(100, ((processedApps + appPercent / 100) / expectedAppCount) * 100)
      : 0;

  // ---- ETA from rolling bytesPerSecond ----
  const remainingBytes = Math.max(0, progress.totalBytes - progress.bytesDownloaded);
  const etaSeconds =
    progress.state === 'downloading' && progress.bytesPerSecond > 0
      ? Math.floor(remainingBytes / progress.bytesPerSecond)
      : null;

  const currentAppLabel =
    (progress.currentAppName || t('prefill.progress.appId', { id: progress.currentAppId })) +
    (progress.state === 'app_completed' ? ` - ${t('prefill.progress.complete')}` : '') +
    (progress.state === 'already_cached' ? ` - ${t('prefill.progress.upToDate')}` : '');

  if (run) {
    const active = isPrefillRunActive(run);
    if (!active && completionContext?.isRunCompletionDismissed(run)) return null;
    const { snapshot } = run;
    const name = run.scheduleName || t('prefill.runs.name', { id: run.runId.slice(0, 8) });
    const finished =
      snapshot.completedApps +
      snapshot.cachedApps +
      snapshot.failedApps +
      snapshot.skippedApps +
      snapshot.cancelledApps;
    const label =
      run.recovering && active
        ? t('prefill.progress.reconnecting')
        : active
          ? getStateLabel()
          : t(`prefill.runs.${snapshot.state}`);
    const reasonKey =
      snapshot.reason === 'skippedOverlap'
        ? 'prefill.runs.overlap'
        : snapshot.reason === 'instance-changed'
          ? 'errors.prefill.instanceChanged'
          : snapshot.reason === 'outcome-unknown'
            ? 'errors.prefill.outcomeUnknown'
            : snapshot.reason === 'runtime-exceeded'
              ? 'signalr.scheduledPrefill.failedMaxRuntime'
              : snapshot.reason === 'operation-not-found'
                ? 'errors.prefill.operationNotFound'
                : null;
    const itemsText = t('prefill.runs.items', {
      count: finished,
      total: snapshot.totalApps
    });
    const transferredText = t('prefill.runs.transferred', {
      size: formatBytes(snapshot.bytesTransferred)
    });
    const outcomesText =
      snapshot.failedApps > 0 || snapshot.skippedApps > 0 || snapshot.cancelledApps > 0
        ? t('prefill.runs.outcomes', {
            completed: snapshot.completedApps + snapshot.cachedApps,
            failed: snapshot.failedApps,
            skipped: snapshot.skippedApps,
            cancelled: snapshot.cancelledApps
          })
        : null;
    return (
      <section data-prefill-run={run.runId} aria-label={name}>
        <Card
          padding={active ? 'md' : 'sm'}
          className={`overflow-hidden prefill-progress-card${
            active ? '' : ' prefill-progress-card--terminal'
          }`}
        >
          <div className="prefill-progress-card__body">
            <div className="prefill-run-heading">
              <div className="min-w-0">
                <h4 className="font-medium text-themed-primary break-words">{name}</h4>
                <p className="text-sm text-themed-muted" role="status">
                  {label}
                </p>
              </div>
              {active && onCancel && (
                <Button
                  type="button"
                  size="md"
                  variant="filled"
                  color="stop"
                  onClick={onCancel}
                  disabled={disabled || isCancelling}
                  aria-label={t('prefill.runs.cancel', { name })}
                >
                  {isCancelling && <LoadingSpinner inline size="xs" />}
                  {isCancelling ? t('prefill.progress.cancelling') : t('common.cancel')}
                </Button>
              )}
              {!active && completionContext && (
                <Button
                  type="button"
                  size="sm"
                  variant="filled"
                  color="secondary"
                  disabled={disabled}
                  onClick={() => completionContext.dismissRunCompletion(run)}
                  aria-label={`${t('common.dismiss')}: ${name}`}
                >
                  {t('common.dismiss')}
                </Button>
              )}
            </div>
            {active && progress.currentAppId && progress.currentAppId !== '0' && (
              <p className="text-sm text-themed-primary break-words">
                {progress.currentAppName ||
                  t('prefill.progress.appId', { id: progress.currentAppId })}
              </p>
            )}
            {active && !run.recovering && progress.state === 'downloading' && (
              <>
                <progress
                  className="prefill-run-progress"
                  max={100}
                  value={appPercent}
                  aria-label={t('prefill.runs.gameProgress', { name })}
                />
                <p className="prefill-run-counts text-xs text-themed-muted">
                  <span>
                    {formatBytes(progress.bytesDownloaded)} / {formatBytes(progress.totalBytes)}
                  </span>
                  <span>{formatSpeed(progress.bytesPerSecond)}</span>
                </p>
              </>
            )}
            {run.recovering && active && (
              <p className="text-sm text-themed-muted">
                {t('prefill.progress.reconnectingMessage')}
              </p>
            )}
            {active ? (
              <>
                <p className="prefill-run-counts text-sm text-themed-muted">
                  <span>{itemsText}</span>
                  <span>{transferredText}</span>
                </p>
                {outcomesText && <p className="text-sm text-themed-muted">{outcomesText}</p>}
              </>
            ) : (
              <dl className="prefill-run-terminal-summary">
                <div>
                  <dt>{t('prefill.runs.processedLabel')}</dt>
                  <dd>{itemsText}</dd>
                </div>
                <div>
                  <dt>{t('prefill.runs.transferredLabel')}</dt>
                  <dd>{transferredText}</dd>
                </div>
                {outcomesText && (
                  <div>
                    <dt>{t('prefill.runs.resultsLabel')}</dt>
                    <dd>{outcomesText}</dd>
                  </div>
                )}
              </dl>
            )}
            {reasonKey && <p className="text-sm text-themed-muted">{t(reasonKey)}</p>}
            {run.historyIncomplete && (
              <Alert color="yellow">{t('prefill.runs.historyIncomplete')}</Alert>
            )}
            {error && <Alert color="red">{error}</Alert>}
            <div className="text-sm text-themed-muted">
              <button
                type="button"
                className="prefill-run-details"
                disabled={disabled}
                aria-expanded={detailsOpen}
                aria-controls={detailsId}
                onClick={() => setDetailsOpen((open) => !open)}
              >
                <ChevronDown
                  size={16}
                  aria-hidden="true"
                  className={`prefill-run-details__icon${
                    detailsOpen ? ' prefill-run-details__icon--open' : ''
                  }`}
                />
                <span>{t('prefill.runs.details')}</span>
              </button>
              <CollapsibleRegion
                open={detailsOpen}
                className="prefill-run-details-region"
                contentClassName="prefill-run-details-content"
              >
                <dl id={detailsId} className="prefill-run-detail-grid">
                  <div className="prefill-run-detail-grid__item">
                    <dt>{t('prefill.runs.startedLabel')}</dt>
                    <dd>{new Date(snapshot.startedAt).toLocaleString()}</dd>
                  </div>
                  <div className="prefill-run-detail-grid__item">
                    <dt>{t('prefill.runs.selectionLabel')}</dt>
                    <dd>
                      {t('prefill.runs.selectionValue', {
                        selection: run.options.selection,
                        count: snapshot.totalApps
                      })}
                    </dd>
                  </div>
                  {run.options.operatingSystems.length > 0 && (
                    <div className="prefill-run-detail-grid__item">
                      <dt>{t('prefill.runs.platformsLabel')}</dt>
                      <dd>{run.options.operatingSystems.join(', ')}</dd>
                    </div>
                  )}
                  {run.options.appIds && (
                    <div className="prefill-run-detail-grid__item">
                      <dt>{t('prefill.runs.appIdsLabel')}</dt>
                      <dd className="tabular-nums">{run.options.appIds.join(', ')}</dd>
                    </div>
                  )}
                  <div className="prefill-run-detail-grid__item prefill-run-detail-grid__item--wide">
                    <dt>{t('prefill.runs.identifierLabel')}</dt>
                    <dd className="prefill-run-detail-grid__identifier">{run.runId}</dd>
                  </div>
                </dl>
              </CollapsibleRegion>
            </div>
          </div>
        </Card>
      </section>
    );
  }

  return (
    <Card padding="md" className="overflow-hidden prefill-progress-card">
      <div className="space-y-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-primary-subtle)]">
              {isReconnecting ? (
                <LoadingSpinner inline size="md" className="text-[var(--theme-primary)]" />
              ) : (
                <Download className="h-5 w-5 animate-pulse text-[var(--theme-primary)]" />
              )}
            </div>
            <div>
              <p className="font-medium text-themed-primary">{getStateLabel()}</p>
              {showAppInfo && (
                <Tooltip content={currentAppLabel} position="top" className="block min-w-0">
                  <p className="text-sm text-themed-muted truncate max-w-[180px] sm:max-w-[300px]">
                    {currentAppLabel}
                  </p>
                </Tooltip>
              )}
            </div>
          </div>

          <div className="flex items-center justify-between gap-3 w-full sm:w-auto">
            {progress.state === 'downloading' && (
              <div className="text-left sm:text-right">
                <p className="text-sm font-medium text-themed-primary">
                  {formatSpeed(progress.bytesPerSecond)}
                </p>
                <p className="text-xs text-themed-muted">
                  {/* V7: ETA is derived from the CURRENT game's bytes, not the whole job, so it is
                      labelled per-game to avoid being read as an overall-job ETA (which would
                      sawtooth at every game boundary). */}
                  {etaSeconds !== null && etaSeconds > 0
                    ? t('prefill.progress.etaCurrentGame', {
                        time: formatEtaShort(etaSeconds)
                      })
                    : `${formatTimeRemaining(Math.floor(progress.elapsedSeconds))} ${t('prefill.progress.elapsed')}`}
                </p>
              </div>
            )}
            <Button
              variant="filled"
              color="stop"
              size="md"
              onClick={onCancel}
              disabled={isCancelling}
              className="min-h-[44px] sm:min-h-10 flex-1 sm:flex-initial"
            >
              {isCancelling ? (
                <>
                  <LoadingSpinner inline size="xs" />
                  {t('prefill.progress.cancelling')}
                </>
              ) : (
                t('common.cancel')
              )}
            </Button>
          </div>
        </div>

        {/* Hide the now-frozen progress bars while the cancel request is in flight so a stale,
            non-advancing bar isn't shown for seconds. The cancel button carries the pending
            state, so no separate status row is needed here. */}
        {!isCancelling && (
          <>
            {/* Overall progress (multi-game jobs only) */}
            {showOverall && !isReconnecting && (
              <div className="space-y-2">
                <div className="flex items-center justify-between text-xs text-themed-muted">
                  <span>
                    {t('prefill.progress.gameXofN', {
                      current: currentGameNumber,
                      total: expectedAppCount
                    })}
                    {failedApps > 0 && (
                      <span className="ml-1.5 font-medium text-[var(--theme-warning)]">
                        {t('prefill.progress.failedCount', { count: failedApps })}
                      </span>
                    )}
                  </span>
                  <span className="font-medium text-[var(--theme-primary)]">
                    {formatPercent(Math.floor(overallPercent), 0)}
                  </span>
                </div>
                <div className="h-2 rounded-full overflow-hidden bg-[var(--theme-progress-bg)]">
                  <div
                    className="h-full rounded-full transition-[width] duration-300 ease-out bg-[var(--theme-primary)] prefill-progress-bar"
                    style={{ '--progress-width': `${overallPercent}%` } as React.CSSProperties}
                  />
                </div>
              </div>
            )}

            {/* Per-app progress bar */}
            <div className="space-y-2">
              <div className="h-3 rounded-full overflow-hidden bg-[var(--theme-progress-bg)]">
                {isReconnecting ? (
                  <div className="h-full rounded-full animate-pulse w-full opacity-50 bg-[var(--theme-warning-indicator)]" />
                ) : progress.state === 'already_cached' ? (
                  <div
                    key={`cached-${progress.currentAppId}`}
                    className="h-full rounded-full bg-[var(--theme-info)] prefill-progress-bar"
                    style={{ '--progress-width': `${appPercent}%` } as React.CSSProperties}
                  />
                ) : progress.state === 'downloading' || progress.state === 'app_completed' ? (
                  <div
                    key={`download-${progress.currentAppId}`}
                    className="h-full rounded-full transition-[width] duration-300 ease-out bg-[var(--theme-primary)] prefill-progress-bar animate-progress-bar-enter"
                    style={{ '--progress-width': `${appPercent}%` } as React.CSSProperties}
                  />
                ) : (
                  <div className="h-full rounded-full animate-pulse w-full opacity-50 bg-[var(--theme-primary)]" />
                )}
              </div>

              {progress.state === 'reconnecting' ? (
                <p className="text-sm text-themed-muted text-center">
                  {t('prefill.progress.reconnectingMessage')}
                </p>
              ) : progress.state === 'downloading' ? (
                <div className="flex items-center justify-between text-xs text-themed-muted">
                  <span>
                    {formatBytes(progress.bytesDownloaded)} / {formatBytes(progress.totalBytes)}
                  </span>
                  <span className="font-medium text-[var(--theme-primary)]">
                    {formatPercent(appPercent, 1)}
                  </span>
                </div>
              ) : progress.state === 'already_cached' ? (
                <div className="flex items-center justify-between text-xs text-themed-muted">
                  <span className="text-[var(--theme-info)]">
                    {t('prefill.progress.gameUpToDate')}
                  </span>
                  <span className="font-medium text-[var(--theme-info)]">
                    {formatPercent(appPercent, 0)}
                  </span>
                </div>
              ) : progress.state === 'app_completed' ? (
                <p className="text-sm text-themed-muted text-center">
                  {t('prefill.progress.loadingNextGame')}...
                </p>
              ) : (
                <p className="text-sm text-themed-muted text-center">
                  {progress.message || t('prefill.progress.preparingOperation')}
                </p>
              )}
            </div>
          </>
        )}
      </div>
    </Card>
  );
}
