import React, { useState, useEffect, useRef, useMemo } from 'react';
import { Database, Clock, Zap, AlertCircle, Loader2 } from 'lucide-react';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { FullScanRequiredModal } from '@components/shared/FullScanRequiredModal';
import { useNotifications } from '@contexts/NotificationsContext';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import { useBackendOperation } from '@hooks/useBackendOperation';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import { formatNextCrawlTime, toTotalSeconds } from '@utils/timeFormatters';
import { storage } from '@utils/storage';

interface DepotMappingManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  steamAuthMode: 'anonymous' | 'authenticated';
  actionLoading: boolean;
  setActionLoading: (loading: boolean) => void;
  isProcessingLogs: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

type DepotSource = 'incremental' | 'full' | 'github';

const DepotMappingManager: React.FC<DepotMappingManagerProps> = ({
  isAuthenticated,
  mockMode,
  steamAuthMode,
  actionLoading,
  setActionLoading,
  isProcessingLogs,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const { notifications, updateNotification } = useNotifications();
  const { progress: picsProgress, isLoading: picsLoading, refreshProgress } = usePicsProgress();
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();
  const depotMappingOp = useBackendOperation('activeDepotMapping', 'depotMapping', 120);
  const [localNextCrawlIn, setLocalNextCrawlIn] = useState<{
    hours: number;
    minutes: number;
    seconds: number;
  } | null>(null);
  const [depotSource, setDepotSource] = useState<DepotSource>('incremental');
  const [changeGapWarning, setChangeGapWarning] = useState<{
    show: boolean;
    changeGap: number;
    estimatedApps: number;
  } | null>(null);
  const [operationType, setOperationType] = useState<'downloading' | 'scanning' | null>(null);
  const [fullScanRequired, setFullScanRequired] = useState(false);
  const [githubDownloadComplete, setGithubDownloadComplete] = useState(false);
  const [githubDownloading, setGithubDownloading] = useState(false);
  const lastViabilityCheck = useRef<number>(0);
  const scanTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const lastProgressUpdateRef = useRef<number>(Date.now());
  const autoSwitchAttemptedRef = useRef<boolean>(false);

  // Derive depotConfig from picsProgress with nextCrawlIn conversion
  const depotConfig = useMemo(() => {
    if (!picsProgress) return null;

    return {
      isRunning: picsProgress.isRunning || false,
      crawlIntervalHours: picsProgress.crawlIntervalHours || 0,
      crawlIncrementalMode:
        picsProgress.crawlIncrementalMode !== undefined ? picsProgress.crawlIncrementalMode : true,
      nextCrawlIn:
        picsProgress.nextCrawlIn !== undefined
          ? (() => {
              const totalSeconds = Math.max(0, Math.floor(picsProgress.nextCrawlIn!));
              const hours = Math.floor(totalSeconds / 3600);
              const minutes = Math.floor((totalSeconds % 3600) / 60);
              const seconds = totalSeconds % 60;
              return { hours, minutes, seconds };
            })()
          : null,
      lastCrawlTime: picsProgress.lastCrawlTime,
      progressPercent: picsProgress.progressPercent || 0
    };
  }, [picsProgress]);

  // Restore depot mapping operation on mount
  useEffect(() => {
    const restoreDepotMapping = async () => {
      try {
        const operation = await depotMappingOp.load();
        if (operation?.data) {
          const data = operation.data as any;
          if (data.operationType) {
            console.log('[DepotMapping] Restoring interrupted depot mapping operation');

            // Clear any old stuck depot_mapping notifications from before page refresh
            const oldNotifications = notifications.filter((n) => n.type === 'depot_mapping');
            oldNotifications.forEach((n) => {
              console.log('[DepotMapping] Clearing old notification:', n.id);
              updateNotification(n.id, { status: 'completed', message: 'Loading...' });
            });

            // Restore loading state
            setActionLoading(true);
            setOperationType(data.operationType);
            if (data.depotSource) {
              setDepotSource(data.depotSource);
            }
            // SignalR will handle the completion when it arrives
          }
        }
      } catch (err) {
        console.error('[DepotMapping] Failed to restore depot mapping operation:', err);
      }
    };

    restoreDepotMapping();
  }, []); // Only run on mount

  // Update local countdown when depotConfig changes
  useEffect(() => {
    if (depotConfig?.nextCrawlIn) {
      setLocalNextCrawlIn(depotConfig.nextCrawlIn);
    } else {
      setLocalNextCrawlIn(null);
    }
  }, [depotConfig?.nextCrawlIn]);

  // Countdown timer - decrements localNextCrawlIn every second
  useEffect(() => {
    if (!localNextCrawlIn || depotConfig?.isRunning || depotConfig?.crawlIntervalHours === 0) {
      return;
    }

    const timer = setInterval(() => {
      setLocalNextCrawlIn((prev) => {
        if (!prev) return null;

        let { hours, minutes, seconds } = prev;

        // Decrement seconds
        seconds--;

        if (seconds < 0) {
          seconds = 59;
          minutes--;
        }

        if (minutes < 0) {
          minutes = 59;
          hours--;
        }

        // If we've counted down to zero, stop
        if (hours <= 0 && minutes <= 0 && seconds <= 0) {
          return { hours: 0, minutes: 0, seconds: 0 };
        }

        return { hours, minutes, seconds };
      });
    }, 1000);

    return () => clearInterval(timer);
  }, [localNextCrawlIn, depotConfig?.isRunning, depotConfig?.crawlIntervalHours]);

  // Auto-select GitHub when Web API is not available (for Apply Now Source)
  useEffect(() => {
    // Web API is not available when:
    // 1. picsProgress says it's not available, OR
    // 2. Steam Web API status shows it's not fully operational (V2 down AND no V1 key)
    const webApiNotAvailable =
      picsProgress?.isWebApiAvailable === false ||
      (!webApiLoading && webApiStatus && !webApiStatus.isFullyOperational);

    // Only auto-switch if Web API is not available and user is not authenticated (GitHub mode available)
    if (webApiNotAvailable && steamAuthMode !== 'authenticated') {
      // Don't override if user already selected GitHub
      if (depotSource !== 'github') {
        console.log('[DepotMapping] Web API unavailable - defaulting Apply Now Source to GitHub mode');
        setDepotSource('github');
      }
    }
  }, [picsProgress?.isWebApiAvailable, webApiStatus, webApiLoading, steamAuthMode, depotSource]);

  // Auto-switch automatic scan schedule to GitHub when Web API is not available
  useEffect(() => {
    // Wait for Web API status to finish loading before making decisions
    if (webApiLoading) return;

    // Web API is not available when:
    // 1. picsProgress says it's not available, OR
    // 2. Steam Web API status shows it's not fully operational (V2 down AND no V1 key)
    const webApiNotAvailable =
      picsProgress?.isWebApiAvailable === false ||
      (!webApiLoading && webApiStatus && !webApiStatus.isFullyOperational);

    const webApiAvailable =
      picsProgress?.isWebApiAvailable === true ||
      (!webApiLoading && webApiStatus && webApiStatus.isFullyOperational);

    // Only auto-switch if:
    // 1. User is authenticated (to avoid 401 errors)
    // 2. Web API is not available (both V2 and V1)
    // 3. User has NOT configured a V1 API key (if they have a key, let them use V1 instead)
    // 4. Steam auth mode is anonymous (GitHub mode is available)
    // 5. Current mode is incremental or full (which require Web API)
    // 6. Haven't already attempted auto-switch
    if (
      isAuthenticated &&
      webApiNotAvailable &&
      !webApiStatus?.hasApiKey &&
      steamAuthMode !== 'authenticated' &&
      depotConfig?.crawlIncrementalMode !== 'github' &&
      !autoSwitchAttemptedRef.current
    ) {
      console.log(
        '[DepotMapping] Web API V2 down and no V1 API key configured - switching automatic scan schedule to GitHub mode'
      );

      // Mark that we've attempted the switch to prevent repeats
      autoSwitchAttemptedRef.current = true;

      // Call API to switch to GitHub mode
      fetch('/api/gameinfo/steamkit/scan-mode', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify('github')
      })
        .then((response) => {
          if (response.ok) {
            // Also set interval to 30 minutes (0.5 hours) for GitHub mode
            return fetch('/api/gameinfo/steamkit/interval', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(0.5)
            });
          } else if (response.status === 401) {
            console.warn('[DepotMapping] Not authorized to switch scan mode - skipping auto-switch');
          }
        })
        .then(() => {
          console.log('[DepotMapping] Successfully switched to GitHub mode with 30-minute interval');
          refreshProgress();
        })
        .catch((error) => {
          console.error('[DepotMapping] Failed to switch to GitHub mode:', error);
        });
    }

    // Reset the flag when Web API becomes available again
    if (webApiAvailable && autoSwitchAttemptedRef.current) {
      console.log('[DepotMapping] Web API is now available - resetting auto-switch flag');
      autoSwitchAttemptedRef.current = false;
    }
  }, [isAuthenticated, picsProgress?.isWebApiAvailable, webApiStatus?.hasApiKey, webApiStatus?.isFullyOperational, webApiLoading, steamAuthMode, depotConfig?.crawlIncrementalMode, refreshProgress]);

  // Check for pending GitHub download from localStorage on mount
  useEffect(() => {
    const downloadComplete = storage.getItem('githubDownloadComplete');
    const downloadTime = storage.getItem('githubDownloadTime');

    if (downloadComplete === 'true' && downloadTime) {
      // Check if the download was within the last hour
      const hourAgo = new Date(Date.now() - 60 * 60 * 1000);
      const downloadDate = new Date(downloadTime);

      if (downloadDate > hourAgo) {
        setGithubDownloadComplete(true);
        // Automatically switch to incremental mode to guide user
        setDepotSource('incremental');
      } else {
        // Clear old download status
        storage.removeItem('githubDownloadComplete');
        storage.removeItem('githubDownloadTime');
      }
    }
  }, []);

  // Clear GitHub download complete flag when a scan finishes
  useEffect(() => {
    if (depotConfig && !depotConfig.isRunning && githubDownloadComplete) {
      // Clear the flag after scan completes
      setGithubDownloadComplete(false);
      storage.removeItem('githubDownloadComplete');
      storage.removeItem('githubDownloadTime');
      storage.removeItem('githubDownloading'); // Make sure this is also cleared
    }
  }, [depotConfig?.isRunning, githubDownloadComplete]);

  // Auto-switch away from GitHub when Steam auth mode changes to authenticated
  useEffect(() => {
    if (steamAuthMode === 'authenticated' && depotSource === 'github') {
      setDepotSource('incremental');
    }
  }, [steamAuthMode, depotSource]);

  // Check if full scan is required (for incremental mode) - for UI display only
  useEffect(() => {
    if (!depotConfig || mockMode || !isAuthenticated) {
      setFullScanRequired(false);
      return;
    }

    const { isRunning, crawlIntervalHours, crawlIncrementalMode } = depotConfig;

    // Skip if not incremental mode or scheduling is disabled
    if (crawlIntervalHours === 0 || !crawlIncrementalMode) {
      setFullScanRequired(false);
      return;
    }

    // Calculate if scan is due
    const totalSeconds = toTotalSeconds(localNextCrawlIn);
    const isDue = totalSeconds <= 0;

    // Only check viability when due and not running (for UI display)
    if (isDue && !isRunning && !actionLoading) {
      // Throttle checks to once per minute
      const now = Date.now();
      if (now - lastViabilityCheck.current > 60000) {
        lastViabilityCheck.current = now;

        ApiService.checkIncrementalViability()
          .then((result) => {
            setFullScanRequired(result.willTriggerFullScan === true);
          })
          .catch((err) => {
            console.error('[DepotMapping] Failed to check viability:', err);
            setFullScanRequired(false);
          });
      }
    } else if (!isDue) {
      setFullScanRequired(false);
      lastViabilityCheck.current = 0;
    }
  }, [depotConfig, localNextCrawlIn, mockMode, isAuthenticated, actionLoading]);

  // Listen for PICS scan completion via SignalR and refresh state
  // ONLY react if we're expecting a completion (operationType is set)
  useEffect(() => {
    if (operationType) {
      const picsNotifications = notifications.filter(
        (n) => n.type === 'depot_mapping' && (n.status === 'completed' || n.status === 'failed')
      );

      if (picsNotifications.length > 0) {
        // Clear operation state - depot mapping is complete/failed
        depotMappingOp.clear().catch((err) => console.error('Failed to clear operation state:', err));

        // Refresh progress data when scan completes
        setTimeout(() => {
          refreshProgress();
          onDataRefresh?.();
        }, 1000);
      }
    }
  }, [notifications, onDataRefresh, operationType]);

  // Clear operation type when scan completes
  useEffect(() => {
    if (!depotConfig?.isRunning && operationType === 'scanning') {
      setOperationType(null);
      setActionLoading(false);
      // Clear timeout when scan completes
      if (scanTimeoutRef.current) {
        clearTimeout(scanTimeoutRef.current);
        scanTimeoutRef.current = null;
      }
    }
  }, [depotConfig?.isRunning, operationType]);

  // Safety timeout: Auto-clear stuck loading state if no progress for 5 minutes
  // This handles cases where SignalR messages are lost during concurrent operations
  useEffect(() => {
    // Only monitor when we're actively scanning
    if (operationType === 'scanning' && depotConfig?.isRunning) {
      // Update last progress time when scan is running
      lastProgressUpdateRef.current = Date.now();

      // Clear any existing timeout
      if (scanTimeoutRef.current) {
        clearTimeout(scanTimeoutRef.current);
      }

      // Set a new timeout - if no state change for 5 minutes, assume stuck
      scanTimeoutRef.current = setTimeout(() => {
        const timeSinceUpdate = Date.now() - lastProgressUpdateRef.current;
        console.warn(
          '[DepotMapping] Scan timeout detected - no updates for',
          Math.round(timeSinceUpdate / 1000),
          'seconds'
        );

        // Force refresh progress to check actual backend state
        refreshProgress().then(() => {
          // If still showing as running after refresh, something is wrong
          // Clear the stuck state
          if (depotConfig?.isRunning) {
            console.warn('[DepotMapping] Forcing clear of stuck loading state');
            setOperationType(null);
            setActionLoading(false);
            depotMappingOp.clear();
            onError?.('Depot scan may have stalled. Please check the status and try again.');
          }
        });
      }, 5 * 60 * 1000); // 5 minutes

      return () => {
        if (scanTimeoutRef.current) {
          clearTimeout(scanTimeoutRef.current);
          scanTimeoutRef.current = null;
        }
      };
    } else {
      // Not scanning - clear timeout
      if (scanTimeoutRef.current) {
        clearTimeout(scanTimeoutRef.current);
        scanTimeoutRef.current = null;
      }
    }
  }, [operationType, depotConfig?.isRunning, depotConfig?.progressPercent]);

  const handleDownloadFromGitHub = async () => {
    setChangeGapWarning(null);
    setActionLoading(true);
    setOperationType('downloading');
    setGithubDownloadComplete(false);
    setGithubDownloading(true);

    // Set downloading flag in localStorage for UniversalNotificationBar
    storage.setItem('githubDownloading', 'true');
    storage.removeItem('githubDownloadComplete');

    // Save operation state for restoration on page refresh
    await depotMappingOp.save({
      operationType: 'downloading',
      depotSource: 'github'
    });

    try {
      await ApiService.downloadPrecreatedDepotData();
      onSuccess?.('GitHub depot data downloaded! Mappings are being applied to your downloads.');
      setGithubDownloadComplete(true);
      setGithubDownloading(false);

      // Update localStorage flags
      storage.removeItem('githubDownloading');
      storage.setItem('githubDownloadComplete', 'true');
      storage.setItem('githubDownloadTime', new Date().toISOString());

      // Clear operation state - download complete
      await depotMappingOp.clear();

      // Refresh the depot config after download
      await refreshProgress();

      setTimeout(() => onDataRefresh?.(), 2000);
    } catch (err: any) {
      onError?.(err.message || 'Failed to download from GitHub');
      setGithubDownloadComplete(false);
      setGithubDownloading(false);

      // Clear downloading flag on error
      storage.removeItem('githubDownloading');

      // Clear operation state - download failed
      await depotMappingOp.clear();
    } finally {
      setActionLoading(false);
      setOperationType(null);
    }
  };

  const executeApplyDepotMappings = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      // If GitHub is selected, download from GitHub
      if (depotSource === 'github') {
        await handleDownloadFromGitHub();
        return;
      }

      setOperationType('scanning');

      // Use Steam scan (incremental or full based on user selection)
      // Check if JSON file exists and needs to be imported to database
      const picsStatus = await ApiService.getPicsStatus();
      const hasJsonFile = picsStatus?.jsonFile?.exists === true;
      const hasDatabaseMappings = (picsStatus?.database?.totalMappings || 0) > 1000;

      // Import JSON to database if needed (JSON exists but database is empty)
      if (hasJsonFile && !hasDatabaseMappings) {
        console.log('[DepotMapping] Importing JSON file to database before scan');
        await fetch('/api/gameinfo/import-pics-data', {
          method: 'POST',
          headers: ApiService.getHeaders()
        });
        onSuccess?.(
          'Imported depot mappings to database - depot count will update after scan completes'
        );
        await new Promise((resolve) => setTimeout(resolve, 1000));
      }

      // Use incremental or full scan based on user selection
      const useIncrementalScan = depotSource === 'incremental';
      console.log(
        '[DepotMapping] Calling triggerSteamKitRebuild with incremental:',
        useIncrementalScan,
        'depotSource:',
        depotSource
      );
      const response = await ApiService.triggerSteamKitRebuild(useIncrementalScan);
      console.log('[DepotMapping] Backend response:', response);

      // Check if depot mapping is already running
      if (response.rebuildInProgress && !response.started) {
        console.log('[DepotMapping] Depot mapping already in progress');
        onError?.('Depot mapping is already in progress. Please wait for it to complete.');
        setActionLoading(false);
        setOperationType(null);
        return;
      }

      // Check if backend says full scan is required (for incremental requests)
      if (response.requiresFullScan) {
        console.log('[DepotMapping] Backend requires full scan - showing modal');
        setChangeGapWarning({
          show: true,
          changeGap: response.changeGap || 25000,
          estimatedApps: response.estimatedApps || 270000
        });
        setActionLoading(false);
        setOperationType(null);
        return;
      }

      const scanType = useIncrementalScan ? 'Incremental' : 'Full';
      onSuccess?.(`${scanType} depot scan started - mappings will be applied when complete`);
      setTimeout(() => onDataRefresh?.(), 2000);

      // Save operation state for restoration on page refresh
      await depotMappingOp.save({
        operationType: 'scanning',
        depotSource,
        scanType
      });

      // Keep operation type active - it will be cleared when scan completes
    } catch (err: any) {
      onError?.(err.message || 'Failed to process depot mappings');
      setOperationType(null);
    } finally {
      setActionLoading(false);
    }
  };

  const formatNextRun = () => {
    if (picsLoading || !depotConfig) return 'Loading...';
    if (depotConfig.crawlIntervalHours === 0) return 'Disabled';
    if (!localNextCrawlIn) return 'Calculating...';
    return formatNextCrawlTime(
      localNextCrawlIn,
      depotConfig.isRunning,
      fullScanRequired,
      depotConfig.crawlIncrementalMode
    );
  };

  return (
    <>
      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <Database className="w-5 h-5 text-themed-primary" />
          <h3 className="text-lg font-semibold text-themed-primary">Depot Mapping</h3>
        </div>

        <p className="text-themed-secondary mb-4">
          Automatically identifies Steam games from depot IDs in download history
        </p>

        {/* GitHub Download In Progress */}
        {githubDownloading && (
          <div
            className="mb-4 p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-info-bg)',
              borderColor: 'var(--theme-info)'
            }}
          >
            <div className="flex items-start gap-3">
              <Loader2
                className="w-5 h-5 flex-shrink-0 mt-0.5 animate-spin"
                style={{ color: 'var(--theme-info)' }}
              />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1" style={{ color: 'var(--theme-info-text)' }}>
                  Downloading Depot Mappings from GitHub...
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-info-text)', opacity: 0.9 }}>
                  Fetching pre-created depot mappings (290k+ depots). This may take a few moments.
                </p>
              </div>
            </div>
          </div>
        )}

        {/* GitHub Download Complete - Incremental Scan Required */}
        {githubDownloadComplete && !depotConfig?.isRunning && !githubDownloading && (
          <div
            className="mb-4 p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-info-bg)',
              borderColor: 'var(--theme-info)'
            }}
          >
            <div className="flex items-start gap-3">
              <Database
                className="w-5 h-5 flex-shrink-0 mt-0.5"
                style={{ color: 'var(--theme-info)' }}
              />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1" style={{ color: 'var(--theme-info-text)' }}>
                  GitHub Data Downloaded - Applying Mappings
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-info-text)', opacity: 0.9 }}>
                  Pre-created depot mappings have been imported from GitHub. The system is now
                  applying these mappings to your download history.
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Automatic Scan Skipped Warning - Don't show if GitHub download is complete or in progress */}
        {/* Note: automaticScanSkipped is now handled via SignalR in App.tsx */}
        {false && (
          <div
            className="mb-4 p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-error-bg)',
              borderColor: 'var(--theme-error)'
            }}
          >
            <div className="flex items-start gap-3">
              <AlertCircle
                className="w-5 h-5 flex-shrink-0 mt-0.5"
                style={{ color: 'var(--theme-error)' }}
              />
              <div className="flex-1">
                <p
                  className="font-medium text-sm mb-1"
                  style={{ color: 'var(--theme-error-text)' }}
                >
                  Automatic Scan Skipped - Data Update Required
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-error-text)', opacity: 0.9 }}>
                  The scheduled incremental scan was skipped because the change gap is too large. Please
                  download the latest pre-created data from GitHub to reset your baseline, then
                  incremental scans will work again.
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Schedule Status */}
        <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
          <div className="flex items-start justify-between gap-4">
            <div className="flex-1">
              <div className="flex items-center gap-2 mb-2">
                <Clock className="w-4 h-4 text-themed-primary" />
                <span className="text-sm font-medium text-themed-secondary">
                  Automatic Schedule
                </span>
              </div>
              <div className="text-xs text-themed-muted space-y-1">
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Runs every:</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig
                      ? 'Loading...'
                      : depotConfig.crawlIntervalHours === 0
                        ? 'Disabled'
                        : (() => {
                            // If Web API is unavailable and user is anonymous, show GitHub interval
                            const webApiNotAvailable =
                              !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational);

                            if (webApiNotAvailable && steamAuthMode !== 'authenticated') {
                              return '30 minutes';
                            }

                            // Otherwise show backend value
                            return depotConfig.crawlIncrementalMode === 'github'
                              ? '30 minutes'
                              : depotConfig.crawlIntervalHours === 0.5
                                ? '30 minutes'
                                : `${depotConfig.crawlIntervalHours} hour${depotConfig.crawlIntervalHours !== 1 ? 's' : ''}`;
                          })()}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Scan mode:</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig
                      ? 'Loading...'
                      : depotConfig.crawlIntervalHours === 0
                        ? 'Disabled'
                        : (() => {
                            // If Web API is unavailable and user is anonymous, show GitHub mode
                            const webApiNotAvailable =
                              !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational);

                            if (webApiNotAvailable && steamAuthMode !== 'authenticated') {
                              return 'GitHub';
                            }

                            // Otherwise show backend value
                            return depotConfig.crawlIncrementalMode === 'github'
                              ? 'GitHub'
                              : depotConfig.crawlIncrementalMode
                                ? 'Incremental'
                                : 'Full';
                          })()}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Next run:</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig || depotConfig.crawlIntervalHours === 0
                      ? 'Disabled'
                      : formatNextRun()}
                  </span>
                </div>
                {depotConfig?.lastCrawlTime && (
                  <div className="flex items-center gap-2">
                    <span style={{ opacity: 0.6 }}>Last run:</span>
                    <span className="font-medium text-themed-primary">
                      {depotConfig.crawlIntervalHours === 0
                        ? 'Disabled'
                        : new Date(depotConfig.lastCrawlTime).toLocaleString()}
                    </span>
                  </div>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2 min-w-[160px]">
              {/* GitHub Mode: Fixed 30-minute interval (either actual GitHub mode or forced due to Web API unavailability) */}
              {(() => {
                const webApiNotAvailable =
                  !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational);
                const isGithubMode =
                  depotConfig?.crawlIncrementalMode === 'github' ||
                  (webApiNotAvailable && steamAuthMode !== 'authenticated');

                if (isGithubMode) {
                  return (
                    <div>
                      <EnhancedDropdown
                        options={[{ value: '0.5', label: 'Every 30 minutes' }]}
                        value="0.5"
                        onChange={() => {}}
                        disabled={true}
                        className="w-full"
                      />
                      <p className="text-xs text-themed-muted mt-1">
                        Interval is fixed at 30 minutes for GitHub mode
                      </p>
                    </div>
                  );
                }

                // Non-GitHub Modes: User-configurable intervals
                return (
                  <EnhancedDropdown
                    options={[
                      { value: '0', label: 'Disabled' },
                      { value: '1', label: 'Every hour' },
                      { value: '6', label: 'Every 6 hours' },
                      { value: '12', label: 'Every 12 hours' },
                      { value: '24', label: 'Every 24 hours' },
                      { value: '48', label: 'Every 2 days' },
                      { value: '168', label: 'Weekly' }
                    ]}
                    value={depotConfig ? String(depotConfig.crawlIntervalHours) : '1'}
                    onChange={async (value) => {
                      const newInterval = Number(value);
                      try {
                        await fetch('/api/gameinfo/steamkit/interval', {
                          method: 'POST',
                          headers: {
                            ...ApiService.getHeaders(),
                            'Content-Type': 'application/json'
                          },
                          body: JSON.stringify(newInterval)
                        });

                        // Refresh the progress data after updating the interval
                        refreshProgress();
                      } catch (error) {
                        console.error('Failed to update crawl interval:', error);
                      }
                    }}
                    disabled={!isAuthenticated || mockMode}
                    className="w-full"
                  />
                );
              })()}
              <EnhancedDropdown
                options={[
                  {
                    value: 'incremental',
                    label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
                      ? 'Incremental'
                      : 'Incremental (Web API required)',
                    disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
                  },
                  {
                    value: 'full',
                    label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
                      ? 'Full'
                      : 'Full (Web API required)',
                    disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
                  },
                  ...(steamAuthMode !== 'authenticated'
                    ? [{ value: 'github', label: 'GitHub' }]
                    : [])
                ]}
                value={(() => {
                  // If Web API is unavailable and user is anonymous, force GitHub mode
                  const webApiNotAvailable =
                    !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational);

                  if (webApiNotAvailable && steamAuthMode !== 'authenticated') {
                    return 'github';
                  }

                  // Otherwise use backend value
                  return depotConfig?.crawlIncrementalMode === 'github'
                    ? steamAuthMode === 'authenticated'
                      ? 'incremental'
                      : 'github'
                    : depotConfig?.crawlIncrementalMode === false
                      ? 'full'
                      : 'incremental';
                })()}
                onChange={async (value) => {
                  try {
                    const wasGithubMode = depotConfig?.crawlIncrementalMode === 'github';

                    // If switching FROM GitHub to another mode, reset interval to 1 hour
                    if (wasGithubMode && value !== 'github') {
                      await fetch('/api/gameinfo/steamkit/interval', {
                        method: 'POST',
                        headers: {
                          ...ApiService.getHeaders(),
                          'Content-Type': 'application/json'
                        },
                        body: JSON.stringify(1)
                      });
                    }

                    // If GitHub is selected, force 30-minute interval
                    if (value === 'github') {
                      // Set interval to 0.5 hours (30 minutes)
                      await fetch('/api/gameinfo/steamkit/interval', {
                        method: 'POST',
                        headers: {
                          ...ApiService.getHeaders(),
                          'Content-Type': 'application/json'
                        },
                        body: JSON.stringify(0.5)
                      });
                    }

                    // Set scan mode
                    const incremental =
                      value === 'incremental' ? true : value === 'github' ? 'github' : false;
                    await fetch('/api/gameinfo/steamkit/scan-mode', {
                      method: 'POST',
                      headers: {
                        ...ApiService.getHeaders(),
                        'Content-Type': 'application/json'
                      },
                      body: JSON.stringify(incremental)
                    });

                    // Refresh the progress data after updating the scan mode
                    refreshProgress();
                  } catch (error) {
                    console.error('Failed to update scan mode:', error);
                  }
                }}
                disabled={
                  !isAuthenticated ||
                  mockMode ||
                  !depotConfig ||
                  depotConfig.crawlIntervalHours === 0
                }
                className="w-full"
              />
            </div>
          </div>
        </div>

        {/* Depot Source Selection */}
        <div className="mb-4">
          <label className="block text-sm font-medium text-themed-secondary mb-2">
            Apply Now Source
          </label>
          <EnhancedDropdown
            options={[
              {
                value: 'incremental',
                label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
                  ? 'Steam (Incremental Scan)'
                  : 'Steam (Incremental Scan - Web API required)',
                disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
              },
              {
                value: 'full',
                label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
                  ? 'Steam (Full Scan)'
                  : 'Steam (Full Scan - Web API required)',
                disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational)
              },
              {
                value: 'github',
                label:
                  steamAuthMode === 'authenticated'
                    ? 'GitHub (Not available with account login)'
                    : githubDownloadComplete
                      ? 'GitHub (Already downloaded)'
                      : 'GitHub (Download)',
                disabled: steamAuthMode === 'authenticated' || githubDownloadComplete
              }
            ]}
            value={depotSource}
            onChange={(value) => setDepotSource(value as DepotSource)}
            disabled={!isAuthenticated || mockMode}
            className="w-full"
          />
          {steamAuthMode === 'authenticated' && (
            <p className="text-xs text-themed-muted mt-2">
              GitHub downloads are disabled when using Steam account login. Switch to anonymous mode
              to use pre-created depot data.
            </p>
          )}
        </div>

        {/* Action Buttons */}
        <div className="flex">
          <Button
            variant="filled"
            color="blue"
            leftSection={<Zap className="w-4 h-4" />}
            onClick={() => executeApplyDepotMappings()}
            disabled={
              actionLoading ||
              isProcessingLogs ||
              depotConfig?.isRunning ||
              mockMode ||
              !isAuthenticated ||
              githubDownloading
            }
            loading={actionLoading || depotConfig?.isRunning}
            fullWidth
          >
            {actionLoading && operationType === 'downloading' && 'Downloading from GitHub...'}
            {actionLoading && operationType === 'scanning' && 'Starting Scan...'}
            {!actionLoading &&
              depotConfig?.isRunning &&
              `Scanning (${Math.round(depotConfig.progressPercent)}%)`}
            {!actionLoading &&
              !depotConfig?.isRunning &&
              githubDownloadComplete &&
              'Applying Mappings...'}
            {!actionLoading && !depotConfig?.isRunning && !githubDownloadComplete && 'Apply Now'}
          </Button>
        </div>

        <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
          <p className="text-xs text-themed-muted leading-relaxed">
            <strong>Automatic Schedule:</strong> Controls scan mode and interval for scheduled
            background runs
            <br />
            <strong>Apply Now Source:</strong> Choose data source when clicking "Apply Now" button
            <br />
            <br />
            <strong>Schedule Modes:</strong>
            <br />
            • <strong>Incremental:</strong> Uses Web API V2/V1 to get changed apps, then PICS for
            depot info (fast, recommended)
            <br />
            • <strong>Full:</strong> Uses Web API V2/V1 to get all apps, then PICS for depot info
            (slower, complete)
            <br />
            {steamAuthMode !== 'authenticated' && (
              <>
                • <strong>GitHub:</strong> Automatically downloads fresh depot data from GitHub
                every 30 minutes
                <br />
              </>
            )}
            <br />
            <strong>Manual Scan Options:</strong>
            <br />
            • <strong>Steam (Incremental):</strong> Web API + PICS for changed apps only
            <br />
            • <strong>Steam (Full):</strong> Web API + PICS for all Steam apps (~300k apps)
            <br />
            {steamAuthMode !== 'authenticated' && (
              <>
                • <strong>GitHub:</strong> Download pre-generated depot mappings (290k+ depots,
                updated daily)
                <br />
              </>
            )}
            <br />
            <em className="text-themed-muted">
              {steamAuthMode === 'authenticated' ? (
                <>
                  💡 Recommended: Use Incremental or Full scans with Web API V2/V1 fallback.
                  Configure API key in Steam Web API Status above for V1 fallback support.
                </>
              ) : (
                <>
                  💡 Recommended: Download from GitHub to get started, then enable "GitHub"
                  schedule mode to automatically download fresh depot data every 30 minutes.
                </>
              )}
            </em>
          </p>
        </div>
      </Card>

      {/* Data Update Required Modal */}
      {changeGapWarning?.show && (
        <FullScanRequiredModal
          changeGap={changeGapWarning.changeGap}
          estimatedApps={changeGapWarning.estimatedApps}
          onCancel={() => setChangeGapWarning(null)}
          onConfirm={async () => {
            setChangeGapWarning(null); // Close the modal immediately
            // Trigger full scan by setting depotSource to 'full' and executing
            setDepotSource('full');
            setActionLoading(true);
            setOperationType('scanning');
            try {
              const response = await ApiService.triggerSteamKitRebuild(false); // false = full scan
              if (response.rebuildInProgress && !response.started) {
                onError?.('Depot mapping is already in progress. Please wait for it to complete.');
                setActionLoading(false);
                setOperationType(null);
                return;
              }
              onSuccess?.('Full depot scan started - mappings will be applied when complete');
              setTimeout(() => onDataRefresh?.(), 2000);
              await depotMappingOp.save({
                operationType: 'scanning',
                depotSource: 'full',
                scanType: 'Full'
              });
            } catch (err: any) {
              onError?.(err.message || 'Failed to start full scan');
              setOperationType(null);
            } finally {
              setActionLoading(false);
            }
          }}
          onDownloadFromGitHub={() => {
            setChangeGapWarning(null); // Close the modal immediately
            handleDownloadFromGitHub();
          }}
          showDownloadOption={true}
          hasSteamApiKey={webApiStatus?.hasApiKey ?? false}
        />
      )}
    </>
  );
};

export default DepotMappingManager;
