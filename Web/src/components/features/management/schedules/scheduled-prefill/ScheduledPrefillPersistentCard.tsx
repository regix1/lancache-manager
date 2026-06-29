import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatTimeRemaining } from '@components/features/prefill/types';
import { formatBytes, formatDateTime } from '@utils/formatters';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import type { ScheduledPrefillPersistentCardProps } from './scheduledPrefillPersistentTypes';

const getSecondsUntil = (expiresAtUtc: string): number =>
  Math.floor((new Date(expiresAtUtc).getTime() - Date.now()) / 1000);

type PipelineTone = 'neutral' | 'success' | 'warning' | 'active' | 'danger';

interface PipelineStep {
  label: string;
  value: string;
  tone: PipelineTone;
}

export function ScheduledPrefillPersistentCard({
  serviceKey,
  embedded = false,
  container,
  selectedGamesCount,
  disabled = false,
  statusLoading = false,
  authenticating = false,
  action = null,
  gameSelectionLoading = false,
  onStart,
  onStop,
  onLogin,
  onSelectGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPersistentCardProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const containersKey = `${baseKey}.persistentContainers`;

  const isRunning = container?.isRunning ?? false;
  const isAuthenticated = container?.isAuthenticated ?? false;
  const isPrefilling = container?.isPrefilling ?? false;
  const isAuthInProgress = isRunning && !isAuthenticated && authenticating;
  const isGameSelectionBlocked = isRunning && !isAuthenticated;

  const daemonAuthTimeRemainingSeconds = container?.daemonAuthExpiresAtUtc
    ? getSecondsUntil(container.daemonAuthExpiresAtUtc)
    : null;

  const pipelineSteps: PipelineStep[] = [
    {
      label: t(`${containersKey}.steps.container`),
      value: isRunning
        ? t('prefill.persistent.status.running')
        : t('prefill.persistent.status.stopped'),
      tone: isRunning ? 'success' : 'neutral'
    },
    {
      label: t(`${containersKey}.steps.account`),
      value: !isRunning
        ? t(`${containersKey}.steps.unavailable`)
        : isAuthInProgress
          ? t('prefill.persistent.authenticating')
          : isAuthenticated
            ? t('prefill.persistent.status.loggedIn')
            : t('prefill.persistent.status.notLoggedIn'),
      tone: !isRunning
        ? 'neutral'
        : isAuthInProgress
          ? 'warning'
          : isAuthenticated
            ? 'success'
            : 'warning'
    },
    {
      label: t(`${containersKey}.steps.activity`),
      value: !isRunning
        ? t(`${containersKey}.steps.unavailable`)
        : isPrefilling
          ? t(`${containersKey}.steps.downloading`)
          : t(`${containersKey}.steps.idle`),
      tone: !isRunning ? 'neutral' : isPrefilling ? 'active' : 'success'
    }
  ];

  const workflowHint = (() => {
    if (!isRunning) {
      return t(`${containersKey}.workflow.stopped`);
    }
    if (container?.needsRelogin) {
      return t('prefill.persistent.needsRelogin');
    }
    if (!isAuthenticated) {
      return t(`${containersKey}.workflow.needsLogin`);
    }
    if (selectedGamesCount === 0) {
      return t(`${containersKey}.workflow.selectGames`);
    }
    if (isPrefilling) {
      return null;
    }
    return t(`${containersKey}.workflow.ready`);
  })();

  return (
    <article
      className={`scheduled-prefill-persistent-card${
        embedded ? ' scheduled-prefill-persistent-card--embedded' : ''
      }`}
    >
      {!embedded && (
        <header className="scheduled-prefill-persistent-card__header">
          <div className="scheduled-prefill-persistent-card__title-block">
            <h4 className="scheduled-prefill-persistent-card__title">
              {t(`${baseKey}.services.${serviceKey}`)}
            </h4>
            <p className="scheduled-prefill-persistent-card__subtitle">
              {t(`${baseKey}.persistentContainer.help`)}
            </p>
          </div>
          <div className="scheduled-prefill-persistent-card__header-badges">
            {statusLoading && <LoadingSpinner inline size="sm" />}
            <Badge variant={isRunning ? 'success' : 'neutral'}>
              {isRunning
                ? t('prefill.persistent.states.running')
                : t('prefill.persistent.states.stopped')}
            </Badge>
          </div>
        </header>
      )}
      {embedded && (
        <div className="scheduled-prefill-persistent-card__header-badges scheduled-prefill-persistent-card__header-badges--embedded">
          {statusLoading && <LoadingSpinner inline size="sm" />}
          <Badge variant={isRunning ? 'success' : 'neutral'}>
            {isRunning
              ? t('prefill.persistent.states.running')
              : t('prefill.persistent.states.stopped')}
          </Badge>
        </div>
      )}

      <div
        className="scheduled-prefill-persistent-card__pipeline"
        aria-label={t(`${containersKey}.pipelineLabel`)}
      >
        {pipelineSteps.map((step) => (
          <div
            key={step.label}
            className={`scheduled-prefill-persistent-card__pipeline-step scheduled-prefill-persistent-card__pipeline-step--${step.tone}`}
          >
            <span className="scheduled-prefill-persistent-card__pipeline-label">{step.label}</span>
            <span className="scheduled-prefill-persistent-card__pipeline-value">
              {step.tone === 'warning' &&
              isAuthInProgress &&
              step.label === t(`${containersKey}.steps.account`) ? (
                <>
                  <LoadingSpinner inline size="xs" />
                  {step.value}
                </>
              ) : (
                step.value
              )}
            </span>
          </div>
        ))}
      </div>

      {container && isRunning && (
        <div className="scheduled-prefill-persistent-card__meta">
          {container.daemonAuthExpiresAtUtc && (
            <div className="scheduled-prefill-persistent-card__meta-item">
              <span className="scheduled-prefill-persistent-card__meta-label">
                {t('prefill.persistent.tokenExpires')}
              </span>
              <span className="scheduled-prefill-persistent-card__meta-value">
                {formatDateTime(container.daemonAuthExpiresAtUtc)}
                {daemonAuthTimeRemainingSeconds !== null && (
                  <span className="scheduled-prefill-persistent-card__meta-detail">
                    {t('prefill.persistent.timeRemaining', {
                      time: formatTimeRemaining(daemonAuthTimeRemainingSeconds)
                    })}
                  </span>
                )}
              </span>
            </div>
          )}
          <div className="scheduled-prefill-persistent-card__meta-item">
            <span className="scheduled-prefill-persistent-card__meta-label">
              {t('prefill.persistent.reloginRequiredBy')}
            </span>
            <span className="scheduled-prefill-persistent-card__meta-value">
              {formatDateTime(container.authExpiresAtUtc)}
              <span className="scheduled-prefill-persistent-card__meta-detail">
                {t('prefill.persistent.timeRemaining', {
                  time: formatTimeRemaining(container.authTimeRemainingSeconds)
                })}
              </span>
            </span>
          </div>
        </div>
      )}

      <div className="scheduled-prefill-persistent-card__stats">
        <div className="scheduled-prefill-persistent-card__stat">
          <span className="scheduled-prefill-persistent-card__stat-value">
            {selectedGamesCount}
          </span>
          <span className="scheduled-prefill-persistent-card__stat-label">
            {t(`${containersKey}.stats.gamesSelected`)}
          </span>
        </div>
        {isPrefilling && container && (
          <div className="scheduled-prefill-persistent-card__stat scheduled-prefill-persistent-card__stat--active">
            <span className="scheduled-prefill-persistent-card__stat-value">
              {formatBytes(container.totalBytesTransferred ?? 0)}
            </span>
            <span className="scheduled-prefill-persistent-card__stat-label">
              {container.currentAppName
                ? t(`${baseKey}.persistentContainer.downloadProgress`, {
                    game: container.currentAppName,
                    bytes: formatBytes(container.totalBytesTransferred ?? 0)
                  })
                : t(`${baseKey}.persistentContainer.downloadProgressGeneric`, {
                    bytes: formatBytes(container.totalBytesTransferred ?? 0)
                  })}
            </span>
          </div>
        )}
      </div>

      {isPrefilling && (
        <div
          className="scheduled-prefill-persistent-card__progress"
          role="progressbar"
          aria-busy="true"
          aria-label={t(`${containersKey}.steps.downloading`)}
        >
          <span className="scheduled-prefill-persistent-card__progress-bar" />
        </div>
      )}

      {workflowHint && (
        <p
          className={`scheduled-prefill-persistent-card__hint${
            container?.needsRelogin ? ' scheduled-prefill-persistent-card__hint--warning' : ''
          }`}
        >
          {workflowHint}
        </p>
      )}

      {selectedGamesCount > 0 && isAuthenticated && (
        <p className="scheduled-prefill-persistent-card__override">
          {t(`${baseKey}.selectedGames.overridePreset`)}
        </p>
      )}

      <footer className="scheduled-prefill-persistent-card__actions">
        <div className="scheduled-prefill-persistent-card__action-group">
          {isRunning ? (
            <Button
              type="button"
              variant="outline"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={onStop}
              disabled={disabled || action === 'start'}
              loading={action === 'stop'}
            >
              {t('prefill.persistent.actions.stop')}
            </Button>
          ) : (
            <Button
              type="button"
              variant="filled"
              color="blue"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={onStart}
              disabled={disabled || action === 'stop'}
              loading={action === 'start'}
            >
              {t('prefill.persistent.actions.start')}
            </Button>
          )}
        </div>

        <div className="scheduled-prefill-persistent-card__action-group">
          {isRunning && !isAuthenticated && (
            <Button
              type="button"
              variant="filled"
              color="blue"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={onLogin}
              disabled={disabled || isAuthInProgress}
              loading={isAuthInProgress}
            >
              {t('prefill.persistent.logIn')}
            </Button>
          )}
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={onSelectGames}
            disabled={disabled || !isRunning || isGameSelectionBlocked}
            loading={gameSelectionLoading}
            title={isGameSelectionBlocked ? t('prefill.persistent.loginToSelectGames') : undefined}
          >
            {t(`${baseKey}.actions.selectGames`)}
          </Button>
        </div>

        {isRunning && isAuthenticated && (
          <div className="scheduled-prefill-persistent-card__action-group scheduled-prefill-persistent-card__action-group--primary">
            {isPrefilling ? (
              <Button
                type="button"
                variant="outline"
                color="red"
                size={SCHEDULED_PREFILL_BUTTON_SIZE}
                onClick={onCancelDownload}
                disabled={disabled || action === 'download'}
                loading={action === 'cancel'}
              >
                {t(`${baseKey}.persistentContainer.cancelDownload`)}
              </Button>
            ) : (
              <Button
                type="button"
                variant="filled"
                color="green"
                size={SCHEDULED_PREFILL_BUTTON_SIZE}
                onClick={onDownload}
                disabled={disabled || selectedGamesCount === 0 || action === 'cancel'}
                loading={action === 'download'}
              >
                {t(`${baseKey}.persistentContainer.downloadNow`)}
              </Button>
            )}
          </div>
        )}
      </footer>
    </article>
  );
}
