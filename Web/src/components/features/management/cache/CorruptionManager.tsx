import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Loader2, ChevronDown, ChevronUp } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useDockerSocket } from '@contexts/DockerSocketContext';
import { useNotifications } from '@contexts/notifications';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import {
  ManagerCardHeader,
  LoadingState,
  EmptyState,
  ReadOnlyBadge
} from '@components/ui/ManagerCard';
import { useFormattedDateTime } from '@/hooks/useFormattedDateTime';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { useManagerLoading } from '@/hooks/useManagerLoading';
import type { CorruptedChunkDetail } from '@/types';

interface CorruptionManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const CorruptionManager: React.FC<CorruptionManagerProps> = ({ authMode, mockMode, onError }) => {
  const { t } = useTranslation();
  const { notifications, addNotification, isAnyRemovalRunning } = useNotifications();
  const { isDockerAvailable } = useDockerSocket();

  // Derive corruption detection scan state from notifications (standardized pattern like GameCacheDetector)
  const activeCorruptionDetectionNotification = notifications.find(
    (n) => n.type === 'corruption_detection' && n.status === 'running'
  );
  const isScanningFromNotification = !!activeCorruptionDetectionNotification;

  // Track local starting state for immediate UI feedback before SignalR events arrive
  const [isStartingScan, setIsStartingScan] = useState(false);

  // Combined scanning state: either notification says running OR we're in starting phase
  const isScanning = isScanningFromNotification || isStartingScan;

  // State
  const [corruptionSummary, setCorruptionSummary] = useState<Record<string, number>>({});
  const [pendingCorruptionRemoval, setPendingCorruptionRemoval] = useState<string | null>(null);
  const [expandedCorruptionService, setExpandedCorruptionService] = useState<string | null>(null);
  const [corruptionDetails, setCorruptionDetails] = useState<
    Record<string, CorruptedChunkDetail[]>
  >({});
  const [loadingDetails, setLoadingDetails] = useState<string | null>(null);
  const { isLoading, hasInitiallyLoaded, setLoading, markLoaded } = useManagerLoading();
  const [startingCorruptionRemoval, setStartingCorruptionRemoval] = useState<string | null>(null);

  // Use shared directory permissions hook
  const { logsReadOnly, cacheReadOnly, logsExist, cacheExist, checkingPermissions } =
    useDirectoryPermissions();
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [hasCachedResults, setHasCachedResults] = useState(false);
  const [missThreshold, setMissThreshold] = useState(3);
  const [compareToCacheLogs, setCompareToCacheLogs] = useState(true);

  const detectionModeOptions = [
    {
      value: 'false',
      label: t('management.corruption.detectionModeLogsOnly'),
      shortLabel: t('management.corruption.detectionModeLogsOnlyShort'),
      description: t('management.corruption.detectionModeLogsOnlyDesc')
    },
    {
      value: 'true',
      label: t('management.corruption.detectionModeCacheLogs'),
      shortLabel: t('management.corruption.detectionModeCacheLogsShort'),
      description: t('management.corruption.detectionModeCacheLogsDesc')
    }
  ];

  const thresholdOptions = [
    {
      value: '3',
      label: t('management.corruption.sensitivityHigh'),
      shortLabel: t('management.corruption.sensitivityHighShort'),
      description: t('management.corruption.sensitivityHighDesc')
    },
    {
      value: '5',
      label: t('management.corruption.sensitivityMedium'),
      shortLabel: t('management.corruption.sensitivityMediumShort'),
      description: t('management.corruption.sensitivityMediumDesc')
    },
    {
      value: '10',
      label: t('management.corruption.sensitivityLow'),
      shortLabel: t('management.corruption.sensitivityLowShort'),
      description: t('management.corruption.sensitivityLowDesc')
    }
  ];

  const formattedLastDetection = useFormattedDateTime(lastDetectionTime);

  // Derive active corruption removal from notifications
  const activeCorruptionRemovalNotification = notifications.find(
    (n) => n.type === 'corruption_removal' && n.status === 'running'
  );
  const removingCorruption =
    (activeCorruptionRemovalNotification?.details?.service as string | null) ?? null;

  // Load cached data from database
  const loadCachedData = useCallback(
    async (showNotification = false) => {
      setLoading(true);
      try {
        const cached = await ApiService.getCachedCorruptionDetection();
        if (cached.hasCachedResults && cached.corruptionCounts) {
          setCorruptionSummary(cached.corruptionCounts);
          setLastDetectionTime(cached.lastDetectionTime || null);
          setHasCachedResults(true);

          // Show notification only when explicitly requested or once per session
          const sessionKey = 'corruptionManager_loadedNotificationShown';
          const alreadyShownThisSession = sessionStorage.getItem(sessionKey) === 'true';
          const totalCorrupted = Object.values(cached.corruptionCounts).reduce((a, b) => a + b, 0);
          const serviceCount = Object.keys(cached.corruptionCounts).filter(
            (k) => cached.corruptionCounts![k] > 0
          ).length;

          if (showNotification || !alreadyShownThisSession) {
            if (totalCorrupted > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.corruption.notifications.loadedResults', {
                  chunks: totalCorrupted.toLocaleString(),
                  services: serviceCount
                }),
                details: { notificationType: 'info' }
              });
            } else if (showNotification) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.corruption.notifications.noCorruptedInPrevious'),
                details: { notificationType: 'success' }
              });
            }
            sessionStorage.setItem(sessionKey, 'true');
          }
        } else {
          setCorruptionSummary({});
          setLastDetectionTime(null);
          setHasCachedResults(false);

          if (showNotification) {
            addNotification({
              type: 'generic',
              status: 'completed',
              message: t('management.corruption.notifications.noPreviousResults'),
              details: { notificationType: 'info' }
            });
          }
        }
        markLoaded();
      } catch (err: unknown) {
        console.error('Failed to load cached corruption data:', err);
        setLoading(false);
      }
    },
    [addNotification, t, setLoading, markLoaded]
  );

  // Start a background scan
  const startScan = useCallback(async () => {
    if (isScanning || mockMode) return;

    // Note: NotificationsContext automatically replaces notifications with the same ID
    // when a new operation starts, so manual dismissal is not needed

    setIsStartingScan(true);
    setCorruptionSummary({});
    setLastDetectionTime(null);
    setHasCachedResults(false);

    try {
      // Start background detection - SignalR will send CorruptionDetectionStarted event
      await ApiService.startCorruptionDetection(missThreshold, compareToCacheLogs);
      // Note: NotificationsContext will create a notification via SignalR (CorruptionDetectionStarted event)
    } catch (err: unknown) {
      console.error('Failed to start corruption scan:', err);
      setIsStartingScan(false);
    }
  }, [isScanning, mockMode, missThreshold, compareToCacheLogs]);

  // Listen for corruption detection completion via notifications
  useEffect(() => {
    // Handle corruption detection completion - ONLY if we were starting a scan
    if (isStartingScan) {
      const corruptionDetectionCompleteNotifs = notifications.filter(
        (n) => n.type === 'corruption_detection' && n.status === 'completed'
      );
      if (corruptionDetectionCompleteNotifs.length > 0) {
        setIsStartingScan(false);
        setLoading(true);

        // Load fresh results from the database (backend already saved them)
        const loadResults = async () => {
          try {
            const result = await ApiService.getCachedCorruptionDetection();
            if (result.hasCachedResults && result.corruptionCounts) {
              setCorruptionSummary(result.corruptionCounts);
              setLastDetectionTime(result.lastDetectionTime || null);
              setHasCachedResults(true);
            } else {
              // Scan completed but found zero corruption
              // Still mark as "has results" so UI shows "No corrupted chunks detected"
              setCorruptionSummary({});
              setLastDetectionTime(new Date().toISOString());
              setHasCachedResults(true);
            }
          } catch (err) {
            console.error('[CorruptionManager] Failed to load detection results:', err);
          } finally {
            markLoaded();
          }
        };
        loadResults();
      }

      // Handle corruption detection failure - ONLY if we were starting a scan
      const corruptionDetectionFailedNotifs = notifications.filter(
        (n) => n.type === 'corruption_detection' && n.status === 'failed'
      );
      if (corruptionDetectionFailedNotifs.length > 0) {
        console.error('[CorruptionManager] Corruption detection failed');
        setIsStartingScan(false);
        // Note: Error is displayed in notification bar, no inline error needed
      }
    }
  }, [notifications, isStartingScan, setLoading, markLoaded]);

  // Listen for corruption removal completion - remove service from local state immediately
  // This follows the same pattern as GameCacheDetector which removes items from state on completion
  useEffect(() => {
    // Find all completed corruption removal notifications
    const completedCorruptionRemovals = notifications.filter(
      (n) => n.type === 'corruption_removal' && n.status === 'completed'
    );

    completedCorruptionRemovals.forEach((notif) => {
      const serviceName = notif.details?.service;
      if (!serviceName) return;

      // Remove the service from local state immediately (backend already handled the removal)
      setCorruptionSummary((prev) => {
        const updated = { ...prev };
        delete updated[serviceName];
        return updated;
      });

      // Also clear expanded state and details for this service
      if (expandedCorruptionService === serviceName) {
        setExpandedCorruptionService(null);
      }
      setCorruptionDetails((prev) => {
        const updated = { ...prev };
        delete updated[serviceName];
        return updated;
      });
    });
  }, [notifications, expandedCorruptionService]);

  // Initial load - load cached data without auto-scanning (matches GameCacheDetector pattern)
  // Note: Directory permissions are now handled by useDirectoryPermissions hook
  useEffect(() => {
    if (!hasInitiallyLoaded) {
      // Only load cached data - don't auto-start scan
      loadCachedData();
    }
  }, [hasInitiallyLoaded, loadCachedData]);

  const handleRemoveCorruption = (service: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }
    setPendingCorruptionRemoval(service);
  };

  const confirmRemoveCorruption = async () => {
    if (!pendingCorruptionRemoval || authMode !== 'authenticated') return;

    const service = pendingCorruptionRemoval;
    setPendingCorruptionRemoval(null);
    setStartingCorruptionRemoval(service);

    try {
      await ApiService.removeCorruptedChunks(service, missThreshold, compareToCacheLogs);
    } catch (err: unknown) {
      console.error('Removal failed:', err);
      onError?.(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.corruption.errors.removeCorrupted', { service })
      );
    } finally {
      setStartingCorruptionRemoval(null);
    }
  };

  const toggleCorruptionDetails = async (service: string) => {
    if (expandedCorruptionService === service) {
      setExpandedCorruptionService(null);
      return;
    }

    setExpandedCorruptionService(service);

    if (!corruptionDetails[service]) {
      setLoadingDetails(service);
      try {
        const details = await ApiService.getCorruptionDetails(
          service,
          false,
          missThreshold,
          compareToCacheLogs
        );
        setCorruptionDetails((prev) => ({ ...prev, [service]: details }));
      } catch (err: unknown) {
        onError?.(
          (err instanceof Error ? err.message : String(err)) ||
            t('management.corruption.errors.loadDetails', { service })
        );
        setExpandedCorruptionService(null);
      } finally {
        setLoadingDetails(null);
      }
    }
  };

  const corruptionList = Object.entries(corruptionSummary)
    .filter(([_, count]) => count > 0)
    .sort((a, b) => b[1] - a[1]);

  const isReadOnly = compareToCacheLogs ? logsReadOnly || cacheReadOnly : logsReadOnly;
  const directoryMissing = compareToCacheLogs ? !logsExist || !cacheExist : !logsExist;
  const hasPermissionIssue = isReadOnly || directoryMissing;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.corruption.help.whatThisDoes.title')}>
        {t('management.corruption.help.whatThisDoes.description')}
      </HelpSection>

      <HelpSection
        title={t('management.corruption.help.whatRemovalDeletes.title')}
        variant="subtle"
      >
        <div className="divide-y divide-[var(--theme-text-muted)]">
          <div className="py-1.5 first:pt-0 last:pb-0">
            <div className="font-medium text-themed-primary">
              {t('management.corruption.help.whatRemovalDeletes.cacheFilesLabel')}
            </div>
            <div className="mt-0.5">
              {t('management.corruption.help.whatRemovalDeletes.cacheFiles')}
            </div>
          </div>
          <div className="py-1.5 first:pt-0 last:pb-0">
            <div className="font-medium text-themed-primary">
              {t('management.corruption.help.whatRemovalDeletes.logEntriesLabel')}
            </div>
            <div className="mt-0.5">
              {t('management.corruption.help.whatRemovalDeletes.logEntries')}
            </div>
          </div>
          <div className="py-1.5 first:pt-0 last:pb-0">
            <div className="font-medium text-themed-primary">
              {t('management.corruption.help.whatRemovalDeletes.databaseRecordsLabel')}
            </div>
            <div className="mt-0.5">
              {t('management.corruption.help.whatRemovalDeletes.databaseRecords')}
            </div>
          </div>
        </div>
      </HelpSection>

      <HelpNote type="warning">
        {compareToCacheLogs
          ? t('management.corruption.help.warning')
          : t('management.corruption.help.warningLogsOnly')}
      </HelpNote>
    </HelpPopover>
  );

  // Action buttons for header - matches GameCacheDetector pattern
  // Cancel is handled by UniversalNotificationBar via CANCEL_CONFIGS
  const headerActions = (
    <div className="flex items-center gap-2">
      <EnhancedDropdown
        options={detectionModeOptions}
        value={String(compareToCacheLogs)}
        onChange={(val: string) => setCompareToCacheLogs(val === 'true')}
        disabled={isLoading || isScanning || isAnyRemovalRunning}
        compactMode={true}
        dropdownWidth="w-72"
        alignRight={true}
        dropdownTitle={t('management.corruption.detectionModeTitle')}
      />
      <EnhancedDropdown
        options={thresholdOptions}
        value={String(missThreshold)}
        onChange={(val: string) => setMissThreshold(Number(val))}
        disabled={isLoading || isScanning || isAnyRemovalRunning}
        compactMode={true}
        dropdownWidth="w-72"
        alignRight={true}
        dropdownTitle={t('management.corruption.sensitivityTitle')}
      />
      <Tooltip content={t('management.corruption.loadPreviousResults')} position="top">
        <Button
          onClick={() => loadCachedData(true)}
          disabled={isLoading || isScanning || isAnyRemovalRunning}
          variant="subtle"
          size="sm"
        >
          {isLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : t('common.load')}
        </Button>
      </Tooltip>
      <Tooltip content={t('management.corruption.scanForCorrupted')} position="top">
        <Button
          onClick={() => startScan()}
          disabled={isLoading || isScanning || isAnyRemovalRunning}
          variant="filled"
          color="blue"
          size="sm"
        >
          {isScanning ? <Loader2 className="w-4 h-4 animate-spin" /> : t('common.scan')}
        </Button>
      </Tooltip>
    </div>
  );

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={AlertTriangle}
          iconColor="yellow"
          title={t('management.corruption.title')}
          subtitle={t('management.corruption.subtitle')}
          helpContent={helpContent}
          permissions={{
            logsReadOnly,
            cacheReadOnly,
            checkingPermissions
          }}
          actions={headerActions}
        />

        {/* Previous Results Badge - matches GameCacheDetector pattern */}
        {hasCachedResults && lastDetectionTime && !isScanning && !isLoading && (
          <Alert color="blue" className="mb-4">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium">{t('common.resultsFromPreviousScan')}</span>
              <span className="text-xs text-themed-muted">{formattedLastDetection}</span>
            </div>
          </Alert>
        )}

        {/* Scanning Status */}
        {isScanning && <LoadingState message={t('management.corruption.scanningMessage')} />}

        {/* Directory Missing Warning */}
        {directoryMissing && (
          <Alert color="red" className="mb-6">
            <div>
              <p className="font-medium">
                {!logsExist && !cacheExist
                  ? t(
                      'management.corruption.alerts.logsAndCacheMissing',
                      'Logs and cache directories do not exist'
                    )
                  : !logsExist
                    ? t('management.corruption.alerts.logsMissing', 'Logs directory does not exist')
                    : t(
                        'management.corruption.alerts.cacheMissing',
                        'Cache directory does not exist'
                      )}
              </p>
              <p className="text-sm mt-1">
                {t(
                  'management.corruption.alerts.directoryNotFound',
                  'The required directories were not found. Ensure they are mounted correctly in docker-compose.'
                )}
              </p>
            </div>
          </Alert>
        )}

        {/* Read-Only Warning */}
        {isReadOnly && !directoryMissing && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">
                {compareToCacheLogs
                  ? logsReadOnly && cacheReadOnly
                    ? t('management.corruption.alerts.logsAndCacheReadOnly')
                    : logsReadOnly
                      ? t('management.corruption.alerts.logsReadOnly')
                      : t('management.corruption.alerts.cacheReadOnly')
                  : t('management.corruption.alerts.logsReadOnly')}
              </p>
              <p className="text-sm mt-1">
                {compareToCacheLogs
                  ? t('management.corruption.alerts.requiresWriteAccess')
                  : t('management.corruption.alerts.requiresLogsWriteAccess')}{' '}
                <code className="bg-themed-tertiary px-1 rounded">:ro</code>{' '}
                {t('management.corruption.alerts.fromVolumeMounts')}
              </p>
            </div>
          </Alert>
        )}

        {/* Docker Socket Warning */}
        {!isDockerAvailable && !hasPermissionIssue && (
          <Alert color="orange" className="mb-6">
            <div className="min-w-0">
              <p className="font-medium">
                {t('management.corruption.alerts.dockerSocketUnavailable')}
              </p>
              <p className="text-sm mt-1">
                {t('management.corruption.alerts.requiresNginxSignal')}
              </p>
              <p className="text-sm mt-2">
                {t('management.logRemoval.alerts.dockerSocket.addVolumes')}
              </p>
              <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1 break-all">
                - /var/run/docker.sock:/var/run/docker.sock
              </code>
            </div>
          </Alert>
        )}

        {/* Content */}
        {hasPermissionIssue || !isDockerAvailable ? (
          <ReadOnlyBadge
            message={
              directoryMissing
                ? t('management.corruption.directoryMissing', 'Required directories not found')
                : isReadOnly
                  ? t('management.corruption.readOnly')
                  : t('management.corruption.dockerSocketRequired')
            }
          />
        ) : (
          <>
            {isLoading && !isScanning ? (
              <LoadingState message={t('management.corruption.loadingCachedData')} />
            ) : hasCachedResults && corruptionList.length > 0 ? (
              <div className="space-y-3">
                {corruptionList.map(([service, count]) => (
                  <div
                    key={`corruption-${service}`}
                    className="rounded-lg border bg-themed-tertiary border-themed-secondary"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-3 p-3">
                      <div className="flex items-center gap-2 flex-1 min-w-0">
                        <Button
                          onClick={() => toggleCorruptionDetails(service)}
                          variant="subtle"
                          size="sm"
                          className="flex-shrink-0"
                          disabled={isAnyRemovalRunning}
                        >
                          {expandedCorruptionService === service ? (
                            <ChevronUp className="w-4 h-4" />
                          ) : (
                            <ChevronDown className="w-4 h-4" />
                          )}
                        </Button>
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
                            <span className="capitalize font-medium text-themed-primary">
                              {service}
                            </span>
                            <span className="text-xs text-themed-muted">
                              ({t('management.corruption.corruptedChunks', { count })})
                            </span>
                          </div>
                        </div>
                      </div>
                      <Tooltip content={t('management.corruption.deleteCorrupted')}>
                        <Button
                          onClick={() => handleRemoveCorruption(service)}
                          disabled={
                            mockMode ||
                            isAnyRemovalRunning ||
                            !!startingCorruptionRemoval ||
                            authMode !== 'authenticated' ||
                            logsReadOnly ||
                            (compareToCacheLogs && cacheReadOnly) ||
                            !isDockerAvailable ||
                            checkingPermissions
                          }
                          variant="filled"
                          color="red"
                          size="sm"
                          loading={
                            removingCorruption === service || startingCorruptionRemoval === service
                          }
                          className="w-full sm:w-auto flex-shrink-0"
                        >
                          {removingCorruption !== service && startingCorruptionRemoval !== service
                            ? t('management.corruption.removeAll')
                            : t('management.corruption.removing')}
                        </Button>
                      </Tooltip>
                    </div>

                    {/* Expandable Details Section */}
                    {expandedCorruptionService === service && (
                      <div className="border-t px-3 py-3 border-themed-secondary">
                        {loadingDetails === service ? (
                          <LoadingState message={t('management.corruption.loadingDetails')} />
                        ) : corruptionDetails[service] && corruptionDetails[service].length > 0 ? (
                          <div className="space-y-2 max-h-96 overflow-y-auto">
                            {corruptionDetails[service].map((chunk, idx) => (
                              <div
                                key={idx}
                                className="p-2 rounded border bg-themed-secondary border-themed-primary"
                              >
                                <div className="flex items-start gap-2">
                                  <AlertTriangle className="w-4 h-4 text-themed-warning flex-shrink-0 mt-0.5" />
                                  <div className="flex-1 min-w-0">
                                    <div className="mb-1">
                                      <Tooltip content={chunk.url}>
                                        <span className="text-xs font-mono text-themed-primary truncate block">
                                          {chunk.url}
                                        </span>
                                      </Tooltip>
                                    </div>
                                    <div className="flex items-center gap-3 text-xs text-themed-muted">
                                      <span>
                                        {t('management.corruption.missCount')}{' '}
                                        <strong className="text-themed-error">
                                          {chunk.miss_count || 0}
                                        </strong>
                                      </span>
                                      {chunk.cache_file_path && (
                                        <Tooltip content={chunk.cache_file_path}>
                                          <span className="truncate">
                                            {t('management.corruption.cache')}{' '}
                                            <code className="text-xs">
                                              {chunk.cache_file_path.split('/').pop() ||
                                                chunk.cache_file_path.split('\\').pop()}
                                            </code>
                                          </span>
                                        </Tooltip>
                                      )}
                                    </div>
                                  </div>
                                </div>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <div className="text-center py-4 text-themed-muted text-sm">
                            {t('management.corruption.noDetailsAvailable')}
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            ) : hasCachedResults && corruptionList.length === 0 ? (
              <EmptyState
                icon={AlertTriangle}
                title={t('management.corruption.emptyStates.noCorrupted.title')}
                subtitle={t('management.corruption.emptyStates.noCorrupted.subtitle')}
              />
            ) : !hasCachedResults && !isScanning && !isLoading ? (
              <EmptyState
                icon={AlertTriangle}
                title={t('management.corruption.emptyStates.noCachedData.title')}
                subtitle={t('management.corruption.emptyStates.noCachedData.subtitle')}
              />
            ) : null}
          </>
        )}
      </Card>

      {/* Corruption Removal Confirmation Modal */}
      <Modal
        opened={pendingCorruptionRemoval !== null}
        onClose={() => setPendingCorruptionRemoval(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.corruption.modal.title')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.corruption.modal.confirmRemove', { service: pendingCorruptionRemoval })}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.corruption.modal.willDelete')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                {compareToCacheLogs && (
                  <li>
                    <strong>{t('management.corruption.modal.cacheFilesLabel')}</strong>{' '}
                    {t('management.corruption.modal.cacheFilesDesc')}
                  </li>
                )}
                <li>
                  <strong>{t('management.corruption.modal.logEntriesLabel')}</strong>{' '}
                  {t('management.corruption.modal.logEntriesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.databaseRecordsLabel')}</strong>{' '}
                  {t('management.corruption.modal.databaseRecordsDesc')}
                </li>
              </ul>
            </div>
          </Alert>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.cache.alerts.important')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.corruption.modal.cannotBeUndone')}</li>
                <li>{t('management.corruption.modal.mayTakeSeveralMinutes')}</li>
                <li>
                  {t('management.corruption.modal.validFilesRemain', {
                    service: pendingCorruptionRemoval
                  })}
                </li>
                <li>
                  {t('management.corruption.modal.removesApproximately', {
                    count: corruptionSummary[pendingCorruptionRemoval || ''] || 0
                  })}
                </li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingCorruptionRemoval(null)}>
              {t('common.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={confirmRemoveCorruption}>
              {compareToCacheLogs
                ? t('management.corruption.modal.deleteCacheAndLogs')
                : t('management.corruption.modal.deleteLogsOnly')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CorruptionManager;
