import React, { useState, useEffect } from 'react';
import { Download, CheckCircle, AlertCircle, Loader2 } from 'lucide-react';
import themeService from '@services/theme.service';

interface PicsProgress {
  isRunning: boolean;
  status: string;
  totalApps: number;
  processedApps: number;
  totalBatches: number;
  processedBatches: number;
  progressPercent: number;
  depotMappingsFound: number;
  depotMappingsFoundInSession: number;
  isReady: boolean;
  lastCrawlTime?: string;
  nextCrawlIn: any; // Can be TimeSpan string, object, or number from backend
  crawlIntervalHours: number;
  isConnected: boolean;
  isLoggedOn: boolean;
}

const PicsProgressBar: React.FC = () => {
  const [progress, setProgress] = useState<PicsProgress | null>(null);
  const [isVisible, setIsVisible] = useState(false);
  const [hideTimeout, setHideTimeout] = useState<NodeJS.Timeout | null>(null);
  const [alwaysVisible, setAlwaysVisible] = useState(false);
  const [wasRunning, setWasRunning] = useState(false);

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
    let intervalId: NodeJS.Timeout | null = null;

    const fetchProgress = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data: PicsProgress = await response.json();
          setProgress(data);

          // Clear any existing hide timeout
          if (hideTimeout) {
            clearTimeout(hideTimeout);
            setHideTimeout(null);
          }

          // If alwaysVisible is enabled, always show the bar
          if (alwaysVisible) {
            setIsVisible(true);
            setWasRunning(data.isRunning);
          } else {
            // Show progress bar only when PICS is actually running
            if (data.isRunning) {
              setIsVisible(true);
              setWasRunning(true);
            } else if (wasRunning && !data.isRunning) {
              // Was just running, now stopped - hide after 10 seconds
              const timeout = setTimeout(() => {
                setIsVisible(false);
                setWasRunning(false);
              }, 10000);
              setHideTimeout(timeout);
            } else if (!data.isRunning && !wasRunning) {
              // Never was running in this session, hide immediately
              setIsVisible(false);
            }
          }
        }
      } catch (error) {
        console.error('Failed to fetch PICS progress:', error);
      }
    };

    // Initial fetch
    fetchProgress();

    // Poll for updates every 2 seconds
    intervalId = setInterval(fetchProgress, 2000);

    return () => {
      if (intervalId) {
        clearInterval(intervalId);
      }
      if (hideTimeout) {
        clearTimeout(hideTimeout);
      }
    };
  }, [alwaysVisible, wasRunning]);

  if (!progress) {
    return null;
  }

  const getStatusIcon = () => {
    if (progress.isRunning) {
      return <Loader2 className="w-4 h-4 animate-spin" style={{ color: 'var(--theme-info-text)' }} />;
    } else if (progress.status === 'Complete') {
      return <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />;
    } else if (progress.status === 'Error occurred') {
      return <AlertCircle className="w-4 h-4" style={{ color: 'var(--theme-error-text)' }} />;
    } else {
      return <Download className="w-4 h-4" style={{ color: 'var(--theme-text-muted)' }} />;
    }
  };

  const getStatusStyle = () => {
    if (progress.isRunning) return { backgroundColor: 'var(--theme-info)' };
    if (progress.status === 'Complete') return { backgroundColor: 'var(--theme-success)' };
    if (progress.status === 'Error occurred') return { backgroundColor: 'var(--theme-error)' };
    return { backgroundColor: 'var(--theme-text-muted)' };
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
              <span className="text-sm font-medium text-themed-primary">
                Steam PICS: {progress.status}
                {!progress.isConnected && ' (Disconnected)'}
                {progress.isConnected && !progress.isLoggedOn && ' (Not logged in)'}
              </span>
              <span className="text-xs text-themed-muted">
                {progress.isRunning && progress.totalBatches > 0
                  ? `${progress.processedBatches}/${progress.totalBatches} batches (${Math.round(progress.progressPercent)}%)`
                  : progress.depotMappingsFoundInSession > 0
                    ? `Found ${progress.depotMappingsFoundInSession.toLocaleString()} new mappings (Total: ${progress.depotMappingsFound.toLocaleString()})`
                    : `${progress.depotMappingsFound.toLocaleString()} depot mappings`
                }
                {progress.isReady && progress.nextCrawlIn && (() => {
                  let totalHours = 0;
                  if (typeof progress.nextCrawlIn === 'object' && progress.nextCrawlIn.totalHours !== undefined) {
                    totalHours = progress.nextCrawlIn.totalHours;
                  } else if (typeof progress.nextCrawlIn === 'string') {
                    const parts = progress.nextCrawlIn.split(':');
                    totalHours = parseInt(parts[0]) + (parseInt(parts[1]) / 60);
                  } else if (typeof progress.nextCrawlIn === 'number') {
                    totalHours = progress.nextCrawlIn / 3600;
                  }
                  return totalHours > 0 ? ` • Next: ${Math.round(totalHours)}h` : '';
                })()}
              </span>
            </div>

            {progress.isRunning && progress.totalBatches > 0 && (
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

            {progress.isRunning && progress.totalApps > 0 && (
              <div className="text-xs text-themed-muted mt-1">
                Processing {progress.processedApps.toLocaleString()}/{progress.totalApps.toLocaleString()} apps
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};

export default PicsProgressBar;