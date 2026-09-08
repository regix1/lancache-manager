import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import {
  ActionMenu,
  ActionMenuDangerItem,
  ActionMenuDivider,
  ActionMenuItem
} from '@components/ui/ActionMenu';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
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

// Overshoot past the footer's bottom so the last row of buttons clears the scroll fold comfortably
// instead of resting flush against (and half-clipped by) the container edge.
const SCROLL_ACTIONS_OVERSHOOT_PX = 32;

// Scroll the modal's scroll area so the card's action footer is fully visible. scrollIntoView with
// block:'nearest' only nudges the nearest edge into view and, if the card is still growing when it
// fires, lands a few pixels short — so instead we find the scrollable ancestor (the modal's
// CustomScrollbar content) and compute the exact distance from live layout, plus an overshoot, then
// clamp to the max scroll. Reading the rects at scroll time (not effect time) keeps it correct even
// if the freshly-grown card hasn't fully settled its height yet.
const scrollActionsIntoView = (footer: HTMLElement | null): void => {
  if (!footer) {
    return;
  }

  let scrollParent: HTMLElement | null = footer.parentElement;
  while (scrollParent) {
    const { overflowY } = getComputedStyle(scrollParent);
    if (
      (overflowY === 'auto' || overflowY === 'scroll') &&
      scrollParent.scrollHeight > scrollParent.clientHeight
    ) {
      break;
    }
    scrollParent = scrollParent.parentElement;
  }

  if (!scrollParent) {
    footer.scrollIntoView({ behavior: 'smooth', block: 'end' });
    return;
  }

  const parentRect = scrollParent.getBoundingClientRect();
  const footerRect = footer.getBoundingClientRect();
  const delta = footerRect.bottom - parentRect.bottom + SCROLL_ACTIONS_OVERSHOOT_PX;
  if (delta <= 0) {
    return;
  }

  const maxScrollTop = scrollParent.scrollHeight - scrollParent.clientHeight;
  const target = Math.min(scrollParent.scrollTop + delta, maxScrollTop);
  scrollParent.scrollTo({ top: target, behavior: 'smooth' });
};

export function ScheduledPrefillPersistentCard({
  serviceKey,
  container,
  selectedGamesCount,
  disabled = false,
  scheduleEnabled,
  statusLoading = false,
  authenticating = false,
  integrationLoginAvailability,
  integrationLoginAvailabilityLoading = false,
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
  const reuseIntegrationDisabled =
    disabled ||
    isAuthInProgress ||
    integrationLoginAvailabilityLoading ||
    !integrationLoginAvailability?.available;
  const isGameSelectionBlocked = isRunning && !isReady;
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
  // What this schedule downloads, and downloading it by hand, belong to the schedule: both grey
  // out while it is switched off. Starting, stopping and logging the container in or out do not,
  // because the container serves every schedule on the platform.
  const selectionDisabled = disabled || !scheduleEnabled;
  // Initial container probe with nothing resolved yet — show the loading view.
  const isContainerLoading = statusLoading && container === undefined;

  // The footer holds every action button. When the container transitions to running the card
  // grows (status line, meta, games, workflow hint), which can push the buttons below the modal's
  // scroll fold. Bring the actions back into view on that transition — same scrollIntoView pattern
  // SchedulesSection uses for its View Schedule buttons. The ref lives on the footer so the buttons
  // themselves land in view, not just the (now off-screen) card header.
  //
  // Gated on a user-initiated Start of THIS card only: `action === 'start'` is set while this card's
  // Start click is in flight, so we remember that intent and consume it when the container actually
  // comes up. A background refresh that flips the container to running (a scheduled run, another
  // tab, a resumed session) never carries that intent, so it never yanks the view.
  const actionsRef = useRef<HTMLElement>(null);
  const wasRunningRef = useRef(isRunning);
  const startRequestedRef = useRef(false);
  const [actionsOpen, setActionsOpen] = useState(false);

  useEffect(() => {
    const wasRunning = wasRunningRef.current;
    wasRunningRef.current = isRunning;

    // Record the in-flight Start click; it stays remembered until the container comes up (consumed
    // below) or the action settles without the container running (cleared below).
    if (action === 'start') {
      startRequestedRef.current = true;
    }

    if (!wasRunning && isRunning && startRequestedRef.current) {
      startRequestedRef.current = false;
      // rAF so the freshly-grown card has settled its layout before we measure/scroll.
      const frame = requestAnimationFrame(() => scrollActionsIntoView(actionsRef.current));
      return () => cancelAnimationFrame(frame);
    }

    // Start settled (button no longer loading) but the container never came up — drop the intent so
    // a later unrelated running transition can't inherit this click's scroll.
    if (action !== 'start' && !isRunning) {
      startRequestedRef.current = false;
    }
  }, [action, isRunning]);

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
    return t(`${containersKey}.workflow.ready`);
  })();

  // The one visible action is the workflow's next step, the same step the hint above the footer
  // names in words: start the container, log in, then download (or cancel the download running).
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
            <StatusDot tone={statusDisplay.tone} label={statusDisplay.label} />
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
                <span className="caps-label scheduled-prefill-persistent-card__meta-label">
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
            </div>
          )}

          <p className="scheduled-prefill-persistent-card__games">
            {t(`${containersKey}.stats.gamesSelected`)}:{' '}
            <strong className="tabular-nums scheduled-prefill-persistent-card__games-count">
              {selectedGamesCount}
            </strong>
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

          {isRunning && !isAnonymous && !isAuthenticated && !isAuthInProgress && (
            <p className="scheduled-prefill-persistent-card__hint">
              {integrationLoginAvailabilityLoading && <LoadingSpinner inline size="xs" />}
              {savedLoginHint}
            </p>
          )}

          <footer ref={actionsRef} className="scheduled-prefill-persistent-card__actions">
            {/* Everything but the next step lives in one menu, in a fixed order, so the footer
                reads the same in every state: an action that cannot run right now is disabled
                rather than missing. The two items that undo work, clearing the selection and
                stopping the container, sit together behind a divider in the danger register. */}
            <ActionMenu
              isOpen={actionsOpen}
              onClose={() => setActionsOpen(false)}
              align="right"
              width="w-48"
              trigger={
                <Button
                  type="button"
                  variant="menu"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  open={actionsOpen}
                  className="w-full"
                  disabled={
                    disabled || action === 'stop' || action === 'logout' || gameSelectionLoading
                  }
                  onClick={() => setActionsOpen((open) => !open)}
                  aria-expanded={actionsOpen}
                  aria-haspopup="menu"
                  rightSection={<ChevronDown size={16} aria-hidden="true" />}
                >
                  {t('management.actions.menuLabel')}
                </Button>
              }
            >
              <ActionMenuItem
                onClick={() => {
                  setActionsOpen(false);
                  onSelectGames();
                }}
                disabled={selectionDisabled || !isRunning || isGameSelectionBlocked}
              >
                {t(`${baseKey}.actions.selectGames`)}
              </ActionMenuItem>
              {isRunning && !isReady && (
                <ActionMenuItem
                  onClick={() => {
                    setActionsOpen(false);
                    onLogin(true);
                  }}
                  disabled={reuseIntegrationDisabled}
                >
                  {t(`${containersKey}.reuseIntegrationLogin`)}
                </ActionMenuItem>
              )}
              {!isAnonymous && isRunning && isAuthenticated && (
                <ActionMenuItem
                  onClick={() => {
                    setActionsOpen(false);
                    onLogout();
                  }}
                  disabled={disabled || isPrefilling || action === 'start' || action === 'stop'}
                >
                  {t('prefill.persistent.logOut')}
                </ActionMenuItem>
              )}
              <ActionMenuDivider />
              <ActionMenuDangerItem
                onClick={() => {
                  setActionsOpen(false);
                  onClearGames();
                }}
                disabled={selectionDisabled || selectedGamesCount === 0 || isPrefilling}
              >
                {t(`${baseKey}.actions.clearGames`)}
              </ActionMenuDangerItem>
              {isRunning && (
                <ActionMenuDangerItem
                  onClick={() => {
                    setActionsOpen(false);
                    onStop();
                  }}
                  disabled={disabled || action === 'start'}
                >
                  {t('prefill.persistent.actions.stop')}
                </ActionMenuDangerItem>
              )}
            </ActionMenu>
            {primaryAction}
          </footer>
        </>
      )}
    </Card>
  );
}
