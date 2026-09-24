import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, AlertCircle, X, XCircle, Info, Clock, MinusCircle } from 'lucide-react';
import type { UnifiedNotification } from '@contexts/notifications';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { formatCount, formatBytes } from '@utils/formatters';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { NOTIFICATION_TITLE_KEYS } from '@contexts/notifications/notificationTitleKeys';
import { CANCEL_CONFIG_BY_TYPE, getNotificationVariant } from './notificationCancel';
import './UnifiedNotificationItem.css';

const FORCE_KILL_TOOLTIP_KEY = 'common.notifications.forceKillOperation';

// ============================================================================
// Notification Helper Functions
// ============================================================================

/**
 * Gets the appropriate icon for a notification based on its status and type. Every icon takes
 * the card's status color from `notification-card__icon`.
 */
const getNotificationIcon = (notification: UnifiedNotification): React.ReactNode => {
  // A card that carries its own semantic in details.notificationType draws that icon, checked
  // BEFORE the status branches: the bridge marks every toast status 'completed', and a run that
  // succeeded with a warning ends 'completed' too, which otherwise short-circuits either one into
  // the CheckCircle.
  if (notification.details?.notificationType) {
    const iconMap: Record<string, React.ReactNode> = {
      success: <CheckCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />,
      error: <XCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />,
      warning: <AlertCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />,
      info: <Info className="notification-card__icon w-4 h-4 flex-shrink-0" />
    };
    return iconMap[notification.details.notificationType] || iconMap.info;
  }

  // Work in flight holds the same icon slot as every other status, so a card's text never moves
  // sideways when its run ends.
  if (
    notification.status === 'running' ||
    notification.status === 'pending' ||
    notification.status === 'cancelling'
  ) {
    return (
      <LoadingSpinner
        inline
        size="sm"
        className="notification-card__icon flex-shrink-0 motion-reduce:animate-none"
      />
    );
  }

  if (notification.status === 'waiting') {
    // Queued behind a conflicting operation - clock, not spinner (nothing is running yet).
    return <Clock className="notification-card__icon w-4 h-4 flex-shrink-0" />;
  }

  if (notification.status === 'skipped') {
    // Nothing was done, so neither the tick nor the cross fits. A struck-through circle says
    // the run passed over its work rather than finishing it or failing at it.
    return <MinusCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />;
  }

  if (notification.status === 'completed') {
    // A cancelled run did not finish its work, so the cross says so where the tick would lie.
    // Its color rides the status color, which is gray rather than the red of a failure.
    if (notification.details?.cancelled) {
      return <XCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />;
    }
    return <CheckCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />;
  }

  // 'cancelled' is its own terminal status (the standard completion handler sets it when the
  // server reports cancelled:true) - without this branch the card renders with no icon at all.
  if (notification.status === 'failed' || notification.status === 'cancelled') {
    return <XCircle className="notification-card__icon w-4 h-4 flex-shrink-0" />;
  }

  return null;
};

// ============================================================================
// Type-Specific Content Renderers
// ============================================================================

interface ContentRendererProps {
  notification: UnifiedNotification;
  t: (key: string, options?: Record<string, unknown>) => string;
  formatBytesLocal: (bytes: number) => string;
}

/**
 * Renders the completion summary line for the types that report one; the card shows it in its
 * detail slot, under the detail message.
 */
const renderCompletionDetails = ({ notification, t, formatBytesLocal }: ContentRendererProps) => {
  const filesDeletedCount = notification.details?.filesDeleted ?? 0;
  const filesDeletedFormatted = formatCount(filesDeletedCount);

  switch (notification.type) {
    case 'cache_clearing':
      if (!notification.details?.filesDeleted) return null;
      return (
        <div>
          {t('common.notifications.filesDeleted', {
            count: filesDeletedCount,
            formattedCount: filesDeletedFormatted
          })}
          {notification.details.bytesDeleted !== undefined &&
            notification.details.bytesDeleted > 0 &&
            ` \u2022 ${t('common.notifications.freed', { value: formatBytesLocal(notification.details.bytesDeleted) })}`}
        </div>
      );

    case 'service_removal':
      if (notification.status !== 'completed') return null;
      return (
        <div>
          {t('common.notifications.cacheFilesDeleted', {
            count: filesDeletedCount,
            formattedCount: filesDeletedFormatted
          })}
          {notification.details?.bytesFreed !== undefined &&
            ` • ${formatBytesLocal(notification.details.bytesFreed)}`}
        </div>
      );

    case 'corruption_removal':
      if (notification.status !== 'completed') return null;
      return <div>{t('common.notifications.corruptedChunksRemoved')}</div>;

    case 'game_removal':
      if (notification.status !== 'completed') return null;
      return (
        <div>
          {t('common.notifications.cacheFilesDeleted', {
            count: filesDeletedCount,
            formattedCount: filesDeletedFormatted
          })}
          {notification.details?.logEntriesRemoved !== undefined &&
            notification.details.logEntriesRemoved > 0 &&
            ` • ${t('common.notifications.logEntriesRemoved', {
              count: notification.details.logEntriesRemoved,
              formattedCount: formatCount(notification.details.logEntriesRemoved)
            })}`}
          {` • ${t('common.notifications.freed', { value: formatBytesLocal(notification.details?.bytesFreed || 0) })}`}
        </div>
      );

    default:
      return null;
  }
};

/**
 * Renders the progress bar for running operations.
 */
const renderProgressBar = ({ notification, t }: ContentRendererProps) => {
  if (notification.status !== 'running') {
    return null;
  }

  // A card that carries no numeric progress renders no bar.
  const isIndeterminate = notification.progressMode === 'indeterminate';
  if (!isIndeterminate && notification.progress === undefined) return null;

  const rawProgress = Number.isFinite(notification.progress) ? (notification.progress ?? 0) : 0;
  const clampedProgress = Math.max(0, Math.min(100, rawProgress));
  const ariaValueText =
    notification.progressAriaValueText ??
    (notification.detailMessage
      ? `${notification.message} ${notification.detailMessage}`
      : notification.message);

  // The fill color follows the card's status color (the card root's `notification-status--*`
  // class), so the bar reads the same as the border and icon. Only the width is per render.
  return (
    <div className="mt-2 tabular-nums">
      <div
        className="notification-progress-track"
        role="progressbar"
        aria-label={notification.message}
        aria-valuetext={ariaValueText}
        {...(isIndeterminate
          ? {}
          : { 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-valuenow': clampedProgress })}
      >
        {isIndeterminate ? (
          <div className="notification-progress-indeterminate" />
        ) : (
          <div
            className="notification-progress-fill"
            style={{ '--progress-width': `${clampedProgress}%` } as React.CSSProperties}
          />
        )}
      </div>
      {!isIndeterminate && (
        <div className="flex flex-wrap justify-between items-center gap-x-3 mt-1">
          <span className="text-xs text-themed-muted tabular-nums">
            {t('common.notifications.progressComplete', {
              value: clampedProgress.toFixed(1)
            })}
          </span>
          {notification.details?.estimatedTime && (
            <span className="text-xs text-themed-muted">
              {t('common.notifications.remaining', { value: notification.details.estimatedTime })}
            </span>
          )}
        </div>
      )}
    </div>
  );
};

const ANNOUNCEMENT_MIN_INTERVAL_MS = 5000;

/** Rate-limit screen-reader updates while keeping stage/terminal changes immediate. */
function useNotificationAnnouncement(notification: UnifiedNotification): string {
  const { t } = useTranslation();
  const recovering =
    notification.details?.recovering === true ||
    notification.details?.connectionRecovering === true;
  const cancelling =
    notification.status === 'cancelling' || notification.details?.cancelRequested === true;
  const terminalText =
    notification.type === 'scheduled_prefill' && isTerminalNotificationStatus(notification.status)
      ? notification.status === 'cancelled' || notification.details?.cancelled
        ? t('prefill.runs.cancelled')
        : notification.status === 'failed'
          ? t('prefill.runs.failed')
          : notification.status === 'skipped'
            ? t('prefill.runs.skipped')
            : t('prefill.runs.completed')
      : null;
  const accessibleText = [
    terminalText,
    notification.progressAriaValueText ??
      [
        cancelling ? t('prefill.progress.cancelling') : null,
        recovering ? t('prefill.progress.reconnecting') : null,
        notification.message,
        notification.detailMessage
      ]
        .filter(Boolean)
        .join(' ')
  ]
    .filter(Boolean)
    .join(' ');
  const [announcement, setAnnouncement] = useState(accessibleText);
  const lastAnnouncementRef = useRef({
    text: accessibleText,
    recovering,
    cancelling,
    at: Date.now(),
    message: notification.message,
    wholePercent: Math.floor(notification.progress ?? 0),
    terminal: isTerminalNotificationStatus(notification.status)
  });

  useEffect(() => {
    const now = Date.now();
    const wholePercent = Math.floor(notification.progress ?? 0);
    const terminal = isTerminalNotificationStatus(notification.status);
    const previous = lastAnnouncementRef.current;
    const shouldAnnounce =
      recovering !== previous.recovering ||
      cancelling !== previous.cancelling ||
      (notification.message !== previous.message && notification.type !== 'scheduled_prefill') ||
      (terminal && !previous.terminal) ||
      ((wholePercent !== previous.wholePercent || accessibleText !== previous.text) &&
        now - previous.at >= ANNOUNCEMENT_MIN_INTERVAL_MS);

    if (shouldAnnounce) {
      if (accessibleText !== previous.text) setAnnouncement(accessibleText);
      lastAnnouncementRef.current = {
        text: accessibleText,
        recovering,
        cancelling,
        at: now,
        message: notification.message,
        wholePercent,
        terminal
      };
    }
  }, [
    accessibleText,
    notification.message,
    notification.progress,
    notification.status,
    notification.type,
    recovering,
    cancelling
  ]);

  return announcement;
}

// Unified notification component that handles all types
// Note: CSS transitions handle animation smoothness outside React's render cycle
export const UnifiedNotificationItem = React.memo(function UnifiedNotificationItem({
  notification,
  onDismiss,
  onCancel,
  isAnimatingOut,
  connectionLost
}: {
  notification: UnifiedNotification;
  onDismiss: (notificationId: string) => void;
  onCancel?: (notification: UnifiedNotification) => void;
  isAnimatingOut?: boolean;
  /** True while the connection banner is up; the bar reads it once and passes it to every card. */
  connectionLost: boolean;
}) {
  const { t } = useTranslation();
  const { status: webApiStatus } = useSteamWebApiStatus();

  // Setup format bytes helper - uses centralized formatter
  const formatBytesLocal = (bytes: number) => formatBytes(bytes, 2, '0 B');

  const rendererProps: ContentRendererProps = {
    notification,
    t,
    formatBytesLocal
  };

  const icon = getNotificationIcon(notification);
  const titleKey = NOTIFICATION_TITLE_KEYS[notification.type];
  const announcement = useNotificationAnnouncement(notification);

  if (notification.controlOnly && !isTerminalNotificationStatus(notification.status)) {
    const canForceStop =
      notification.details?.cancelRequested === true &&
      notification.details?.cancelSent === true &&
      Boolean(notification.details?.operationId) &&
      CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind === 'serverOp';
    // A waiting row says what it waits for; its message is already a sentence. [106]
    const isWaiting = notification.status === 'waiting' && !notification.details?.cancelRequested;

    return (
      <div className="background-task-control-row rounded text-sm text-themed-primary">
        <span className="background-task-control-row__name">
          {titleKey ? t(titleKey) : notification.message}
          {notification.details?.service && (
            <span className="capitalize"> · {notification.details.service}</span>
          )}
          {notification.details?.gameName && <> · {notification.details.gameName}</>}
        </span>
        <span
          className={`background-task-control-row__status text-xs text-themed-secondary${isWaiting ? '' : ' capitalize'}`}
          role="status"
        >
          {isWaiting
            ? notification.message
            : t(
                `common.notifications.condensedStatus.${notification.details?.cancelRequested ? 'cancelling' : notification.status}`
              )}
        </span>
        {/* A cancel cannot reach an unreachable server, so the row offers none until the
            connection returns; it has no close button either. */}
        {onCancel && !connectionLost && (
          <Button
            type="button"
            onClick={() => onCancel(notification)}
            variant="filled"
            color="stop"
            size="sm"
            className="background-task-control-row__cancel"
            loading={notification.details?.cancelPending}
            disabled={notification.details?.cancelRequested && !canForceStop}
            stableWidth
            aria-busy={notification.details?.cancelPending === true}
            aria-label={t(
              canForceStop
                ? 'common.notifications.forceStop'
                : 'common.notifications.cancelOperationAria'
            )}
            title={canForceStop ? t(FORCE_KILL_TOOLTIP_KEY) : undefined}
          >
            {t(canForceStop ? 'common.notifications.forceStop' : 'common.cancel')}
          </Button>
        )}
      </div>
    );
  }

  // A cancel cannot reach an unreachable server, so a live run card offers its close button in the
  // cancel button's place; it hides the card on this screen only. Only run cards carry operationIds.
  const closable =
    connectionLost &&
    notification.details?.operationIds !== undefined &&
    !isTerminalNotificationStatus(notification.status);
  const completionSummary = renderCompletionDetails(rendererProps);
  // Depot mapping says how it reached Steam: signed in or anonymous, and whether a Web API key
  // is configured.
  const authBadges =
    notification.type === 'depot_mapping' && notification.details?.isLoggedOn !== undefined ? (
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="neutral">
          {notification.details.isLoggedOn
            ? t('common.notifications.steamAuthenticated')
            : t('common.notifications.steamAnonymous')}
        </Badge>
        {webApiStatus?.hasApiKey && (
          <Badge variant="info">{t('common.notifications.webApiKey')}</Badge>
        )}
      </div>
    ) : null;

  return (
    <div
      className={`notification-card notification-status--${getNotificationVariant(notification)}${
        isAnimatingOut ? ' opacity-0' : ''
      } flex items-start sm:items-center gap-3 p-2 rounded bg-[var(--theme-bg-secondary)] transition-opacity duration-300 ease-out motion-reduce:transition-none`}
    >
      {icon}

      {/* Screen readers need a live region, so this repeats the card's own sentence. It sits
          above the visible message in the DOM, so selecting the card used to copy that sentence
          twice; select-none leaves it out of the selection without changing what is announced. */}
      <div
        className="sr-only select-none"
        role={notification.status === 'failed' ? 'alert' : 'status'}
        aria-live={notification.status === 'failed' ? 'assertive' : 'polite'}
        aria-atomic="true"
      >
        {announcement}
      </div>

      <div className="flex-1 min-w-0">
        {titleKey && (
          <div className="mb-1.5 font-mono text-[11px] leading-none font-semibold tracking-[0.08em] uppercase text-themed-secondary">
            {t(titleKey)}
          </div>
        )}

        {/* One layout for every type: message, reconnecting line, detail slot, progress, error.
            Every line wraps rather than truncates, because a cut-off stage line hides the only
            explanation the card gives. */}
        <div className={titleKey ? 'notification-card__body' : undefined}>
          <div className="text-sm font-medium text-themed-primary break-words">
            {notification.message}
          </div>

          {(notification.details?.recovering || notification.details?.connectionRecovering) &&
            !isTerminalNotificationStatus(notification.status) && (
              <p className="text-xs text-themed-muted mt-0.5">
                {t('prefill.progress.reconnectingMessage')}
              </p>
            )}

          {/* Detail slot: the detail message, then a finished card's summary, then depot
              mapping's sign-in badges. */}
          {(notification.detailMessage || completionSummary || authBadges) && (
            <div className="mt-0.5 min-w-0 space-y-0.5 text-xs text-themed-muted whitespace-normal break-words tabular-nums">
              {notification.detailMessage && <div>{notification.detailMessage}</div>}
              {completionSummary}
              {authBadges}
            </div>
          )}

          {/* Progress bar for running operations */}
          {renderProgressBar(rendererProps)}

          {/* Error message - only show if different from main message */}
          {notification.error && notification.error !== notification.message && (
            <div className="text-xs text-themed-muted mt-0.5 break-words">{notification.error}</div>
          )}
        </div>
      </div>

      {/* Action buttons */}
      <div className="flex items-center gap-2 flex-shrink-0">
        {/* Cancel button for operations that support cancellation. 'waiting' is cancellable
            too: the queued op is a real tracker registration, so the universal cancel path
            dequeues it and its run row ends the card.
            A serverOp cancel is an API call keyed by details.operationId - the same field
            handleCancel bails on - so a card that has none shows no X. Every run card carries its
            id from its first row; a sign-in card has none until its login has an operation.
            clientQueue cards carry no operation id by design and keep theirs. */}
        {!closable &&
          notification.type in CANCEL_CONFIG_BY_TYPE &&
          (notification.status === 'running' ||
            notification.status === 'waiting' ||
            notification.status === 'cancelling') &&
          (CANCEL_CONFIG_BY_TYPE[notification.type].cancelKind !== 'serverOp' ||
            Boolean(notification.details?.operationId)) &&
          onCancel && (
            <Tooltip
              content={t(
                notification.details?.cancelRequested &&
                  CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind === 'serverOp'
                  ? FORCE_KILL_TOOLTIP_KEY
                  : CANCEL_CONFIG_BY_TYPE[notification.type].tooltipKey
              )}
              position="left"
            >
              <button
                onClick={() => onCancel(notification)}
                disabled={notification.details?.cancelPending}
                className="flex h-11 w-11 min-h-11 min-w-11 items-center justify-center rounded transition-colors hover:bg-themed-hover motion-reduce:transition-none"
                aria-label={
                  notification.details?.cancelRequested &&
                  CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind === 'serverOp'
                    ? t(FORCE_KILL_TOOLTIP_KEY)
                    : t('common.notifications.cancelOperationAria')
                }
              >
                {notification.details?.cancelPending ? (
                  <LoadingSpinner inline size="sm" />
                ) : (
                  <X className="w-4 h-4 text-themed-secondary" />
                )}
              </button>
            </Tooltip>
          )}
        {(isTerminalNotificationStatus(notification.status) || closable) && (
          <button
            onClick={() => onDismiss(notification.id)}
            className="flex h-11 w-11 min-h-11 min-w-11 items-center justify-center rounded transition-colors hover:bg-themed-hover motion-reduce:transition-none"
            aria-label={t('common.dismiss')}
          >
            <X className="w-4 h-4 text-themed-secondary" />
          </button>
        )}
      </div>
    </div>
  );
});
