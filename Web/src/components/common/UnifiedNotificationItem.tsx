import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import {
  CheckCircle,
  AlertCircle,
  X,
  Trash2,
  XCircle,
  Info,
  Clock,
  MinusCircle
} from 'lucide-react';
import type { UnifiedNotification } from '@contexts/notifications';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { formatCount, formatBytes } from '@utils/formatters';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { NOTIFICATION_TITLE_KEYS } from '@contexts/notifications/notificationTitleKeys';
import { CANCEL_CONFIG_BY_TYPE, getNotificationColor } from './notificationCancel';

const FORCE_KILL_TOOLTIP_KEY = 'common.notifications.forceKillOperation';

// ============================================================================
// Notification Helper Functions
// ============================================================================

/**
 * Gets the appropriate icon for a notification based on its status and type.
 */
const getNotificationIcon = (notification: UnifiedNotification): React.ReactNode => {
  const color = getNotificationColor(notification);

  // Toast-style (generic) notifications: icon follows details.notificationType,
  // checked BEFORE the status branches - the bridge marks every toast
  // status 'completed', which otherwise short-circuits an error toast into the
  // green CheckCircle.
  if (notification.type === 'generic' && notification.details?.notificationType) {
    const iconMap: Record<string, React.ReactNode> = {
      success: <CheckCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-success)]" />,
      error: <XCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-error)]" />,
      warning: <AlertCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-warning)]" />,
      info: <Info className="w-4 h-4 flex-shrink-0 text-[var(--theme-info)]" />
    };
    return iconMap[notification.details.notificationType] || iconMap.info;
  }

  if (notification.status === 'running') {
    return <LoadingSpinner inline size="sm" className="flex-shrink-0" style={{ color }} />;
  }

  if (notification.status === 'waiting') {
    // Queued behind a conflicting operation - clock, not spinner (nothing is running yet).
    return <Clock className="w-4 h-4 flex-shrink-0 text-[var(--theme-waiting)]" />;
  }

  if (notification.status === 'skipped') {
    // Nothing was done, so neither the tick nor the cross fits. A struck-through circle says
    // the run passed over its work rather than finishing it or failing at it. The colour is a
    // constant for this status, so it is a class like the waiting branch above, not an inline style.
    return <MinusCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-warning)]" />;
  }

  if (notification.status === 'completed') {
    // A cancelled run did not finish its work, so the cross says so where the tick would lie.
    // Its colour rides the status colour, which is grey rather than the red of a failure.
    if (notification.details?.cancelled) {
      return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color }} />;
    }
    return <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color }} />;
  }

  // 'cancelled' is its own terminal status (the standard completion handler sets it when the
  // server reports cancelled:true) - without this branch the card renders with no icon at all.
  if (notification.status === 'failed' || notification.status === 'cancelled') {
    return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color }} />;
  }

  return null;
};

// ============================================================================
// Type-Specific Content Renderers
// ============================================================================

interface ContentRendererProps {
  notification: UnifiedNotification;
  t: (key: string, options?: Record<string, unknown>) => string;
  webApiStatus: { hasApiKey?: boolean } | null | undefined;
  formatBytesLocal: (bytes: number) => string;
}

/**
 * Renders the title/message area for game removal notifications.
 */
const renderGameRemovalTitle = ({ notification }: ContentRendererProps) => (
  <div className="flex items-center gap-2">
    <Trash2 className="w-3 h-3 text-themed-muted flex-shrink-0" />
    <span className="text-sm font-medium text-themed-primary break-words sm:truncate">
      {notification.message}
    </span>
  </div>
);

/**
 * Renders the title/message area for depot mapping notifications.
 */
const renderDepotMappingTitle = ({ notification, t, webApiStatus }: ContentRendererProps) => (
  <div className="flex items-center gap-2 flex-wrap">
    <span className="text-sm font-medium text-themed-primary">{notification.message}</span>
    {/* Auth mode badge for depot mapping */}
    {notification.details?.isLoggedOn !== undefined && (
      <div className="flex items-center gap-2">
        <Badge variant="neutral">
          {notification.details.isLoggedOn
            ? t('common.notifications.steamAuthenticated')
            : t('common.notifications.steamAnonymous')}
        </Badge>
        {/* Show Web API Key pill when API key is configured */}
        {webApiStatus?.hasApiKey && (
          <Badge variant="info">{t('common.notifications.webApiKey')}</Badge>
        )}
      </div>
    )}
  </div>
);

/**
 * Renders the default title/message area.
 */
const renderDefaultTitle = ({ notification }: ContentRendererProps) => (
  <div
    className={`text-sm font-medium text-themed-primary break-words ${notification.type === 'corruption_detection' ? 'whitespace-normal' : 'sm:truncate'}`}
  >
    {notification.message}
  </div>
);

/**
 * Renders completion details for various notification types.
 */
const renderCompletionDetails = ({ notification, t, formatBytesLocal }: ContentRendererProps) => {
  const filesDeletedCount = notification.details?.filesDeleted ?? 0;
  const filesDeletedFormatted = formatCount(filesDeletedCount);

  switch (notification.type) {
    case 'cache_clearing':
      if (!notification.details?.filesDeleted) return null;
      return (
        <div className="text-xs text-themed-muted mt-0.5">
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
        <div className="text-xs text-themed-muted mt-0.5">
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
      return (
        <div className="text-xs text-themed-muted mt-0.5">
          {t('common.notifications.corruptedChunksRemoved')}
        </div>
      );

    case 'game_removal':
      if (notification.status !== 'completed') return null;
      return (
        <div className="text-xs text-themed-muted mt-0.5">
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

  // Some types don't show a progress bar. service_removal reports status text only. Cards that
  // carry no numeric progress still render no bar via the `progress === undefined` guard below.
  if (notification.type === 'service_removal') {
    return null;
  }

  const isIndeterminate = notification.progressMode === 'indeterminate';
  if (!isIndeterminate && notification.progress === undefined) return null;

  const rawProgress = Number.isFinite(notification.progress) ? (notification.progress ?? 0) : 0;
  const clampedProgress = Math.max(0, Math.min(100, rawProgress));
  const ariaValueText =
    notification.progressAriaValueText ??
    (notification.detailMessage
      ? `${notification.message} ${notification.detailMessage}`
      : notification.message);

  // The fill colour follows the card's status colour, so the bar reads the same as the
  // border and icon. It is passed as a custom property rather than a Tailwind class
  // because the value is a theme variable resolved at runtime.
  const trackStyle = {
    '--notification-progress-color': getNotificationColor(notification)
  } as React.CSSProperties;

  return (
    <div className="mt-2 tabular-nums">
      <div
        className="notification-progress-track"
        style={trackStyle}
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
          <div className="notification-progress-fill" style={{ width: `${clampedProgress}%` }} />
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
  const accessibleText =
    notification.progressAriaValueText ??
    [notification.message, notification.detailMessage].filter(Boolean).join(' ');
  const [announcement, setAnnouncement] = useState(accessibleText);
  const lastAnnouncementRef = useRef({
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
      notification.message !== previous.message ||
      (terminal && !previous.terminal) ||
      (wholePercent !== previous.wholePercent && now - previous.at >= ANNOUNCEMENT_MIN_INTERVAL_MS);

    if (shouldAnnounce) {
      setAnnouncement(accessibleText);
      lastAnnouncementRef.current = {
        at: now,
        message: notification.message,
        wholePercent,
        terminal
      };
    }
  }, [accessibleText, notification.message, notification.progress, notification.status]);

  return announcement;
}

// Unified notification component that handles all types
// Note: CSS transitions handle animation smoothness outside React's render cycle
export const UnifiedNotificationItem = ({
  notification,
  onDismiss,
  onCancel,
  isAnimatingOut
}: {
  notification: UnifiedNotification;
  onDismiss: () => void;
  onCancel?: () => void;
  isAnimatingOut?: boolean;
}) => {
  const { t } = useTranslation();
  const { status: webApiStatus } = useSteamWebApiStatus();

  // Setup format bytes helper - uses centralized formatter
  const formatBytesLocal = (bytes: number) => formatBytes(bytes, 2, '0 B');

  const rendererProps: ContentRendererProps = {
    notification,
    t,
    webApiStatus,
    formatBytesLocal
  };

  const color = getNotificationColor(notification);
  const icon = getNotificationIcon(notification);
  const titleKey = NOTIFICATION_TITLE_KEYS[notification.type];
  const announcement = useNotificationAnnouncement(notification);

  // Determine which title renderer to use
  const renderTitle = () => {
    switch (notification.type) {
      case 'game_removal':
        return renderGameRemovalTitle(rendererProps);
      case 'depot_mapping':
        return renderDepotMappingTitle(rendererProps);
      default:
        return renderDefaultTitle(rendererProps);
    }
  };

  return (
    <div
      className="flex items-start sm:items-center gap-3 p-2 rounded-lg bg-[var(--theme-bg-secondary)] transition-opacity duration-300 ease-out motion-reduce:transition-none"
      style={{
        borderLeft: `3px solid ${color}`,
        opacity: isAnimatingOut ? 0 : 1
      }}
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

        {renderTitle()}

        {/* Detail message (except for service_removal which shows details differently) */}
        {notification.detailMessage && notification.type !== 'service_removal' && (
          <div className="text-xs text-themed-muted mt-0.5 min-w-0 whitespace-normal break-words tabular-nums">
            {notification.detailMessage}
          </div>
        )}

        {/* Type-specific completion details */}
        {renderCompletionDetails(rendererProps)}

        {/* Progress bar for running operations */}
        {renderProgressBar(rendererProps)}

        {/* Error message - only show if different from main message */}
        {notification.error && notification.error !== notification.message && (
          <div className="text-xs text-themed-muted mt-0.5">{notification.error}</div>
        )}
      </div>

      {/* Action buttons */}
      <div className="flex items-center gap-2 flex-shrink-0">
        {/* Cancel button for operations that support cancellation. 'waiting' is cancellable
            too: the queued op is a real tracker registration, so the universal cancel path
            dequeues it (-> OperationWaitingComplete{cancelled} -> card terminal).
            A serverOp cancel is an API call keyed by details.operationId - the same field
            handleCancel bails on - so a card that has none shows no X, unless its registry entry
            sets allowsDeferredCancel, meaning the id arrives on a later progress event and the
            watchdog in the bar sends the cancel then. Sign-in cards have neither: their X only set
            cancelRequested, sent nothing, and then re-labelled itself a force kill. clientQueue
            cards carry no operation id by design and keep theirs. */}
        {notification.type in CANCEL_CONFIG_BY_TYPE &&
          (notification.status === 'running' || notification.status === 'waiting') &&
          (CANCEL_CONFIG_BY_TYPE[notification.type].cancelKind !== 'serverOp' ||
            CANCEL_CONFIG_BY_TYPE[notification.type].allowsDeferredCancel ||
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
                onClick={onCancel}
                className="flex h-11 w-11 min-h-11 min-w-11 items-center justify-center rounded transition-colors hover:bg-themed-hover motion-reduce:transition-none"
                aria-label={
                  notification.details?.cancelRequested &&
                  CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind === 'serverOp'
                    ? t(FORCE_KILL_TOOLTIP_KEY)
                    : t('common.notifications.cancelOperationAria')
                }
              >
                <X className="w-4 h-4 text-themed-secondary" />
              </button>
            </Tooltip>
          )}
        {isTerminalNotificationStatus(notification.status) && (
          <button
            onClick={onDismiss}
            className="flex h-11 w-11 min-h-11 min-w-11 items-center justify-center rounded transition-colors hover:bg-themed-hover motion-reduce:transition-none"
            aria-label={t('common.dismiss')}
          >
            <X className="w-4 h-4 text-themed-secondary" />
          </button>
        )}
      </div>
    </div>
  );
};
