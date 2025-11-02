import React, { useState, useEffect, useRef } from 'react';
import { Download, CheckCircle, AlertCircle, Loader2, X, User, UserX, Trash2, XCircle, Info } from 'lucide-react';
import themeService from '@services/theme.service';
import ApiService from '@services/api.service';
import { usePicsProgress } from '@hooks/usePicsProgress';
import { toTotalHours } from '@utils/timeFormatters';
import { useData } from '@contexts/DataContext';
import { storage } from '@utils/storage';

const LogProcessingNotification = ({
  processing,
  onDismiss
}: {
  processing: any;
  onDismiss: () => void;
}) => {
  if (!processing) return null;

  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid ${
          processing.status === 'complete'
            ? 'var(--theme-success)'
            : processing.status === 'failed'
            ? 'var(--theme-error)'
            : 'var(--theme-info)'
        }`
      }}
    >
      {processing.status === 'processing' && (
        <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
      )}
      {processing.status === 'complete' && (
        <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
      )}
      {processing.status === 'failed' && (
        <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
      )}
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-themed-primary truncate">
          {processing.message}
        </div>
        {processing.detailMessage && (
          <div className="text-xs text-themed-muted mt-0.5">{processing.detailMessage}</div>
        )}
        {processing.progress > 0 && processing.status === 'processing' && (
          <div className="mt-2">
            <div
              className="w-full rounded-full h-2"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-2 rounded-full transition-all duration-300"
                style={{
                  backgroundColor: 'var(--theme-info)',
                  width: `${Math.max(0, Math.min(100, processing.progress))}%`
                }}
              />
            </div>
            <div className="flex justify-between items-center mt-1">
              <span className="text-xs text-themed-muted">
                {processing.progress.toFixed(1)}% complete
              </span>
              {processing.estimatedTime && (
                <span className="text-xs text-themed-muted">{processing.estimatedTime} remaining</span>
              )}
            </div>
          </div>
        )}
        {processing.status === 'failed' && processing.error && (
          <div className="text-xs text-themed-muted mt-0.5">{processing.error}</div>
        )}
      </div>
      {(processing.status === 'complete' || processing.status === 'failed') && (
        <button
          onClick={onDismiss}
          className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
          aria-label="Dismiss"
        >
          <X className="w-4 h-4 text-themed-secondary" />
        </button>
      )}
    </div>
  );
};

const DatabaseResetNotification = ({
  reset,
  onDismiss
}: {
  reset: any;
  onDismiss: () => void;
}) => {
  if (!reset) return null;

  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid ${
          reset.status === 'complete'
            ? 'var(--theme-success)'
            : reset.status === 'failed'
            ? 'var(--theme-error)'
            : 'var(--theme-info)'
        }`
      }}
    >
      {reset.status === 'resetting' && (
        <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
      )}
      {reset.status === 'complete' && (
        <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
      )}
      {reset.status === 'failed' && (
        <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
      )}
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-themed-primary truncate">
          {reset.message}
        </div>
        {reset.progress > 0 && reset.status === 'resetting' && (
          <div className="mt-2">
            <div
              className="w-full rounded-full h-2"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-2 rounded-full transition-all duration-300"
                style={{
                  backgroundColor: 'var(--theme-info)',
                  width: `${Math.max(0, Math.min(100, reset.progress))}%`
                }}
              />
            </div>
            <div className="text-xs text-themed-muted mt-1">
              {reset.progress.toFixed(1)}% complete
            </div>
          </div>
        )}
        {reset.status === 'failed' && reset.error && (
          <div className="text-xs text-themed-muted mt-0.5">{reset.error}</div>
        )}
      </div>
      {(reset.status === 'complete' || reset.status === 'failed') && (
        <button
          onClick={onDismiss}
          className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
          aria-label="Dismiss"
        >
          <X className="w-4 h-4 text-themed-secondary" />
        </button>
      )}
    </div>
  );
};

const CacheClearingNotification = ({
  clearing,
  onDismiss,
  onCancel
}: {
  clearing: any;
  onDismiss: () => void;
  onCancel: () => void;
}) => {
  if (!clearing) return null;

  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid ${
          clearing.status === 'complete'
            ? 'var(--theme-success)'
            : clearing.status === 'failed'
            ? 'var(--theme-error)'
            : 'var(--theme-info)'
        }`
      }}
    >
      {clearing.status === 'clearing' && (
        <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
      )}
      {clearing.status === 'complete' && (
        <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
      )}
      {clearing.status === 'failed' && (
        <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
      )}
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-themed-primary truncate">
          {clearing.status === 'clearing' && 'Clearing cache...'}
          {clearing.status === 'complete' && 'Cache cleared successfully'}
          {clearing.status === 'failed' && 'Cache clearing failed'}
        </div>
        <div className="text-xs text-themed-muted mt-0.5">
          {clearing.filesDeleted.toLocaleString()} files deleted
        </div>
        {clearing.progress > 0 && clearing.status === 'clearing' && (
          <div className="mt-2">
            <div
              className="w-full rounded-full h-2"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-2 rounded-full transition-all duration-300"
                style={{
                  backgroundColor: 'var(--theme-info)',
                  width: `${Math.max(0, Math.min(100, clearing.progress))}%`
                }}
              />
            </div>
            <div className="text-xs text-themed-muted mt-1">
              {clearing.progress.toFixed(1)}% complete
            </div>
          </div>
        )}
        {clearing.status === 'failed' && clearing.error && (
          <div className="text-xs text-themed-muted mt-0.5">{clearing.error}</div>
        )}
      </div>
      <div className="flex items-center gap-2 flex-shrink-0">
        {clearing.status === 'clearing' && (
          <button
            onClick={onCancel}
            className="p-1 rounded hover:bg-themed-hover transition-colors"
            aria-label="Cancel operation"
            title="Cancel cache clearing"
          >
            <X className="w-4 h-4 text-themed-secondary" />
          </button>
        )}
        {(clearing.status === 'complete' || clearing.status === 'failed') && (
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

const ServiceRemovalNotification = ({
  removal,
  onDismiss
}: {
  removal: any;
  onDismiss: () => void;
}) => {
  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid ${
          removal.status === 'complete'
            ? 'var(--theme-success)'
            : removal.status === 'failed'
            ? 'var(--theme-error)'
            : 'var(--theme-warning)'
        }`
      }}
    >
      {removal.status === 'removing' && (
        <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-warning)' }} />
      )}
      {removal.status === 'complete' && (
        <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
      )}
      {removal.status === 'failed' && (
        <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
      )}
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-themed-primary truncate">
          {removal.message || (
            <>
              {removal.status === 'removing' && `Removing ${removal.service} logs...`}
              {removal.status === 'complete' && `Removed ${removal.service} logs successfully`}
              {removal.status === 'failed' && `Failed to remove ${removal.service} logs`}
            </>
          )}
        </div>
        {removal.status === 'removing' && removal.progress !== undefined && (
          <div className="mt-1">
            <div className="flex items-center justify-between text-xs text-themed-muted mb-0.5">
              <span>{removal.progress.toFixed(1)}%</span>
              {removal.linesProcessed !== undefined && removal.linesRemoved !== undefined && (
                <span>{removal.linesRemoved.toLocaleString()} removed / {removal.linesProcessed.toLocaleString()} processed</span>
              )}
            </div>
            <div className="w-full bg-themed-tertiary rounded-full h-1.5">
              <div
                className="h-1.5 rounded-full transition-all duration-300"
                style={{
                  width: `${removal.progress}%`,
                  backgroundColor: 'var(--theme-warning)'
                }}
              />
            </div>
          </div>
        )}
        {removal.status === 'failed' && removal.error && (
          <div className="text-xs text-themed-muted mt-0.5">{removal.error}</div>
        )}
      </div>
      {(removal.status === 'complete' || removal.status === 'failed') && (
        <button
          onClick={onDismiss}
          className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
          aria-label="Dismiss"
        >
          <X className="w-4 h-4 text-themed-secondary" />
        </button>
      )}
    </div>
  );
};

const DepotMappingNotification = ({
  mapping
}: {
  mapping: any;
}) => {
  if (!mapping || !mapping.isProcessing) return null;

  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid var(--theme-warning)`
      }}
    >
      <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-warning)' }} />
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-themed-primary truncate">
          Depot Mapping: {mapping.processedMappings} / {mapping.totalMappings} downloads
        </div>
        <div className="text-xs text-themed-muted mt-0.5">
          {mapping.message}
          {mapping.mappingsApplied !== undefined && (
            <span> • {mapping.mappingsApplied} mappings applied</span>
          )}
        </div>
        {mapping.percentComplete > 0 && (
          <div className="mt-2">
            <div
              className="w-full rounded-full h-2"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-2 rounded-full transition-all duration-300"
                style={{
                  backgroundColor: 'var(--theme-warning)',
                  width: `${Math.max(0, Math.min(100, mapping.percentComplete))}%`
                }}
              />
            </div>
            <div className="text-xs text-themed-muted mt-1">
              {mapping.percentComplete.toFixed(1)}% complete
            </div>
          </div>
        )}
      </div>
    </div>
  );
};

const GameRemovalNotification = ({
  removal,
  onDismiss
}: {
  removal: any;
  onDismiss: () => void;
}) => {
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
        borderLeft: `3px solid ${
          removal.status === 'completed'
            ? 'var(--theme-success)'
            : removal.status === 'failed'
            ? 'var(--theme-error)'
            : 'var(--theme-info)'
        }`
      }}
    >
      {removal.status === 'removing' && (
        <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
      )}
      {removal.status === 'completed' && (
        <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
      )}
      {removal.status === 'failed' && (
        <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
      )}
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <Trash2 className="w-3 h-3 text-themed-muted flex-shrink-0" />
          <span className="text-sm font-medium text-themed-primary truncate">
            {removal.status === 'removing' && `Removing ${removal.gameName}...`}
            {removal.status === 'completed' && `Removed ${removal.gameName}`}
            {removal.status === 'failed' && `Failed to remove ${removal.gameName}`}
          </span>
        </div>
        {removal.status === 'completed' && removal.filesDeleted !== undefined && (
          <div className="text-xs text-themed-muted mt-0.5">
            {removal.filesDeleted.toLocaleString()} cache files deleted
            {removal.logEntriesRemoved !== undefined && removal.logEntriesRemoved > 0 && ` • ${removal.logEntriesRemoved.toLocaleString()} log entries removed`}
            {` • ${formatBytes(removal.bytesFreed || 0)} freed`}
          </div>
        )}
        {removal.status === 'failed' && removal.error && (
          <div className="text-xs text-themed-muted mt-0.5">{removal.error}</div>
        )}
      </div>
      {(removal.status === 'completed' || removal.status === 'failed') && (
        <button
          onClick={onDismiss}
          className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
          aria-label="Dismiss"
        >
          <X className="w-4 h-4 text-themed-secondary" />
        </button>
      )}
    </div>
  );
};

const GenericNotificationItem = ({
  notification,
  onDismiss
}: {
  notification: any;
  onDismiss: () => void;
}) => {
  const getNotificationColor = () => {
    switch (notification.type) {
      case 'success': return 'var(--theme-success)';
      case 'error': return 'var(--theme-error)';
      case 'warning': return 'var(--theme-warning)';
      case 'info': return 'var(--theme-info)';
      default: return 'var(--theme-info)';
    }
  };

  const getNotificationIcon = () => {
    switch (notification.type) {
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
  };

  return (
    <div
      className="flex items-center gap-3 p-2 rounded-lg"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderLeft: `3px solid ${getNotificationColor()}`
      }}
    >
      {getNotificationIcon()}
      <div className="flex-1 min-w-0">
        <div className="text-sm text-themed-primary">
          {notification.message}
        </div>
      </div>
      <button
        onClick={onDismiss}
        className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
        aria-label="Dismiss"
      >
        <X className="w-4 h-4 text-themed-secondary" />
      </button>
    </div>
  );
};

const UniversalNotificationBar: React.FC = () => {
  const { progress } = usePicsProgress({ pollingInterval: 2000 });
  const {
    backgroundRemovals,
    clearBackgroundRemoval,
    backgroundLogProcessing,
    setBackgroundLogProcessing,
    backgroundCacheClearing,
    setBackgroundCacheClearing,
    updateBackgroundCacheClearing,
    backgroundServiceRemovals,
    clearBackgroundServiceRemoval,
    backgroundDatabaseReset,
    setBackgroundDatabaseReset,
    backgroundDepotMapping,
    genericNotifications,
    clearNotification
  } = useData();
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [progress?.isRunning]); // githubDownloadStatus intentionally excluded to prevent re-creating interval

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
  const handleCancelCacheClearing = async () => {
    if (!backgroundCacheClearing?.id) return;

    try {
      updateBackgroundCacheClearing({ status: 'clearing' }); // Keep showing while cancelling
      await ApiService.cancelCacheClear(backgroundCacheClearing.id);
      setBackgroundCacheClearing(null);
    } catch (err) {
      console.error('Failed to cancel cache clearing:', err);
      // Keep showing the operation even if cancel fails
    }
  };

  // Auto-clear completed/failed background removals after 10 seconds
  const autoClearTimersRef = useRef<Map<number, NodeJS.Timeout>>(new Map());

  useEffect(() => {
    backgroundRemovals.forEach((removal) => {
      // Only set timer if this removal doesn't already have one
      if ((removal.status === 'completed' || removal.status === 'failed') &&
          !autoClearTimersRef.current.has(removal.gameAppId)) {
        const timer = setTimeout(() => {
          clearBackgroundRemoval(removal.gameAppId);
          autoClearTimersRef.current.delete(removal.gameAppId);
        }, 10000);
        autoClearTimersRef.current.set(removal.gameAppId, timer);
      }
    });

    // Clean up timers for removals that no longer exist
    const currentGameIds = new Set(backgroundRemovals.map(r => r.gameAppId));
    autoClearTimersRef.current.forEach((timer, gameAppId) => {
      if (!currentGameIds.has(gameAppId)) {
        clearTimeout(timer);
        autoClearTimersRef.current.delete(gameAppId);
      }
    });
  }, [backgroundRemovals, clearBackgroundRemoval]);

  // Auto-clear generic notifications after 10 seconds
  const genericNotificationTimersRef = useRef<Map<number, NodeJS.Timeout>>(new Map());

  useEffect(() => {
    genericNotifications.forEach((notification) => {
      // Only set timer if this notification doesn't already have one
      if (!genericNotificationTimersRef.current.has(notification.id)) {
        const timer = setTimeout(() => {
          clearNotification(notification.id);
          genericNotificationTimersRef.current.delete(notification.id);
        }, 10000);
        genericNotificationTimersRef.current.set(notification.id, timer);
      }
    });

    // Clean up timers for notifications that no longer exist
    const currentNotificationIds = new Set(genericNotifications.map(n => n.id));
    genericNotificationTimersRef.current.forEach((timer, id) => {
      if (!currentNotificationIds.has(id)) {
        clearTimeout(timer);
        genericNotificationTimersRef.current.delete(id);
      }
    });
  }, [genericNotifications, clearNotification]);

  useEffect(() => {
    const hasBackgroundActivity =
      backgroundRemovals.length > 0 ||
      backgroundLogProcessing !== null ||
      backgroundCacheClearing !== null ||
      backgroundServiceRemovals.length > 0 ||
      backgroundDatabaseReset !== null ||
      backgroundDepotMapping !== null ||
      genericNotifications.length > 0;

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
    // Note: wasRunning intentionally excluded from dependencies to prevent infinite loop
    // We read its current value but don't re-run when it changes
    // Only track existence of background operations, not their content, to prevent flickering on progress updates
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    progress,
    alwaysVisible,
    githubDownloadStatus,
    backgroundRemovals.length,
    backgroundLogProcessing !== null, // Only care if it exists
    backgroundCacheClearing !== null, // Only care if it exists
    backgroundServiceRemovals.length,
    backgroundDatabaseReset !== null, // Only care if it exists
    backgroundDepotMapping !== null, // Only care if it exists
    genericNotifications.length
  ]);

  // Cleanup timeout on unmount
  useEffect(() => {
    return () => {
      if (hideTimeoutRef.current) {
        clearTimeout(hideTimeoutRef.current);
      }
      // Clear all auto-clear timers
      autoClearTimersRef.current.forEach(timer => clearTimeout(timer));
      autoClearTimersRef.current.clear();
      // Clear all generic notification timers
      genericNotificationTimersRef.current.forEach(timer => clearTimeout(timer));
      genericNotificationTimersRef.current.clear();
    };
  }, []);

  // Don't return early if any background operations are active
  const hasAnyActivity =
    progress ||
    githubDownloadStatus ||
    backgroundRemovals.length > 0 ||
    backgroundLogProcessing ||
    backgroundCacheClearing ||
    backgroundServiceRemovals.length > 0 ||
    backgroundDatabaseReset ||
    backgroundDepotMapping ||
    genericNotifications.length > 0;

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

        {/* Background Removals */}
        {backgroundRemovals.map((removal) => (
          <GameRemovalNotification
            key={removal.gameAppId}
            removal={removal}
            onDismiss={() => clearBackgroundRemoval(removal.gameAppId)}
          />
        ))}

        {/* Log Processing */}
        <LogProcessingNotification
          processing={backgroundLogProcessing}
          onDismiss={() => setBackgroundLogProcessing(null)}
        />

        {/* Cache Clearing */}
        <CacheClearingNotification
          clearing={backgroundCacheClearing}
          onDismiss={() => setBackgroundCacheClearing(null)}
          onCancel={handleCancelCacheClearing}
        />

        {/* Service Removals */}
        {backgroundServiceRemovals.map((removal) => (
          <ServiceRemovalNotification
            key={`${removal.service}-${removal.startedAt.getTime()}`}
            removal={removal}
            onDismiss={() => clearBackgroundServiceRemoval(removal.service)}
          />
        ))}

        {/* Database Reset */}
        <DatabaseResetNotification
          reset={backgroundDatabaseReset}
          onDismiss={() => setBackgroundDatabaseReset(null)}
        />

        {/* Depot Mapping */}
        <DepotMappingNotification mapping={backgroundDepotMapping} />

        {/* Generic Notifications */}
        {genericNotifications.map((notification) => (
          <GenericNotificationItem
            key={notification.id}
            notification={notification}
            onDismiss={() => clearNotification(notification.id)}
          />
        ))}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;