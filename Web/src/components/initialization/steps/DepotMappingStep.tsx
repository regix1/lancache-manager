import React, { useState, useEffect } from 'react';
import { Map, Loader2, CheckCircle, Home } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import { FullScanRequiredModal } from '@components/shared/FullScanRequiredModal';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';

interface DepotMappingStepProps {
  onComplete: () => void;
  onSkip?: () => void;
}

export const DepotMappingStep: React.FC<DepotMappingStepProps> = ({ onComplete, onSkip }) => {
  const { progress: picsProgress } = usePicsProgress();
  const { status: steamApiStatus } = useSteamWebApiStatus();
  const [mapping, setMapping] = useState(false);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [changeGapWarning, setChangeGapWarning] = useState<{
    show: boolean;
    changeGap: number;
    estimatedApps: number;
  } | null>(null);
  const [phase, setPhase] = useState<'scanning' | 'applying' | null>(null);
  const [applyingMappings, setApplyingMappings] = useState(false);

  const progress = picsProgress?.progressPercent || 0;
  const statusMessage = picsProgress?.status || '';
  const isRunning = picsProgress?.isRunning || false;

  useEffect(() => {
    if (picsProgress?.isRunning && picsProgress.progressPercent < 100) {
      setMapping(true);
      setPhase('scanning');
    }
  }, []);

  useEffect(() => {
    const applyMappingsAfterScan = async () => {
      if (mapping && !isRunning && progress >= 100 && phase === 'scanning' && !applyingMappings) {
        try {
          setPhase('applying');
          setApplyingMappings(true);
          await ApiService.applyDepotMappings();
          setTimeout(() => {
            setComplete(true);
            setMapping(false);
            setApplyingMappings(false);
            setPhase(null);
          }, 1000);
        } catch (err: any) {
          console.error('[DepotMapping] Error applying mappings:', err);
          setError(err.message || 'Failed to apply depot mappings');
          setMapping(false);
          setApplyingMappings(false);
          setPhase(null);
        }
      }
    };
    applyMappingsAfterScan();
  }, [mapping, isRunning, progress, phase, applyingMappings]);

  const startDepotMapping = async () => {
    setError(null);
    await proceedWithScan(false);
  };

  const proceedWithScan = async (forceFull = false) => {
    setMapping(true);
    setPhase('scanning');

    try {
      const useIncremental = !forceFull;
      const response = await ApiService.triggerSteamKitRebuild(useIncremental);

      if (response.requiresFullScan) {
        setChangeGapWarning({
          show: true,
          changeGap: response.changeGap || 25000,
          estimatedApps: response.estimatedApps || 270000
        });
        setMapping(false);
        return;
      }
    } catch (err: any) {
      console.error('[DepotMapping] Error:', err);
      setError(err.message || 'Failed to start depot scan');
      setMapping(false);
      setPhase(null);
    }
  };

  const handleDownloadFromGitHub = async () => {
    setChangeGapWarning(null);
    setMapping(true);

    try {
      await ApiService.downloadPrecreatedDepotData();
      setTimeout(() => {
        setComplete(true);
        setMapping(false);
      }, 2000);
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
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div
          className="w-16 h-16 rounded-full flex items-center justify-center mb-4"
          style={{
            backgroundColor: complete
              ? 'var(--theme-success-bg)'
              : mapping
                ? 'var(--theme-primary-bg, var(--theme-info-bg))'
                : 'var(--theme-info-bg)'
          }}
        >
          {complete ? (
            <CheckCircle className="w-8 h-8" style={{ color: 'var(--theme-success)' }} />
          ) : mapping ? (
            <Loader2 className="w-8 h-8 animate-spin" style={{ color: 'var(--theme-primary)' }} />
          ) : (
            <Map className="w-8 h-8" style={{ color: 'var(--theme-info)' }} />
          )}
        </div>
        <h3 className="text-xl font-semibold text-themed-primary mb-1">
          {complete ? 'Setup Complete!' : 'Map Game Depots'}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {complete
            ? 'All downloads have been mapped to games'
            : 'Map depot IDs to game names for your downloads'}
        </p>
      </div>

      {/* Content */}
      {!mapping && !complete && (
        <div
          className="p-4 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <p className="text-sm text-themed-secondary mb-2">
            This step runs an incremental PICS scan to update depot mappings, then applies them to your downloads.
          </p>
          <p className="text-sm text-themed-muted">
            You can skip this and map depots later from the Management tab.
          </p>
        </div>
      )}

      {/* Progress Display */}
      {mapping && !complete && (
        <div
          className="p-4 rounded-lg text-center"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          {phase === 'scanning' ? (
            <>
              <p className="text-base font-medium text-themed-primary mb-1">
                {statusMessage || 'Scanning Steam for depot mappings...'}
              </p>
              <p className="text-sm text-themed-secondary">{progress.toFixed(0)}% complete</p>
            </>
          ) : phase === 'applying' ? (
            <>
              <p className="text-base font-medium text-themed-primary mb-1">
                Applying depot mappings...
              </p>
              <p className="text-sm text-themed-secondary">This may take a moment</p>
            </>
          ) : (
            <p className="text-base font-medium text-themed-primary">Processing...</p>
          )}
        </div>
      )}

      {/* Complete Summary */}
      {complete && (
        <div className="space-y-4">
          <div
            className="p-4 rounded-lg"
            style={{ backgroundColor: 'var(--theme-success-bg)' }}
          >
            <p className="text-sm text-center" style={{ color: 'var(--theme-success-text)' }}>
              Setup complete! Your Lancache Manager is ready to use.
            </p>
          </div>

          <div
            className="p-4 rounded-lg"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
          >
            <h4 className="text-sm font-semibold text-themed-primary mb-3 text-center">What's Next?</h4>
            <ul className="text-sm text-themed-secondary space-y-2">
              <li className="flex items-start gap-2">
                <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-success)' }} />
                <span>PICS depot mappings updated</span>
              </li>
              <li className="flex items-start gap-2">
                <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-success)' }} />
                <span>Cache logs processed</span>
              </li>
              <li className="flex items-start gap-2">
                <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-success)' }} />
                <span>Downloads mapped to games</span>
              </li>
              <li className="flex items-start gap-2">
                <Home className="w-4 h-4 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-primary)' }} />
                <span>View your dashboard for cache statistics</span>
              </li>
            </ul>
          </div>
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

      {/* Action Buttons */}
      <div className="pt-2">
        {!mapping && !complete && (
          <div className="flex gap-3">
            <Button variant="filled" color="blue" onClick={startDepotMapping} className="flex-1">
              Scan & Map Depots
            </Button>
            <Button variant="default" onClick={handleSkip} className="flex-1">
              Skip for Now
            </Button>
          </div>
        )}

        {complete && (
          <Button variant="filled" color="green" size="lg" onClick={onComplete} fullWidth>
            Go to Dashboard
          </Button>
        )}
      </div>

      {/* Full Scan Required Modal */}
      {changeGapWarning?.show && (
        <FullScanRequiredModal
          changeGap={changeGapWarning.changeGap}
          estimatedApps={changeGapWarning.estimatedApps}
          onConfirm={() => {
            setChangeGapWarning(null);
            proceedWithScan(true);
          }}
          onCancel={() => setChangeGapWarning(null)}
          onDownloadFromGitHub={handleDownloadFromGitHub}
          showDownloadOption={true}
          hasSteamApiKey={steamApiStatus?.hasApiKey ?? false}
        />
      )}
    </div>
  );
};
