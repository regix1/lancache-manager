import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import StatusDot from '@components/common/StatusDot';
import { formatTimeRemaining } from '@components/features/prefill/types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useCountdownTimer } from '@hooks/useCountdownTimer';
import { loginAttemptTimeoutMs } from '@hooks/loginAttemptTimeout';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import {
  getPersistentServiceId,
  getScheduledPrefillServiceStatus,
  getScheduledPrefillStatusFact,
  isScheduledPrefillAnonymousService,
  SCHEDULED_PREFILL_PLATFORM_UI
} from './scheduledPrefillPlatformUi';
import {
  getPersistentLoginFailure,
  isPersistentLoginIntegrationReuse,
  usePersistentLoginCanceling,
  usePersistentLoginStoreState
} from './persistentLoginStore';
import type { ScheduledPrefillPersistentCardProps } from './scheduledPrefillPersistentTypes';
import { getIntegrationReasonKey } from '../../../../../types';

// The service dialog body: a Container section and, for account services, an Account section.
export function ScheduledPrefillPersistentCard({
  serviceKey,
  onStop,
  onLogout,
  container,
  disabled = false,
  listLoaded,
  listFailed,
  justLoggedIn,
  actionError,
  actionNotice,
  integrationLoginError,
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
  const serviceId = getPersistentServiceId(serviceKey);
  const serviceName = t(`prefill.persistent.services.${serviceKey}`);
  const ServiceIcon = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey].icon;

  const loginState = usePersistentLoginStoreState(serviceId);
  const loginCanceling = usePersistentLoginCanceling(serviceId);
  const loginError = isAnonymous ? null : loginState.error;

  const sessionUnavailableState = isAnonymous ? null : loginState.sessionUnavailableState;
  const isSessionUnavailable = sessionUnavailableState !== null;

  const status = getScheduledPrefillServiceStatus(serviceKey, {
    container,
    listLoaded,
    listFailed,
    action,
    authenticating,
    loginError: isAnonymous ? null : getPersistentLoginFailure(loginState)
  });
  const containerFact = getScheduledPrefillStatusFact(status.container, container, t);
  // While a cancel is still ending the old attempt, Log in waits; the status line says why [96].
  const accountFact = loginCanceling
    ? { tone: null, busy: true, label: t(`${baseKey}.serviceStatus.cancelingLogin`) }
    : getScheduledPrefillStatusFact(status.account, container, t);
  const savedLoginInProgress =
    status.account === 'loggingIn' && isPersistentLoginIntegrationReuse(serviceId);
  const accountLabel = savedLoginInProgress
    ? t(`${baseKey}.account.savedLoginInProgress`)
    : accountFact.label;
  // A concurrent-run daemon reports its downloads as runs, not isPrefilling, and Log out cancels them.
  const isPrefilling = status.container === 'downloading';
  const actionPending = action !== null;
  const needsLogin =
    status.account === 'loginRequired' ||
    status.account === 'loginExpired' ||
    status.account === 'loginFailed' ||
    status.account === 'loggingIn';
  const loginDisabled =
    disabled || actionPending || status.account === 'loggingIn' || loginCanceling;

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
  const reuseIntegrationDisabled =
    loginDisabled ||
    integrationLoginAvailabilityLoading ||
    !integrationLoginAvailability?.available;

  const loginKey =
    serviceKey === 'steam' ? 'loginToSteam' : serviceKey === 'epic' ? 'loginToEpic' : 'loginToXbox';
  const containerRunning = container?.isRunning === true;
  // The Log out fallback marks its notice with a flag in the failure slot; the notice is not a
  // failure, so it skips the error wording.
  const actionErrorAlert = actionNotice ? (
    <Alert color="red">{t('prefill.persistent.messages.logoutFallbackNotice')}</Alert>
  ) : (
    actionError && (
      <Alert color="red">{t(`${baseKey}.persistentContainer.error`, { error: actionError })}</Alert>
    )
  );

  // With no status known the dialog shows only the load error and its Retry; sections would read
  // "Unknown" with nothing to act on.
  if (status.container === 'unknown') return null;

  return (
    <>
      <section className="scheduled-prefill-platform-block">
        <h3 className="scheduled-prefill-platform-block__title">
          {t(`${containersKey}.steps.container`)}
        </h3>
        {/* Start and stop errors share one slot with login errors, so an account service shows
            them in its Account section and an anonymous one here. */}
        {isAnonymous && actionErrorAlert}
        <span
          className="scheduled-prefill-persistent-card__status"
          role="status"
          aria-live="polite"
        >
          {containerFact.busy && <LoadingSpinner inline size="xs" />}
          {containerFact.tone !== null && (
            <StatusDot tone={containerFact.tone} label={containerFact.label} />
          )}
          <span className="scheduled-prefill-persistent-card__status-text">
            {containerFact.label}
          </span>
        </span>
        {isAnonymous && (
          <p className="scheduled-prefill-persistent-card__hint">
            {t(`${baseKey}.account.anonymous`, { service: serviceName })}
          </p>
        )}
        <div className="scheduled-prefill-persistent-card__actions">
          {containerRunning ? (
            <Button
              type="button"
              variant="default"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={onStop}
              disabled={disabled || actionPending}
            >
              {t('prefill.persistent.actions.stop')}
            </Button>
          ) : (
            <Button
              type="button"
              variant="filled"
              color="run"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={onStart}
              disabled={disabled || actionPending || status.container === 'checking'}
            >
              {t('prefill.persistent.actions.start')}
            </Button>
          )}
        </div>
      </section>

      {!isAnonymous && (
        <section className="scheduled-prefill-platform-block">
          <h3 className="scheduled-prefill-platform-block__title">
            {t(`${containersKey}.steps.account`)}
          </h3>
          {actionErrorAlert}
          {integrationLoginError && (
            <Alert color="red">
              {t(`${baseKey}.summaryError`, { error: integrationLoginError })}
            </Alert>
          )}
          {isSessionUnavailable && (
            <Alert color="yellow">
              {t(
                sessionUnavailableState === 'errored'
                  ? 'prefill.persistent.sessionErrored'
                  : 'prefill.persistent.sessionUnavailable'
              )}
            </Alert>
          )}
          {loginError &&
            !isSessionUnavailable &&
            (loginState.endReason === 'timedOut' ? (
              <Alert color="red" title={t('prefill.persistent.loginTimedOutTitle')}>
                {t('prefill.persistent.loginTimedOutBody', {
                  count: Math.round(loginAttemptTimeoutMs(serviceId) / 60_000)
                })}
              </Alert>
            ) : (
              <Alert color="red" title={t('common.errors.signInFailed', { platform: serviceName })}>
                {loginError}
              </Alert>
            ))}
          {justLoggedIn && status.account === 'loggedIn' && (
            <Alert
              color="green"
              title={t(`${baseKey}.account.loggedInNote.title`, { service: serviceName })}
            >
              {t(`${baseKey}.account.loggedInNote.body`, { service: serviceName })}
            </Alert>
          )}
          <span
            className="scheduled-prefill-persistent-card__status"
            role="status"
            aria-live="polite"
          >
            {accountFact.busy && <LoadingSpinner inline size="xs" />}
            {accountFact.tone !== null && (
              <StatusDot tone={accountFact.tone} label={accountLabel} />
            )}
            <span className="scheduled-prefill-persistent-card__status-text">{accountLabel}</span>
          </span>
          {savedLoginInProgress && (
            <p className="scheduled-prefill-persistent-card__hint">
              {t(`${baseKey}.account.savedLoginWait`)}
            </p>
          )}

          {(needsLogin || status.account === 'checkedAfterStart') && (
            <p className="scheduled-prefill-persistent-card__hint">
              {t(`${baseKey}.account.loginHelp`, { service: serviceName })}
            </p>
          )}
          {needsLogin && (
            <div className="scheduled-prefill-persistent-card__actions scheduled-prefill-persistent-card__actions--login">
              <Button
                type="button"
                variant="filled"
                color="primary"
                size={SCHEDULED_PREFILL_BUTTON_SIZE}
                leftSection={<ServiceIcon size={16} />}
                onClick={() => onLogin(false)}
                disabled={loginDisabled}
              >
                {t(`${baseKey}.${loginKey}`)}
              </Button>
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
            </div>
          )}

          {(status.account === 'loggedIn' || status.account === 'loggingOut') && container && (
            <>
              <div className="scheduled-prefill-persistent-card__fact">
                <span className="caps-label scheduled-prefill-persistent-card__fact-label">
                  {t('prefill.persistent.reloginRequiredBy')}
                </span>
                <span className="scheduled-prefill-persistent-card__meta-value tabular-nums">
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
              {container.needsRelogin && !loginError && (
                <p className="scheduled-prefill-persistent-card__hint scheduled-prefill-persistent-card__hint--warning">
                  {t('prefill.persistent.needsRelogin')}
                </p>
              )}
              <div className="scheduled-prefill-persistent-card__actions">
                <Button
                  type="button"
                  variant="default"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  onClick={onLogout}
                  disabled={disabled || isPrefilling || actionPending}
                >
                  {t('prefill.persistent.logOut')}
                </Button>
              </div>
              {isPrefilling && (
                <p className="scheduled-prefill-persistent-card__hint">
                  {t(`${baseKey}.account.logoutWhileDownloading`)}
                </p>
              )}
            </>
          )}
        </section>
      )}
    </>
  );
}
