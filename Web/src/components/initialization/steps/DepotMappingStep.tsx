import React, { useState, useEffect } from 'react';
import { Map, Loader2, SkipForward, CheckCircle, Home } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import { FullScanRequiredModal } from '@components/shared/FullScanRequiredModal';

interface DepotMappingStepProps {
  onComplete: () => void;
  onSkip?: () => void;
}

export const DepotMappingStep: React.FC<DepotMappingStepProps> = ({ onComplete, onSkip }) => {
  const [mapping, setMapping] = useState(false);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<number>(0);
  const [statusMessage, setStatusMessage] = useState<string>('');
  const [changeGapWarning, setChangeGapWarning] = useState<{
    show: boolean;
    changeGap: number;
    estimatedApps: number;
  } | null>(null);

  // Log when component mounts and check for active PICS scan
  useEffect(() => {
    console.log('[DepotMapping] Component mounted - Step 5 is now active');

    // Check if PICS scan is already running (page reload restoration)
    const checkActiveScan = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data = await response.json();
          if (data.isRunning) {
            console.log('[DepotMapping] Detected active PICS scan on mount, restoring...');
            setMapping(true);
            setProgress(data.progressPercent || 0);
            setStatusMessage(data.status || 'Processing...');
          }
        }
      } catch (error) {
        console.error('[DepotMapping] Failed to check PICS scan status:', error);
      }
    };

    checkActiveScan();
  }, []);

  // Poll for PICS progress
  useEffect(() => {
    if (!mapping) return;

    const pollProgress = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data = await response.json();

          if (data.isRunning) {
            setProgress(data.progressPercent || 0);
            setStatusMessage(data.status || 'Processing...');
          } else if (data.progressPercent === 100 || !data.isRunning) {
            // PICS crawl completed
            setProgress(100);
            setStatusMessage('Applying depot mappings...');

            // Give it a moment to ensure mappings are applied
            setTimeout(() => {
              setComplete(true);
              setMapping(false);
              console.log('[DepotMapping] Complete!');
            }, 2000);
          }
        }
      } catch (err) {
        console.error('[DepotMapping] Error polling progress:', err);
      }
    };

    const interval = setInterval(pollProgress, 1000);
    return () => clearInterval(interval);
  }, [mapping]);

  const startDepotMapping = async () => {
    console.log('[DepotMapping] Starting depot mapping process...');
    setError(null);
    await proceedWithScan(false);
  };

  const proceedWithScan = async (forceFull: boolean = false) => {
    console.log('[DepotMapping] Starting PICS scan...');
    setMapping(true);
    setProgress(0);
    setStatusMessage('Starting scan...');

    try {
      // Use incremental scan by default for initialization (faster), unless forcing full
      const useIncremental = !forceFull;
      console.log('[DepotMapping] Triggering PICS crawl (incremental:', useIncremental, ')...');
      const response = await ApiService.triggerSteamKitRebuild(useIncremental);
      console.log('[DepotMapping] Backend response:', response);

      // Check if backend says full scan is required (for incremental requests)
      if (response.requiresFullScan) {
        console.log('[DepotMapping] Backend requires full scan - showing modal');
        setChangeGapWarning({
          show: true,
          changeGap: response.changeGap || 25000,
          estimatedApps: response.estimatedApps || 270000
        });
        setMapping(false);
        return;
      }

      console.log('[DepotMapping] PICS crawl started successfully');
    } catch (err: any) {
      console.error('[DepotMapping] Error:', err);
      setError(err.message || 'Failed to start depot scan');
      setMapping(false);
    }
  };

  const handleDownloadFromGitHub = async () => {
    setChangeGapWarning(null);
    setMapping(true);
    setProgress(0);
    setStatusMessage('Downloading from GitHub...');

    try {
      await ApiService.downloadPrecreatedDepotData();
      setProgress(100);
      setStatusMessage('Download complete!');

      setTimeout(() => {
        setComplete(true);
        setMapping(false);
      }, 1000);
    } catch (err: any) {
      console.error('[DepotMapping] Error downloading from GitHub:', err);
      setError(err.message || 'Failed to download from GitHub');
      setMapping(false);
    }
  };

  const handleSkip = () => {
    if (onSkip) {
      onSkip();
    } else {
      onComplete();
    }
  };

  return (
    <div className="space-y-6">
      {/* Modern Card Layout */}
      <div className="rounded-xl overflow-hidden border"
           style={{
             backgroundColor: 'var(--theme-card-bg)',
             borderColor: 'var(--theme-card-border)'
           }}>
        {/* Gradient Banner with Icon */}
        <div className="relative h-32 flex items-center justify-center"
             style={{
               background: complete
                 ? 'linear-gradient(135deg, var(--theme-success) 0%, var(--theme-success-dark, var(--theme-success)) 100%)'
                 : mapping
                   ? 'linear-gradient(135deg, var(--theme-primary) 0%, var(--theme-primary-dark, var(--theme-primary)) 100%)'
                   : 'linear-gradient(135deg, var(--theme-info) 0%, var(--theme-info-dark, var(--theme-info)) 100%)'
             }}>
          {complete ? (
            <CheckCircle size={64} className="text-white" />
          ) : mapping ? (
            <Loader2 size={64} className="text-white animate-spin" />
          ) : (
            <Map size={64} className="text-white" />
          )}
        </div>

        {/* Content Section */}
        <div className="p-8">
          <h2 className="text-2xl font-bold text-themed-primary mb-2 text-center">
            {complete ? 'Setup Complete!' : 'Map Game Depots'}
          </h2>
          <p className="text-themed-secondary text-center mb-6">
            {complete
              ? 'All downloads have been mapped to games'
              : 'Would you like to map depot IDs to game names now?'}
          </p>

          {/* Info Box (when not mapping and not complete) */}
          {!mapping && !complete && (
            <div className="p-4 rounded-lg mb-6"
                 style={{
                   backgroundColor: 'var(--theme-info-bg)',
                   color: 'var(--theme-info-text)'
                 }}>
              <p className="text-sm mb-2">
                This step will run an incremental PICS scan to update depot mappings, then apply them to your downloads.
                This helps you see game names instead of depot IDs.
              </p>
              <p className="text-sm font-semibold">
                You can skip this step and map depots later from the Management tab.
              </p>
            </div>
          )}

          {/* Progress Display */}
          {mapping && !complete && (
            <div className="space-y-4 mb-6">
              <div className="p-6 rounded-lg text-center"
                   style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
                <Loader2 className="w-12 h-12 animate-spin mx-auto mb-3" style={{ color: 'var(--theme-primary)' }} />
                <p className="text-lg font-semibold text-themed-primary mb-1">
                  {statusMessage || 'Processing...'}
                </p>
                <p className="text-sm text-themed-secondary">
                  {progress.toFixed(0)}% complete
                </p>
              </div>
            </div>
          )}

          {/* Completion Summary */}
          {complete && (
            <div className="space-y-4 mb-6">
              <div className="p-4 rounded-lg"
                   style={{
                     backgroundColor: 'var(--theme-success-bg)',
                     color: 'var(--theme-success-text)'
                   }}>
                <p className="text-sm flex items-center justify-center gap-2">
                  <CheckCircle className="w-4 h-4" />
                  Setup complete! Your Lancache Manager is ready to use.
                </p>
              </div>

              <div className="p-6 rounded-lg"
                   style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
                <div className="text-center">
                  <h3 className="text-lg font-semibold text-themed-primary mb-3">What's Next?</h3>
                  <ul className="text-sm text-themed-secondary space-y-2 text-left max-w-md mx-auto">
                    <li className="flex items-start gap-2">
                      <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-success)' }} />
                      <span>PICS depot mappings updated</span>
                    </li>
                    <li className="flex items-start gap-2">
                      <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-success)' }} />
                      <span>Cache logs have been processed</span>
                    </li>
                    <li className="flex items-start gap-2">
                      <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-success)' }} />
                      <span>Downloads are mapped to games</span>
                    </li>
                    <li className="flex items-start gap-2">
                      <Home className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-primary)' }} />
                      <span>View your dashboard to see cache statistics</span>
                    </li>
                    <li className="flex items-start gap-2">
                      <Home className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-primary)' }} />
                      <span>Check the Downloads tab to see identified games</span>
                    </li>
                  </ul>
                </div>
              </div>
            </div>
          )}

          {/* Error Message */}
          {error && (
            <div className="p-4 rounded-lg mb-6"
                 style={{
                   backgroundColor: 'var(--theme-error-bg)',
                   color: 'var(--theme-error-text)'
                 }}>
              <p className="text-sm">{error}</p>
            </div>
          )}

          {/* Action Buttons */}
          {!mapping && !complete && (
            <div className="flex gap-3">
              <Button
                variant="filled"
                color="blue"
                leftSection={<Map className="w-4 h-4" />}
                onClick={startDepotMapping}
                fullWidth
              >
                Scan & Map Depots
              </Button>
              <Button
                variant="default"
                leftSection={<SkipForward className="w-4 h-4" />}
                onClick={handleSkip}
                fullWidth
              >
                Skip for Now
              </Button>
            </div>
          )}

          {complete && (
            <Button
              variant="filled"
              color="green"
              size="lg"
              leftSection={<Home className="w-5 h-5" />}
              onClick={onComplete}
              fullWidth
            >
              Go to Dashboard
            </Button>
          )}
        </div>
      </div>

      {/* Full Scan Required Modal */}
      {changeGapWarning?.show && (
        <FullScanRequiredModal
          changeGap={changeGapWarning.changeGap}
          estimatedApps={changeGapWarning.estimatedApps}
          onConfirm={() => {
            setChangeGapWarning(null);
            proceedWithScan(true); // Force full scan
          }}
          onCancel={() => setChangeGapWarning(null)}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
        />
      )}
    </div>
  );
};
