import React, { useState, useEffect, useRef } from 'react';
import { Database, Clock, Zap } from 'lucide-react';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { FullScanRequiredModal } from '@components/shared/FullScanRequiredModal';

interface DepotMappingManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  actionLoading: boolean;
  setActionLoading: (loading: boolean) => void;
  isProcessingLogs: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

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
  nextCrawlIn: any;
  crawlIntervalHours: number;
  crawlIncrementalMode: boolean;
  lastScanWasForced?: boolean;
  isConnected: boolean;
  isLoggedOn: boolean;
}

type DepotSource = 'incremental' | 'full' | 'github';

const DepotMappingManager: React.FC<DepotMappingManagerProps> = ({
  isAuthenticated,
  mockMode,
  actionLoading,
  setActionLoading,
  isProcessingLogs,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const [depotProcessing, setDepotProcessing] = useState<PicsProgress | null>(null);
  const [depotSource, setDepotSource] = useState<DepotSource>('incremental');
  const [changeGapWarning, setChangeGapWarning] = useState<{
    show: boolean;
    changeGap: number;
    estimatedApps: number;
  } | null>(null);
  const [operationType, setOperationType] = useState<'downloading' | 'scanning' | null>(null);
  const depotPollingInterval = useRef<NodeJS.Timeout | null>(null);
  const hasShownForcedScanWarning = useRef(false);

  useEffect(() => {
    if (mockMode) {
      return;
    }

    startDepotPolling();

    return () => {
      if (depotPollingInterval.current) {
        clearInterval(depotPollingInterval.current);
      }
    };
  }, [mockMode]);

  // Detect when Steam forced a full scan during execution (when incremental was requested)
  // This is informational only - the scan is already running and can't be cancelled
  useEffect(() => {
    if (depotProcessing?.lastScanWasForced && depotProcessing?.isRunning && !hasShownForcedScanWarning.current) {
      hasShownForcedScanWarning.current = true;
      onError?.(
        'Steam API forced a full scan due to outdated depot data. Now scanning ~270,000 apps via Web API. This will take 15-30 minutes. Future scans will be faster with regular updates.'
      );
    }

    // Reset the warning flag when scan completes
    if (!depotProcessing?.isRunning && hasShownForcedScanWarning.current) {
      hasShownForcedScanWarning.current = false;
    }

    // Clear operation type when scan completes
    if (!depotProcessing?.isRunning && operationType === 'scanning') {
      setOperationType(null);
    }
  }, [depotProcessing?.lastScanWasForced, depotProcessing?.isRunning, onError, operationType]);

  const startDepotPolling = () => {
    const checkDepotStatus = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data: PicsProgress = await response.json();
          setDepotProcessing(data);
        }
      } catch (error) {
        console.error('Failed to fetch depot status:', error);
      }
    };

    checkDepotStatus();
    depotPollingInterval.current = setInterval(checkDepotStatus, 3000);
  };

  const handleDownloadFromGitHub = async () => {
    setChangeGapWarning(null);
    setActionLoading(true);
    setOperationType('downloading');

    try {
      await ApiService.downloadPrecreatedDepotData();
      onSuccess?.('Pre-created depot data downloaded and imported successfully');
      setTimeout(() => onDataRefresh?.(), 2000);
    } catch (err: any) {
      onError?.(err.message || 'Failed to download from GitHub');
    } finally {
      setActionLoading(false);
      setOperationType(null);
    }
  };

  const executeApplyDepotMappings = async (forceFull: boolean = false) => {
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
        onSuccess?.('Imported depot mappings to database - depot count will update after scan completes');
        await new Promise(resolve => setTimeout(resolve, 1000));
      }

      // Use the selected scan mode (or force full if modal confirmed)
      const useIncrementalScan = forceFull ? false : depotSource === 'incremental';
      console.log('[DepotMapping] Calling triggerSteamKitRebuild with incremental:', useIncrementalScan, 'depotSource:', depotSource, 'forceFull:', forceFull);
      const response = await ApiService.triggerSteamKitRebuild(useIncrementalScan);
      console.log('[DepotMapping] Backend response:', response);

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

      // Keep operation type active - it will be cleared when scan completes
    } catch (err: any) {
      onError?.(err.message || 'Failed to process depot mappings');
      setOperationType(null);
    } finally {
      setActionLoading(false);
    }
  };


  const formatNextRun = () => {
    if (depotProcessing?.isRunning) {
      return 'Running now';
    }

    if (depotProcessing?.nextCrawlIn !== undefined && depotProcessing?.nextCrawlIn !== null) {
      let totalSeconds: number;

      if (typeof depotProcessing.nextCrawlIn === 'object' && depotProcessing.nextCrawlIn.totalSeconds !== undefined) {
        totalSeconds = depotProcessing.nextCrawlIn.totalSeconds;
      } else if (typeof depotProcessing.nextCrawlIn === 'object' && depotProcessing.nextCrawlIn.totalHours !== undefined) {
        totalSeconds = depotProcessing.nextCrawlIn.totalHours * 3600;
      } else if (typeof depotProcessing.nextCrawlIn === 'string') {
        const parts = depotProcessing.nextCrawlIn.split(':');
        if (parts.length >= 3) {
          const dayHourPart = parts[0].split('.');
          let hours = 0;
          let days = 0;

          if (dayHourPart.length === 2) {
            days = parseInt(dayHourPart[0]) || 0;
            hours = parseInt(dayHourPart[1]) || 0;
          } else {
            hours = parseInt(parts[0]) || 0;
          }

          const minutes = parseInt(parts[1]) || 0;
          const seconds = parseInt(parts[2]) || 0;
          totalSeconds = (days * 86400) + (hours * 3600) + (minutes * 60) + seconds;
        } else {
          return 'Loading...';
        }
      } else if (typeof depotProcessing.nextCrawlIn === 'number') {
        totalSeconds = depotProcessing.nextCrawlIn;
      } else {
        return 'Loading...';
      }

      if (!isFinite(totalSeconds) || isNaN(totalSeconds)) return 'Loading...';

      if (totalSeconds <= 0) {
        return 'Due now';
      }

      const totalHours = totalSeconds / 3600;

      if (totalHours > 24) {
        const days = Math.floor(totalHours / 24);
        const hours = Math.floor(totalHours % 24);
        return hours > 0 ? `${days}d ${hours}h` : `${days} days`;
      }
      const hours = Math.floor(totalHours);
      const minutes = Math.floor((totalSeconds % 3600) / 60);
      return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
    }

    return 'Loading...';
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

        {/* Schedule Status */}
        <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
          <div className="flex items-start justify-between gap-4">
            <div className="flex-1">
              <div className="flex items-center gap-2 mb-2">
                <Clock className="w-4 h-4 text-themed-primary" />
                <span className="text-sm font-medium text-themed-secondary">Automatic Schedule</span>
              </div>
              <div className="text-xs text-themed-muted space-y-1">
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Runs every:</span>
                  <span className="font-medium text-themed-primary">
                    {depotProcessing?.crawlIntervalHours !== undefined
                      ? depotProcessing.crawlIntervalHours === 0
                        ? 'Disabled'
                        : `${depotProcessing.crawlIntervalHours} hour${depotProcessing.crawlIntervalHours !== 1 ? 's' : ''}`
                      : 'Loading...'}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Scan mode:</span>
                  <span className="font-medium text-themed-primary">
                    {depotProcessing?.crawlIntervalHours === 0
                      ? 'Disabled'
                      : depotProcessing?.crawlIncrementalMode !== undefined
                        ? depotProcessing.crawlIncrementalMode ? 'Incremental' : 'Full'
                        : 'Loading...'}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <span style={{ opacity: 0.6 }}>Next run:</span>
                  <span className="font-medium text-themed-primary">
                    {depotProcessing?.crawlIntervalHours === 0 ? 'Disabled' : formatNextRun()}
                  </span>
                </div>
                {depotProcessing?.lastCrawlTime && (
                  <div className="flex items-center gap-2">
                    <span style={{ opacity: 0.6 }}>Last run:</span>
                    <span className="font-medium text-themed-primary">
                      {depotProcessing.crawlIntervalHours === 0
                        ? 'Disabled'
                        : new Date(depotProcessing.lastCrawlTime).toLocaleString()}
                    </span>
                  </div>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2 min-w-[120px]">
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
                value={String(depotProcessing?.crawlIntervalHours || 1)}
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

                    const response = await fetch('/api/gameinfo/steamkit/progress');
                    if (response.ok) {
                      const data: PicsProgress = await response.json();
                      setDepotProcessing(data);
                    }
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
                value={String(depotProcessing?.crawlIncrementalMode ?? true)}
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

                    const response = await fetch('/api/gameinfo/steamkit/progress');
                    if (response.ok) {
                      const data: PicsProgress = await response.json();
                      setDepotProcessing(data);
                    }
                  } catch (error) {
                    console.error('Failed to update scan mode:', error);
                  }
                }}
                disabled={!isAuthenticated || mockMode || depotProcessing?.crawlIntervalHours === 0}
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
              { value: 'github', label: 'GitHub (Download)' }
            ]}
            value={depotSource}
            onChange={(value) => setDepotSource(value as DepotSource)}
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
              depotProcessing?.isRunning ||
              mockMode ||
              !isAuthenticated
            }
            loading={actionLoading || depotProcessing?.isRunning}
            fullWidth
          >
            {actionLoading && operationType === 'downloading' && 'Downloading from GitHub...'}
            {actionLoading && operationType === 'scanning' && 'Starting Scan...'}
            {!actionLoading && depotProcessing?.isRunning && `Scanning (${Math.round(depotProcessing.progressPercent)}%)`}
            {!actionLoading && !depotProcessing?.isRunning && 'Apply Now'}
          </Button>
        </div>


        <div className="mt-4 p-3 bg-themed-tertiary rounded-lg">
          <p className="text-xs text-themed-muted leading-relaxed">
            <strong>Automatic Schedule:</strong> Controls scan mode and interval for scheduled background runs
            <br />
            <strong>Apply Now Source:</strong> Choose data source when clicking "Apply Now" button
            <br />
            <strong>Steam (Incremental):</strong> Only scans apps that changed since last run (faster, recommended)
            <br />
            <strong>Steam (Full Scan):</strong> Re-scans all Steam apps from scratch (slower, ensures complete data)
            <br />
            <strong>GitHub (Download):</strong> Downloads pre-generated mappings from GitHub (fast, 290k+ depots, full replacement)
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
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
        />
      )}
    </>
  );
};

export default DepotMappingManager;
