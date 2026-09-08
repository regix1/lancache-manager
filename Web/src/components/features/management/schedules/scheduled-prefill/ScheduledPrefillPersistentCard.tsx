import { useId, useState, type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import StatusDot from '@components/common/StatusDot';
import { formatTimeRemaining } from '@components/features/prefill/types';
import { formatBytes } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import {
  getPersistentServiceId,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import { usePersistentLoginStoreState } from './persistentLoginStore';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import type { ScheduledPrefillPersistentCardProps } from './scheduledPrefillPersistentTypes';

// Matches StatusDot's `tone` prop exactly (@components/common/StatusDot) so statusDisplay.tone can be
// passed straight through.
type StatusTone = 'idle' | 'warning' | 'info' | 'running';

interface StatusDisplay {
  tone: StatusTone;
  label: string;
  busy: boolean;
}

export function ScheduledPrefillPersistentCard({
  serviceKey,
  scheduleControls,
  containerSettings,
  gameSelectionLoading = false,
  onSelectGames,
  onClearGames,
  onStop,
  onLogout,
  container,
  selectedGamesCount,
  disabled = false,
  scheduleEnabled,
  statusLoading = false,
  authenticating = false,
  integrationLoginAvailability,
  integrationLoginAvailabilityLoading = false,
  action = null,
  onStart,
  onLogin,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPersistentCardProps) {
  const { t } = useTranslation();
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsId = useId();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const containersKey = `${baseKey}.persistentContainers`;
  const authExpiresAt = useFormattedDateTime(container?.authExpiresAtUtc);

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
  // The REST snapshot owns confirmed state; activity fills the initial loading window only.
  const activity = useActivityStatus();
  const activityPlatformKey = serviceKey.toLowerCase();
  const isRunning =
    container?.isRunning ??
    activity.isActive('persistentContainer', activityPlatformKey, 'running');
  const isAuthenticated =
    container?.isAuthenticated ??
    activity.isActive('persistentContainer', activityPlatformKey, 'authenticated');
  const isPrefilling = container?.isPrefilling ?? false;
  // Anonymous services never need to authenticate, so they're "ready" the moment they're
  // running; authenticated services are only ready once login succeeds.
  const isReady = isAnonymous || isAuthenticated;
  const isAuthInProgress = !isAnonymous && isRunning && !isAuthenticated && authenticating;
  const savedLoginHint = (() => {
    if (integrationLoginAvailabilityLoading) return t(`${containersKey}.savedLoginChecking`);
    if (integrationLoginAvailability?.available && integrationLoginAvailability.account?.trim()) {
      return t(`${containersKey}.savedLoginAvailable`, {
        account: integrationLoginAvailability.account
      });
    }
    switch (integrationLoginAvailability?.reason) {
      case 'account-required':
        return t(`${containersKey}.savedLoginAccountRequired`);
      case 'no-saved-login':
        return t(`${containersKey}.savedLoginMissing`);
      default:
        return t(`${containersKey}.savedLoginUnknown`);
    }
  })();
  // Every control below the schedule selector follows the schedule's enabled state. The selector
  // and its Actions menu stay outside this gate so an individual schedule can be switched back on.
  const selectionDisabled = disabled || !scheduleEnabled;
  const containerActionPending = action === 'stop' || action === 'logout';
  const reuseIntegrationDisabled =
    disabled ||
    containerActionPending ||
    isAuthInProgress ||
    integrationLoginAvailabilityLoading ||
    !integrationLoginAvailability?.available;
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
        ? { tone: 'info', label: t(`${containersKey}.steps.downloading`), busy: false }
        : { tone: 'running', label: t('prefill.persistent.status.running'), busy: false };
    }
    if (isAuthInProgress) {
      return { tone: 'warning', label: t('prefill.persistent.authenticating'), busy: true };
    }
    if (!isAuthenticated) {
      return { tone: 'warning', label: t('prefill.persistent.status.notLoggedIn'), busy: false };
    }
    if (isPrefilling) {
      return { tone: 'info', label: t(`${containersKey}.steps.downloading`), busy: false };
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
    if (isPrefilling) {
      return null;
    }
    return null;
  })();

  // The primary action follows the container's current state.
  let primaryAction: ReactNode;
  if (!isRunning) {
    primaryAction = (
      <Button
        type="button"
        variant="filled"
        color="run"
        size={SCHEDULED_PREFILL_BUTTON_SIZE}
        onClick={onStart}
        disabled={disabled || action === 'stop'}
        loading={action === 'start'}
      >
        {t('prefill.persistent.actions.start')}
      </Button>
    );
  } else if (!isReady) {
    primaryAction = (
      <Button
        type="button"
        variant="filled"
        color="primary"
        size={SCHEDULED_PREFILL_BUTTON_SIZE}
        onClick={() => onLogin(false)}
        disabled={disabled || isAuthInProgress}
      >
        {t(`${containersKey}.manualLogin`)}
      </Button>
    );
  } else if (isPrefilling) {
    primaryAction = (
      <Button
        type="button"
        variant="filled"
        color="stop"
        size={SCHEDULED_PREFILL_BUTTON_SIZE}
        onClick={onCancelDownload}
        disabled={disabled || action === 'download' || !container?.runId}
        loading={action === 'cancel'}
      >
        {t(`${baseKey}.persistentContainer.cancelDownload`)}
      </Button>
    );
  } else {
    primaryAction = (
      <Button
        type="button"
        variant="filled"
        color="run"
        size={SCHEDULED_PREFILL_BUTTON_SIZE}
        onClick={onDownload}
        disabled={selectionDisabled || action === 'cancel'}
        loading={action === 'download'}
      >
        {t(`${baseKey}.persistentContainer.downloadNow`)}
      </Button>
    );
  }

  return (
    <Card padding="md" className="scheduled-prefill-persistent-card">
      {scheduleControls}
      <fieldset className="scheduled-prefill-persistent-card__controls" disabled={disabled}>
        <header className="scheduled-prefill-persistent-card__header">
          <div className="scheduled-prefill-persistent-card__title-block">
            <h4 className="scheduled-prefill-persistent-card__title">
              {t(`${baseKey}.platforms.sections.persistentContainer`)}
            </h4>
          </div>
          <span
            className="scheduled-prefill-persistent-card__status"
            role="status"
            aria-live="polite"
          >
            <StatusDot tone={statusDisplay.tone} label={statusDisplay.label} />
            <span className="scheduled-prefill-persistent-card__status-text">
              {(statusDisplay.busy || statusLoading) && <LoadingSpinner inline size="xs" />}
              {statusDisplay.label}
            </span>
          </span>
        </header>

        {isContainerLoading ? (
          <div
            className="scheduled-prefill-persistent-card__state"
            role="status"
            aria-live="polite"
          >
            <LoadingSpinner inline size="sm" />
            <span>{t(`${containersKey}.loadingStatus`)}</span>
          </div>
        ) : (
          <>
            {!isAnonymous && container && isRunning && isAuthenticated && (
              <div className="scheduled-prefill-persistent-card__fact">
                <span className="caps-label scheduled-prefill-persistent-card__fact-label">
                  {t('prefill.persistent.reloginRequiredBy')}
                </span>
                <span className="scheduled-prefill-persistent-card__meta-value">
                  {authExpiresAt}
                  <span className="scheduled-prefill-persistent-card__meta-detail">
                    {/* Zero gets a finished sentence of its own instead of being poured into
                        "{{time}} remaining". formatTimeRemaining answers zero with a word, not a
                        duration, so the two together read "Expiring... remaining" in English and
                        stack two expiry clauses in Chinese. */}
                    {container.authTimeRemainingSeconds > 0
                      ? t('prefill.persistent.timeRemaining', {
                          time: formatTimeRemaining(container.authTimeRemainingSeconds)
                        })
                      : t('prefill.persistent.signInExpired')}
                  </span>
                </span>
              </div>
            )}

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

            {container?.needsRelogin && workflowHint && (
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
              {primaryAction}
              {isRunning && !isReady && (
                <Tooltip
                  content={savedLoginHint}
                  className="scheduled-prefill-persistent-card__login-help"
                >
                  <span
                    tabIndex={reuseIntegrationDisabled ? 0 : undefined}
                    aria-label={savedLoginHint}
                  >
                    <Button
                      type="button"
                      variant="default"
                      size={SCHEDULED_PREFILL_BUTTON_SIZE}
                      onClick={() => onLogin(true)}
                      disabled={reuseIntegrationDisabled}
                    >
                      {t(`${containersKey}.reuseIntegrationLogin`)}
                    </Button>
                  </span>
                </Tooltip>
              )}
              <Button
                type="button"
                variant="default"
                size={SCHEDULED_PREFILL_BUTTON_SIZE}
                onClick={onSelectGames}
                disabled={
                  selectionDisabled ||
                  containerActionPending ||
                  !isRunning ||
                  !isReady ||
                  gameSelectionLoading
                }
                loading={gameSelectionLoading}
              >
                {t(`${baseKey}.actions.selectGames`)}
                <Badge variant="info">{selectedGamesCount}</Badge>
              </Button>
              {selectedGamesCount > 0 && (
                <Button
                  type="button"
                  variant="default"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  onClick={onClearGames}
                  disabled={selectionDisabled || containerActionPending || isPrefilling}
                >
                  {t(`${baseKey}.actions.clearGames`)}
                </Button>
              )}
              {!isAnonymous && isRunning && isAuthenticated && (
                <Button
                  type="button"
                  variant="default"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  onClick={onLogout}
                  disabled={
                    disabled || isPrefilling || action === 'start' || containerActionPending
                  }
                  loading={action === 'logout'}
                >
                  {t('prefill.persistent.logOut')}
                </Button>
              )}
              {isRunning && (
                <Button
                  type="button"
                  variant="default"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  onClick={onStop}
                  disabled={disabled || action === 'start' || containerActionPending}
                  loading={action === 'stop'}
                >
                  {t('prefill.persistent.actions.stop')}
                </Button>
              )}
            </footer>
          </>
        )}
        {containerSettings && (
          <div className="scheduled-prefill-persistent-card__settings">
            <Button
              type="button"
              variant="transparent"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              className="scheduled-prefill-persistent-card__settings-toggle"
              disabled={disabled}
              aria-expanded={settingsOpen}
              aria-controls={settingsId}
              onClick={() => setSettingsOpen((open) => !open)}
              leftSection={
                <ChevronDown
                  size={16}
                  aria-hidden="true"
                  className={`transition-transform duration-300 motion-reduce:transition-none ${settingsOpen ? 'rotate-180' : ''}`}
                />
              }
            >
              {t(`${baseKey}.settings.sharedContainerSettings`)}
            </Button>
            <CollapsibleRegion
              open={settingsOpen}
              contentClassName="scheduled-prefill-persistent-card__settings-content"
            >
              <div id={settingsId} className="flex flex-col gap-4">
                {containerSettings(disabled)}
              </div>
            </CollapsibleRegion>
          </div>
        )}
      </fieldset>
    </Card>
  );
}
