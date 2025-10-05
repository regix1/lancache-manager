import React, { useState, useEffect } from 'react';
import { Database, Loader, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface PicsProgress {
  isRunning: boolean;
  status: string;
  currentStatus?: string;
  progress?: number;
  appsProcessed?: number;
  totalApps?: number;
  depotsFound?: number;
}

interface PicsProgressStepProps {
  onComplete: () => void;
}

export const PicsProgressStep: React.FC<PicsProgressStepProps> = ({ onComplete }) => {
  const [progress, setProgress] = useState<PicsProgress | null>(null);
  const [isComplete, setIsComplete] = useState(false);

  useEffect(() => {
    let pollingInterval: NodeJS.Timeout | null = null;

    const checkProgress = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data: PicsProgress = await response.json();
          setProgress(data);

          // Check if PICS crawl is complete
          if (!data.isRunning && data.status === 'Complete') {
            setIsComplete(true);
            if (pollingInterval) {
              clearInterval(pollingInterval);
            }
            // Don't auto-continue - let user click the Continue button
          }
        }
      } catch (error) {
        console.error('Failed to fetch PICS progress:', error);
      }
    };

    // Start polling
    checkProgress();
    pollingInterval = setInterval(checkProgress, 1000); // Poll every second

    return () => {
      if (pollingInterval) {
        clearInterval(pollingInterval);
      }
    };
  }, [onComplete]);

  return (
    <div className="space-y-6">
      <div className="text-center">
        <div className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
             style={{ backgroundColor: isComplete ? 'var(--theme-success)/10' : 'var(--theme-primary)/10' }}>
          {isComplete ? (
            <CheckCircle size={32} style={{ color: 'var(--theme-success)' }} />
          ) : (
            <Database size={32} style={{ color: 'var(--theme-primary)' }} className="animate-pulse" />
          )}
        </div>
        <h2 className="text-2xl font-bold text-themed-primary mb-2">
          {isComplete ? 'PICS Data Ready!' : 'Building Steam Depot Mappings'}
        </h2>
        <p className="text-themed-secondary">
          {isComplete
            ? 'Depot mappings successfully created'
            : 'Fetching and processing depot information from Steam...'}
        </p>
      </div>

      {/* Status Display */}
      <div className="space-y-4">
        {/* Status Box */}
        <div className="p-6 rounded-lg text-center"
             style={{
               backgroundColor: isComplete ? 'var(--theme-success-bg)' : 'var(--theme-bg-tertiary)',
               borderColor: isComplete ? 'var(--theme-success)' : 'var(--theme-border)',
               color: isComplete ? 'var(--theme-success-text)' : 'var(--theme-primary)'
             }}>
          {isComplete ? (
            <>
              <CheckCircle className="w-12 h-12 mx-auto mb-3" style={{ color: 'var(--theme-success)' }} />
              <p className="text-lg font-semibold mb-2">Setup Complete!</p>
              <p className="text-sm opacity-90">
                {progress?.depotsFound ? `${progress.depotsFound.toLocaleString()} depot mappings ready` : 'Depot mappings are ready'}
              </p>
            </>
          ) : (
            <>
              <Loader className="w-12 h-12 mx-auto mb-3 animate-spin" style={{ color: 'var(--theme-primary)' }} />
              <p className="text-lg font-semibold mb-2">
                {progress?.currentStatus || progress?.status || 'Processing...'}
              </p>
              {progress?.appsProcessed !== undefined && progress?.totalApps !== undefined && (
                <p className="text-sm opacity-75">
                  {progress.appsProcessed.toLocaleString()} / {progress.totalApps.toLocaleString()} apps processed
                </p>
              )}
            </>
          )}
        </div>

        {/* Info Text */}
        {!isComplete && (
          <div className="p-4 rounded-lg"
               style={{
                 backgroundColor: 'var(--theme-info-bg)',
                 borderColor: 'var(--theme-info)',
                 color: 'var(--theme-info-text)'
               }}>
            <p className="text-sm text-center">
              Building depot mappings from Steam. This typically takes 1-5 minutes depending on your connection.
            </p>
          </div>
        )}
      </div>

      {/* Manual Continue Button (only show if complete and not auto-continuing) */}
      {isComplete && (
        <Button
          variant="filled"
          color="green"
          leftSection={<CheckCircle className="w-4 h-4" />}
          onClick={onComplete}
          fullWidth
        >
          Continue
        </Button>
      )}
    </div>
  );
};
