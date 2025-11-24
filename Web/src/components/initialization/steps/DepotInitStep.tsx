import React, { useEffect, useState } from 'react';
import { Cloud, Database, Loader2, AlertTriangle, ArrowLeft, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { useSignalR } from '@contexts/SignalRContext';
import ApiService from '@services/api.service';

interface DepotInitStepProps {
  picsData: any;
  usingSteamAuth?: boolean;
  hideOptions?: boolean; // Hide all options (GitHub-only mode)
  onDownloadPrecreated: () => void;
  onGenerateOwn: () => void;
  onContinue: () => void;
  onBackToSteamAuth?: () => void;
  onComplete: () => void; // Auto-advance when GitHub download completes
}

export const DepotInitStep: React.FC<DepotInitStepProps> = ({
  picsData,
  usingSteamAuth = false,
  hideOptions = false,
  onDownloadPrecreated,
  onGenerateOwn,
  onContinue,
  onBackToSteamAuth,
  onComplete
}) => {
  const signalR = useSignalR();
  const [initializing, setInitializing] = useState(false);
  const [selectedMethod, setSelectedMethod] = useState<'cloud' | 'generate' | 'continue' | null>(
    null
  );
  const [downloadStatus, setDownloadStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState(0);

  // Listen to SignalR events for depot mapping
  useEffect(() => {
    const handleDepotMappingStarted = (payload: any) => {
      console.log('[DepotInit] DepotMappingStarted received:', payload);

      if (payload.scanMode === 'github') {
        setInitializing(true);
        setSelectedMethod('cloud');
        setDownloadStatus(payload.message || 'Downloading depot mappings from GitHub...');
        setProgress(0);
      } else {
        // Steam PICS scan started - don't handle here, PicsProgressStep will handle it
        console.log('[DepotInit] Steam PICS scan started, ignoring in DepotInitStep');
      }
    };

    const handleDepotMappingProgress = (payload: any) => {
      console.log('[DepotInit] DepotMappingProgress received:', payload);

      // Only handle GitHub mode progress here
      if (selectedMethod === 'cloud') {
        setProgress(payload.percentComplete || 0);
        setDownloadStatus(payload.message || 'Processing depot mappings...');
      }
    };

    const handleDepotMappingComplete = (payload: any) => {
      console.log('[DepotInit] DepotMappingComplete received:', payload);

      // Only handle GitHub mode completion here
      if (payload.scanMode === 'github') {
        if (payload.success) {
          setDownloadStatus('Success! Depot mappings imported.');
          setProgress(100);
          setInitializing(false);

          // Brief delay to show success message, then auto-advance
          setTimeout(() => {
            setSelectedMethod(null);
            setDownloadStatus(null);
            onComplete();
          }, 1500);
        } else {
          setError(payload.error || payload.message || 'Failed to download depot data');
          setInitializing(false);
          setSelectedMethod(null);
          setDownloadStatus(null);
        }
      } else if (selectedMethod === 'generate' || selectedMethod === 'continue') {
        // Steam scan completed - step will have already advanced to PicsProgressStep
        console.log('[DepotInit] Steam scan completed, ignoring in DepotInitStep');
      }
    };

    // Register SignalR listeners
    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handleDepotMappingComplete);

    // Cleanup
    return () => {
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handleDepotMappingComplete);
    };
  }, [signalR, selectedMethod, onComplete]);

  // Check if operation is already running on mount
  useEffect(() => {
    const checkActiveOperation = async () => {
      try {
        const status = await ApiService.getPicsStatus();
        if (status?.steamKit2?.isRebuildRunning) {
          console.log('[DepotInit] Detected active operation on mount');
          setInitializing(true);
          setDownloadStatus('Operation in progress...');
        }
      } catch (error) {
        console.error('[DepotInit] Failed to check status:', error);
      }
    };

    checkActiveOperation();
  }, []);

  // Auto-trigger GitHub download when in GitHub-only mode (hideOptions=true)
  useEffect(() => {
    if (hideOptions && !initializing && !selectedMethod && !error) {
      console.log('[DepotInit] GitHub-only mode detected, auto-triggering download...');
      handleDownload();
    }
  }, [hideOptions]); // Only run when component mounts or hideOptions changes

  const shouldShowContinueOption = () => {
    if (!picsData) return false;
    return picsData.jsonFile?.exists === true;
  };

  const handleDownload = async () => {
    setInitializing(true);
    setSelectedMethod('cloud');
    setError(null);
    setDownloadStatus('Starting download...');

    try {
      await ApiService.downloadPrecreatedDepotData();
      // SignalR will handle the rest
    } catch (err: any) {
      console.error('[DepotInit] Error in handleDownload:', err);
      setError(err.message || 'Failed to download pre-created depot data from GitHub');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
    }
  };

  const handleGenerate = async () => {
    setInitializing(true);
    setSelectedMethod('generate');
    setError(null);

    try {
      const response = await ApiService.triggerSteamKitRebuild(false);

      if (response.requiresFullScan) {
        setError('Unable to start full scan. Please try again or download from GitHub.');
        setInitializing(false);
        setSelectedMethod(null);
        return;
      }

      // SignalR will send DepotMappingStarted, then parent will advance to PicsProgressStep
      onGenerateOwn();
    } catch (err: any) {
      console.error('[DepotInit] Error in handleGenerate:', err);
      setError(err.message || 'Failed to start depot generation');
      setInitializing(false);
      setSelectedMethod(null);
    }
  };

  const handleContinueUpdate = async () => {
    setInitializing(true);
    setSelectedMethod('continue');
    setError(null);
    setDownloadStatus('Starting incremental update...');

    try {
      const response = await ApiService.triggerSteamKitRebuild(true);

      if (response.requiresFullScan) {
        setDownloadStatus(
          `Change gap too large (${response.changeGap || 'unknown'}). Starting full scan instead...`
        );

        await new Promise((resolve) => setTimeout(resolve, 1000));

        const fullScanResponse = await ApiService.triggerSteamKitRebuild(false);

        if (fullScanResponse.requiresFullScan) {
          setError('Unable to start scan. Please try downloading from GitHub instead.');
          setInitializing(false);
          setSelectedMethod(null);
          setDownloadStatus(null);
          return;
        }
      }

      // SignalR will send DepotMappingStarted, then parent will advance to PicsProgressStep
      onContinue();
    } catch (err: any) {
      console.error('[DepotInit] Error in handleContinue:', err);
      setError(err.message || 'Failed to run incremental update');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
    }
  };

  // GitHub-only mode: Show beautiful card layout
  if (hideOptions) {
    return (
      <div className="space-y-6">
        {/* Modern Card Layout */}
        <div
          className="rounded-xl overflow-hidden border"
          style={{
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)'
          }}
        >
          {/* Gradient Banner with Icon */}
          <div
            className="relative h-32 flex items-center justify-center"
            style={{
              background:
                progress === 100
                  ? 'linear-gradient(135deg, var(--theme-success) 0%, var(--theme-success-dark, var(--theme-success)) 100%)'
                  : initializing
                    ? 'linear-gradient(135deg, var(--theme-primary) 0%, var(--theme-primary-dark, var(--theme-primary)) 100%)'
                    : 'linear-gradient(135deg, var(--theme-info) 0%, var(--theme-info-dark, var(--theme-info)) 100%)'
            }}
          >
            {progress === 100 ? (
              <CheckCircle size={64} className="text-white" />
            ) : initializing ? (
              <Loader2 size={64} className="text-white animate-spin" />
            ) : (
              <Cloud size={64} className="text-white" />
            )}
          </div>

          {/* Content Section */}
          <div className="p-8">
            <h2 className="text-2xl font-bold text-themed-primary mb-2 text-center">
              {progress === 100
                ? 'Download Complete!'
                : initializing
                  ? 'Downloading Depot Mappings'
                  : 'Preparing Download'}
            </h2>
            <p className="text-themed-secondary text-center mb-6">
              {progress === 100
                ? 'GitHub depot data has been imported successfully'
                : initializing
                  ? 'Fetching pre-created depot mappings from GitHub...'
                  : 'Setting up depot mapping download from GitHub'}
            </p>

            {/* Info Box (when not downloading and not complete) */}
            {!initializing && progress === 0 && !error && (
              <div
                className="p-4 rounded-lg mb-6"
                style={{
                  backgroundColor: 'var(--theme-info-bg)',
                  color: 'var(--theme-info-text)'
                }}
              >
                <p className="text-sm">
                  Downloading community-maintained depot mappings from GitHub. This provides access
                  to 290,000+ game depot mappings and typically takes 30-60 seconds.
                </p>
              </div>
            )}

            {/* Progress Display */}
            {initializing && progress < 100 && (
              <div className="space-y-4 mb-6">
                {/* Status Message */}
                <div
                  className="p-4 rounded-lg text-center"
                  style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                >
                  <p className="text-sm font-semibold text-themed-primary">
                    {downloadStatus || 'Downloading...'}
                  </p>
                </div>

                {/* Progress Bar */}
                {progress > 0 && (
                  <div>
                    <div
                      className="w-full rounded-full h-3 overflow-hidden mb-2"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full transition-all duration-500 ease-out"
                        style={{
                          width: `${progress}%`,
                          backgroundColor: 'var(--theme-primary)'
                        }}
                      />
                    </div>
                    <p className="text-sm text-themed-secondary text-center">
                      {progress.toFixed(1)}% complete
                    </p>
                  </div>
                )}
              </div>
            )}

            {/* Success Message */}
            {progress === 100 && (
              <div
                className="p-4 rounded-lg mb-6"
                style={{
                  backgroundColor: 'var(--theme-success-bg)',
                  color: 'var(--theme-success-text)'
                }}
              >
                <p className="text-sm flex items-center justify-center gap-2">
                  <CheckCircle className="w-4 h-4" />
                  {downloadStatus || 'Depot mappings imported successfully!'}
                </p>
              </div>
            )}

            {/* Error Message */}
            {error && (
              <div
                className="p-4 rounded-lg mb-6"
                style={{
                  backgroundColor: 'var(--theme-error-bg)',
                  color: 'var(--theme-error-text)'
                }}
              >
                <p className="text-sm">{error}</p>
              </div>
            )}
          </div>
        </div>
      </div>
    );
  }

  // Regular mode: Show choice options
  return (
    <>
      <p className="text-themed-secondary text-center mb-6">
        To identify Steam games from your cache logs, depot mapping data is required. Choose how
        you'd like to initialize this data:
      </p>

      {/* Steam Auth Warning Banner */}
      {usingSteamAuth && (
        <div
          className="mb-6 p-4 rounded-lg border-2"
          style={{
            backgroundColor: 'var(--theme-warning-bg)',
            borderColor: 'var(--theme-warning)',
            color: 'var(--theme-warning-text)'
          }}
        >
          <div className="flex items-start gap-3">
            <AlertTriangle className="w-5 h-5 flex-shrink-0 mt-0.5" />
            <div className="flex-1">
              <p className="text-sm font-semibold mb-1">Steam Account Authenticated</p>
              <p className="text-sm mb-3">
                Since you logged in with your Steam account, the GitHub download option is
                unavailable. Your personalized depot data will be generated using your Steam login,
                which provides access to all games including playtests and restricted content.
              </p>
              {onBackToSteamAuth && (
                <Button
                  size="sm"
                  variant="default"
                  leftSection={<ArrowLeft className="w-3 h-3" />}
                  onClick={onBackToSteamAuth}
                >
                  Change Authentication Method
                </Button>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Download Status Display */}
      {downloadStatus && (
        <div
          className="mb-6 p-4 rounded-lg"
          style={{
            backgroundColor:
              progress === 100 ? 'var(--theme-success-bg)' : 'var(--theme-info-bg)',
            borderColor: progress === 100 ? 'var(--theme-success)' : 'var(--theme-info)',
            color: progress === 100 ? 'var(--theme-success-text)' : 'var(--theme-info-text)'
          }}
        >
          <div className="flex items-center gap-3 mb-3">
            {progress === 100 ? (
              <CheckCircle className="w-5 h-5 flex-shrink-0" />
            ) : (
              <Loader2 className="w-5 h-5 animate-spin flex-shrink-0" />
            )}
            <div className="flex-1">
              <p className="text-sm font-semibold">{downloadStatus}</p>
            </div>
          </div>

          {progress > 0 && progress < 100 && (
            <div>
              <div
                className="w-full rounded-full h-2 overflow-hidden mb-1"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <div
                  className="h-full transition-all duration-300"
                  style={{
                    width: `${progress}%`,
                    backgroundColor: 'var(--theme-primary)'
                  }}
                />
              </div>
              <p className="text-xs text-right">{progress.toFixed(1)}%</p>
            </div>
          )}
        </div>
      )}

      {/* Error Message */}
      {error && (
        <div
          className="mb-6 p-4 rounded-lg"
          style={{
            backgroundColor: 'var(--theme-error-bg)',
            borderColor: 'var(--theme-error)',
            color: 'var(--theme-error-text)'
          }}
        >
          <p className="text-sm">{error}</p>
        </div>
      )}

      {/* Show PICS data status if available */}
      {picsData && (
        <div
          className="mb-6 p-4 rounded-lg"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderColor: 'var(--theme-info)',
            color: 'var(--theme-info-text)'
          }}
        >
          <p className="text-sm">
            <strong>Current PICS Data Status:</strong>
            <br />
            {picsData.jsonFile?.exists && (
              <>
                JSON File: {picsData.jsonFile?.totalMappings?.toLocaleString() ?? 0} mappings
                <br />
              </>
            )}
            Database: {picsData.database?.totalMappings?.toLocaleString() ?? 0} mappings
            <br />
            SteamKit2: {picsData.steamKit2?.depotCount?.toLocaleString() ?? 0} depots
            {picsData.steamKit2?.isReady ? ' (Ready)' : ' (Not Ready)'}
          </p>
        </div>
      )}

      {/* Options Grid */}
      <div
        className={`grid grid-cols-1 gap-4 ${shouldShowContinueOption() ? 'md:grid-cols-3' : 'md:grid-cols-2'}`}
      >
        {/* Cloud Download Option */}
        <div
          className="p-5 rounded-lg border-2 transition-all flex flex-col"
          style={{
            backgroundColor:
              selectedMethod === 'cloud' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
            borderColor:
              selectedMethod === 'cloud' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
            minHeight: '280px'
          }}
        >
          <div className="flex items-center gap-2 mb-3">
            <Cloud size={20} style={{ color: 'var(--theme-info)' }} />
            <h3 className="text-base font-semibold text-themed-primary">Pre-created Data</h3>
          </div>
          <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
            Download community-maintained depot mappings from GitHub.
            {picsData &&
              (picsData.database?.totalMappings > 0 || picsData.steamKit2?.depotCount > 0) && (
                <span className="text-themed-success"> Will update existing data.</span>
              )}
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li>✓ Quick setup (~30 seconds)</li>
            <li>✓ 290,000+ mappings ready</li>
            <li>✓ Regularly updated</li>
            <li>✓ Won't delete existing data</li>
          </ul>
          <Button
            variant="filled"
            color="blue"
            size="sm"
            leftSection={
              initializing && selectedMethod === 'cloud' ? (
                <Loader2 className="w-3 h-3 animate-spin" />
              ) : (
                <Cloud className="w-3 h-3" />
              )
            }
            onClick={handleDownload}
            disabled={initializing || usingSteamAuth}
            fullWidth
            className="mt-auto"
          >
            {usingSteamAuth
              ? 'Unavailable (Steam Login)'
              : initializing && selectedMethod === 'cloud'
                ? 'Downloading...'
                : 'Download Pre-created'}
          </Button>
        </div>

        {/* Generate Own Option */}
        <div
          className="p-5 rounded-lg border-2 transition-all flex flex-col"
          style={{
            backgroundColor:
              selectedMethod === 'generate'
                ? 'var(--theme-primary)/10'
                : 'var(--theme-bg-tertiary)',
            borderColor:
              selectedMethod === 'generate'
                ? 'var(--theme-primary)'
                : 'var(--theme-border-primary)',
            minHeight: '280px'
          }}
        >
          <div className="flex items-center gap-2 mb-3">
            <Database size={20} style={{ color: 'var(--theme-success)' }} />
            <h3 className="text-base font-semibold text-themed-primary">Generate Fresh</h3>
          </div>
          <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
            Build your own depot mappings directly from Steam.
            <span className="text-themed-warning"> Always starts fresh - overwrites existing data.</span>
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li>✓ Latest data from Steam</li>
            <li>✓ Complete fresh rebuild</li>
            <li>✓ Overwrites any existing data</li>
            <li>○ Takes 10-30 minutes for full scan</li>
          </ul>
          <Button
            variant="filled"
            color="green"
            size="sm"
            leftSection={
              initializing && selectedMethod === 'generate' ? (
                <Loader2 className="w-3 h-3 animate-spin" />
              ) : (
                <Database className="w-3 h-3" />
              )
            }
            onClick={handleGenerate}
            disabled={initializing}
            fullWidth
            className="mt-auto"
          >
            {initializing && selectedMethod === 'generate'
              ? 'Processing...'
              : 'Generate Fresh Data'}
          </Button>
        </div>

        {/* Continue Option - Show only when JSON data exists */}
        {shouldShowContinueOption() && (
          <div
            className="p-5 rounded-lg border-2 transition-all flex flex-col"
            style={{
              backgroundColor:
                selectedMethod === 'continue'
                  ? 'var(--theme-primary)/10'
                  : 'var(--theme-bg-tertiary)',
              borderColor:
                selectedMethod === 'continue'
                  ? 'var(--theme-primary)'
                  : 'var(--theme-border-primary)',
              minHeight: '280px'
            }}
          >
            <div className="flex items-center gap-2 mb-3">
              <Database size={20} style={{ color: 'var(--theme-warning)' }} />
              <h3 className="text-base font-semibold text-themed-primary">Continue</h3>
            </div>
            <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
              Update existing depot mappings with latest changes from Steam.
              <span className="text-themed-success"> Incremental update only.</span>
            </p>
            <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
              <li>✓ Fast incremental update (~1-2 minutes)</li>
              <li>✓ Uses existing {picsData?.jsonFile?.totalMappings?.toLocaleString()} mappings</li>
              <li>✓ Fetches only new/changed data</li>
              <li>✓ Perfect for regular updates</li>
            </ul>
            <Button
              variant="filled"
              color="orange"
              size="sm"
              leftSection={
                initializing && selectedMethod === 'continue' ? (
                  <Loader2 className="w-3 h-3 animate-spin" />
                ) : (
                  <Database className="w-3 h-3" />
                )
              }
              onClick={handleContinueUpdate}
              disabled={initializing}
              fullWidth
              className="mt-auto"
            >
              {initializing && selectedMethod === 'continue'
                ? 'Updating...'
                : 'Continue with Update'}
            </Button>
          </div>
        )}
      </div>
    </>
  );
};
