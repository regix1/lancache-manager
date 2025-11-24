import React, { useState, useEffect } from 'react';
import { FileText, Loader2, SkipForward, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { useSignalR } from '@contexts/SignalRContext';
import ApiService from '@services/api.service';

interface LogProcessingStepProps {
  onComplete: () => void;
  onSkip: () => void;
  onProcessingStateChange?: (isProcessing: boolean) => void;
}

interface ProcessingProgress {
  isProcessing: boolean;
  progress: number;
  status: string;
  linesProcessed?: number;
  totalLines?: number;
  entriesProcessed?: number;
  mbProcessed?: number;
  mbTotal?: number;
}

export const LogProcessingStep: React.FC<LogProcessingStepProps> = ({
  onComplete,
  onSkip,
  onProcessingStateChange
}) => {
  const signalR = useSignalR();
  const [processing, setProcessing] = useState(false);
  const [progress, setProgress] = useState<ProcessingProgress | null>(null);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Notify parent when processing state changes (to disable back button)
  useEffect(() => {
    onProcessingStateChange?.(processing);
  }, [processing, onProcessingStateChange]);

  // Check if processing is already active on mount (page reload restoration)
  useEffect(() => {
    const checkActiveProcessing = async () => {
      try {
        const status = await ApiService.getProcessingStatus();
        if (status.isProcessing) {
          console.log('[LogProcessing] Detected active processing on mount, restoring...');
          setProcessing(true);
          setProgress(status);
        }
      } catch (error) {
        console.error('[LogProcessing] Failed to check processing status:', error);
      }
    };

    checkActiveProcessing();
  }, []);

  // Listen to SignalR events for log processing
  useEffect(() => {
    const handleProcessingProgress = (payload: any) => {
      console.log('[LogProcessing] ProcessingProgress received:', payload);

      const currentProgress = payload.percentComplete || payload.progress || 0;
      const status = payload.status || 'processing';

      // Check if complete
      if (status === 'complete' || payload.status === 'complete') {
        console.log('[LogProcessing] Processing completed via SignalR');
        setProgress({
          isProcessing: false,
          progress: 100,
          status: 'complete',
          entriesProcessed: payload.entriesProcessed,
          linesProcessed: payload.linesProcessed || payload.totalLines,
          totalLines: payload.totalLines,
          mbProcessed: payload.mbTotal,
          mbTotal: payload.mbTotal
        });
        setComplete(true);
        setProcessing(false);
        return;
      }

      // Update progress
      setProgress({
        isProcessing: true,
        progress: Math.min(99.9, currentProgress),
        status: status,
        mbProcessed: payload.mbProcessed,
        mbTotal: payload.mbTotal,
        entriesProcessed: payload.entriesProcessed,
        totalLines: payload.totalLines,
        linesProcessed: payload.linesProcessed
      });
    };

    const handleFastProcessingComplete = (payload: any) => {
      console.log('[LogProcessing] FastProcessingComplete received:', payload);
      setProgress({
        isProcessing: false,
        progress: 100,
        status: 'complete',
        entriesProcessed: payload.entriesProcessed,
        linesProcessed: payload.linesProcessed,
        totalLines: payload.linesProcessed
      });
      setComplete(true);
      setProcessing(false);
    };

    // Register SignalR listeners
    signalR.on('ProcessingProgress', handleProcessingProgress);
    signalR.on('FastProcessingComplete', handleFastProcessingComplete);

    // Cleanup
    return () => {
      signalR.off('ProcessingProgress', handleProcessingProgress);
      signalR.off('FastProcessingComplete', handleFastProcessingComplete);
    };
  }, [signalR]);

  const startLogProcessing = async () => {
    setProcessing(true);
    setError(null);
    setComplete(false);

    try {
      // IMPORTANT: During initialization, always start from the beginning of the log
      console.log('[LogProcessing] Resetting log position to beginning (top)...');
      await ApiService.resetLogPosition('top');
      console.log('[LogProcessing] Log position reset complete, starting processing...');

      // Start log processing - SignalR will handle progress updates
      await ApiService.processAllLogs();
    } catch (err: any) {
      setError(err.message || 'Failed to start log processing');
      setProcessing(false);
    }
  };

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
            background: complete
              ? 'linear-gradient(135deg, var(--theme-success) 0%, var(--theme-success-dark, var(--theme-success)) 100%)'
              : processing
                ? 'linear-gradient(135deg, var(--theme-primary) 0%, var(--theme-primary-dark, var(--theme-primary)) 100%)'
                : 'linear-gradient(135deg, var(--theme-info) 0%, var(--theme-info-dark, var(--theme-info)) 100%)'
          }}
        >
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
            <div
              className="p-4 rounded-lg mb-6"
              style={{
                backgroundColor: 'var(--theme-info-bg)',
                color: 'var(--theme-info-text)'
              }}
            >
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
                  <div
                    className="w-full rounded-full h-3 overflow-hidden mb-2"
                    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                  >
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
              <div
                className="grid grid-cols-2 gap-4 p-4 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
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
                {progress.mbProcessed !== undefined && progress.mbTotal !== undefined && (
                  <div>
                    <p className="text-xs text-themed-muted mb-1">Data Processed</p>
                    <p className="text-sm font-semibold text-themed-primary">
                      {progress.mbProcessed.toFixed(1)} / {progress.mbTotal.toFixed(1)} MB
                    </p>
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Success Message */}
          {complete && (
            <div
              className="p-4 rounded-lg mb-6"
              style={{
                backgroundColor: 'var(--theme-success-bg)',
                color: 'var(--theme-success-text)'
              }}
            >
              <p className="text-sm flex items-center justify-center gap-2">
                <CheckCircle className="w-4 h-4" />
                Log processing complete! Click Continue to proceed.
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
