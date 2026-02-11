import React, { useState, useEffect, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Database, Clock, Zap, AlertCircle, Loader2, ExternalLink } from 'lucide-react';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { FullScanRequiredModal } from '@components/modals/setup/FullScanRequiredModal';
import { useNotifications } from '@contexts/notifications';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import { formatNextCrawlTime, toTotalSeconds } from '@utils/timeFormatters';
import { storage } from '@utils/storage';
import { isAbortError } from '@utils/error';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { ManagerCardHeader } from '@components/ui/ManagerCard';

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
  onNavigateToSteamApi?: () => void;
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
  onDataRefresh,
  onNavigateToSteamApi
}) => {
  const { t } = useTranslation();
  const { notifications } = useNotifications();
  const { progress: picsProgress, isLoading: picsLoading, refreshProgress } = usePicsProgress();
  const { status: webApiStatus, loading: webApiLoading } = useSteamWebApiStatus();

  // Derive depot mapping operation state from notifications (standardized pattern)
  const activeDepotNotification = notifications.find(
    n => n.type === 'depot_mapping' && n.status === 'running'
  );
  const isDepotMappingFromNotification = !!activeDepotNotification;
  const [localNextCrawlIn, setLocalNextCrawlIn] = useState<{
    hours: number;
    minutes: number;
    seconds: number;
  } | null>(null);
  const [depotSource, setDepotSource] = useState<DepotSource>(() => {
    // Load last selected source from localStorage
    const savedSource = storage.getItem('depotSource');
    return (savedSource as DepotSource) || 'incremental';
  });
  const [changeGapWarning, setChangeGapWarning] = useState<{
    show: boolean;
    changeGap?: number;
    estimatedApps?: number;
    message?: string;
  } | null>(null);
  const [operationType, setOperationType] = useState<'downloading' | 'scanning' | null>(null);
  const [fullScanRequired, setFullScanRequired] = useState(false);
  const [githubDownloadComplete, setGithubDownloadComplete] = useState(false);
  const [githubDownloading, setGithubDownloading] = useState(false);
  const lastViabilityCheck = useRef<number>(0);
  const scanTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const lastProgressUpdateRef = useRef<number>(Date.now());
  const autoSwitchAttemptedRef = useRef<boolean>(false);
  const applyInProgressRef = useRef<boolean>(false);

  // Derive depotConfig from picsProgress with nextCrawlIn conversion
  const depotConfig = useMemo(() => {
    if (!picsProgress) return null;

    return {
      isProcessing: picsProgress.isProcessing || false,
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

  // Format last crawl time with timezone awareness
  const formattedLastCrawlTime = useFormattedDateTime(depotConfig?.lastCrawlTime || null);

  // Restore depot mapping operation state from notifications on mount
  // Note: NotificationsContext handles the actual recovery via recoverDepotMapping
  useEffect(() => {
    // If there's an active depot mapping notification, restore local UI state
    if (isDepotMappingFromNotification && activeDepotNotification) {
      // Restore loading state based on notification
      // Note: operationType may be set dynamically, use type assertion for extensible details
      const notificationDetails = activeDepotNotification.details as { operationType?: 'downloading' | 'scanning' } | undefined;
      if (notificationDetails?.operationType) {
        setOperationType(notificationDetails.operationType);
        setActionLoading(true);
      } else {
        // Default to scanning if no specific operation type
        setOperationType('scanning');
        setActionLoading(true);
      }
    }
  }, [isDepotMappingFromNotification]); // Only trigger on notification state changes

  // Refresh progress when component mounts to get accurate nextCrawlIn
  // This ensures the countdown reflects the actual time remaining, not stale cached data
  useEffect(() => {
    refreshProgress();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // Only on mount

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
    if (!localNextCrawlIn || depotConfig?.isProcessing || depotConfig?.crawlIntervalHours === 0) {
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
  }, [localNextCrawlIn, depotConfig?.isProcessing, depotConfig?.crawlIntervalHours]);

  // Auto-select GitHub when Web API is not available (for Apply Now Source)
  // This runs whenever Web API status changes to ensure we don't have an invalid selection
  useEffect(() => {
    // Wait for Web API status to finish loading
    if (webApiLoading || !webApiStatus) return;

    // Web API is unavailable only if V2 is down AND there's no API key
    // If user has an API key, they can always use V1 fallback
    const webApiNotAvailable =
      !webApiStatus.isV2Available && !webApiStatus.hasApiKey;

    // If Web API is not available, switch to GitHub
    // AND current selection requires Web API, switch to GitHub
    if (webApiNotAvailable) {
      const currentSource = depotSource;
      // Only switch if current selection is 'incremental' or 'full' (which require Web API)
      if (currentSource === 'incremental' || currentSource === 'full') {
        setDepotSource('github');
        storage.setItem('depotSource', 'github');
      }
    }
  }, [picsProgress?.isWebApiAvailable, webApiStatus, webApiLoading, depotSource]);

  // Auto-switch automatic scan schedule to GitHub when Web API is not available
  useEffect(() => {
    // Wait for Web API status to finish loading before making decisions
    if (webApiLoading) return;

    // Web API is unavailable only if V2 is down AND there's no API key
    // If user has an API key, they can always use V1 fallback
    const webApiNotAvailable =
      !webApiLoading && webApiStatus && !webApiStatus.isV2Available && !webApiStatus.hasApiKey;

    const webApiAvailable =
      picsProgress?.isWebApiAvailable === true ||
      (!webApiLoading && webApiStatus && (webApiStatus.isV2Available || webApiStatus.hasApiKey));

    // Only auto-switch if:
    // 1. User is authenticated (to avoid 401 errors)
    // 2. Web API is not available (both V2 and V1)
    // 3. User has NOT configured a V1 API key (if they have a key, let them use V1 instead)
    // 4. Current mode is incremental or full (which require Web API)
    // 5. Haven't already attempted auto-switch
    if (
      isAuthenticated &&
      webApiNotAvailable &&
      !webApiStatus?.hasApiKey &&
      depotConfig?.crawlIncrementalMode !== 'github' &&
      !autoSwitchAttemptedRef.current
    ) {
      // Mark that we've attempted the switch to prevent repeats
      autoSwitchAttemptedRef.current = true;

      // Call API to switch to GitHub mode
      fetch('/api/depots/rebuild/config/mode', ApiService.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify('github')
      }))
        .then((response) => {
          if (response.ok) {
            // Also set interval to 30 minutes (0.5 hours) for GitHub mode
            return fetch('/api/depots/rebuild/config/interval', ApiService.getFetchOptions({
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(0.5)
            }));
          } else if (response.status === 401) {
            console.warn('[DepotMapping] Not authorized to switch scan mode - skipping auto-switch');
            // Reset flag so it can be retried when user authenticates
            autoSwitchAttemptedRef.current = false;
            return Promise.reject('Not authorized');
          } else {
            console.error('[DepotMapping] Failed to set scan mode:', response.status, response.statusText);
            // Reset flag so it can be retried
            autoSwitchAttemptedRef.current = false;
            return Promise.reject(`Failed with status ${response.status}`);
          }
        })
        .then((intervalResponse) => {
          if (intervalResponse && intervalResponse.ok) {
            // Force a refresh to update the UI with persisted values
            setTimeout(() => refreshProgress(), 1000);
          } else if (intervalResponse && !intervalResponse.ok) {
            console.error('[DepotMapping] Failed to set interval:', intervalResponse.status);
            // Reset flag so interval can be retried
            autoSwitchAttemptedRef.current = false;
          }
        })
        .catch((error) => {
          if (error !== 'Not authorized') {
            console.error('[DepotMapping] Error during auto-switch to GitHub mode:', error);
          }
        });
    }

    // Reset the flag when Web API becomes available again
    if (webApiAvailable && autoSwitchAttemptedRef.current) {
      // console.log('[DepotMapping] Web API is now available - resetting auto-switch flag');
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
        storage.setItem('depotSource', 'incremental');
      } else {
        // Clear old download status
        storage.removeItem('githubDownloadComplete');
        storage.removeItem('githubDownloadTime');
      }
    }
  }, []);

  // Clear GitHub download complete flag when a scan finishes
  useEffect(() => {
    if (depotConfig && !depotConfig.isProcessing && githubDownloadComplete) {
      // Clear the flag after scan completes
      setGithubDownloadComplete(false);
      storage.removeItem('githubDownloadComplete');
      storage.removeItem('githubDownloadTime');
      storage.removeItem('githubDownloading'); // Make sure this is also cleared
    }
  }, [depotConfig?.isProcessing, githubDownloadComplete]);

  // NOTE: "Apply Now Source" is intentionally NOT synced with the automatic schedule mode.
  // These are independent controls - users may want to run a different source manually
  // than what runs automatically on schedule (e.g., schedule=GitHub but manual=Incremental).

  // NOTE: GitHub mode is now available for all users regardless of Steam auth mode.
  // Users can use GitHub mode even when authenticated with Steam if Web API is unavailable.

  // Check if full scan is required (for incremental mode) - for UI display only
  useEffect(() => {
    if (!depotConfig || mockMode || !isAuthenticated) {
      setFullScanRequired(false);
      return;
    }

    const { isProcessing, crawlIntervalHours, crawlIncrementalMode } = depotConfig;

    // Skip if scheduling is disabled, not incremental mode, or GitHub mode (GitHub doesn't need viability checks)
    if (crawlIntervalHours === 0 || !crawlIncrementalMode || crawlIncrementalMode === 'github') {
      setFullScanRequired(false);
      return;
    }

    // Calculate if scan is due
    const totalSeconds = toTotalSeconds(localNextCrawlIn);
    const isDue = totalSeconds <= 0;

    // Only check viability when due and not running (for UI display)
    if (isDue && !isProcessing && !actionLoading) {
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
        // Note: Operation state now handled by NotificationsContext

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
    if (!depotConfig?.isProcessing && operationType === 'scanning') {
      setOperationType(null);
      setActionLoading(false);
      // Clear timeout when scan completes
      if (scanTimeoutRef.current) {
        clearTimeout(scanTimeoutRef.current);
        scanTimeoutRef.current = null;
      }
    }
  }, [depotConfig?.isProcessing, operationType]);

  // Safety timeout: Auto-clear stuck loading state if no progress for 5 minutes
  // This handles cases where SignalR messages are lost during concurrent operations
  useEffect(() => {
    // Only monitor when we're actively scanning
    if (operationType === 'scanning' && depotConfig?.isProcessing) {
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
          if (depotConfig?.isProcessing) {
            console.warn('[DepotMapping] Forcing clear of stuck loading state');
            setOperationType(null);
            setActionLoading(false);
            // Note: Operation state now handled by NotificationsContext
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
  }, [operationType, depotConfig?.isProcessing, depotConfig?.progressPercent]);

  // Listen for "change gap too large" errors from automatic scans
  useEffect(() => {
    const handleShowFullScanModal = (event: Event) => {
      const customEvent = event as CustomEvent;
      // Show the modal with the error message
      setChangeGapWarning({
        show: true,
        changeGap: undefined, // We don't have the exact gap from the error
        estimatedApps: undefined,
        message: customEvent.detail?.error || t('management.depotMapping.errors.changeGapTooLarge')
      });
    };

    window.addEventListener('show-full-scan-modal', handleShowFullScanModal);
    return () => window.removeEventListener('show-full-scan-modal', handleShowFullScanModal);
  }, []);

  const handleDownloadFromGitHub = async () => {
    // Set loading states FIRST to prevent double-clicking
    setActionLoading(true);
    setOperationType('downloading');
    setGithubDownloadComplete(false);
    setGithubDownloading(true);

    // Close modal AFTER setting loading states
    setChangeGapWarning(null);

    // Set downloading flag in localStorage for UniversalNotificationBar
    storage.setItem('githubDownloading', 'true');
    storage.removeItem('githubDownloadComplete');

    // Note: NotificationsContext will create a notification via SignalR when the download starts

    try {
      await ApiService.downloadPrecreatedDepotData();
      onSuccess?.('GitHub depot data downloaded! Mappings are being applied to your downloads.');
      setGithubDownloadComplete(true);
      setGithubDownloading(false);

      // Update localStorage flags
      storage.removeItem('githubDownloading');
      storage.setItem('githubDownloadComplete', 'true');
      storage.setItem('githubDownloadTime', new Date().toISOString());

      // Refresh the depot config after download
      await refreshProgress();

      setTimeout(() => onDataRefresh?.(), 2000);
    } catch (err: unknown) {
      // Don't show error for user-initiated cancellation
      if (!isAbortError(err)) {
        onError?.((err instanceof Error ? err.message : String(err)) || t('management.depotMapping.errors.failedToDownloadFromGitHub'));
      }
      setGithubDownloadComplete(false);
      setGithubDownloading(false);

      // Clear downloading flag on error/cancel
      storage.removeItem('githubDownloading');
    } finally {
      setActionLoading(false);
      setOperationType(null);
    }
  };

  const executeApplyDepotMappings = async () => {
    // Prevent double-clicks (ref check is synchronous, state is async)
    if (applyInProgressRef.current) {
      return;
    }
    applyInProgressRef.current = true;

    if (!isAuthenticated) {
      onError?.('Authentication required');
      applyInProgressRef.current = false;
      return;
    }

    // Set loading states IMMEDIATELY for visual feedback before any async work
    setActionLoading(true);

    // If GitHub is selected, download from GitHub
    if (depotSource === 'github') {
      // handleDownloadFromGitHub sets its own operationType to 'downloading'
      try {
        await handleDownloadFromGitHub();
      } finally {
        applyInProgressRef.current = false;
      }
      return;
    }

    // For Steam scans, set operation type immediately (before async API calls)
    setOperationType('scanning');

    try {

      // Use Steam scan (incremental or full based on user selection)
      // Check if JSON file exists and needs to be imported to database
      const picsStatus = await ApiService.getPicsStatus();
      const hasJsonFile = picsStatus?.jsonFile?.exists === true;
      const hasDatabaseMappings = (picsStatus?.database?.totalMappings || 0) > 1000;

      // Import JSON to database if needed (JSON exists but database is empty)
      // Only do this for INCREMENTAL scans - they need a baseline to build upon
      // Full scans create fresh data from scratch, so they don't need this
      const useIncrementalScan = depotSource === 'incremental';
      if (hasJsonFile && !hasDatabaseMappings && useIncrementalScan) {
        // console.log('[DepotMapping] Importing JSON file to database before incremental scan');
        await fetch('/api/depots/import?source=local', ApiService.getFetchOptions({
          method: 'POST'
        }));
        onSuccess?.(
          'Imported depot mappings to database - depot count will update after scan completes'
        );
        await new Promise((resolve) => setTimeout(resolve, 1000));
      }

      // Use incremental or full scan based on user selection
      const response = await ApiService.triggerSteamKitRebuild(useIncrementalScan);
      // console.log('[DepotMapping] Backend response:', response);

      // Check if depot mapping is already running
      if (response.rebuildInProgress && !response.started) {
        // console.log('[DepotMapping] Depot mapping already in progress');
        onError?.('Depot mapping is already in progress. Please wait for it to complete.');
        setActionLoading(false);
        setOperationType(null);
        applyInProgressRef.current = false;
        return;
      }

      // Check if backend says full scan is required (for incremental requests)
      if (response.requiresFullScan) {
        // console.log('[DepotMapping] Backend requires full scan - showing modal');
        setChangeGapWarning({
          show: true,
          changeGap: response.changeGap || 25000,
          estimatedApps: response.estimatedApps || 270000
        });
        setActionLoading(false);
        setOperationType(null);
        applyInProgressRef.current = false;
        return;
      }

      const scanType = useIncrementalScan ? 'Incremental' : 'Full';
      onSuccess?.(`${scanType} depot scan started - mappings will be applied when complete`);
      setTimeout(() => onDataRefresh?.(), 2000);

      // Note: NotificationsContext will create a notification via SignalR (DepotMappingStarted event)
      // and recovery is handled by recoverDepotMapping

      // Keep operation type active - it will be cleared when scan completes
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || t('management.depotMapping.errors.failedToProcessDepotMappings'));
      setOperationType(null);
    } finally {
      setActionLoading(false);
      applyInProgressRef.current = false;
    }
  };

  const formatNextRun = () => {
    if (picsLoading || !depotConfig) return t('common.loading');
    if (depotConfig.crawlIntervalHours === 0) return t('management.depotMapping.schedule.disabled');
    if (!localNextCrawlIn) return t('management.depotMapping.schedule.calculating');
    return formatNextCrawlTime(
      localNextCrawlIn,
      depotConfig.isProcessing,
      fullScanRequired,
      depotConfig.crawlIncrementalMode
    );
  };

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={Database}
          iconColor="indigo"
          title={t('management.depotMapping.title')}
          subtitle={t('management.depotMapping.subtitle')}
          helpContent={
            <HelpPopover position="left" width={320}>
              <HelpSection title={t('management.depotMapping.help.scanModes.title')} variant="subtle">
                <div className="divide-y divide-[var(--theme-text-muted)]">
                  <div className="py-1.5 first:pt-0 last:pb-0">
                    <div className="font-medium text-themed-primary">{t('management.depotMapping.help.scanModes.incremental.term')}</div>
                    <div className="mt-0.5">{t('management.depotMapping.help.scanModes.incremental.description')}</div>
                  </div>
                  <div className="py-1.5 first:pt-0 last:pb-0">
                    <div className="font-medium text-themed-primary">{t('management.depotMapping.help.scanModes.full.term')}</div>
                    <div className="mt-0.5">{t('management.depotMapping.help.scanModes.full.description')}</div>
                  </div>
                  <div className="py-1.5 first:pt-0 last:pb-0">
                    <div className="font-medium text-themed-primary">{t('management.depotMapping.help.scanModes.github.term')}</div>
                    <div className="mt-0.5">{t('management.depotMapping.help.scanModes.github.description')}</div>
                  </div>
                </div>
              </HelpSection>

              <HelpSection title={t('management.depotMapping.help.settings.title')} variant="subtle">
                {t('management.depotMapping.help.settings.description')}
              </HelpSection>

              <HelpNote type="info">
                {t('management.depotMapping.help.note')}
              </HelpNote>
            </HelpPopover>
          }
          actions={
            onNavigateToSteamApi && (
              <Button
                variant="outline"
                size="sm"
                onClick={onNavigateToSteamApi}
                rightSection={<ExternalLink className="w-3.5 h-3.5" />}
              >
                {t('management.depotMapping.configureSteamApi')}
              </Button>
            )
          }
        />

        {/* GitHub Download In Progress */}
        {githubDownloading && (
          <div className="mb-4 p-3 rounded-lg border bg-themed-info border-themed-info">
            <div className="flex items-start gap-3">
              <Loader2 className="w-5 h-5 flex-shrink-0 mt-0.5 animate-spin icon-info" />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1 text-themed-info">
                  {t('management.depotMapping.downloadingFromGitHub')}
                </p>
                <p className="text-xs text-themed-info opacity-90">
                  {t('management.depotMapping.downloadingDescription')}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* GitHub Download Complete - Incremental Scan Required */}
        {githubDownloadComplete && !depotConfig?.isProcessing && !githubDownloading && (
          <div className="mb-4 p-3 rounded-lg border bg-themed-info border-themed-info">
            <div className="flex items-start gap-3">
              <Database className="w-5 h-5 flex-shrink-0 mt-0.5 icon-info" />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1 text-themed-info">
                  {t('management.depotMapping.githubDownloadComplete')}
                </p>
                <p className="text-xs text-themed-info opacity-90">
                  {t('management.depotMapping.applyingMappingsDescription')}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Automatic Scan Skipped Warning - Don't show if GitHub download is complete or in progress */}
        {/* Note: automaticScanSkipped is now handled via SignalR in App.tsx */}
        {false && (
          <div className="mb-4 p-3 rounded-lg border bg-themed-error border-themed-error">
            <div className="flex items-start gap-3">
              <AlertCircle className="w-5 h-5 flex-shrink-0 mt-0.5 icon-error" />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1 text-themed-error">
                  {t('management.depotMapping.scanSkipped.title')}
                </p>
                <p className="text-xs text-themed-error opacity-90">
                  {t('management.depotMapping.scanSkipped.description')}
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Schedule Status */}
        <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
          <div className="flex flex-col lg:flex-row lg:items-start lg:justify-between gap-4">
            <div className="flex-1">
              <div className="flex items-center gap-2 mb-3">
                <Clock className="w-4 h-4 text-themed-primary" />
                <span className="text-sm font-medium text-themed-secondary">
                  {t('management.depotMapping.schedule.automaticSchedule')}
                </span>
              </div>
              <div className="text-xs text-themed-muted space-y-2 sm:space-y-1.5">
                <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                  <span className="opacity-60 text-left whitespace-nowrap">{t('management.depotMapping.schedule.runsEvery')}</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig
                      ? t('common.loading')
                      : depotConfig.crawlIntervalHours === 0
                        ? t('management.depotMapping.schedule.disabled')
                        : (() => {
                            // If Web API is unavailable (V2 down AND no API key), show GitHub interval
                            const webApiNotAvailable =
                              !webApiStatus?.isV2Available && !webApiStatus?.hasApiKey;

                            if (webApiNotAvailable) {
                              return t('management.depotMapping.intervals.30min');
                            }

                            // Otherwise show backend value
                            return depotConfig.crawlIncrementalMode === 'github'
                              ? t('management.depotMapping.intervals.30min')
                              : depotConfig.crawlIntervalHours === 0.5
                                ? t('management.depotMapping.intervals.30min')
                                : t('management.depotMapping.intervals.hours', { count: depotConfig.crawlIntervalHours });
                          })()}
                  </span>
                </div>
                <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                  <span className="opacity-60 text-left whitespace-nowrap">{t('management.depotMapping.schedule.scanMode')}</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig
                      ? t('common.loading')
                      : depotConfig.crawlIntervalHours === 0
                        ? t('management.depotMapping.schedule.disabled')
                        : (() => {
                            // If Web API is unavailable (V2 down AND no API key), show GitHub mode
                            const webApiNotAvailable =
                              !webApiStatus?.isV2Available && !webApiStatus?.hasApiKey;

                            if (webApiNotAvailable) {
                              return t('management.depotMapping.modes.github');
                            }

                            // Otherwise show backend value
                            return depotConfig.crawlIncrementalMode === 'github'
                              ? t('management.depotMapping.modes.github')
                              : depotConfig.crawlIncrementalMode
                                ? t('management.depotMapping.modes.incremental')
                                : t('management.depotMapping.modes.full');
                          })()}
                  </span>
                </div>
                <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                  <span className="opacity-60 text-left whitespace-nowrap">{t('management.depotMapping.schedule.nextRun')}</span>
                  <span className="font-medium text-themed-primary">
                    {!depotConfig || depotConfig.crawlIntervalHours === 0
                      ? t('management.depotMapping.schedule.disabled')
                      : formatNextRun()}
                  </span>
                </div>
                {depotConfig?.lastCrawlTime && (
                  <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                    <span className="opacity-60 text-left whitespace-nowrap">{t('management.depotMapping.schedule.lastRun')}</span>
                    <span className="font-medium text-themed-primary">
                      {depotConfig.crawlIntervalHours === 0
                        ? t('management.depotMapping.schedule.disabled')
                        : formattedLastCrawlTime}
                    </span>
                  </div>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2 w-full lg:w-auto lg:min-w-[200px]">
              {/* GitHub Mode: Fixed 30-minute interval (either actual GitHub mode or forced due to Web API unavailability) */}
              {(() => {
                // Web API is unavailable only if V2 is down AND there's no API key
                const webApiNotAvailable =
                  !webApiStatus?.isV2Available && !webApiStatus?.hasApiKey;
                const isGithubMode =
                  depotConfig?.crawlIncrementalMode === 'github' || webApiNotAvailable;

                if (isGithubMode) {
                  return (
                    <div>
                    <EnhancedDropdown
                      options={[{ value: '0.5', label: t('management.depotMapping.intervals.every30Min') }]}
                      value="0.5"
                      onChange={() => {}}
                      disabled={true}
                      className="w-full"
                    />
                      <p className="text-xs text-themed-muted mt-1">
                        {t('management.depotMapping.intervalFixedNote')}
                      </p>
                    </div>
                  );
                }

                // Non-GitHub Modes: User-configurable intervals
                return (
                  <EnhancedDropdown
                    options={[
                      { value: '0', label: t('management.depotMapping.intervals.disabled') },
                      { value: '1', label: t('management.depotMapping.intervals.everyHour') },
                      { value: '6', label: t('management.depotMapping.intervals.every6Hours') },
                      { value: '12', label: t('management.depotMapping.intervals.every12Hours') },
                      { value: '24', label: t('management.depotMapping.intervals.every24Hours') },
                      { value: '48', label: t('management.depotMapping.intervals.every2Days') },
                      { value: '168', label: t('management.depotMapping.intervals.weekly') }
                    ]}
                    value={depotConfig ? String(depotConfig.crawlIntervalHours) : '1'}
                    onChange={async (value) => {
                      const newInterval = Number(value);
                      try {
                        await fetch('/api/depots/rebuild/config/interval', ApiService.getFetchOptions({
                          method: 'PUT',
                          headers: { 'Content-Type': 'application/json' },
                          body: JSON.stringify(newInterval)
                        }));

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
                    label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
                      ? t('management.depotMapping.modes.incremental')
                      : t('management.depotMapping.modes.incrementalWebApiRequired'),
                    disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
                  },
                  {
                    value: 'full',
                    label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
                      ? t('management.depotMapping.modes.full')
                      : t('management.depotMapping.modes.fullWebApiRequired'),
                    disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
                  },
                  { value: 'github', label: t('management.depotMapping.modes.github') }
                ]}
                value={(() => {
                  // If Web API is unavailable (V2 down AND no API key), force GitHub mode
                  const webApiNotAvailable =
                    !webApiStatus?.isV2Available && !webApiStatus?.hasApiKey;

                  if (webApiNotAvailable) {
                    return 'github';
                  }

                  // Otherwise use backend value
                  return depotConfig?.crawlIncrementalMode === 'github'
                    ? 'github'
                    : depotConfig?.crawlIncrementalMode === false
                      ? 'full'
                      : 'incremental';
                })()}
                onChange={async (value) => {
                  try {
                    const wasGithubMode = depotConfig?.crawlIncrementalMode === 'github';

                    // If switching FROM GitHub to another mode, reset interval to 1 hour
                    if (wasGithubMode && value !== 'github') {
                      await fetch('/api/depots/rebuild/config/interval', ApiService.getFetchOptions({
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(1)
                      }));
                    }

                    // If GitHub is selected, force 30-minute interval
                    if (value === 'github') {
                      // Set interval to 0.5 hours (30 minutes)
                      await fetch('/api/depots/rebuild/config/interval', ApiService.getFetchOptions({
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(0.5)
                      }));

                      // IMPORTANT: Also update the "Apply Now Source" to match
                      // When user selects GitHub schedule, they likely want GitHub for manual apply too
                      setDepotSource('github');
                      storage.setItem('depotSource', 'github');
                    }

                    // Set scan mode
                    const incremental =
                      value === 'incremental' ? true : value === 'github' ? 'github' : false;
                    await fetch('/api/depots/rebuild/config/mode', ApiService.getFetchOptions({
                      method: 'PUT',
                      headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify(incremental)
                    }));

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
            {t('management.depotMapping.applyNowSource')}
          </label>
          <EnhancedDropdown
            options={[
              {
                value: 'incremental',
                label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
                  ? t('management.depotMapping.sources.steamIncremental')
                  : t('management.depotMapping.sources.steamIncrementalWebApiRequired'),
                disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
              },
              {
                value: 'full',
                label: (picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
                  ? t('management.depotMapping.sources.steamFull')
                  : t('management.depotMapping.sources.steamFullWebApiRequired'),
                disabled: !(picsProgress?.isWebApiAvailable || webApiStatus?.isFullyOperational || webApiStatus?.hasApiKey)
              },
              {
                value: 'github',
                label: githubDownloadComplete
                  ? t('management.depotMapping.sources.githubAlreadyDownloaded')
                  : t('management.depotMapping.sources.githubDownload'),
                disabled: githubDownloadComplete
              }
            ]}
            value={depotSource}
            onChange={(value) => {
              const newSource = value as DepotSource;
              setDepotSource(newSource);
              // Save to localStorage for persistence
              storage.setItem('depotSource', newSource);
            }}
            disabled={!isAuthenticated || mockMode}
            className="w-full"
          />
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
              depotConfig?.isProcessing ||
              mockMode ||
              !isAuthenticated ||
              githubDownloading
            }
            loading={actionLoading || depotConfig?.isProcessing}
            fullWidth
          >
            {actionLoading && operationType === 'downloading' && t('management.depotMapping.buttons.downloadingFromGitHub')}
            {actionLoading && operationType === 'scanning' && t('management.depotMapping.buttons.startingScan')}
            {!actionLoading &&
              depotConfig?.isProcessing &&
              t('management.depotMapping.buttons.scanning', { percent: Math.round(depotConfig.progressPercent) })}
            {!actionLoading &&
              !depotConfig?.isProcessing &&
              githubDownloadComplete &&
              t('management.depotMapping.buttons.applyingMappings')}
            {!actionLoading && !depotConfig?.isProcessing && !githubDownloadComplete && t('management.depotMapping.buttons.applyNow')}
          </Button>
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
            storage.setItem('depotSource', 'full');
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
              // Note: NotificationsContext will create a notification via SignalR (DepotMappingStarted event)
            } catch (err: unknown) {
              onError?.((err instanceof Error ? err.message : String(err)) || t('management.depotMapping.errors.failedToStartFullScan'));
              setOperationType(null);
            } finally {
              setActionLoading(false);
            }
          }}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
          hasSteamApiKey={webApiStatus?.hasApiKey ?? false}
          isDownloading={githubDownloading}
        />
      )}
    </>
  );
};

export default DepotMappingManager;
