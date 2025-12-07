import React, { useEffect, useState } from 'react';
import { Cloud, Database, Loader2, AlertTriangle, ArrowLeft, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { useSignalR } from '@contexts/SignalRContext';
import type {
  DepotMappingStartedPayload,
  DepotMappingProgressPayload,
  DepotMappingCompletePayload
} from '@contexts/SignalRContext/types';
import ApiService from '@services/api.service';

/** PICS data status from the API */
interface PicsStatus {
  jsonFile?: {
    exists: boolean;
    totalMappings?: number;
  };
  database?: {
    totalMappings?: number;
  };
  steamKit2?: {
    isReady: boolean;
    isRebuildRunning?: boolean;
  };
}

interface DepotInitStepProps {
  picsData: PicsStatus | null;
  usingSteamAuth?: boolean;
  hideOptions?: boolean;
  onDownloadPrecreated: () => void;
  onGenerateOwn: () => void;
  onContinue: () => void;
  onBackToSteamAuth?: () => void;
  onComplete: () => void;
}

export const DepotInitStep: React.FC<DepotInitStepProps> = ({
  picsData,
  usingSteamAuth = false,
  hideOptions = false,
  onDownloadPrecreated: _onDownloadPrecreated,
  onGenerateOwn,
  onContinue,
  onBackToSteamAuth,
  onComplete
}) => {
  const signalR = useSignalR();
  const [initializing, setInitializing] = useState(false);
  const [selectedMethod, setSelectedMethod] = useState<'cloud' | 'generate' | 'continue' | null>(null);
  const [downloadStatus, setDownloadStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState(0);

  useEffect(() => {
    const handleDepotMappingStarted = (payload: DepotMappingStartedPayload) => {
      if (payload.scanMode === 'github') {
        setInitializing(true);
        setSelectedMethod('cloud');
        setDownloadStatus(payload.message || 'Downloading depot mappings from GitHub...');
        setProgress(0);
      }
    };

    const handleDepotMappingProgress = (payload: DepotMappingProgressPayload) => {
      if (selectedMethod === 'cloud') {
        setProgress(payload.percentComplete || 0);
        setDownloadStatus(payload.message || 'Processing depot mappings...');
      }
    };

    const handleDepotMappingComplete = (payload: DepotMappingCompletePayload) => {
      if (payload.scanMode === 'github') {
        if (payload.success) {
          setDownloadStatus('Success! Depot mappings imported.');
          setProgress(100);
          setInitializing(false);
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
      }
    };

    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handleDepotMappingComplete);

    return () => {
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handleDepotMappingComplete);
    };
  }, [signalR, selectedMethod, onComplete]);

  useEffect(() => {
    const checkActiveOperation = async () => {
      try {
        const status = await ApiService.getPicsStatus();
        if (status?.steamKit2?.isRebuildRunning) {
          setInitializing(true);
          setDownloadStatus('Operation in progress...');
        }
      } catch (error) {
        console.error('[DepotInit] Failed to check status:', error);
      }
    };
    checkActiveOperation();
  }, []);

  useEffect(() => {
    if (hideOptions && !initializing && !selectedMethod && !error) {
      handleDownload();
    }
  }, [hideOptions]);

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
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || 'Failed to download pre-created depot data from GitHub');
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
      onGenerateOwn();
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || 'Failed to start depot generation');
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
        setDownloadStatus(`Change gap too large. Starting full scan...`);
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
      onContinue();
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || 'Failed to run incremental update');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
    }
  };

  // GitHub-only mode - auto-download view
  if (hideOptions) {
    return (
      <div className="space-y-5">
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div
            className="w-16 h-16 rounded-full flex items-center justify-center mb-4"
            style={{
              backgroundColor: progress === 100
                ? 'var(--theme-success-bg)'
                : initializing
                  ? 'var(--theme-primary-bg, var(--theme-info-bg))'
                  : 'var(--theme-info-bg)'
            }}
          >
            {progress === 100 ? (
              <CheckCircle className="w-8 h-8" style={{ color: 'var(--theme-success)' }} />
            ) : initializing ? (
              <Loader2 className="w-8 h-8 animate-spin" style={{ color: 'var(--theme-primary)' }} />
            ) : (
              <Cloud className="w-8 h-8" style={{ color: 'var(--theme-info)' }} />
            )}
          </div>
          <h3 className="text-xl font-semibold text-themed-primary mb-1">
            {progress === 100 ? 'Download Complete!' : initializing ? 'Downloading Depot Mappings' : 'Preparing Download'}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {progress === 100
              ? 'GitHub depot data has been imported successfully'
              : 'Fetching pre-created depot mappings from GitHub...'}
          </p>
        </div>

        {/* Progress */}
        {initializing && progress < 100 && (
          <div className="space-y-3">
            <div
              className="p-3 rounded-lg text-center"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <p className="text-sm font-medium text-themed-primary">{downloadStatus || 'Downloading...'}</p>
            </div>
            {progress > 0 && (
              <div>
                <div
                  className="w-full rounded-full h-2.5 overflow-hidden"
                  style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                >
                  <div
                    className="h-full transition-all duration-500 ease-out rounded-full"
                    style={{ width: `${progress}%`, backgroundColor: 'var(--theme-primary)' }}
                  />
                </div>
                <p className="text-sm text-themed-secondary text-center mt-2">{progress.toFixed(1)}%</p>
              </div>
            )}
          </div>
        )}

        {/* Success */}
        {progress === 100 && (
          <div
            className="p-4 rounded-lg text-center"
            style={{ backgroundColor: 'var(--theme-success-bg)' }}
          >
            <p className="text-sm" style={{ color: 'var(--theme-success-text)' }}>
              {downloadStatus || 'Depot mappings imported successfully!'}
            </p>
          </div>
        )}

        {/* Error */}
        {error && (
          <div
            className="p-3 rounded-lg"
            style={{ backgroundColor: 'var(--theme-error-bg)' }}
          >
            <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>{error}</p>
          </div>
        )}
      </div>
    );
  }

  // Regular mode - show choice options
  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div
          className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
          style={{ backgroundColor: 'var(--theme-info-bg)' }}
        >
          <Database className="w-7 h-7" style={{ color: 'var(--theme-info)' }} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">Initialize Depot Data</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          Choose how to obtain depot mapping data
        </p>
      </div>

      {/* Steam Auth Warning */}
      {usingSteamAuth && (
        <div
          className="p-3 rounded-lg flex items-start gap-3"
          style={{ backgroundColor: 'var(--theme-warning-bg)' }}
        >
          <AlertTriangle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-warning)' }} />
          <div className="flex-1">
            <p className="text-sm" style={{ color: 'var(--theme-warning-text)' }}>
              GitHub download unavailable with Steam login. Your personalized depot data will be generated from Steam.
            </p>
            {onBackToSteamAuth && (
              <Button
                size="xs"
                variant="outline"
                onClick={onBackToSteamAuth}
                className="mt-2"
              >
                <ArrowLeft className="w-3 h-3 mr-1" />
                Change Auth Method
              </Button>
            )}
          </div>
        </div>
      )}

      {/* Status Display */}
      {downloadStatus && (
        <div
          className="p-3 rounded-lg"
          style={{
            backgroundColor: progress === 100 ? 'var(--theme-success-bg)' : 'var(--theme-info-bg)'
          }}
        >
          <div className="flex items-center gap-2 mb-2">
            {progress === 100 ? (
              <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success)' }} />
            ) : (
              <Loader2 className="w-4 h-4 animate-spin" style={{ color: 'var(--theme-info)' }} />
            )}
            <p
              className="text-sm font-medium"
              style={{ color: progress === 100 ? 'var(--theme-success-text)' : 'var(--theme-info-text)' }}
            >
              {downloadStatus}
            </p>
          </div>
          {progress > 0 && progress < 100 && (
            <div
              className="w-full rounded-full h-1.5 overflow-hidden"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-full transition-all duration-300"
                style={{ width: `${progress}%`, backgroundColor: 'var(--theme-primary)' }}
              />
            </div>
          )}
        </div>
      )}

      {/* Error */}
      {error && (
        <div
          className="p-3 rounded-lg"
          style={{ backgroundColor: 'var(--theme-error-bg)' }}
        >
          <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>{error}</p>
        </div>
      )}

      {/* PICS Status */}
      {picsData && (
        <div
          className="p-3 rounded-lg text-sm"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <p className="text-themed-secondary">
            <strong className="text-themed-primary">Current Status:</strong>{' '}
            {picsData.jsonFile?.exists && `JSON: ${picsData.jsonFile?.totalMappings?.toLocaleString() ?? 0} mappings. `}
            DB: {picsData.database?.totalMappings?.toLocaleString() ?? 0} mappings.
            {picsData.steamKit2?.isReady ? ' SteamKit ready.' : ''}
          </p>
        </div>
      )}

      {/* Options Grid */}
      <div className={`grid grid-cols-1 gap-3 ${shouldShowContinueOption() ? 'md:grid-cols-3' : 'md:grid-cols-2'}`}>
        {/* Cloud Download */}
        <div
          className="p-4 rounded-lg border-2 flex flex-col"
          style={{
            backgroundColor: selectedMethod === 'cloud' ? 'var(--theme-primary-bg, rgba(var(--theme-primary-rgb), 0.1))' : 'var(--theme-bg-tertiary)',
            borderColor: selectedMethod === 'cloud' ? 'var(--theme-primary)' : 'var(--theme-border-primary)'
          }}
        >
          <div className="flex items-center gap-2 mb-2">
            <Cloud className="w-5 h-5" style={{ color: 'var(--theme-info)' }} />
            <h4 className="font-semibold text-themed-primary">Pre-created</h4>
          </div>
          <p className="text-xs text-themed-secondary mb-3 flex-grow">
            Download from GitHub. Quick setup (~30s).
          </p>
          <Button
            variant="filled"
            color="blue"
            size="sm"
            onClick={handleDownload}
            disabled={initializing || usingSteamAuth}
            fullWidth
          >
            {initializing && selectedMethod === 'cloud' && <Loader2 className="w-3 h-3 animate-spin mr-1" />}
            {usingSteamAuth ? 'Unavailable' : initializing && selectedMethod === 'cloud' ? 'Downloading...' : 'Download'}
          </Button>
        </div>

        {/* Generate Fresh */}
        <div
          className="p-4 rounded-lg border-2 flex flex-col"
          style={{
            backgroundColor: selectedMethod === 'generate' ? 'var(--theme-primary-bg, rgba(var(--theme-primary-rgb), 0.1))' : 'var(--theme-bg-tertiary)',
            borderColor: selectedMethod === 'generate' ? 'var(--theme-primary)' : 'var(--theme-border-primary)'
          }}
        >
          <div className="flex items-center gap-2 mb-2">
            <Database className="w-5 h-5" style={{ color: 'var(--theme-success)' }} />
            <h4 className="font-semibold text-themed-primary">Generate Fresh</h4>
          </div>
          <p className="text-xs text-themed-secondary mb-3 flex-grow">
            Build from Steam. Takes 10-30 minutes.
          </p>
          <Button
            variant="filled"
            color="green"
            size="sm"
            onClick={handleGenerate}
            disabled={initializing}
            fullWidth
          >
            {initializing && selectedMethod === 'generate' && <Loader2 className="w-3 h-3 animate-spin mr-1" />}
            {initializing && selectedMethod === 'generate' ? 'Processing...' : 'Generate'}
          </Button>
        </div>

        {/* Continue Update */}
        {shouldShowContinueOption() && (
          <div
            className="p-4 rounded-lg border-2 flex flex-col"
            style={{
              backgroundColor: selectedMethod === 'continue' ? 'var(--theme-primary-bg, rgba(var(--theme-primary-rgb), 0.1))' : 'var(--theme-bg-tertiary)',
              borderColor: selectedMethod === 'continue' ? 'var(--theme-primary)' : 'var(--theme-border-primary)'
            }}
          >
            <div className="flex items-center gap-2 mb-2">
              <Database className="w-5 h-5" style={{ color: 'var(--theme-warning)' }} />
              <h4 className="font-semibold text-themed-primary">Continue</h4>
            </div>
            <p className="text-xs text-themed-secondary mb-3 flex-grow">
              Incremental update. Fast (~1-2 min).
            </p>
            <Button
              variant="filled"
              color="orange"
              size="sm"
              onClick={handleContinueUpdate}
              disabled={initializing}
              fullWidth
            >
              {initializing && selectedMethod === 'continue' && <Loader2 className="w-3 h-3 animate-spin mr-1" />}
              {initializing && selectedMethod === 'continue' ? 'Updating...' : 'Update'}
            </Button>
          </div>
        )}
      </div>
    </div>
  );
};
