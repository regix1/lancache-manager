import React, { useState, useEffect } from 'react';
import { Download, CheckCircle, AlertCircle, Loader2 } from 'lucide-react';

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

          // Show progress bar when PICS is running or has connection issues
          const shouldShow = data.isRunning || !data.isConnected || !data.isLoggedOn;

          if (shouldShow) {
            setIsVisible(true);
          } else if (data.isReady && !data.isRunning && data.isConnected && data.isLoggedOn) {
            // Hide after 3 seconds when processing is complete
            const timeout = setTimeout(() => {
              setIsVisible(false);
            }, 3000);
            setHideTimeout(timeout);
          }
        }
      } catch (error) {
        console.error('Failed to fetch PICS progress:', error);
      }
    };

    // Initial fetch
    fetchProgress();

    // Poll for updates every 2 seconds when running
    intervalId = setInterval(fetchProgress, 2000);

    return () => {
      if (intervalId) {
        clearInterval(intervalId);
      }
      if (hideTimeout) {
        clearTimeout(hideTimeout);
      }
    };
  }, [hideTimeout]);

  if (!isVisible || !progress) {
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
      className="w-full border-b shadow-sm"
      style={{
        backgroundColor: 'var(--theme-nav-bg)',
        borderColor: 'var(--theme-nav-border)'
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