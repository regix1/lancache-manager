import React, { useState, useEffect } from 'react';
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
import { useNotifications, type UnifiedNotification } from '@contexts/NotificationsContext';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import themeService from '@services/theme.service';
import { Tooltip } from '@components/ui/Tooltip';

// Unified notification component that handles all types
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
  const { status: webApiStatus } = useSteamWebApiStatus();

  const getStatusColor = () => {
    // Check for cancellation first
    if (notification.details?.cancelled) {
      return 'var(--theme-error)';
    }

    switch (notification.status) {
      case 'completed':
        return 'var(--theme-success)';
      case 'failed':
        return 'var(--theme-error)';
      case 'running':
        switch (notification.type) {
          case 'service_removal':
          case 'depot_mapping':
            return 'var(--theme-warning)';
          default:
            return 'var(--theme-info)';
        }
      default:
        return 'var(--theme-info)';
    }
  };

  const getStatusIcon = () => {
    if (notification.status === 'running') {
      return (
        <Loader2
          className="w-4 h-4 animate-spin flex-shrink-0"
          style={{ color: getStatusColor() }}
        />
      );
    } else if (notification.status === 'completed') {
      // Show XCircle (error icon) for cancelled operations
      if (notification.details?.cancelled) {
        return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: getStatusColor() }} />;
      }
      return <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: getStatusColor() }} />;
    } else if (notification.status === 'failed') {
      return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: getStatusColor() }} />;
    } else if (notification.details?.notificationType) {
      // For generic notifications
      switch (notification.details.notificationType) {
        case 'success':
          return (
            <CheckCircle
              className="w-4 h-4 flex-shrink-0"
              style={{ color: 'var(--theme-success)' }}
            />
          );
        case 'error':
          return (
            <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
          );
        case 'warning':
          return (
            <AlertCircle
              className="w-4 h-4 flex-shrink-0"
              style={{ color: 'var(--theme-warning)' }}
            />
          );
        case 'info':
          return <Info className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-info)' }} />;
        default:
          return <Info className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-info)' }} />;
      }
    }
    return null;
  };

  const formatBytes = (bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
  };

  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid ${getStatusColor()}`,
        transition: 'opacity 0.3s ease-out',
        opacity: isAnimatingOut ? 0 : 1
      }}
    >
      {getStatusIcon()}

      <div className="flex-1 min-w-0">
        {/* Title with icon for game removal */}
        {notification.type === 'game_removal' ? (
          <div className="flex items-center gap-2">
            <Trash2 className="w-3 h-3 text-themed-muted flex-shrink-0" />
            <span className="text-sm font-medium text-themed-primary truncate">
              {notification.message}
            </span>
          </div>
        ) : notification.type === 'depot_mapping' ? (
          <div className="flex items-center gap-2 flex-wrap">
            <span className="text-sm font-medium text-themed-primary">{notification.message}</span>
            {/* Auth mode badge for depot mapping */}
            {notification.details?.isLoggedOn !== undefined && (
              <div className="flex items-center gap-2">
                <span
                  className="flex items-center gap-1 text-xs px-2 py-0.5 rounded flex-shrink-0"
                  style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    color: 'var(--theme-text-muted)'
                  }}
                >
                  {notification.details.isLoggedOn ? (
                    <>
                      <User className="w-3 h-3" />
                      <span>Steam Authenticated</span>
                    </>
                  ) : (
                    <>
                      <UserX className="w-3 h-3" />
                      <span>Steam Anonymous</span>
                    </>
                  )}
                </span>
                {/* Show Web API Key pill when using V1 API key in anonymous mode */}
                {!notification.details.isLoggedOn && webApiStatus?.hasApiKey && !webApiStatus.isV2Available && (
                  <span
                    className="flex items-center gap-1 text-xs px-2 py-0.5 rounded flex-shrink-0"
                    style={{
                      backgroundColor: 'var(--theme-info-bg)',
                      color: 'var(--theme-info-text)'
                    }}
                  >
                    <Key className="w-3 h-3" />
                    <span>Web API Key</span>
                  </span>
                )}
              </div>
            )}
          </div>
        ) : (
          <div className="text-sm font-medium text-themed-primary truncate">
            {notification.message}
          </div>
        )}

        {/* Detail message (except for depot_mapping and service_removal which show details in progress bar) */}
        {notification.detailMessage &&
          notification.type !== 'depot_mapping' &&
          notification.type !== 'service_removal' && (
            <div className="text-xs text-themed-muted mt-0.5">{notification.detailMessage}</div>
          )}

        {/* Type-specific details */}
        {notification.type === 'cache_clearing' &&
          notification.details?.filesDeleted !== undefined && (
            <div className="text-xs text-themed-muted mt-0.5">
              {notification.details.filesDeleted.toLocaleString()} files deleted
            </div>
          )}

        {notification.type === 'service_removal' && notification.status === 'running' && (
          <>
            {notification.progress !== undefined && (
              <div className="mt-1">
                <div className="flex items-center justify-between text-xs text-themed-muted mb-0.5">
                  <span>{notification.progress.toFixed(1)}%</span>
                  {notification.details?.linesProcessed !== undefined && (
                    <span>
                      {notification.details.linesProcessed.toLocaleString()} lines processed
                    </span>
                  )}
                </div>
                <div className="w-full bg-themed-tertiary rounded-full h-1.5">
                  <div
                    className="h-1.5 rounded-full transition-all duration-300"
                    style={{
                      width: `${notification.progress}%`,
                      backgroundColor: 'var(--theme-warning)'
                    }}
                  />
                </div>
              </div>
            )}
          </>
        )}

        {notification.type === 'depot_mapping' && notification.status === 'running' && (
          <>
            {notification.progress !== undefined && (
              <div className="mt-2">
                {/* Detail message and percentage on same line */}
                <div className="flex items-center justify-between text-xs mb-1.5">
                  <span className="text-themed-muted">
                    {notification.detailMessage || 'Processing...'}
                  </span>
                  <span className="text-themed-muted font-medium ml-3">
                    {Math.round(notification.progress)}%
                  </span>
                </div>

                {/* Clean progress bar */}
                <div className="w-full bg-themed-tertiary rounded-full h-2 overflow-hidden">
                  <div
                    className="h-2 rounded-full transition-all duration-300"
                    style={{
                      width: `${notification.progress}%`,
                      backgroundColor: 'var(--theme-warning)'
                    }}
                  />
                </div>
              </div>
            )}
          </>
        )}

        {notification.type === 'game_removal' && notification.status === 'completed' && (
          <div className="text-xs text-themed-muted mt-0.5">
            {notification.details?.filesDeleted?.toLocaleString() || 0} cache files deleted
            {notification.details?.logEntriesRemoved !== undefined &&
              notification.details.logEntriesRemoved > 0 &&
              ` • ${notification.details.logEntriesRemoved.toLocaleString()} log entries removed`}
            {` • ${formatBytes(notification.details?.bytesFreed || 0)} freed`}
          </div>
        )}

        {/* Progress bar for running operations (except service removal and depot mapping which have custom progress) */}
        {notification.status === 'running' &&
          notification.progress !== undefined &&
          notification.progress > 0 &&
          notification.type !== 'service_removal' &&
          notification.type !== 'depot_mapping' && (
            <div className="mt-2">
              <div
                className="w-full rounded-full h-2"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <div
                  className="h-2 rounded-full transition-all duration-300"
                  style={{
                    backgroundColor: getStatusColor(),
                    width: `${Math.max(0, Math.min(100, notification.progress))}%`
                  }}
                />
              </div>
              <div className="flex justify-between items-center mt-1">
                <span className="text-xs text-themed-muted">
                  {notification.progress.toFixed(1)}% complete
                </span>
                {notification.details?.estimatedTime && (
                  <span className="text-xs text-themed-muted">
                    {notification.details.estimatedTime} remaining
                  </span>
                )}
              </div>
            </div>
          )}

        {/* Error message */}
        {notification.error && (
          <div className="text-xs text-themed-muted mt-0.5">{notification.error}</div>
        )}
      </div>

      {/* Action buttons */}
      <div className="flex items-center gap-2 flex-shrink-0">
        {(notification.type === 'cache_clearing' || notification.type === 'service_removal' || notification.type === 'depot_mapping') &&
          notification.status === 'running' &&
          onCancel && (
            notification.details?.cancelling ? (
              <div className="flex items-center gap-1.5 px-2 py-1 rounded text-xs" style={{ backgroundColor: 'var(--theme-error-bg)', color: 'var(--theme-error)' }}>
                <Loader2 className="w-3 h-3 animate-spin" />
                <span>Cancelling...</span>
              </div>
            ) : (
              <Tooltip content={
                notification.type === 'cache_clearing' ? 'Cancel cache clearing' :
                notification.type === 'service_removal' ? 'Cancel service removal' :
                'Cancel depot mapping'
              } position="left">
                <button
                  onClick={onCancel}
                  className="p-1 rounded hover:bg-themed-hover transition-colors"
                  aria-label="Cancel operation"
                >
                  <X className="w-4 h-4 text-themed-secondary" />
                </button>
              </Tooltip>
            )
          )}
        {(notification.status === 'completed' || notification.status === 'failed') && (
          <button
            onClick={onDismiss}
            className="p-1 rounded hover:bg-themed-hover transition-colors"
            aria-label="Dismiss"
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
      }, 300); // Match CSS transition duration
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
      }, 300); // Match this with CSS transition duration
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
    }, 300); // Match CSS transition duration
  };

  // Cancel handler for cache clearing
  const handleCancelCacheClearing = async (notification: UnifiedNotification) => {
    const operationId = notification.details?.operationId;
    const notificationId = notification.id;

    if (!operationId) {
      console.error('[UniversalNotificationBar] No operationId found for cache clearing notification');
      removeNotification(notificationId);
      return;
    }

    // Set cancelling state to show UI feedback
    updateNotification(notificationId, {
      details: {
        ...notification.details,
        cancelling: true
      }
    });

    try {
      // First attempt: Try graceful cancellation
      await ApiService.cancelCacheClear(operationId);
      removeNotification(notificationId);
    } catch (err: any) {
      // If operation is already completed/not found, just dismiss the notification silently
      if (
        err?.message?.includes('Operation not found') ||
        err?.message?.includes('already completed')
      ) {
        console.log(
          '[UniversalNotificationBar] Operation already completed, dismissing notification'
        );
        removeNotification(notificationId);
      } else {
        console.error('Failed to cancel cache clearing:', err);
        // Try force kill as fallback
        try {
          await ApiService.forceKillCacheClear(operationId);
          removeNotification(notificationId);
        } catch (forceErr) {
          console.error('Force kill also failed:', forceErr);
          // Still remove notification to prevent stuck UI
          removeNotification(notificationId);
        }
      }
    }
  };

  // Cancel handler for service removal
  const handleCancelServiceRemoval = async (notification: UnifiedNotification) => {
    const notificationId = notification.id;

    // Set cancelling state to show UI feedback
    updateNotification(notificationId, {
      details: {
        ...notification.details,
        cancelling: true
      }
    });

    try {
      // First attempt: Try graceful cancellation
      await ApiService.cancelServiceRemoval();
      removeNotification(notificationId);
    } catch (err: any) {
      // If operation is already completed/not found, just dismiss the notification silently
      if (
        err?.message?.includes('not found') ||
        err?.message?.includes('No service removal')
      ) {
        console.log(
          '[UniversalNotificationBar] Service removal already completed, dismissing notification'
        );
        removeNotification(notificationId);
      } else {
        console.error('Failed to cancel service removal:', err);
        // Try force kill as fallback
        try {
          await ApiService.forceKillServiceRemoval();
          removeNotification(notificationId);
        } catch (forceErr) {
          console.error('Force kill also failed:', forceErr);
          // Still remove notification to prevent stuck UI
          removeNotification(notificationId);
        }
      }
    }
  };

  // Cancel handler for depot mapping
  const handleCancelDepotMapping = async (notification: UnifiedNotification) => {
    const notificationId = notification.id;

    // Set cancelling state to show UI feedback
    updateNotification(notificationId, {
      details: {
        ...notification.details,
        cancelling: true
      }
    });

    try {
      await ApiService.cancelSteamKitRebuild();
      removeNotification(notificationId);
    } catch (err: any) {
      console.error('Failed to cancel depot mapping:', err);
      // Still remove notification to prevent stuck UI
      removeNotification(notificationId);
    }
  };

  // Don't render if no notifications and not animating
  if (!shouldRender) {
    return null;
  }

  return (
    <div
      className="w-full border-b shadow-sm"
      style={{
        backgroundColor: 'var(--theme-nav-bg)',
        borderColor: 'var(--theme-nav-border)',
        transition: 'transform 0.3s ease-out, opacity 0.3s ease-out',
        transform: isAnimatingOut ? 'translateY(-100%)' : 'translateY(0)',
        opacity: isAnimatingOut ? 0 : 1,
        ...(stickyDisabled
          ? {}
          : {
              position: 'sticky',
              top: 0,
              zIndex: 50
            })
      }}
    >
      <div className="container mx-auto px-4 py-2 space-y-2">
        {/* Unified Notifications */}
        {notifications.map((notification) => (
          <UnifiedNotificationItem
            key={notification.id}
            notification={notification}
            onDismiss={() => handleDismiss(notification.id)}
            onCancel={
              notification.type === 'cache_clearing'
                ? () => handleCancelCacheClearing(notification)
                : notification.type === 'service_removal'
                  ? () => handleCancelServiceRemoval(notification)
                  : notification.type === 'depot_mapping'
                    ? () => handleCancelDepotMapping(notification)
                    : undefined
            }
            isAnimatingOut={dismissingIds.has(notification.id)}
          />
        ))}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;
