import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatTimeRemaining } from '@components/features/prefill/types';
import { formatBytes, formatDateTime } from '@utils/formatters';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import type { ScheduledPrefillPersistentCardProps } from './scheduledPrefillPersistentTypes';

const getSecondsUntil = (expiresAtUtc: string): number =>
  Math.floor((new Date(expiresAtUtc).getTime() - Date.now()) / 1000);

type StatusTone = 'idle' | 'warning' | 'active' | 'running';

interface StatusDisplay {
  tone: StatusTone;
  label: string;
  busy: boolean;
}

export function ScheduledPrefillPersistentCard({
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
  // Initial container probe with nothing resolved yet — show the loading view.
  const isContainerLoading = statusLoading && container === undefined;

  const daemonAuthTimeRemainingSeconds = container?.daemonAuthExpiresAtUtc
    ? getSecondsUntil(container.daemonAuthExpiresAtUtc)
    : null;

  // One compact status line replaces the three tinted pipeline boxes: a coloured
  // dot carries meaning (green = logged in, info = downloading, amber = needs
  // attention, muted = idle) and the label spells it out.
  const statusDisplay: StatusDisplay = (() => {
    if (!isRunning) {
      return { tone: 'idle', label: t('prefill.persistent.status.stopped'), busy: false };
    }
    if (isAuthInProgress) {
      return { tone: 'warning', label: t('prefill.persistent.authenticating'), busy: true };
    }
    if (!isAuthenticated) {
      return { tone: 'warning', label: t('prefill.persistent.status.notLoggedIn'), busy: false };
    }
    if (isPrefilling) {
      return { tone: 'active', label: t(`${containersKey}.steps.downloading`), busy: false };
    }
    return { tone: 'running', label: t('prefill.persistent.status.loggedIn'), busy: false };
  })();

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
    <Card padding="md" className="scheduled-prefill-persistent-card">
      <header className="scheduled-prefill-persistent-card__header">
        <div className="scheduled-prefill-persistent-card__title-block">
          <h4 className="scheduled-prefill-persistent-card__title">
            {t(`${baseKey}.platforms.sections.persistentContainer`)}
          </h4>
          <p className="scheduled-prefill-persistent-card__subtitle">
            {t(`${baseKey}.persistentContainer.help`)}
          </p>
        </div>
        {statusLoading && (
          <div className="scheduled-prefill-persistent-card__header-badges">
            <LoadingSpinner inline size="sm" />
          </div>
        )}
      </header>

      {isContainerLoading ? (
        <div className="scheduled-prefill-persistent-card__state" role="status" aria-live="polite">
          <LoadingSpinner inline size="sm" />
          <span>{t(`${containersKey}.loadingStatus`)}</span>
        </div>
      ) : (
        <>
          <div
            className="scheduled-prefill-persistent-card__status"
            role="status"
            aria-live="polite"
          >
            <span
              className={`scheduled-prefill-persistent-card__status-dot scheduled-prefill-persistent-card__status-dot--${statusDisplay.tone}`}
              aria-hidden="true"
            />
            <span className="scheduled-prefill-persistent-card__status-text">
              {statusDisplay.busy && <LoadingSpinner inline size="xs" />}
              {statusDisplay.label}
            </span>
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

          <p className="scheduled-prefill-persistent-card__games">
            {t(`${containersKey}.stats.gamesSelected`)}: <strong>{selectedGamesCount}</strong>
          </p>

          {isPrefilling && container && (
            <p className="scheduled-prefill-persistent-card__downloading">
              {container.currentAppName
                ? t(`${baseKey}.persistentContainer.downloadProgress`, {
                    game: container.currentAppName,
                    bytes: formatBytes(container.totalBytesTransferred ?? 0)
                  })
                : t(`${baseKey}.persistentContainer.downloadProgressGeneric`, {
                    bytes: formatBytes(container.totalBytesTransferred ?? 0)
                  })}
            </p>
          )}

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
                  variant="filled"
                  color="red"
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
                variant="filled"
                color="gray"
                size={SCHEDULED_PREFILL_BUTTON_SIZE}
                onClick={onSelectGames}
                disabled={disabled || !isRunning || isGameSelectionBlocked}
                loading={gameSelectionLoading}
                title={
                  isGameSelectionBlocked ? t('prefill.persistent.loginToSelectGames') : undefined
                }
              >
                {t(`${baseKey}.actions.selectGames`)}
              </Button>
            </div>

            {isRunning && isAuthenticated && (
              <div className="scheduled-prefill-persistent-card__action-group scheduled-prefill-persistent-card__action-group--primary">
                {isPrefilling ? (
                  <Button
                    type="button"
                    variant="filled"
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
        </>
      )}
    </Card>
  );
}
