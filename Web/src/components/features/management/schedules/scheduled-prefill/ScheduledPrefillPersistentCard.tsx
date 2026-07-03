import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatTimeRemaining } from '@components/features/prefill/types';
import { formatBytes, formatDateTime } from '@utils/formatters';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import {
  getPersistentServiceId,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import { usePersistentLoginStoreState } from './persistentLoginStore';
import type { ScheduledPrefillPersistentCardProps } from './scheduledPrefillPersistentTypes';

type StatusTone = 'idle' | 'warning' | 'active' | 'running';

interface StatusDisplay {
  tone: StatusTone;
  label: string;
  busy: boolean;
}

export function ScheduledPrefillPersistentCard({
  serviceKey,
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
  onLogout,
  onSelectGames,
  onClearGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPersistentCardProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const containersKey = `${baseKey}.persistentContainers`;

  // Anonymous services (Battle.net/Riot) have no login step: the persistent container is
  // ready as soon as it's running, so every authenticated-gated conditional below treats
  // "running" as sufficient and the login/logout controls never render.
  const isAnonymous = isScheduledPrefillAnonymousService(serviceKey);
  // Login-flow state lives in the module-level persistent-login store (survives the auth modal
  // being hidden/unmounted), so the row can show its own login error directly - no more floating
  // alert rendered outside any card (diagnostic §6 item 6).
  const loginState = usePersistentLoginStoreState(getPersistentServiceId(serviceKey));
  const loginError = isAnonymous ? null : loginState.error;
  // Set when a challenge poll 404'd (the daemon session behind it is gone - diagnostic ADDENDUM),
  // distinct from `loginError`: this is a terminal "nothing to resume, press Start" state, not a
  // failed login attempt, so it renders its own friendly copy instead of the loginFailed wrapper.
  // The backend distinguishes a session that flipped to Error (socket dropped) from one that was
  // never started, so this picks between two copies rather than one generic message.
  const sessionUnavailableState = isAnonymous ? null : loginState.sessionUnavailableState;
  const isSessionUnavailable = sessionUnavailableState !== null;
  const isRunning = container?.isRunning ?? false;
  const isAuthenticated = container?.isAuthenticated ?? false;
  const isPrefilling = container?.isPrefilling ?? false;
  // Anonymous services never need to authenticate, so they're "ready" the moment they're
  // running; authenticated services are only ready once login succeeds.
  const isReady = isAnonymous || isAuthenticated;
  const isAuthInProgress = !isAnonymous && isRunning && !isAuthenticated && authenticating;
  const isGameSelectionBlocked = isRunning && !isReady;
  // Initial container probe with nothing resolved yet — show the loading view.
  const isContainerLoading = statusLoading && container === undefined;

  // One compact status line replaces the three tinted pipeline boxes: a coloured
  // dot carries meaning (green = logged in, info = downloading, amber = needs
  // attention, muted = idle) and the label spells it out.
  const statusDisplay: StatusDisplay = (() => {
    if (!isRunning) {
      return { tone: 'idle', label: t('prefill.persistent.status.stopped'), busy: false };
    }
    if (isAnonymous) {
      return isPrefilling
        ? { tone: 'active', label: t(`${containersKey}.steps.downloading`), busy: false }
        : { tone: 'running', label: t('prefill.persistent.status.running'), busy: false };
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
      return isAnonymous
        ? t(`${containersKey}.workflow.stoppedAnonymous`)
        : t(`${containersKey}.workflow.stopped`);
    }
    if (!isAnonymous && container?.needsRelogin) {
      return t('prefill.persistent.needsRelogin');
    }
    if (!isAnonymous && !isAuthenticated) {
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
            {isAnonymous
              ? t(`${containersKey}.anonymous.${serviceKey}.description`)
              : t(`${baseKey}.persistentContainer.help`)}
          </p>
        </div>
        {(isAnonymous || statusLoading) && (
          <div className="scheduled-prefill-persistent-card__header-badges">
            {isAnonymous && (
              <Badge variant="success">{t(`${containersKey}.anonymous.badge`)}</Badge>
            )}
            {statusLoading && <LoadingSpinner inline size="sm" />}
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

          {isSessionUnavailable && (
            <Alert color="yellow" className="scheduled-prefill-persistent-card__auth-alert">
              {t(
                sessionUnavailableState === 'errored'
                  ? 'prefill.persistent.sessionErrored'
                  : 'prefill.persistent.sessionUnavailable'
              )}
            </Alert>
          )}

          {loginError && !isSessionUnavailable && (
            <Alert color="red" className="scheduled-prefill-persistent-card__auth-alert">
              {t('prefill.persistent.loginFailed', { error: loginError })}
            </Alert>
          )}

          {!isAnonymous && container && isRunning && (
            <div className="scheduled-prefill-persistent-card__meta">
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
                !isAnonymous && container?.needsRelogin
                  ? ' scheduled-prefill-persistent-card__hint--warning'
                  : ''
              }`}
            >
              {workflowHint}
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
              {isRunning && !isReady && (
                <Button
                  type="button"
                  variant="filled"
                  color="blue"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  onClick={onLogin}
                  disabled={disabled || isAuthInProgress}
                >
                  {t('prefill.persistent.logIn')}
                </Button>
              )}
              {!isAnonymous && isRunning && isAuthenticated && (
                <Button
                  type="button"
                  variant="filled"
                  color="gray"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  onClick={onLogout}
                  disabled={disabled || isPrefilling || action === 'start' || action === 'stop'}
                  loading={action === 'logout'}
                >
                  {t('prefill.persistent.logOut')}
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
              <Button
                type="button"
                variant="filled"
                color="gray"
                size={SCHEDULED_PREFILL_BUTTON_SIZE}
                onClick={onClearGames}
                disabled={disabled || selectedGamesCount === 0 || isPrefilling}
              >
                {t(`${baseKey}.actions.clearGames`)}
              </Button>
            </div>

            {isRunning && isReady && (
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
