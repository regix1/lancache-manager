import React, { useState } from 'react';
import { FileText, Loader2, SkipForward, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';

interface LogProcessingStepProps {
  onComplete: () => void;
  onSkip: () => void;
}

export const LogProcessingStep: React.FC<LogProcessingStepProps> = ({ onComplete, onSkip }) => {
  const [processing, setProcessing] = useState(false);
  const [progress, setProgress] = useState<any>(null);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Check if processing is already active on mount (page reload restoration)
  React.useEffect(() => {
    const checkActiveProcessing = async () => {
      try {
        const status = await ApiService.getProcessingStatus();
        if (status.isProcessing) {
          console.log('[LogProcessing] Detected active processing on mount, restoring...');
          setProcessing(true);
          setProgress(status);
          startPolling();
        }
      } catch (error) {
        console.error('[LogProcessing] Failed to check processing status:', error);
      }
    };

    checkActiveProcessing();
  }, []);

  const startPolling = () => {
    const pollingInterval = setInterval(async () => {
      try {
        const status = await ApiService.getProcessingStatus();
        setProgress(status);

        // Check if complete - must have finished processing
        // Handle both empty log files (0 lines = 0% progress) and populated logs (100% progress)
        const isFullyComplete =
          !status.isProcessing && (
            // Normal completion: progress reached 100%
            (status.progress === 100 || status.percentComplete === 100) ||
            // Empty file completion: 0 total lines and 0 processed (progress will be 0%)
            (status.totalLines === 0 && status.linesProcessed === 0) ||
            // Alternative: position-based completion
            (status.currentPosition !== undefined && status.totalSize !== undefined &&
             status.currentPosition >= status.totalSize) ||
            // Rust processor explicitly marked as complete (handles invalid/empty logs)
            (status.status === 'complete')
          );

        if (isFullyComplete) {
          setComplete(true);
          clearInterval(pollingInterval);
          // Don't auto-continue - let user click the Continue button
        }
      } catch (err) {
        console.error('Failed to fetch processing status:', err);
      }
    }, 1000);

    return () => clearInterval(pollingInterval);
  };

  const startLogProcessing = async () => {
    setProcessing(true);
    setError(null);

    try {
      // Start log processing
      await ApiService.processAllLogs();

      // Start polling for progress
      startPolling();
    } catch (err: any) {
      setError(err.message || 'Failed to start log processing');
      setProcessing(false);
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
                 : processing
                   ? 'linear-gradient(135deg, var(--theme-primary) 0%, var(--theme-primary-dark, var(--theme-primary)) 100%)'
                   : 'linear-gradient(135deg, var(--theme-info) 0%, var(--theme-info-dark, var(--theme-info)) 100%)'
             }}>
          {complete ? (
            <CheckCircle size={64} className="text-white" />
          ) : processing ? (
            <Loader2 size={64} className="text-white animate-spin" />
          ) : (
            <FileText size={64} className="text-white" />
          )}
        </div>

        {/* Content Section */}
        <div className="p-8">
          <h2 className="text-2xl font-bold text-themed-primary mb-2 text-center">
            {complete ? 'Log Processing Complete!' : 'Process Cache Logs'}
          </h2>
          <p className="text-themed-secondary text-center mb-6">
            {complete
              ? 'All cache logs have been processed'
              : 'Would you like to process all cache logs now?'}
          </p>

          {/* Info Box (when not processing and not complete) */}
          {!processing && !complete && (
            <div className="p-4 rounded-lg mb-6"
                 style={{
                   backgroundColor: 'var(--theme-info-bg)',
                   color: 'var(--theme-info-text)'
                 }}>
              <p className="text-sm mb-2">
                Processing cache logs will identify all downloads and games in your cache history.
                This can take several minutes depending on the size of your logs.
              </p>
              <p className="text-sm font-semibold">
                You can skip this step and process logs later from the Management tab.
              </p>
            </div>
          )}

          {/* Progress Display */}
          {processing && progress && !complete && (
            <div className="space-y-4 mb-6">
              {/* Progress Bar */}
              {progress.progress !== undefined && (
                <div>
                  <div className="w-full rounded-full h-3 overflow-hidden mb-2"
                       style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
                    <div
                      className="h-full transition-all duration-500 ease-out"
                      style={{
                        width: `${progress.progress}%`,
                        backgroundColor: 'var(--theme-primary)'
                      }}
                    />
                  </div>
                  <p className="text-sm text-themed-secondary text-center">
                    {progress.progress.toFixed(1)}% complete
                  </p>
                </div>
              )}

              {/* Status Info Grid */}
              <div className="grid grid-cols-2 gap-4 p-4 rounded-lg"
                   style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
                <div>
                  <p className="text-xs text-themed-muted mb-1">Status</p>
                  <p className="text-sm font-semibold text-themed-primary">
                    {progress.status || 'Processing...'}
                  </p>
                </div>
                {progress.linesProcessed !== undefined && (
                  <div>
                    <p className="text-xs text-themed-muted mb-1">Lines Processed</p>
                    <p className="text-sm font-semibold text-themed-primary">
                      {progress.linesProcessed.toLocaleString()}
                    </p>
                  </div>
                )}
                {progress.entriesProcessed !== undefined && (
                  <div>
                    <p className="text-xs text-themed-muted mb-1">Entries Processed</p>
                    <p className="text-sm font-semibold text-themed-primary">
                      {progress.entriesProcessed.toLocaleString()}
                    </p>
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Success Message */}
          {complete && (
            <div className="p-4 rounded-lg mb-6"
                 style={{
                   backgroundColor: 'var(--theme-success-bg)',
                   color: 'var(--theme-success-text)'
                 }}>
              <p className="text-sm flex items-center justify-center gap-2">
                <CheckCircle className="w-4 h-4" />
                Log processing complete! Click Continue to proceed.
              </p>
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
          {!processing && !complete && (
            <div className="flex gap-3">
              <Button
                variant="filled"
                color="blue"
                leftSection={<FileText className="w-4 h-4" />}
                onClick={startLogProcessing}
                fullWidth
              >
                Process All Logs
              </Button>
              <Button
                variant="default"
                leftSection={<SkipForward className="w-4 h-4" />}
                onClick={onSkip}
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
              leftSection={<CheckCircle className="w-4 h-4" />}
              onClick={onComplete}
              fullWidth
            >
              Continue
            </Button>
          )}
        </div>
      </div>
    </div>
  );
};
