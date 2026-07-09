import React, { useState, useEffect, useRef } from 'react';
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
import type { CancelKind } from '@contexts/notifications/types';

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
    new CustomEvent('show-toast', { detail: { type: 'error', message: i18n.t(i18nKey) } })
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

  switch (notification.status) {
    case 'completed':
      return 'var(--theme-success)';
    case 'failed':
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

  if (notification.status === 'failed') {
    return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color }} />;
  }

  // For generic notifications (toast-style)
  if (notification.details?.notificationType) {
    const iconMap: Record<string, React.ReactNode> = {
      success: <CheckCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-success)]" />,
      error: <XCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-error)]" />,
      warning: <AlertCircle className="w-4 h-4 flex-shrink-0 text-[var(--theme-warning)]" />,
      info: <Info className="w-4 h-4 flex-shrink-0 text-[var(--theme-info)]" />
    };
    return iconMap[notification.details.notificationType] || iconMap.info;
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
  <div className="text-sm font-medium text-themed-primary break-words sm:truncate">
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
  if (notification.status !== 'running' || notification.progress === undefined) {
    return null;
  }

  // Some types don't show progress bars. xbox_game_mapping (login + catalog resolve) is a
  // quick operation with no meaningful granular progress, so it shows a status message only.
  if (notification.type === 'service_removal' || notification.type === 'xbox_game_mapping') {
    return null;
  }

  const color = getNotificationColor(notification);

  return (
    <div className="mt-2">
      <div className="w-full rounded-full h-2 bg-[var(--theme-bg-tertiary)]">
        <div
          className="h-2 rounded-full progress-bar-animate"
          style={{
            backgroundColor: color,
            width: `${Math.max(0, Math.min(100, notification.progress))}%`
          }}
        />
      </div>
      <div className="flex justify-between items-center mt-1">
        <span className="text-xs text-themed-muted">
          {t('common.notifications.progressComplete', {
            value: notification.progress.toFixed(1)
          })}
        </span>
        {notification.details?.estimatedTime && (
          <span className="text-xs text-themed-muted">
            {t('common.notifications.remaining', { value: notification.details.estimatedTime })}
          </span>
        )}
      </div>
    </div>
  );
};

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
      className="flex items-start sm:items-center gap-3 p-2 rounded-lg bg-[var(--theme-bg-secondary)] transition-opacity duration-300 ease-out"
      style={{
        borderLeft: `3px solid ${color}`,
        opacity: isAnimatingOut ? 0 : 1
      }}
    >
      {icon}

      <div className="flex-1 min-w-0">
        {renderTitle()}

        {/* Detail message (except for service_removal which shows details differently) */}
        {notification.detailMessage && notification.type !== 'service_removal' && (
          <div className="text-xs text-themed-muted mt-0.5">{notification.detailMessage}</div>
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
                className="p-1 rounded hover:bg-themed-hover transition-colors"
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
        {(notification.status === 'completed' || notification.status === 'failed') && (
          <button
            onClick={onDismiss}
            className="p-1 rounded hover:bg-themed-hover transition-colors"
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

    window.addEventListener('stickynotificationschange', handleStickyChange);
    return () => window.removeEventListener('stickynotificationschange', handleStickyChange);
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

    window.addEventListener('notification-removing', handleNotificationRemoving as EventListener);
    return () =>
      window.removeEventListener(
        'notification-removing',
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

  return (
    <div className={`w-full ${!stickyDisabled ? 'sticky top-12 z-40 md:top-0 md:z-50' : ''}`}>
      <div
        className="w-full border-b shadow-sm bg-[var(--theme-nav-bg)] border-[var(--theme-nav-border)] transition duration-300 ease-out"
        style={{
          transform: isAnimatingOut ? 'translateY(-100%)' : 'translateY(0)',
          opacity: isAnimatingOut ? 0 : 1
        }}
      >
        <div className="container mx-auto px-4 py-2 space-y-2">
          {/* Unified Notifications - completed/failed first, then running */}
          {[...notifications]
            .sort((a, b) => {
              const statusOrder: Partial<Record<NotificationStatus, number>> = {
                completed: 0,
                failed: 1,
                cancelled: 1,
                running: 2,
                cancelling: 2,
                waiting: 3,
                pending: 4
              };
              return (statusOrder[a.status] ?? 4) - (statusOrder[b.status] ?? 4);
            })
            .map((notification) => (
              <UnifiedNotificationItem
                key={notification.id}
                notification={notification}
                onDismiss={() => handleDismiss(notification.id)}
                onCancel={getCancelHandler(notification)}
                isAnimatingOut={dismissingIds.has(notification.id)}
              />
            ))}
        </div>
      </div>
    </div>
  );
};

export default UniversalNotificationBar;
