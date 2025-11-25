import React, { useState, useEffect } from 'react';
import { FileText, Loader2, CheckCircle } from 'lucide-react';
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

  useEffect(() => {
    onProcessingStateChange?.(processing);
  }, [processing, onProcessingStateChange]);

  useEffect(() => {
    const checkActiveProcessing = async () => {
      try {
        const status = await ApiService.getProcessingStatus();
        if (status.isProcessing) {
          setProcessing(true);
          setProgress(status);
        }
      } catch (error) {
        console.error('[LogProcessing] Failed to check processing status:', error);
      }
    };
    checkActiveProcessing();
  }, []);

  // Helper function for flexible status checking (handles variations from SignalR)
  const isCompleteStatus = (status?: string): boolean => {
    if (!status) return false;
    const normalized = status.toLowerCase();
    return normalized === 'complete' || normalized === 'completed' || normalized === 'done' || normalized === 'finished';
  };

  useEffect(() => {
    const handleProcessingProgress = (payload: any) => {
      const currentProgress = payload.percentComplete || payload.progress || 0;
      const status = payload.status || 'processing';

      if (isCompleteStatus(status)) {
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

    signalR.on('ProcessingProgress', handleProcessingProgress);
    signalR.on('FastProcessingComplete', handleFastProcessingComplete);

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
      await ApiService.resetLogPosition('top');
      await ApiService.processAllLogs();
    } catch (err: any) {
      setError(err.message || 'Failed to start log processing');
      setProcessing(false);
    }
  };

  const progressPercent = progress?.progress || 0;

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div
          className="w-16 h-16 rounded-full flex items-center justify-center mb-4"
          style={{
            backgroundColor: complete
              ? 'var(--theme-success-bg)'
              : processing
                ? 'var(--theme-primary-bg, var(--theme-info-bg))'
                : 'var(--theme-info-bg)'
          }}
        >
          {complete ? (
            <CheckCircle className="w-8 h-8" style={{ color: 'var(--theme-success)' }} />
          ) : processing ? (
            <Loader2 className="w-8 h-8 animate-spin" style={{ color: 'var(--theme-primary)' }} />
          ) : (
            <FileText className="w-8 h-8" style={{ color: 'var(--theme-info)' }} />
          )}
        </div>
        <h3 className="text-xl font-semibold text-themed-primary mb-1">
          {complete ? 'Log Processing Complete!' : 'Process Cache Logs'}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {complete
            ? 'All cache logs have been processed'
            : 'Identify downloads and games from your cache history'}
        </p>
      </div>

      {/* Info (when not processing) */}
      {!processing && !complete && (
        <div
          className="p-4 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <p className="text-sm text-themed-secondary mb-2">
            Processing logs identifies all downloads and games in your cache history.
            This can take several minutes depending on log size.
          </p>
          <p className="text-sm text-themed-muted">
            You can skip this and process logs later from the Management tab.
          </p>
        </div>
      )}

      {/* Progress Display */}
      {processing && progress && !complete && (
        <div className="space-y-4">
          {/* Progress Bar */}
          <div>
            <div
              className="w-full rounded-full h-2.5 overflow-hidden"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-full transition-all duration-500 ease-out rounded-full"
                style={{
                  width: `${progressPercent}%`,
                  backgroundColor: 'var(--theme-primary)'
                }}
              />
            </div>
            <p className="text-sm text-themed-secondary text-center mt-2">
              {progressPercent.toFixed(1)}% complete
            </p>
          </div>

          {/* Stats Grid */}
          <div
            className="grid grid-cols-2 gap-3 p-4 rounded-lg"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
          >
            <div>
              <p className="text-xs text-themed-muted">Status</p>
              <p className="text-sm font-medium text-themed-primary">{progress.status || 'Processing...'}</p>
            </div>
            {progress.linesProcessed !== undefined && (
              <div>
                <p className="text-xs text-themed-muted">Lines</p>
                <p className="text-sm font-medium text-themed-primary">{progress.linesProcessed.toLocaleString()}</p>
              </div>
            )}
            {progress.entriesProcessed !== undefined && (
              <div>
                <p className="text-xs text-themed-muted">Entries</p>
                <p className="text-sm font-medium text-themed-primary">{progress.entriesProcessed.toLocaleString()}</p>
              </div>
            )}
            {progress.mbProcessed !== undefined && progress.mbTotal !== undefined && (
              <div>
                <p className="text-xs text-themed-muted">Data</p>
                <p className="text-sm font-medium text-themed-primary">
                  {progress.mbProcessed.toFixed(1)} / {progress.mbTotal.toFixed(1)} MB
                </p>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Success */}
      {complete && (
        <div
          className="p-4 rounded-lg text-center"
          style={{ backgroundColor: 'var(--theme-success-bg)' }}
        >
          <p className="text-sm" style={{ color: 'var(--theme-success-text)' }}>
            Log processing complete! Click Continue to proceed.
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

      {/* Action Buttons */}
      <div className="pt-2">
        {!processing && !complete && (
          <div className="flex gap-3">
            <Button variant="filled" color="blue" onClick={startLogProcessing} className="flex-1">
              Process All Logs
            </Button>
            <Button variant="default" onClick={onSkip} className="flex-1">
              Skip for Now
            </Button>
          </div>
        )}

        {complete && (
          <Button variant="filled" color="green" onClick={onComplete} fullWidth>
            Continue
          </Button>
        )}
      </div>
    </div>
  );
};
