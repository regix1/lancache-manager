import React, { useState, useEffect, useRef } from 'react';
import { Download, CheckCircle, AlertCircle, Loader2, X, User, UserX } from 'lucide-react';
import themeService from '@services/theme.service';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { usePicsProgress } from '@hooks/usePicsProgress';
import { toTotalHours } from '@utils/timeFormatters';

const PicsProgressBar: React.FC = () => {
  const { progress } = usePicsProgress({ pollingInterval: 2000 });
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

  useEffect(() => {
    // If alwaysVisible is enabled or GitHub download is active, always show the bar
    if (alwaysVisible || githubDownloadStatus === 'downloading') {
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
  }, [progress, alwaysVisible, wasRunning, githubDownloadStatus]);

  // Cleanup timeout on unmount
  useEffect(() => {
    return () => {
      if (hideTimeoutRef.current) {
        clearTimeout(hideTimeoutRef.current);
      }
    };
  }, []);

  // Don't return early if GitHub download is active - we still want to show that
  if (!progress && !githubDownloadStatus) {
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
      console.log('[PicsProgressBar] Scan cancelled successfully');
    } catch (error) {
      console.error('[PicsProgressBar] Failed to cancel scan:', error);
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
        maxHeight: isVisible ? '200px' : '0px',
        opacity: isVisible ? 1 : 0,
        borderBottomWidth: isVisible ? '1px' : '0px',
        transition: 'max-height 0.5s cubic-bezier(0.4, 0, 0.2, 1), opacity 0.5s cubic-bezier(0.4, 0, 0.2, 1), border-bottom-width 0.5s cubic-bezier(0.4, 0, 0.2, 1)'
      }}
    >
      <div className="container mx-auto px-4 py-2">
        <div className="flex items-center gap-3">
          {getStatusIcon()}
          <div className="flex-1">
            <div className="flex items-center justify-between mb-1">
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium text-themed-primary">
                  {/* Prioritize PICS status when running */}
                  {progress?.isRunning ? (
                    <>
                      Steam PICS: {progress.status}
                      {!progress.isConnected && ' (Disconnected)'}
                      {progress.isConnected && !progress.isLoggedOn && ' (Not logged in)'}
                    </>
                  ) : githubDownloadStatus === 'downloading' ? (
                    'GitHub: Downloading depot mappings...'
                  ) : githubDownloadStatus === 'complete' ? (
                    'GitHub: Applying depot mappings to downloads...'
                  ) : progress ? (
                    <>
                      Steam PICS: {progress.status}
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
                    {progress.isReady && progress.nextCrawlIn && (() => {
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
          {progress?.isRunning && (
            <Button
              onClick={handleCancel}
              disabled={cancelling}
              variant="subtle"
              color="red"
              size="xs"
              loading={cancelling}
              leftSection={!cancelling && <X className="w-3 h-3" />}
            >
              {cancelling ? 'Cancelling' : 'Cancel'}
            </Button>
          )}
        </div>
      </div>
    </div>
  );
};

export default PicsProgressBar;