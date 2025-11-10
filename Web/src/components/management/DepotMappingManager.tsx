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
    }
  }, [depotConfig?.isRunning, operationType]);

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

  const executeApplyDepotMappings = async (forceFull = false) => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }

    setActionLoading(true);
    try {
      // If GitHub is selected, download from GitHub
      if (depotSource === 'github' && !forceFull) {
        await handleDownloadFromGitHub();
        return;
      }

      setOperationType('scanning');

      // Otherwise, use Steam scan (incremental or full)
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

      // Use the selected scan mode (or force full if modal confirmed)
      const useIncrementalScan = forceFull ? false : depotSource === 'incremental';
      console.log(
        '[DepotMapping] Calling triggerSteamKitRebuild with incremental:',
        useIncrementalScan,
        'depotSource:',
        depotSource,
        'forceFull:',
        forceFull
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
                  Automatic Scan Skipped - Full Scan Required
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-error-text)', opacity: 0.9 }}>
                  The scheduled incremental scan was skipped because Steam requires a full scan. The
                  change gap is too large for an incremental update. Please manually run a full scan
                  or download pre-created data from GitHub.
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
                        : `${depotConfig.crawlIntervalHours} hour${depotConfig.crawlIntervalHours !== 1 ? 's' : ''}`}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Scan mode:</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig
                      ? 'Loading...'
                      : depotConfig.crawlIntervalHours === 0
                        ? 'Disabled'
                        : depotConfig.crawlIncrementalMode
                          ? 'Incremental'
                          : 'Full'}
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
              <EnhancedDropdown
                options={[
                  { value: 'true', label: 'Incremental' },
                  { value: 'false', label: 'Full scan' }
                ]}
                value={depotConfig ? String(depotConfig.crawlIncrementalMode) : 'true'}
                onChange={async (value) => {
                  const incremental = value === 'true';
                  try {
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
              { value: 'incremental', label: 'Steam (Incremental)' },
              { value: 'full', label: 'Steam (Full Scan)' },
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
            <strong>Steam (Incremental):</strong> Scans apps that changed since last run.{' '}
            {steamAuthMode === 'authenticated'
              ? 'Uses your authenticated Steam session.'
              : 'Uses anonymous Steam access (public games only).'}
            <br />
            <strong>Steam (Full Scan):</strong> Re-scans all Steam apps from scratch.{' '}
            {steamAuthMode === 'authenticated'
              ? 'Uses your authenticated Steam session to access all games including playtest and restricted titles.'
              : 'Uses anonymous Steam access (slower, public games only).'}
            <br />
            <strong>GitHub (Download):</strong>{' '}
            {steamAuthMode === 'authenticated'
              ? 'Not available when using Steam account login - authenticated scans provide more complete data for your library.'
              : 'Downloads pre-generated mappings from GitHub (fast, 290k+ depots, anonymous data only).'}
          </p>
        </div>
      </Card>

      {/* Full Scan Required Modal */}
      {changeGapWarning?.show && (
        <FullScanRequiredModal
          changeGap={changeGapWarning.changeGap}
          estimatedApps={changeGapWarning.estimatedApps}
          onConfirm={() => {
            setChangeGapWarning(null);
            executeApplyDepotMappings(true); // Force full scan
          }}
          onCancel={() => setChangeGapWarning(null)}
          onDownloadFromGitHub={() => {
            setChangeGapWarning(null); // Close the modal immediately
            handleDownloadFromGitHub();
          }}
          showDownloadOption={true}
        />
      )}
    </>
  );
};

export default DepotMappingManager;
