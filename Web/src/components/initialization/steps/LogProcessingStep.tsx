import React, { useState } from 'react';
import { FileText, Loader, SkipForward, CheckCircle } from 'lucide-react';
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

  const startLogProcessing = async () => {
    setProcessing(true);
    setError(null);

    try {
      // Start log processing
      await ApiService.processAllLogs();

      // Poll for progress
      const pollingInterval = setInterval(async () => {
        try {
          const status = await ApiService.getProcessingStatus();
          setProgress(status);

          // Check if complete - must have finished processing AND have data
          // Check for either 100% progress OR explicitly not processing with processed data
          const isFullyComplete =
            (!status.isProcessing && status.linesProcessed && status.linesProcessed > 0 &&
             (status.progress === 100 || status.percentComplete === 100)) ||
            (!status.isProcessing && status.currentPosition && status.totalSize &&
             status.currentPosition >= status.totalSize);

          if (isFullyComplete) {
            setComplete(true);
            clearInterval(pollingInterval);
            // Don't auto-continue - let user click the Continue button
          }
        } catch (err) {
          console.error('Failed to fetch processing status:', err);
        }
      }, 1000);

      // Store interval ID for cleanup
      return () => clearInterval(pollingInterval);
    } catch (err: any) {
      setError(err.message || 'Failed to start log processing');
      setProcessing(false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="text-center">
        <div className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
             style={{ backgroundColor: complete ? 'var(--theme-success)/10' : 'var(--theme-primary)/10' }}>
          {complete ? (
            <CheckCircle size={32} style={{ color: 'var(--theme-success)' }} />
          ) : processing ? (
            <Loader size={32} style={{ color: 'var(--theme-primary)' }} className="animate-spin" />
          ) : (
            <FileText size={32} style={{ color: 'var(--theme-primary)' }} />
          )}
        </div>
        <h2 className="text-2xl font-bold text-themed-primary mb-2">
          {complete ? 'Log Processing Complete!' : 'Process Cache Logs'}
        </h2>
        <p className="text-themed-secondary">
          {complete
            ? 'All cache logs have been processed'
            : 'Would you like to process all cache logs now?'}
        </p>
      </div>

      {!processing && !complete && (
        <div className="p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-info-bg)',
               borderColor: 'var(--theme-info)',
               color: 'var(--theme-info-text)'
             }}>
          <p className="text-sm">
            Processing cache logs will identify all downloads and games in your cache history.
            This can take several minutes depending on the size of your logs.
          </p>
          <p className="text-sm mt-2">
            <strong>You can skip this step and process logs later from the Management tab.</strong>
          </p>
        </div>
      )}

      {/* Progress Display */}
      {processing && progress && !complete && (
        <div className="space-y-4">
          {/* Progress Bar */}
          {progress.progress !== undefined && (
            <div className="w-full bg-themed-border rounded-full h-3 overflow-hidden">
              <div
                className="h-full transition-all duration-500 ease-out"
                style={{
                  width: `${progress.progress}%`,
                  backgroundColor: 'var(--theme-primary)'
                }}
              />
            </div>
          )}

          {/* Status Info */}
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
            {progress.progress !== undefined && (
              <div>
                <p className="text-xs text-themed-muted mb-1">Completion</p>
                <p className="text-sm font-semibold text-themed-primary">
                  {progress.progress.toFixed(1)}%
                </p>
              </div>
            )}
          </div>
        </div>
      )}

      {complete && (
        <div className="p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-success-bg)',
               borderColor: 'var(--theme-success)',
               color: 'var(--theme-success-text)'
             }}>
          <p className="text-sm flex items-center gap-2">
            <CheckCircle className="w-4 h-4" />
            Log processing complete! Click Continue to proceed.
          </p>
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
  );
};
