import React, { useState, useEffect, useRef } from 'react';
import { Download, CheckCircle, AlertCircle, Loader2, X, User, UserX, Trash2, XCircle } from 'lucide-react';
import themeService from '@services/theme.service';
import ApiService from '@services/api.service';
import { usePicsProgress } from '@hooks/usePicsProgress';
import { toTotalHours } from '@utils/timeFormatters';
import { useData } from '@contexts/DataContext';

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
    backgroundDepotMapping
  } = useData();
  const [isVisible, setIsVisible] = useState(false);
  const hideTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const hideTimeoutSetRef = useRef<boolean>(false);
  const [alwaysVisible, setAlwaysVisible] = useState(false);
  const [wasRunning, setWasRunning] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [githubDownloadStatus, setGithubDownloadStatus] = useState<'downloading' | 'complete' | null>(null);
  // Check for GitHub download status
  useEffect(() => {
    const checkGithubStatus = () => {
      const downloading = localStorage.getItem('githubDownloading');
      const downloadComplete = localStorage.getItem('githubDownloadComplete');

      // If PICS is running, clear GitHub complete status and show PICS instead
      if (progress?.isRunning && downloadComplete === 'true') {
        localStorage.removeItem('githubDownloadComplete');
        localStorage.removeItem('githubDownloadTime');
        setGithubDownloadStatus(null);
        return;
      }

      if (downloading === 'true') {
        setGithubDownloadStatus('downloading');
      } else if (downloadComplete === 'true') {
        setGithubDownloadStatus('complete');
      } else {
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

  useEffect(() => {
    const hasBackgroundActivity =
      backgroundRemovals.length > 0 ||
      backgroundLogProcessing !== null ||
      backgroundCacheClearing !== null ||
      backgroundServiceRemovals.length > 0 ||
      backgroundDatabaseReset !== null ||
      backgroundDepotMapping !== null;

    // If alwaysVisible is enabled or GitHub download is active or background activity exists, always show the bar
    if (alwaysVisible || githubDownloadStatus === 'downloading' || hasBackgroundActivity) {
      // Clear any pending hide timeout
      if (hideTimeoutRef.current) {
        clearTimeout(hideTimeoutRef.current);
        hideTimeoutRef.current = null;
        hideTimeoutSetRef.current = false;
      }
      setIsVisible(true);
      setWasRunning((progress?.isRunning || githubDownloadStatus === 'downloading') ?? false);
    } else {
      // Show progress bar when PICS is running or GitHub download is complete
      if (progress?.isRunning || githubDownloadStatus === 'complete') {
        // Clear any pending hide timeout since we're running again
        if (hideTimeoutRef.current) {
          clearTimeout(hideTimeoutRef.current);
          hideTimeoutRef.current = null;
          hideTimeoutSetRef.current = false;
        }
        setIsVisible(true);
        setWasRunning(true);
      } else if (wasRunning && !progress?.isRunning && githubDownloadStatus !== 'complete') {
        // Was just running, now stopped - hide after 10 seconds (but only set timeout ONCE)
        if (!hideTimeoutSetRef.current) {
          hideTimeoutSetRef.current = true;
          hideTimeoutRef.current = setTimeout(() => {
            setIsVisible(false);
            setWasRunning(false);
            hideTimeoutSetRef.current = false;
          }, 10000);
        }
      } else if (!progress?.isRunning && !wasRunning && githubDownloadStatus !== 'complete') {
        // Never was running in this session, hide immediately
        setIsVisible(false);
      }
    }
  }, [
    progress,
    alwaysVisible,
    wasRunning,
    githubDownloadStatus,
    backgroundRemovals.length,
    backgroundLogProcessing,
    backgroundCacheClearing,
    backgroundServiceRemovals.length,
    backgroundDatabaseReset,
    backgroundDepotMapping
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
    backgroundRemovals.length > 0 ||
    backgroundLogProcessing ||
    backgroundCacheClearing ||
    backgroundServiceRemovals.length > 0 ||
    backgroundDatabaseReset ||
    backgroundDepotMapping;

  if (!hasAnyActivity) {
    return null;
  }

  const formatBytes = (bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
  };

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
          <div
            key={removal.gameAppId}
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
                  {removal.status === 'completed' &&
                    `Removed ${removal.gameName}`}
                  {removal.status === 'failed' && `Failed to remove ${removal.gameName}`}
                </span>
              </div>
              {removal.status === 'completed' && removal.filesDeleted !== undefined && (
                <div className="text-xs text-themed-muted mt-0.5">
                  {removal.filesDeleted.toLocaleString()} files • {formatBytes(removal.bytesFreed || 0)} freed
                </div>
              )}
              {removal.status === 'failed' && removal.error && (
                <div className="text-xs text-themed-muted mt-0.5">{removal.error}</div>
              )}
            </div>
            {/* Only show X button when complete or failed, not during removal */}
            {(removal.status === 'completed' || removal.status === 'failed') && (
              <button
                onClick={() => clearBackgroundRemoval(removal.gameAppId)}
                className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
                aria-label="Dismiss"
              >
                <X className="w-4 h-4 text-themed-secondary" />
              </button>
            )}
          </div>
        ))}

        {/* Log Processing */}
        {backgroundLogProcessing && (
          <div
            className="flex items-center gap-3 p-2 rounded-lg"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderLeft: `3px solid ${
                backgroundLogProcessing.status === 'complete'
                  ? 'var(--theme-success)'
                  : backgroundLogProcessing.status === 'failed'
                  ? 'var(--theme-error)'
                  : 'var(--theme-info)'
              }`
            }}
          >
            {backgroundLogProcessing.status === 'processing' && (
              <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
            )}
            {backgroundLogProcessing.status === 'complete' && (
              <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
            )}
            {backgroundLogProcessing.status === 'failed' && (
              <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
            )}
            <div className="flex-1 min-w-0">
              <div className="text-sm font-medium text-themed-primary truncate">
                {backgroundLogProcessing.message}
              </div>
              {backgroundLogProcessing.detailMessage && (
                <div className="text-xs text-themed-muted mt-0.5">{backgroundLogProcessing.detailMessage}</div>
              )}
              {backgroundLogProcessing.progress > 0 && backgroundLogProcessing.status === 'processing' && (
                <div className="mt-2">
                  <div
                    className="w-full rounded-full h-2"
                    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                  >
                    <div
                      className="h-2 rounded-full transition-all duration-300"
                      style={{
                        backgroundColor: 'var(--theme-info)',
                        width: `${Math.max(0, Math.min(100, backgroundLogProcessing.progress))}%`
                      }}
                    />
                  </div>
                  <div className="flex justify-between items-center mt-1">
                    <span className="text-xs text-themed-muted">
                      {backgroundLogProcessing.progress.toFixed(1)}% complete
                    </span>
                    {backgroundLogProcessing.estimatedTime && (
                      <span className="text-xs text-themed-muted">{backgroundLogProcessing.estimatedTime} remaining</span>
                    )}
                  </div>
                </div>
              )}
              {backgroundLogProcessing.status === 'failed' && backgroundLogProcessing.error && (
                <div className="text-xs text-themed-muted mt-0.5">{backgroundLogProcessing.error}</div>
              )}
            </div>
            {/* Only show X button when complete or failed, not during processing */}
            {(backgroundLogProcessing.status === 'complete' || backgroundLogProcessing.status === 'failed') && (
              <button
                onClick={() => setBackgroundLogProcessing(null)}
                className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
                aria-label="Dismiss"
              >
                <X className="w-4 h-4 text-themed-secondary" />
              </button>
            )}
          </div>
        )}

        {/* Cache Clearing */}
        {backgroundCacheClearing && (
          <div
            className="flex items-center gap-3 p-2 rounded-lg"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderLeft: `3px solid ${
                backgroundCacheClearing.status === 'complete'
                  ? 'var(--theme-success)'
                  : backgroundCacheClearing.status === 'failed'
                  ? 'var(--theme-error)'
                  : 'var(--theme-info)'
              }`
            }}
          >
            {backgroundCacheClearing.status === 'clearing' && (
              <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
            )}
            {backgroundCacheClearing.status === 'complete' && (
              <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
            )}
            {backgroundCacheClearing.status === 'failed' && (
              <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
            )}
            <div className="flex-1 min-w-0">
              <div className="text-sm font-medium text-themed-primary truncate">
                {backgroundCacheClearing.status === 'clearing' && 'Clearing cache...'}
                {backgroundCacheClearing.status === 'complete' && 'Cache cleared successfully'}
                {backgroundCacheClearing.status === 'failed' && 'Cache clearing failed'}
              </div>
              <div className="text-xs text-themed-muted mt-0.5">
                {backgroundCacheClearing.filesDeleted.toLocaleString()} files deleted
              </div>
              {backgroundCacheClearing.progress > 0 && backgroundCacheClearing.status === 'clearing' && (
                <div className="mt-2">
                  <div
                    className="w-full rounded-full h-2"
                    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                  >
                    <div
                      className="h-2 rounded-full transition-all duration-300"
                      style={{
                        backgroundColor: 'var(--theme-info)',
                        width: `${Math.max(0, Math.min(100, backgroundCacheClearing.progress))}%`
                      }}
                    />
                  </div>
                  <div className="text-xs text-themed-muted mt-1">
                    {backgroundCacheClearing.progress.toFixed(1)}% complete
                  </div>
                </div>
              )}
              {backgroundCacheClearing.status === 'failed' && backgroundCacheClearing.error && (
                <div className="text-xs text-themed-muted mt-0.5">{backgroundCacheClearing.error}</div>
              )}
            </div>
            <div className="flex items-center gap-2 flex-shrink-0">
              {/* Show X button during clearing (to cancel) or when complete/failed (to dismiss) */}
              {backgroundCacheClearing.status === 'clearing' && (
                <button
                  onClick={handleCancelCacheClearing}
                  className="p-1 rounded hover:bg-themed-hover transition-colors"
                  aria-label="Cancel operation"
                  title="Cancel cache clearing"
                >
                  <X className="w-4 h-4 text-themed-secondary" />
                </button>
              )}
              {(backgroundCacheClearing.status === 'complete' || backgroundCacheClearing.status === 'failed') && (
                <button
                  onClick={() => setBackgroundCacheClearing(null)}
                  className="p-1 rounded hover:bg-themed-hover transition-colors"
                  aria-label="Dismiss"
                >
                  <X className="w-4 h-4 text-themed-secondary" />
                </button>
              )}
            </div>
          </div>
        )}

        {/* Service Removals */}
        {backgroundServiceRemovals.map((removal) => (
          <div
            key={removal.service}
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
                {removal.status === 'removing' && `Removing ${removal.service} logs...`}
                {removal.status === 'complete' && `Removed ${removal.service} logs successfully`}
                {removal.status === 'failed' && `Failed to remove ${removal.service} logs`}
              </div>
              {removal.status === 'failed' && removal.error && (
                <div className="text-xs text-themed-muted mt-0.5">{removal.error}</div>
              )}
            </div>
            {/* Only show X button when complete or failed, not during removal */}
            {(removal.status === 'complete' || removal.status === 'failed') && (
              <button
                onClick={() => clearBackgroundServiceRemoval(removal.service)}
                className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
                aria-label="Dismiss"
              >
                <X className="w-4 h-4 text-themed-secondary" />
              </button>
            )}
          </div>
        ))}

        {/* Database Reset */}
        {backgroundDatabaseReset && (
          <div
            className="flex items-center gap-3 p-2 rounded-lg"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderLeft: `3px solid ${
                backgroundDatabaseReset.status === 'complete'
                  ? 'var(--theme-success)'
                  : backgroundDatabaseReset.status === 'failed'
                  ? 'var(--theme-error)'
                  : 'var(--theme-info)'
              }`
            }}
          >
            {backgroundDatabaseReset.status === 'resetting' && (
              <Loader2 className="w-4 h-4 animate-spin flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
            )}
            {backgroundDatabaseReset.status === 'complete' && (
              <CheckCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
            )}
            {backgroundDatabaseReset.status === 'failed' && (
              <XCircle className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
            )}
            <div className="flex-1 min-w-0">
              <div className="text-sm font-medium text-themed-primary truncate">
                {backgroundDatabaseReset.message}
              </div>
              {backgroundDatabaseReset.progress > 0 && backgroundDatabaseReset.status === 'resetting' && (
                <div className="mt-2">
                  <div
                    className="w-full rounded-full h-2"
                    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                  >
                    <div
                      className="h-2 rounded-full transition-all duration-300"
                      style={{
                        backgroundColor: 'var(--theme-info)',
                        width: `${Math.max(0, Math.min(100, backgroundDatabaseReset.progress))}%`
                      }}
                    />
                  </div>
                  <div className="text-xs text-themed-muted mt-1">
                    {backgroundDatabaseReset.progress.toFixed(1)}% complete
                  </div>
                </div>
              )}
              {backgroundDatabaseReset.status === 'failed' && backgroundDatabaseReset.error && (
                <div className="text-xs text-themed-muted mt-0.5">{backgroundDatabaseReset.error}</div>
              )}
            </div>
            {/* Only show X button when complete or failed, not during reset */}
            {(backgroundDatabaseReset.status === 'complete' || backgroundDatabaseReset.status === 'failed') && (
              <button
                onClick={() => setBackgroundDatabaseReset(null)}
                className="flex-shrink-0 p-1 rounded hover:bg-themed-hover transition-colors"
                aria-label="Dismiss"
              >
                <X className="w-4 h-4 text-themed-secondary" />
              </button>
            )}
          </div>
        )}

        {/* Depot Mapping */}
        {backgroundDepotMapping && backgroundDepotMapping.isProcessing && (
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
                Depot Mapping: {backgroundDepotMapping.processedMappings} / {backgroundDepotMapping.totalMappings} downloads
              </div>
              <div className="text-xs text-themed-muted mt-0.5">
                {backgroundDepotMapping.message}
                {backgroundDepotMapping.mappingsApplied !== undefined && (
                  <span> • {backgroundDepotMapping.mappingsApplied} mappings applied</span>
                )}
              </div>
              {backgroundDepotMapping.percentComplete > 0 && (
                <div className="mt-2">
                  <div
                    className="w-full rounded-full h-2"
                    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                  >
                    <div
                      className="h-2 rounded-full transition-all duration-300"
                      style={{
                        backgroundColor: 'var(--theme-warning)',
                        width: `${Math.max(0, Math.min(100, backgroundDepotMapping.percentComplete))}%`
                      }}
                    />
                  </div>
                  <div className="text-xs text-themed-muted mt-1">
                    {backgroundDepotMapping.percentComplete.toFixed(1)}% complete
                  </div>
                </div>
              )}
            </div>
            {/* No X button - depot mapping cannot be cancelled */}
          </div>
        )}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;