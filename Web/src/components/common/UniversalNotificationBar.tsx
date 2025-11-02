import React, { useState, useEffect, useRef } from 'react';
import { Download, CheckCircle, AlertCircle, Loader2, X, User, UserX, Trash2, XCircle, Info } from 'lucide-react';
import themeService from '@services/theme.service';
import ApiService from '@services/api.service';
import { usePicsProgress } from '@hooks/usePicsProgress';
import { toTotalHours } from '@utils/timeFormatters';
import { useNotifications, UnifiedNotification } from '@contexts/NotificationsContext';
import { storage } from '@utils/storage';

// Unified notification component that handles all types
const UnifiedNotificationItem = ({
  notification,
  onDismiss,
  onCancel
}: {
  notification: UnifiedNotification;
  onDismiss: () => void;
  onCancel?: () => void;
}) => {
  const getStatusColor = () => {
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
      return <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: getStatusColor() }} />;
    } else if (notification.status === 'completed') {
      return <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: getStatusColor() }} />;
    } else if (notification.status === 'failed') {
      return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: getStatusColor() }} />;
    } else if (notification.details?.notificationType) {
      // For generic notifications
      switch (notification.details.notificationType) {
        case 'success':
          return <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />;
        case 'error':
          return <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />;
        case 'warning':
          return <AlertCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-warning)' }} />;
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
        borderLeft: `3px solid ${getStatusColor()}`
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
        ) : (
          <div className="text-sm font-medium text-themed-primary truncate">
            {notification.message}
          </div>
        )}

        {/* Detail message */}
        {notification.detailMessage && (
          <div className="text-xs text-themed-muted mt-0.5">{notification.detailMessage}</div>
        )}

        {/* Type-specific details */}
        {notification.type === 'cache_clearing' && notification.details?.filesDeleted !== undefined && (
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
                  {notification.details?.linesProcessed !== undefined && notification.details?.linesRemoved !== undefined && (
                    <span>
                      {notification.details.linesRemoved.toLocaleString()} removed / {notification.details.linesProcessed.toLocaleString()} processed
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

        {notification.type === 'game_removal' && notification.status === 'completed' && (
          <div className="text-xs text-themed-muted mt-0.5">
            {notification.details?.filesDeleted?.toLocaleString() || 0} cache files deleted
            {notification.details?.logEntriesRemoved !== undefined && notification.details.logEntriesRemoved > 0 &&
              ` • ${notification.details.logEntriesRemoved.toLocaleString()} log entries removed`
            }
            {` • ${formatBytes(notification.details?.bytesFreed || 0)} freed`}
          </div>
        )}

        {notification.type === 'depot_mapping' && notification.details?.isProcessing && (
          <>
            <div className="text-xs text-themed-muted mt-0.5">
              {notification.details.processedMappings} / {notification.details.totalMappings} downloads
              {notification.details.mappingsApplied !== undefined && (
                <span> • {notification.details.mappingsApplied} mappings applied</span>
              )}
            </div>
            {notification.details.percentComplete !== undefined && notification.details.percentComplete > 0 && (
              <div className="mt-2">
                <div
                  className="w-full rounded-full h-2"
                  style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                >
                  <div
                    className="h-2 rounded-full transition-all duration-300"
                    style={{
                      backgroundColor: 'var(--theme-warning)',
                      width: `${Math.max(0, Math.min(100, notification.details.percentComplete))}%`
                    }}
                  />
                </div>
                <div className="text-xs text-themed-muted mt-1">
                  {notification.details.percentComplete.toFixed(1)}% complete
                </div>
              </div>
            )}
          </>
        )}

        {/* Progress bar for running operations (except depot mapping and service removal which have custom progress) */}
        {notification.status === 'running' &&
         notification.progress !== undefined &&
         notification.progress > 0 &&
         notification.type !== 'depot_mapping' &&
         notification.type !== 'service_removal' && (
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
                <span className="text-xs text-themed-muted">{notification.details.estimatedTime} remaining</span>
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
        {notification.type === 'cache_clearing' && notification.status === 'running' && onCancel && (
          <button
            onClick={onCancel}
            className="p-1 rounded hover:bg-themed-hover transition-colors"
            aria-label="Cancel operation"
            title="Cancel cache clearing"
          >
            <X className="w-4 h-4 text-themed-secondary" />
          </button>
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
  const { progress } = usePicsProgress({ pollingInterval: 2000 });
  const { notifications, removeNotification } = useNotifications();
  const [isVisible, setIsVisible] = useState(false);
  const hideTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const hideTimeoutSetRef = useRef<boolean>(false);
  const [alwaysVisible, setAlwaysVisible] = useState(false);
  const [wasRunning, setWasRunning] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [githubDownloadStatus, setGithubDownloadStatus] = useState<'downloading' | 'complete' | null>(null);
  const githubDownloadStatusRef = useRef<'downloading' | 'complete' | null>(null);
  const isVisibleRef = useRef(false);
  const wasRunningRef = useRef(false);

  // Keep refs in sync with state
  useEffect(() => {
    githubDownloadStatusRef.current = githubDownloadStatus;
  }, [githubDownloadStatus]);

  useEffect(() => {
    isVisibleRef.current = isVisible;
  }, [isVisible]);

  useEffect(() => {
    wasRunningRef.current = wasRunning;
  }, [wasRunning]);

  // Check for GitHub download status
  useEffect(() => {
    const checkGithubStatus = () => {
      const downloading = storage.getItem('githubDownloading');
      const downloadComplete = storage.getItem('githubDownloadComplete');
      const currentStatus = githubDownloadStatusRef.current;

      // If PICS is running, clear GitHub complete status and show PICS instead
      if (progress?.isRunning && downloadComplete === 'true') {
        storage.removeItem('githubDownloadComplete');
        storage.removeItem('githubDownloadTime');
        if (currentStatus !== null) {
          setGithubDownloadStatus(null);
        }
        return;
      }

      // Only update state if it actually changed
      if (downloading === 'true' && currentStatus !== 'downloading') {
        setGithubDownloadStatus('downloading');
      } else if (downloadComplete === 'true' && currentStatus !== 'complete') {
        setGithubDownloadStatus('complete');
      } else if (downloading !== 'true' && downloadComplete !== 'true' && currentStatus !== null) {
        setGithubDownloadStatus(null);
      }
    };

    // Check on mount and periodically
    checkGithubStatus();
    const interval = setInterval(checkGithubStatus, 500);

    // Listen for storage events (cross-tab communication)
    window.addEventListener('storage', checkGithubStatus);

    return () => {
      clearInterval(interval);
      window.removeEventListener('storage', checkGithubStatus);
    };
  }, [progress?.isRunning]);

  useEffect(() => {
    // Load the setting on mount
    setAlwaysVisible(themeService.getPicsAlwaysVisible());

    // Listen for visibility setting changes
    const handleVisibilityChange = () => {
      const newSetting = themeService.getPicsAlwaysVisible();
      setAlwaysVisible(newSetting);
    };

    window.addEventListener('picsvisibilitychange', handleVisibilityChange);

    return () => {
      window.removeEventListener('picsvisibilitychange', handleVisibilityChange);
    };
  }, []);

  // Cancel handler for cache clearing
  const handleCancelCacheClearing = async (notificationId: string) => {
    try {
      await ApiService.cancelCacheClear(notificationId);
      removeNotification(notificationId);
    } catch (err) {
      console.error('Failed to cancel cache clearing:', err);
    }
  };

  useEffect(() => {
    const hasBackgroundActivity = notifications.length > 0;

    // If alwaysVisible is enabled or GitHub download is active or background activity exists, always show the bar
    if (alwaysVisible || githubDownloadStatus === 'downloading' || hasBackgroundActivity) {
      // Clear any pending hide timeout
      if (hideTimeoutRef.current) {
        clearTimeout(hideTimeoutRef.current);
        hideTimeoutRef.current = null;
        hideTimeoutSetRef.current = false;
      }
      if (!isVisibleRef.current) {
        setIsVisible(true);
      }
      const newWasRunning = (progress?.isRunning || githubDownloadStatus === 'downloading') ?? false;
      if (wasRunningRef.current !== newWasRunning) {
        setWasRunning(newWasRunning);
      }
    } else {
      // Show progress bar when PICS is running or GitHub download is complete
      if (progress?.isRunning || githubDownloadStatus === 'complete') {
        // Clear any pending hide timeout since we're running again
        if (hideTimeoutRef.current) {
          clearTimeout(hideTimeoutRef.current);
          hideTimeoutRef.current = null;
          hideTimeoutSetRef.current = false;
        }
        if (!isVisibleRef.current) {
          setIsVisible(true);
        }
        if (!wasRunningRef.current) {
          setWasRunning(true);
        }
      } else if (wasRunning && !progress?.isRunning && githubDownloadStatus !== 'complete') {
        // Was just running, now stopped - hide after 10 seconds (but only set timeout ONCE)
        if (!hideTimeoutSetRef.current) {
          hideTimeoutSetRef.current = true;
          hideTimeoutRef.current = setTimeout(() => {
            if (isVisibleRef.current) {
              setIsVisible(false);
            }
            if (wasRunningRef.current) {
              setWasRunning(false);
            }
            hideTimeoutSetRef.current = false;
          }, 10000);
        }
      } else if (!progress?.isRunning && !wasRunning && githubDownloadStatus !== 'complete') {
        // Never was running in this session, hide immediately
        if (isVisibleRef.current) {
          setIsVisible(false);
        }
      }
    }
  }, [
    progress,
    alwaysVisible,
    githubDownloadStatus,
    notifications.length,
    wasRunning
  ]);

  // Cleanup timeout on unmount
  useEffect(() => {
    return () => {
      if (hideTimeoutRef.current) {
        clearTimeout(hideTimeoutRef.current);
      }
    };
  }, []);

  // Don't return early if any background operations are active
  const hasAnyActivity =
    progress ||
    githubDownloadStatus ||
    notifications.length > 0;

  if (!hasAnyActivity) {
    return null;
  }

  const getStatusIcon = () => {
    if (githubDownloadStatus === 'downloading') {
      return <Loader2 className="w-4 h-4 animate-spin" style={{ color: 'var(--theme-info-text)' }} />;
    } else if (githubDownloadStatus === 'complete') {
      return <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />;
    } else if (progress?.isRunning) {
      return <Loader2 className="w-4 h-4 animate-spin" style={{ color: 'var(--theme-info-text)' }} />;
    } else if (progress?.status === 'Complete') {
      return <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />;
    } else if (progress?.status === 'Error occurred') {
      return <AlertCircle className="w-4 h-4" style={{ color: 'var(--theme-error-text)' }} />;
    } else {
      return <Download className="w-4 h-4" style={{ color: 'var(--theme-text-muted)' }} />;
    }
  };

  const getStatusStyle = () => {
    if (githubDownloadStatus === 'downloading') return { backgroundColor: 'var(--theme-info)' };
    if (githubDownloadStatus === 'complete') return { backgroundColor: 'var(--theme-success)' };
    if (progress?.isRunning) return { backgroundColor: 'var(--theme-info)' };
    if (progress?.status === 'Complete') return { backgroundColor: 'var(--theme-success)' };
    if (progress?.status === 'Error occurred') return { backgroundColor: 'var(--theme-error)' };
    return { backgroundColor: 'var(--theme-text-muted)' };
  };

  const handleCancel = async () => {
    if (!progress?.isRunning) return;

    try {
      setCancelling(true);
      await ApiService.cancelSteamKitRebuild();
      console.log('[UniversalNotificationBar] Scan cancelled successfully');
    } catch (error) {
      console.error('[UniversalNotificationBar] Failed to cancel scan:', error);
    } finally {
      setCancelling(false);
    }
  };

  return (
    <div
      className="w-full border-b shadow-sm overflow-hidden"
      style={{
        backgroundColor: 'var(--theme-nav-bg)',
        borderColor: 'var(--theme-nav-border)',
        maxHeight: isVisible ? '400px' : '0px',
        opacity: isVisible ? 1 : 0,
        borderBottomWidth: isVisible ? '1px' : '0px',
        transition: 'max-height 0.5s cubic-bezier(0.4, 0, 0.2, 1), opacity 0.5s cubic-bezier(0.4, 0, 0.2, 1), border-bottom-width 0.5s cubic-bezier(0.4, 0, 0.2, 1)'
      }}
    >
      <div className="container mx-auto px-4 py-2 space-y-2">
        {/* PICS/GitHub Progress */}
        {(progress || githubDownloadStatus) && (
        <div className="flex items-center gap-3">
          {getStatusIcon()}
          <div className="flex-1">
            <div className="flex items-center justify-between mb-1">
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium text-themed-primary">
                  {/* Prioritize PICS status when running */}
                  {progress?.isRunning ? (
                    <>
                      Steam PICS: {progress.errorMessage && progress.status === 'Error occurred'
                        ? `Failed - ${progress.errorMessage}`
                        : progress.status}
                      {!progress.errorMessage && !progress.isConnected && ' (Disconnected)'}
                      {!progress.errorMessage && progress.isConnected && !progress.isLoggedOn && ' (Not logged in)'}
                    </>
                  ) : githubDownloadStatus === 'downloading' ? (
                    'GitHub: Downloading depot mappings...'
                  ) : githubDownloadStatus === 'complete' ? (
                    'GitHub: Applying depot mappings to downloads...'
                  ) : progress ? (
                    <>
                      Steam PICS: {progress.errorMessage && progress.status === 'Error occurred' ? `Failed - ${progress.errorMessage}` : progress.status}
                      {!progress.isConnected && ' (Disconnected)'}
                      {progress.isConnected && !progress.isLoggedOn && ' (Not logged in)'}
                    </>
                  ) : null}
                </span>
                {/* Auth mode indicator */}
                {progress?.isConnected && (
                  <span className="flex items-center gap-1 text-xs px-2 py-0.5 rounded" style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    color: 'var(--theme-text-muted)'
                  }}>
                    {progress.isLoggedOn ? (
                      <>
                        <User className="w-3 h-3" />
                        <span>Authenticated</span>
                      </>
                    ) : (
                      <>
                        <UserX className="w-3 h-3" />
                        <span>Anonymous</span>
                      </>
                    )}
                  </span>
                )}
              </div>
              <span className="text-xs text-themed-muted">
                {/* Prioritize PICS info when running */}
                {progress?.isRunning && progress.totalBatches > 0 ? (
                  `${progress.processedBatches}/${progress.totalBatches} batches (${Math.round(progress.progressPercent)}%)`
                ) : githubDownloadStatus === 'downloading' ? (
                  'Fetching 290k+ depot mappings from GitHub...'
                ) : githubDownloadStatus === 'complete' ? (
                  'Mapping depots to your download history...'
                ) : progress ? (
                  <>
                    {progress.depotMappingsFoundInSession > 0
                      ? `Found ${progress.depotMappingsFoundInSession.toLocaleString()} new mappings (Total: ${progress.depotMappingsFound.toLocaleString()})`
                      : `${progress.depotMappingsFound.toLocaleString()} depot mappings`
                    }
                    {progress.isReady && progress.nextCrawlIn != null && (() => {
                      const totalHours = toTotalHours(progress.nextCrawlIn);
                      return totalHours > 0 ? ` • Next: ${Math.round(totalHours)}h` : '';
                    })()}
                  </>
                ) : null}
              </span>
            </div>

            {progress?.isRunning && progress?.totalBatches > 0 && (
              <div
                className="w-full rounded-full h-2"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <div
                  className="h-2 rounded-full transition-all duration-300"
                  style={{
                    ...getStatusStyle(),
                    width: `${Math.max(0, Math.min(100, progress.progressPercent))}%`
                  }}
                />
              </div>
            )}

            {progress?.isRunning && progress?.totalApps > 0 && (
              <div className="text-xs text-themed-muted mt-1">
                Processing {progress.processedApps.toLocaleString()}/{progress.totalApps.toLocaleString()} apps
              </div>
            )}
          </div>

          {/* Cancel button */}
          {progress?.isRunning && progress?.status !== 'Error occurred' && (
            <button
              onClick={handleCancel}
              disabled={cancelling}
              className="p-1 rounded hover:bg-themed-hover transition-colors"
              aria-label="Cancel scan"
              title="Cancel PICS scan"
            >
              {cancelling ? (
                <Loader2 className="w-4 h-4 animate-spin text-themed-secondary" />
              ) : (
                <X className="w-4 h-4 text-themed-secondary" />
              )}
            </button>
          )}
        </div>
        )}

        {/* Unified Notifications */}
        {notifications.map((notification) => (
          <UnifiedNotificationItem
            key={notification.id}
            notification={notification}
            onDismiss={() => removeNotification(notification.id)}
            onCancel={notification.type === 'cache_clearing' ? () => handleCancelCacheClearing(notification.id) : undefined}
          />
        ))}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;
