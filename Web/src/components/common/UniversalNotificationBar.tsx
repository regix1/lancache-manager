import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import {
  CheckCircle,
  AlertCircle,
  Loader2,
  X,
  User,
  UserX,
  Trash2,
  XCircle,
  Info,
  Key
} from 'lucide-react';
import ApiService from '@services/api.service';
import {
  useNotifications,
  type UnifiedNotification,
  NOTIFICATION_ANIMATION_DURATION_MS
} from '@contexts/notifications';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import themeService from '@services/theme.service';
import { Tooltip } from '@components/ui/Tooltip';
import { formatBytes } from '@utils/formatters';

// ============================================================================
// Cancellable Operation Types
// ============================================================================

const CANCEL_TOOLTIP_KEYS: Record<string, string> = {
  cache_clearing: 'common.notifications.cancelCacheClearing',
  log_removal: 'common.notifications.cancelLogRemoval',
  depot_mapping: 'common.notifications.cancelDepotMapping',
  corruption_removal: 'common.notifications.cancelCorruptionRemoval',
  corruption_detection: 'common.notifications.cancelCorruptionDetection',
  log_processing: 'common.notifications.cancelLogProcessing',
  game_detection: 'common.notifications.cancelGameDetection',
  game_removal: 'common.notifications.cancelGameRemoval',
  service_removal: 'common.notifications.cancelServiceRemoval',
  database_reset: 'common.notifications.cancelDatabaseReset',
  data_import: 'common.notifications.cancelDataImport'
};

// ============================================================================
// Cancel Handler
// ============================================================================

const handleCancel = async (
  notification: UnifiedNotification,
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void,
  removeNotification: (id: string) => void
) => {
  const operationId = notification.details?.operationId;
  if (!operationId) {
    console.error('[UniversalNotificationBar] No operationId for cancel');
    removeNotification(notification.id);
    return;
  }

  updateNotification(notification.id, {
    details: { ...notification.details, cancelling: true }
  });

  try {
    await ApiService.cancelOperation(operationId);
    // Do NOT remove the notification here!
    // The SignalR completion event (with cancelled: true) will:
    // 1. Update the notification status to 'completed' with cancellation message
    // 2. Schedule auto-dismiss after CANCELLED_NOTIFICATION_DELAY_MS (3000ms)
  } catch (err) {
    console.error('Cancel failed:', err);
    const errorMessage = err instanceof Error ? err.message : '';
    if (errorMessage.includes('not found') || errorMessage.includes('Not Found')) {
      // Operation already completed/removed from tracker - dismiss the notification
      removeNotification(notification.id);
    } else {
      // Other error - remove the cancelling state but don't remove the notification
      // The operation may still complete and send a SignalR event
      updateNotification(notification.id, {
        details: { ...notification.details, cancelling: false }
      });
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
    return <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color }} />;
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
    <span className="text-sm font-medium text-themed-primary truncate">{notification.message}</span>
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
  <div className="text-sm font-medium text-themed-primary truncate">{notification.message}</div>
);

/**
 * Renders completion details for various notification types.
 */
const renderCompletionDetails = ({ notification, t, formatBytesLocal }: ContentRendererProps) => {
  const filesDeletedCount = notification.details?.filesDeleted ?? 0;
  const filesDeletedFormatted = filesDeletedCount.toLocaleString();

  switch (notification.type) {
    case 'cache_clearing':
      if (!notification.details?.filesDeleted) return null;
      return (
        <div className="text-xs text-themed-muted mt-0.5">
          {t('common.notifications.filesDeleted', {
            count: filesDeletedCount,
            formattedCount: filesDeletedFormatted
          })}
          {notification.details?.bytesDeleted !== undefined && notification.details.bytesDeleted > 0 &&
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
              formattedCount: notification.details.logEntriesRemoved.toLocaleString()
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

  // Some types don't show progress bars
  if (notification.type === 'service_removal') {
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
      className="flex items-center gap-3 p-2 rounded-lg bg-[var(--theme-bg-secondary)] transition-opacity duration-300 ease-out"
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
        {/* Cancel button for operations that support cancellation */}
        {notification.type in CANCEL_TOOLTIP_KEYS &&
          notification.status === 'running' &&
          onCancel &&
          (notification.details?.cancelling ? (
            <div className="flex items-center gap-1.5 px-2 py-1 rounded text-xs bg-[var(--theme-error-bg)] text-[var(--theme-error)]">
              <Loader2 className="w-3 h-3 animate-spin" />
              <span>{t('common.notifications.cancelling')}</span>
            </div>
          ) : (
            <Tooltip
              content={t(CANCEL_TOOLTIP_KEYS[notification.type])}
              position="left"
            >
              <button
                onClick={onCancel}
                className="p-1 rounded hover:bg-themed-hover transition-colors"
                aria-label={t('common.notifications.cancelOperationAria')}
              >
                <X className="w-4 h-4 text-themed-secondary" />
              </button>
            </Tooltip>
          ))}
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
    if (!(notification.type in CANCEL_TOOLTIP_KEYS)) {
      return undefined;
    }

    return () => handleCancel(notification, updateNotification, removeNotification);
  };

  // Don't render if no notifications and not animating
  if (!shouldRender) {
    return null;
  }

  return (
    <div
      className={`w-full border-b shadow-sm bg-[var(--theme-nav-bg)] border-[var(--theme-nav-border)] transition-all duration-300 ease-out ${!stickyDisabled ? 'notification-bar-sticky' : ''}`}
      style={{
        transform: isAnimatingOut ? 'translateY(-100%)' : 'translateY(0)',
        opacity: isAnimatingOut ? 0 : 1
      }}
    >
      <div className="container mx-auto px-4 py-2 space-y-2">
        {/* Unified Notifications - completed/failed first, then running */}
        {[...notifications]
          .sort((a, b) => {
            const statusOrder = { completed: 0, failed: 1, running: 2, pending: 3 };
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
  );
};

export default UniversalNotificationBar;
