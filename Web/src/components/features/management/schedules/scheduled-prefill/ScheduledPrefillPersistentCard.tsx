import { type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import StatusDot from '@components/common/StatusDot';
import { formatTimeRemaining } from '@components/features/prefill/types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useCountdownTimer } from '@hooks/useCountdownTimer';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import {
  getPersistentServiceId,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import { usePersistentLoginStoreState } from './persistentLoginStore';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import type { ScheduledPrefillPersistentCardProps } from './scheduledPrefillPersistentTypes';
import { getIntegrationReasonKey } from '../../../../../types';

type StatusTone = 'idle' | 'warning' | 'info' | 'running';

interface StatusDisplay {
  tone: StatusTone;
  label: string;
  busy: boolean;
}

export function ScheduledPrefillPersistentCard({
  serviceKey,
  onStop,
  onLogout,
  container,
  disabled = false,
  statusLoading = false,
  authenticating = false,
  integrationLoginAvailability,
  integrationLoginAvailabilityLoading = false,
  action = null,
  onStart,
  onLogin
}: ScheduledPrefillPersistentCardProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const containersKey = `${baseKey}.persistentContainers`;
  const authExpiresAt = useFormattedDateTime(container?.authExpiresAtUtc);
  const timeRemaining = useCountdownTimer(container?.authExpiresAtUtc ?? null, false);

  const isAnonymous = isScheduledPrefillAnonymousService(serviceKey);

  const loginState = usePersistentLoginStoreState(getPersistentServiceId(serviceKey));
  const loginError = isAnonymous ? null : loginState.error;

  const sessionUnavailableState = isAnonymous ? null : loginState.sessionUnavailableState;
  const isSessionUnavailable = sessionUnavailableState !== null;

  const activity = useActivityStatus();
  const activityPlatformKey = serviceKey.toLowerCase();
  const isRunning =
    container?.isRunning ??
    activity.isActive('persistentContainer', activityPlatformKey, 'running');
  const isAuthenticated =
    container?.isAuthenticated ??
    activity.isActive('persistentContainer', activityPlatformKey, 'authenticated');
  const isPrefilling = container?.isPrefilling ?? false;

  const isReady = isAnonymous || isAuthenticated;
  const isAuthInProgress = !isAnonymous && isRunning && !isAuthenticated && authenticating;
  const savedLoginHint = (() => {
    if (integrationLoginAvailabilityLoading) return t(`${containersKey}.savedLoginChecking`);
    if (integrationLoginAvailability?.available && integrationLoginAvailability.account?.trim()) {
      return t(`${containersKey}.savedLoginAvailable`, {
        account: integrationLoginAvailability.account
      });
    }
    if (integrationLoginAvailability?.available) return t('errors.integration.loginAvailable');
    if (integrationLoginAvailability?.available === false) {
      return t(getIntegrationReasonKey(integrationLoginAvailability.reason));
    }
    return t('errors.integration.statusUnavailable');
  })();

  const containerActionPending = action === 'stop' || action === 'logout';
  const reuseIntegrationDisabled =
    disabled ||
    containerActionPending ||
    isAuthInProgress ||
    integrationLoginAvailabilityLoading ||
    !integrationLoginAvailability?.available;

  const isContainerLoading = statusLoading && container === undefined;

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
  } else {
    primaryAction = null;
  }

  return (
    <Card padding="md" className="scheduled-prefill-persistent-card">
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
                    {timeRemaining > 0
                      ? t('prefill.persistent.timeRemaining', {
                          time: formatTimeRemaining(timeRemaining)
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
                  content={disabled ? null : savedLoginHint}
                  className="scheduled-prefill-persistent-card__login-help"
                >
                  <span
                    tabIndex={!disabled && reuseIntegrationDisabled ? 0 : undefined}
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
      </fieldset>
    </Card>
  );
}
