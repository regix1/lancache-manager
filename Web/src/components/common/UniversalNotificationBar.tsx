import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import {
  CheckCircle,
  AlertCircle,
  X,
  User,
  UserX,
  Trash2,
  XCircle,
  Info,
  Key,
  Clock
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
import themeService from '@services/theme.service';
import { Tooltip } from '@components/ui/Tooltip';
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
import { CondensedNotificationItem } from './CondensedNotificationItem';

// ============================================================================
// Cancellable Operation Types (derived from the registry — single source)
// ============================================================================

interface CancelConfig {
  cancelKind: CancelKind;
  tooltipKey: string;
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
      map[entry.type] = { cancelKind: entry.cancelKind, tooltipKey: entry.cancelTooltipKey };
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
  removeNotification: (id: string) => void
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

  if (!cancelRequested) {
    updateNotification(notification.id, {
      details: { ...notification.details, cancelRequested: true, cancelSent: true }
    });

    try {
      await ApiService.cancelOperation(operationId);
    } catch (err) {
      console.error('Cancel failed:', getErrorMessage(err));
      const errorMessage = err instanceof Error ? err.message : '';
      if (
        errorMessage.includes('not found') ||
        errorMessage.includes('Not Found') ||
        errorMessage.includes('cannot be cancelled')
      ) {
        removeNotification(notification.id);
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
      removeNotification(notification.id);
    } else {
      notifyToastError('common.notifications.forceKillOperationFailed');
    }
  }
};

// ============================================================================
// Notification Helper Functions
// ============================================================================

/**
 * Gets the status color for a notification based on its current state.
 * Blue (info) = Running/Processing
 * Green (success) = Completed
 * Red (error) = Failed/Cancelled
 */
const getNotificationColor = (notification: UnifiedNotification): string => {
  if (notification.details?.cancelled) {
    return 'var(--theme-error)';
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

  switch (notification.status) {
    case 'completed':
      return 'var(--theme-success)';
    case 'failed':
    case 'cancelled':
      return 'var(--theme-error)';
    case 'waiting':
      return 'var(--theme-waiting)';
    case 'running':
    default:
      return 'var(--theme-info)';
  }
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

  if (notification.status === 'completed') {
    // Show XCircle (error icon) for cancelled operations
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
        <span className="flex items-center gap-1 text-xs px-2 py-0.5 rounded flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-muted)]">
          {notification.details.isLoggedOn ? (
            <>
              <User className="w-3 h-3" />
              <span>{t('common.notifications.steamAuthenticated')}</span>
            </>
          ) : (
            <>
              <UserX className="w-3 h-3" />
              <span>{t('common.notifications.steamAnonymous')}</span>
            </>
          )}
        </span>
        {/* Show Web API Key pill when API key is configured */}
        {webApiStatus?.hasApiKey && (
          <span className="flex items-center gap-1 text-xs px-2 py-0.5 rounded flex-shrink-0 bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]">
            <Key className="w-3 h-3" />
            <span>{t('common.notifications.webApiKey')}</span>
          </span>
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

      <div
        className="sr-only"
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
            dequeues it (-> OperationWaitingComplete{cancelled} -> card terminal). */}
        {notification.type in CANCEL_CONFIG_BY_TYPE &&
          (notification.status === 'running' || notification.status === 'waiting') &&
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

  // Per-service display preference (full | condensed), live from the Schedules page. Empty until
  // seeded; an absent key resolves to full, so this drives display only and never the transport.
  const displayModes = useScheduleDisplayModes();
  // 768px anchors the established table/tile split; below it the bar caps full cards.
  const isMobile = useMediaQuery('(max-width: 767px)');
  // Hover-expand keys off pointer capability, not viewport width: a mouse-driven window between
  // the mobile boundary and a desktop breakpoint can still hover, while a large touch screen
  // cannot (its compatibility mouse events would latch a hover open with no way to unhover).
  const canHover = useMediaQuery('(hover: hover) and (pointer: fine)');
  // Ephemeral per-notification expand state for condensed lines (tap/keyboard on the thin bar);
  // deliberately not persisted, matching that no notification card persists its expand state.
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());

  const toggleExpanded = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }, []);

  // Prune expanded ids once their notifications are gone so the set can't leak across a session.
  useEffect(() => {
    setExpandedIds((prev) => {
      if (prev.size === 0) return prev;
      const currentIds = new Set(notifications.map((n) => n.id));
      let changed = false;
      const next = new Set<string>();
      prev.forEach((id) => {
        if (currentIds.has(id)) {
          next.add(id);
        } else {
          changed = true;
        }
      });
      return changed ? next : prev;
    });
  }, [notifications]);

  // Tracks notifications where a deferred cancel has already been fired, so the
  // watchdog effect below never fires the same cancel twice when notifications
  // re-render. Pruned as notifications disappear.
  const deferredCancelFiredRef = useRef<Set<string>>(new Set());

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
    } else if (shouldRender) {
      // Start animation out when notifications are cleared
      setIsAnimatingOut(true);
      // Wait for animation to complete before unmounting
      const timer = setTimeout(() => {
        setShouldRender(false);
        setIsAnimatingOut(false);
      }, NOTIFICATION_ANIMATION_DURATION_MS);

      return () => clearTimeout(timer);
    }
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

    return () => handleCancel(notification, updateNotification, removeNotification);
  };

  // Don't render if no notifications and not animating
  if (!shouldRender) {
    return null;
  }

  const statusOrder: Partial<Record<NotificationStatus, number>> = {
    completed: 0,
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
          fullItems.length > 0 ? 'border-b shadow-sm border-[var(--theme-nav-border)]' : ''
        }`}
        style={{
          transform: isAnimatingOut ? 'translateY(-100%)' : 'translateY(0)',
          opacity: isAnimatingOut ? 0 : 1
        }}
      >
        {/* Condensed lines span the bar edge to edge, flush under the navigation, so they read
            as the nav's own bottom edge. Rendered only when present, so the default all-full
            desktop path is the untouched full-card container below. */}
        {condensedGroups.size > 0 && (
          <div className="condensed-notification-strip">
            {[...condensedGroups.entries()].map(([groupKey, group]) => {
              // The live run outranks terminal toasts for the line's colour, fill, and pulse:
              // the line always wears the notification's status colour (blue running, green
              // completed, red failed) and pulses softly while work is ongoing.
              const representative =
                group.find((n) => !isTerminalNotificationStatus(n.status)) ?? group[0];
              const lineColor = getNotificationColor(representative);
              return (
                <CondensedNotificationItem
                  key={groupKey}
                  notification={representative}
                  groupCount={group.length}
                  color={lineColor}
                  canHover={canHover}
                  tapExpanded={expandedIds.has(groupKey)}
                  onTapToggle={() => toggleExpanded(groupKey)}
                >
                  <div className="container mx-auto px-4 pb-2 space-y-2">
                    {group.map((notification) => (
                      <UnifiedNotificationItem
                        key={notification.id}
                        notification={notification}
                        onDismiss={() => handleDismiss(notification.id)}
                        onCancel={getCancelHandler(notification)}
                        isAnimatingOut={dismissingIds.has(notification.id)}
                      />
                    ))}
                  </div>
                </CondensedNotificationItem>
              );
            })}
          </div>
        )}
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
