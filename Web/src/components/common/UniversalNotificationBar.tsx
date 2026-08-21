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
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import i18n from '../../i18n';
import {
  useNotifications,
  type UnifiedNotification,
  type NotificationStatus,
  NOTIFICATION_ANIMATION_DURATION_MS
} from '@contexts/notifications';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { formatCount, formatBytes } from '@utils/formatters';
import { VARIANT_BY_STATUS } from '@utils/statusVariant';
import themeService from '@services/theme.service';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { NOTIFICATION_REGISTRY } from '@contexts/notifications/notificationRegistry';
import {
  SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY,
  MOBILE_FULL_CARD_CAP
} from '@contexts/notifications/constants';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import type { CancelKind } from '@contexts/notifications/types';
import { NOTIFICATION_TITLE_KEYS } from '@contexts/notifications/notificationTitleKeys';
import { APP_EVENTS } from '@utils/constants';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { useScheduleDisplayModes } from '@hooks/useScheduleDisplayModes';
import { CondensedNotificationStrip } from './CondensedNotificationStrip';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';

// ============================================================================
// Cancellable Operation Types (derived from the registry — single source)
// ============================================================================

interface CancelConfig {
  cancelKind: CancelKind;
  tooltipKey: string;
  allowsDeferredCancel: boolean;
}

/**
 * Per-type cancel config derived from NOTIFICATION_REGISTRY (every entry with
 * cancelKind !== 'none' that carries a tooltip key). This includes the
 * client-only `bulk_removal` type, whose metadata-only registry entry
 * (cancelKind 'clientQueue') makes the X button flip a flag the always-mounted
 * BulkRemovalProvider's cascade effect observes — so the registry loop is the
 * single source for cancel wiring.
 */
const CANCEL_CONFIG_BY_TYPE: Record<string, CancelConfig> = (() => {
  const map: Record<string, CancelConfig> = {};
  for (const entry of NOTIFICATION_REGISTRY) {
    if (entry.cancelKind !== 'none' && entry.cancelTooltipKey) {
      map[entry.type] = {
        cancelKind: entry.cancelKind,
        tooltipKey: entry.cancelTooltipKey,
        allowsDeferredCancel: entry.allowsDeferredCancel === true
      };
    }
  }
  return map;
})();

// ============================================================================
// Cancel Handler
// ============================================================================

const FORCE_KILL_TOOLTIP_KEY = 'common.notifications.forceKillOperation';

/**
 * Surface a genuine cancel/force-kill failure to the user via the `show-toast` bridge. `handleCancel`
 * is a module-level helper (not a hook/component), so `useErrorHandler` is unavailable here - this
 * mirrors it using the documented non-hook escape hatch, which NotificationsContext bridges into the
 * same generic notification the hook would create.
 */
const notifyToastError = (i18nKey: string): void => {
  window.dispatchEvent(
    new CustomEvent(APP_EVENTS.SHOW_TOAST, {
      detail: { type: 'error', message: i18n.t(i18nKey) }
    })
  );
};

const handleCancel = async (
  notification: UnifiedNotification,
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void,
  removeNotification: (id: string) => void,
  getLiveNotification: (id: string) => UnifiedNotification | undefined
) => {
  const cancelKind = CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind ?? 'none';

  // Client-driven bulk notifications (cancelKind 'clientQueue') are not tied to
  // a single server operation - the initiating BulkRemovalProvider orchestrates
  // a loop of per-item operations. Flip cancelRequested/cancelling=true for UI
  // feedback ONLY. The provider lives at app root and never unmounts, so its
  // cascade effect always observes the flag and cancels the live run - no
  // module-level registry bridge is needed.
  if (cancelKind === 'clientQueue') {
    updateNotification(notification.id, {
      details: { ...notification.details, cancelRequested: true, cancelling: true }
    });
    return;
  }

  // cancelKind === 'serverOp' below (cancelKind 'none' types never reach here -
  // they show no cancel button).
  const operationId = notification.details?.operationId;
  const cancelRequested = notification.details?.cancelRequested === true;

  // Race case: user clicked X before operationId arrived. Remember intent; watchdog fires cancel when opId lands.
  if (!operationId) {
    updateNotification(notification.id, {
      details: { ...notification.details, cancelRequested: true }
    });
    return;
  }

  // A card's slot id is shared between a queued operation and the run promoted in its place, so by
  // the time the server answers, this card may already belong to a DIFFERENT operation that is
  // still going. Drop it only while it still carries the operation this click cancelled - the same
  // guard the waiting-complete handler applies for the same promotion window.
  const removeIfStillThisOperation = (): void => {
    if (getLiveNotification(notification.id)?.details?.operationId === operationId) {
      removeNotification(notification.id);
    }
  };

  if (!cancelRequested) {
    updateNotification(notification.id, {
      details: { ...notification.details, cancelRequested: true, cancelSent: true }
    });

    try {
      const result = await ApiService.cancelOperation(operationId);
      if (result.alreadyFinished === true) {
        // The operation was already terminal, so no cancellation event will ever arrive for this
        // card. Its terminal event was gated out or lost, leaving it stuck on screen - drop it,
        // exactly as the "already gone" branch below does when the operation has been evicted.
        removeIfStillThisOperation();
      }
    } catch (err) {
      console.error('Cancel failed:', getErrorMessage(err));
      const errorMessage = err instanceof Error ? err.message : '';
      if (
        errorMessage.includes('not found') ||
        errorMessage.includes('Not Found') ||
        errorMessage.includes('cannot be cancelled')
      ) {
        removeIfStillThisOperation();
      } else {
        // Genuine cancel failure (not the "already gone" case above) - the operation is still
        // running, so tell the user rather than leaving the reset X button as the only signal.
        // This is a module-level helper (no hooks available), so report via the show-toast bridge.
        notifyToastError('common.notifications.cancelOperationFailed');
        updateNotification(notification.id, {
          details: { ...notification.details, cancelRequested: false, cancelSent: false }
        });
      }
    }
    return;
  }

  updateNotification(notification.id, {
    details: { ...notification.details, cancelSent: true }
  });

  try {
    await ApiService.forceKillOperation(operationId);
  } catch (err) {
    console.error('Force kill failed:', getErrorMessage(err));
    const errorMessage = err instanceof Error ? err.message : '';
    if (errorMessage.includes('not found') || errorMessage.includes('Not Found')) {
      removeIfStillThisOperation();
    } else {
      notifyToastError('common.notifications.forceKillOperationFailed');
    }
  }
};

// ============================================================================
// Notification Helper Functions
// ============================================================================

/**
 * The badge variant a status resolves to, drawn as a solid line colour. `neutral` has no
 * status token of its own and borrows the grey the neutral badge fill is built from
 * (`badges.css:93`), so grey means the same thing on a card as it does on a pill.
 */
const STATUS_COLOR_BY_VARIANT: Record<string, string> = {
  success: 'var(--theme-success)',
  error: 'var(--theme-error)',
  warning: 'var(--theme-warning)',
  info: 'var(--theme-info)',
  waiting: 'var(--theme-waiting)',
  neutral: 'var(--theme-text-secondary)'
};

/**
 * Gets the status color for a notification based on its current state, via the shared
 * status vocabulary so a card and a badge never disagree about the same word.
 */
const getNotificationColor = (notification: UnifiedNotification): string => {
  if (notification.details?.cancelled) {
    return STATUS_COLOR_BY_VARIANT[VARIANT_BY_STATUS['cancelled']];
  }

  // Toast-style notifications carry their real semantic in details.notificationType
  // (the show-toast bridge stores every toast with status 'completed'), so color by
  // that semantic first - an error toast must read red, never completed-green.
  // Scoped to generic so operation cards keep pure status semantics.
  if (notification.type === 'generic' && notification.details?.notificationType) {
    const typeColorMap: Record<string, string> = {
      success: 'var(--theme-success)',
      error: 'var(--theme-error)',
      warning: 'var(--theme-warning)',
      info: 'var(--theme-info)'
    };
    const typeColor = typeColorMap[notification.details.notificationType];
    if (typeColor) {
      return typeColor;
    }
  }

  // `skipped` is amber because the run did nothing, so it is neither the green of a finished
  // run nor the red of a broken one, and warning already has a glow tone in the condensed strip.
  // `pending` and `cancelling` carry no row of their own: both are still in flight, so they
  // read the way a running run does.
  return STATUS_COLOR_BY_VARIANT[VARIANT_BY_STATUS[notification.status] ?? 'info'];
};

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
          {notification.details?.bytesDeleted !== undefined &&
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

// ============================================================================
// Main Components
// ============================================================================

// Unified notification component that handles all types
// Note: CSS transitions handle animation smoothness outside React's render cycle
const UnifiedNotificationItem = ({
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
            watchdog below sends the cancel then. Sign-in cards have neither: their X only set
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

const UniversalNotificationBar: React.FC = () => {
  const { notifications, removeNotification, updateNotification } = useNotifications();
  const [stickyDisabled, setStickyDisabled] = useState(
    themeService.getDisableStickyNotificationsSync()
  );
  const [isAnimatingOut, setIsAnimatingOut] = useState(false);
  const [shouldRender, setShouldRender] = useState(false);
  const [dismissingIds, setDismissingIds] = useState<Set<string>>(new Set());
  // Whether the condensed strip's revealed cards are currently in the bar's flow. They share
  // this bar's surface with the full cards, so the bar's bottom border and shadow must hold
  // under them even when no full card renders below the strip.
  const [stripOpen, setStripOpen] = useState(false);

  // Per-service display preference (full | condensed), live from the Schedules page. Empty until
  // seeded; an absent key resolves to full, so this drives display only and never the transport.
  const displayModes = useScheduleDisplayModes();
  // 768px anchors the established table/tile split; below it the bar caps full cards.
  const isMobile = useMediaQuery('(max-width: 767px)');
  // Hover-expand keys off pointer capability, not viewport width: a mouse-driven window between
  // the mobile boundary and a desktop breakpoint can still hover, while a large touch screen
  // cannot (its compatibility mouse events would latch a hover open with no way to unhover).
  const canHover = useMediaQuery('(hover: hover) and (pointer: fine)');
  // Tracks notifications where a deferred cancel has already been fired, so the
  // watchdog effect below never fires the same cancel twice when notifications
  // re-render. Pruned as notifications disappear.
  const deferredCancelFiredRef = useRef<Set<string>>(new Set());

  // The cancel round trip is async, so handleCancel's captured card can be stale by the time the
  // server answers. It reads the committed list through this ref instead before removing anything.
  const notificationsRef = useRef<UnifiedNotification[]>(notifications);
  useEffect(() => {
    notificationsRef.current = notifications;
  }, [notifications]);

  // Deferred-cancel watchdog: only when user clicked X before operationId existed.
  useEffect(() => {
    notifications.forEach((n) => {
      const opId = n.details?.operationId;
      // Watchdog is serverOp-only: clientQueue (bulk_removal) carries no
      // server operationId and is cancelled via the provider cascade instead.
      if (
        n.status === 'running' &&
        CANCEL_CONFIG_BY_TYPE[n.type]?.cancelKind === 'serverOp' &&
        n.details?.cancelRequested &&
        !n.details?.cancelSent &&
        opId &&
        !deferredCancelFiredRef.current.has(n.id)
      ) {
        deferredCancelFiredRef.current.add(n.id);
        // Reset cancelRequested so the NEXT real click is a soft cancel, not a premature force-kill.
        updateNotification(n.id, {
          details: { ...n.details, cancelRequested: false, cancelSent: true }
        });
        // Background retry of a cancel the user already requested - the notification stays visible
        // either way, so this only needs a console trail, not a second user-facing error.
        ApiService.cancelOperation(opId).catch((err) => {
          console.error('[UniversalNotificationBar] Deferred cancel failed:', getErrorMessage(err));
        });
      }
    });

    // Prune entries whose notifications are no longer in the list so the set
    // doesn't leak across long sessions.
    const currentIds = new Set(notifications.map((n) => n.id));
    deferredCancelFiredRef.current.forEach((id) => {
      if (!currentIds.has(id)) deferredCancelFiredRef.current.delete(id);
    });
  }, [notifications, updateNotification]);

  // Listen for sticky notifications setting changes
  useEffect(() => {
    const handleStickyChange = () => {
      setStickyDisabled(themeService.getDisableStickyNotificationsSync());
    };

    window.addEventListener(APP_EVENTS.STICKY_NOTIFICATIONS_CHANGE, handleStickyChange);
    return () =>
      window.removeEventListener(APP_EVENTS.STICKY_NOTIFICATIONS_CHANGE, handleStickyChange);
  }, []);

  // Listen for notification removal events (for auto-dismiss animation)
  useEffect(() => {
    const handleNotificationRemoving = (event: CustomEvent) => {
      const notificationId = event.detail.notificationId;
      setDismissingIds((prev) => new Set(prev).add(notificationId));

      // Clean up after animation completes
      setTimeout(() => {
        setDismissingIds((prev) => {
          const newSet = new Set(prev);
          newSet.delete(notificationId);
          return newSet;
        });
      }, NOTIFICATION_ANIMATION_DURATION_MS);
    };

    window.addEventListener(
      APP_EVENTS.NOTIFICATION_REMOVING,
      handleNotificationRemoving as EventListener
    );
    return () =>
      window.removeEventListener(
        APP_EVENTS.NOTIFICATION_REMOVING,
        handleNotificationRemoving as EventListener
      );
  }, []);

  // Handle animation when notifications appear/disappear
  useEffect(() => {
    if (notifications.length > 0) {
      // Show immediately when notifications appear
      setShouldRender(true);
      setIsAnimatingOut(false);
      return;
    }
    if (!shouldRender) {
      return;
    }
    // The bar just emptied. Do NOT start the slide-out immediately: a burst can remove the last
    // notification and add a new one a fraction of a second later (one scheduled run finishing as
    // the next appears, or a run that finishes almost instantly). Starting the exit right away and
    // then reversing it when the new notification lands paints a visible dip-and-return flash on
    // the whole bar. So hold the bar fully visible for one animation beat first; a notification
    // that arrives during the hold cancels both timers with nothing ever dipped. Only a bar still
    // empty after the hold plays the slide-out, then unmounts when it finishes.
    const holdTimer = window.setTimeout(
      () => setIsAnimatingOut(true),
      NOTIFICATION_ANIMATION_DURATION_MS
    );
    const unmountTimer = window.setTimeout(() => {
      setShouldRender(false);
      setIsAnimatingOut(false);
    }, NOTIFICATION_ANIMATION_DURATION_MS * 2);

    return () => {
      window.clearTimeout(holdTimer);
      window.clearTimeout(unmountTimer);
    };
  }, [notifications.length, shouldRender]);

  // Animated dismiss handler
  const handleDismiss = (notificationId: string) => {
    // Add to dismissing set to trigger animation
    setDismissingIds((prev) => new Set(prev).add(notificationId));

    // Wait for animation to complete, then remove
    setTimeout(() => {
      removeNotification(notificationId);
      setDismissingIds((prev) => {
        const newSet = new Set(prev);
        newSet.delete(notificationId);
        return newSet;
      });
    }, NOTIFICATION_ANIMATION_DURATION_MS);
  };

  // Create cancel handler for a notification
  const getCancelHandler = (notification: UnifiedNotification) => {
    if (!(notification.type in CANCEL_CONFIG_BY_TYPE)) {
      return undefined;
    }

    return () =>
      handleCancel(notification, updateNotification, removeNotification, (id: string) =>
        notificationsRef.current.find((n) => n.id === id)
      );
  };

  // Don't render if no notifications and not animating
  if (!shouldRender) {
    return null;
  }

  const statusOrder: Partial<Record<NotificationStatus, number>> = {
    completed: 0,
    // Terminal and benign, so it sorts with the finished runs rather than dropping to the end.
    skipped: 0,
    failed: 1,
    cancelled: 1,
    running: 2,
    cancelling: 2,
    waiting: 3,
    pending: 4
  };
  // Completed/failed first, then running (comparator unchanged from the original single-list render).
  const sorted = [...notifications].sort(
    (a, b) => (statusOrder[a.status] ?? 4) - (statusOrder[b.status] ?? 4)
  );

  // Classify each notification (in the sorted order) as condensed or full. A notification
  // condenses when its service is set to condensed, OR on mobile once the full-card cap is
  // reached. Expansion never changes membership: a tap-expanded item stays in the condensed
  // group and reveals its card in place via CollapsibleRegion, so its toggle line keeps focus
  // and remains available to collapse it. The comparator above is untouched; only grouping
  // changes.
  let fullOrder = 0;
  const classified = sorted.map((notification) => {
    // Generic toasts (Run Now acknowledgments) carry their owning serviceKey in details.
    const serviceKey =
      SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY[notification.type] ??
      notification.details?.serviceKey;
    const condensedByService = serviceKey !== undefined && displayModes[serviceKey] === 'condensed';
    const orderAmongFull = condensedByService ? -1 : fullOrder++;
    const condensedByCap = isMobile && orderAmongFull >= MOBILE_FULL_CARD_CAP;
    return { notification, serviceKey, condensed: condensedByService || condensedByCap };
  });
  // One line per service in the condensed group: a manual run's acknowledgment toast and the
  // run's own lifecycle notification fold into a single disclosure instead of stacking a line
  // per notification. Notifications without a serviceKey keep a line each. Map preserves the
  // sorted order via first insertion.
  const condensedGroups = new Map<string, UnifiedNotification[]>();
  for (const item of classified) {
    if (!item.condensed) {
      continue;
    }
    const groupKey =
      item.serviceKey !== undefined ? `svc:${item.serviceKey}` : `id:${item.notification.id}`;
    const group = condensedGroups.get(groupKey);
    if (group) {
      group.push(item.notification);
    } else {
      condensedGroups.set(groupKey, [item.notification]);
    }
  }
  const fullItems = classified.filter((item) => !item.condensed);

  return (
    <div className={`w-full ${!stickyDisabled ? 'sticky top-12 z-40 md:top-0 md:z-50' : ''}`}>
      <div
        className={`w-full bg-[var(--theme-nav-bg)] transition duration-300 ease-out motion-reduce:transition-none ${
          fullItems.length > 0 || stripOpen
            ? 'border-b shadow-sm border-[var(--theme-nav-border)]'
            : ''
        }`}
        style={{
          transform: isAnimatingOut ? 'translateY(-100%)' : 'translateY(0)',
          opacity: isAnimatingOut ? 0 : 1
        }}
      >
        {/* One strip spans the bar edge to edge, flush under the navigation: every compacted
            service keeps its status colour as a segment of the single line (the live run
            outranks terminal toasts for each segment's colour, fill, and pulse), and the whole
            line is one disclosure target. Rendered only when present, so the default all-full
            desktop path is the untouched full-card container below. */}
        {/* Rendered unconditionally: with zero segments the strip renders null itself, after
            fading its line out. A conditional unmount here would blink the line off in one
            frame instead. */}
        {
          <CondensedNotificationStrip
            segments={[...condensedGroups.entries()].map(([groupKey, group]) => {
              const representative =
                group.find((n) => !isTerminalNotificationStatus(n.status)) ?? group[0];
              return {
                key: groupKey,
                notification: representative,
                color: getNotificationColor(representative)
              };
            })}
            canHover={canHover}
            onOpenChange={setStripOpen}
          >
            <div className="container mx-auto px-4 pb-2">
              {/* On a phone the opened panel caps at roughly two cards and scrolls for the rest,
                  so a burst of compacted runs cannot push the page out of reach. Desktop keeps
                  the full-height panel. radius none: cards sit flush against the viewport and a
                  rounded clip would shave their corners. */}
              {isMobile ? (
                <CustomScrollbar maxHeight="12rem" paddingMode="compact" radius="none">
                  <div className="space-y-2">
                    {[...condensedGroups.values()].flat().map((notification) => (
                      <UnifiedNotificationItem
                        key={notification.id}
                        notification={notification}
                        onDismiss={() => handleDismiss(notification.id)}
                        onCancel={getCancelHandler(notification)}
                        isAnimatingOut={dismissingIds.has(notification.id)}
                      />
                    ))}
                  </div>
                </CustomScrollbar>
              ) : (
                <div className="space-y-2">
                  {[...condensedGroups.values()].flat().map((notification) => (
                    <UnifiedNotificationItem
                      key={notification.id}
                      notification={notification}
                      onDismiss={() => handleDismiss(notification.id)}
                      onCancel={getCancelHandler(notification)}
                      isAnimatingOut={dismissingIds.has(notification.id)}
                    />
                  ))}
                </div>
              )}
            </div>
          </CondensedNotificationStrip>
        }
        {fullItems.length > 0 && (
          <div className="container mx-auto px-4 py-2 space-y-2">
            {fullItems.map(({ notification }) => (
              <UnifiedNotificationItem
                key={notification.id}
                notification={notification}
                onDismiss={() => handleDismiss(notification.id)}
                onCancel={getCancelHandler(notification)}
                isAnimatingOut={dismissingIds.has(notification.id)}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;
