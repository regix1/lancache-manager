import React, { useState, useEffect } from 'react';
import { Map, Loader, SkipForward, CheckCircle, Home } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';

interface DepotMappingStepProps {
  onComplete: () => void;
}

export const DepotMappingStep: React.FC<DepotMappingStepProps> = ({ onComplete }) => {
  const [mapping, setMapping] = useState(false);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<number>(0);
  const [statusMessage, setStatusMessage] = useState<string>('');

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
    console.log('[DepotMapping] Starting PICS scan and depot mapping...');
    setMapping(true);
    setError(null);
    setProgress(0);
    setStatusMessage('Starting scan...');

    try {
      // Use incremental scan by default for initialization (faster)
      // This will scan PICS data and automatically apply depot mappings when complete
      console.log('[DepotMapping] Triggering incremental PICS crawl...');
      await ApiService.triggerSteamKitRebuild(true);
      console.log('[DepotMapping] PICS crawl started successfully');
    } catch (err: any) {
      console.error('[DepotMapping] Error:', err);
      setError(err.message || 'Failed to start depot scan');
      setMapping(false);
    }
  };

  const handleSkip = () => {
    onComplete();
  };

  return (
    <div className="space-y-6">
      <div className="text-center">
        <div className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
             style={{ backgroundColor: complete ? 'var(--theme-success)/10' : 'var(--theme-primary)/10' }}>
          {complete ? (
            <CheckCircle size={32} style={{ color: 'var(--theme-success)' }} />
          ) : mapping ? (
            <Loader size={32} style={{ color: 'var(--theme-primary)' }} className="animate-spin" />
          ) : (
            <Map size={32} style={{ color: 'var(--theme-primary)' }} />
          )}
        </div>
        <h2 className="text-2xl font-bold text-themed-primary mb-2">
          {complete ? 'Setup Complete!' : 'Map Game Depots'}
        </h2>
        <p className="text-themed-secondary">
          {complete
            ? 'All downloads have been mapped to games'
            : 'Would you like to map depot IDs to game names now?'}
        </p>
      </div>

      {!mapping && !complete && (
        <div className="p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-info-bg)',
               borderColor: 'var(--theme-info)',
               color: 'var(--theme-info-text)'
             }}>
          <p className="text-sm">
            This step will run an incremental PICS scan to update depot mappings, then apply them to your downloads.
            This helps you see game names instead of depot IDs.
          </p>
          <p className="text-sm mt-2">
            <strong>You can skip this step and map depots later from the Management tab.</strong>
          </p>
        </div>
      )}

      {/* Progress Display */}
      {mapping && !complete && (
        <div className="space-y-4">
          {/* Progress Bar */}
          <div className="w-full bg-themed-border rounded-full h-3 overflow-hidden">
            <div
              className="h-full transition-all duration-500 ease-out"
              style={{
                width: `${progress}%`,
                backgroundColor: 'var(--theme-primary)'
              }}
            />
          </div>

          <div className="p-4 rounded-lg text-center"
               style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
            <Loader className="w-8 h-8 animate-spin mx-auto mb-2" style={{ color: 'var(--theme-primary)' }} />
            <p className="text-sm text-themed-primary font-semibold">
              {statusMessage || 'Processing...'}
            </p>
            <p className="text-xs text-themed-muted mt-1">
              {progress.toFixed(0)}% complete
            </p>
          </div>
        </div>
      )}

      {complete && (
        <div className="space-y-4">
          <div className="p-4 rounded-lg"
               style={{
                 backgroundColor: 'var(--theme-success-bg)',
                 borderColor: 'var(--theme-success)',
                 color: 'var(--theme-success-text)'
               }}>
            <p className="text-sm flex items-center gap-2">
              <CheckCircle className="w-4 h-4" />
              Setup complete! Your Lancache Manager is ready to use.
            </p>
          </div>

          <div className="grid grid-cols-1 gap-4 p-6 rounded-lg"
               style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
            <div className="text-center">
              <h3 className="text-lg font-semibold text-themed-primary mb-2">What's Next?</h3>
              <ul className="text-sm text-themed-secondary space-y-2 text-left max-w-md mx-auto">
                <li>✓ PICS depot mappings updated</li>
                <li>✓ Cache logs have been processed</li>
                <li>✓ Downloads are mapped to games</li>
                <li>→ View your dashboard to see cache statistics</li>
                <li>→ Check the Downloads tab to see identified games</li>
              </ul>
            </div>
          </div>
        </div>
      )}

      {error && (
        <div className="p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-error-bg)',
               borderColor: 'var(--theme-error)',
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
  );
};
