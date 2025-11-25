import React, { useState, useEffect } from 'react';
import { Database, CheckCircle, Loader2 } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import ApiService from '@services/api.service';

interface PicsProgressStepProps {
  onComplete: () => void;
  onProcessingStateChange?: (isProcessing: boolean) => void;
  onCancel?: () => void;
}

export const PicsProgressStep: React.FC<PicsProgressStepProps> = ({
  onComplete,
  onProcessingStateChange,
  onCancel
}) => {
  const { progress } = usePicsProgress();
  const [isComplete, setIsComplete] = useState(false);
  const [isCancelling, setIsCancelling] = useState(false);

  const handleCancel = async () => {
    try {
      setIsCancelling(true);
      await ApiService.cancelSteamKitRebuild();
      onCancel?.();
    } catch (error: any) {
      console.error('Failed to cancel PICS rebuild:', error);
      onCancel?.();
    } finally {
      setIsCancelling(false);
    }
  };

  useEffect(() => {
    const isProcessing = progress?.isRunning || false;
    onProcessingStateChange?.(isProcessing);
  }, [progress?.isRunning, onProcessingStateChange]);

  const isInitializing = () => {
    if (!progress) return true;
    if (progress.isRunning && (progress.progressPercent === 0 || !progress.progressPercent)) {
      return true;
    }
    return false;
  };

  const getStatusMessage = () => {
    if (!progress) return 'Initializing...';
    if (progress.status === 'Idle' && progress.isRunning) {
      return 'Connecting to Steam...';
    }
    return progress.status || 'Processing...';
  };

  useEffect(() => {
    // Check for various completion status values
    const status = progress?.status?.toLowerCase() || '';
    const isFinished = status === 'complete' || status === 'completed' || status === 'done';

    if (progress && !progress.isRunning && (isFinished || progress.progressPercent >= 100)) {
      if (!isComplete) {
        setIsComplete(true);
        // Auto-advance after showing success
        setTimeout(() => {
          onComplete();
        }, 1500);
      }
    }
  }, [progress, isComplete, onComplete]);

  const progressPercent = Math.min(100, Math.max(0, progress?.progressPercent || 0));

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div
          className="w-16 h-16 rounded-full flex items-center justify-center mb-4"
          style={{
            backgroundColor: isComplete ? 'var(--theme-success-bg)' : 'var(--theme-primary-bg, var(--theme-info-bg))'
          }}
        >
          {isComplete ? (
            <CheckCircle className="w-8 h-8" style={{ color: 'var(--theme-success)' }} />
          ) : (
            <Database className="w-8 h-8 animate-pulse" style={{ color: 'var(--theme-primary)' }} />
          )}
        </div>
        <h3 className="text-xl font-semibold text-themed-primary mb-1">
          {isComplete ? 'PICS Data Ready!' : 'Building Steam Depot Mappings'}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {isComplete
            ? 'Depot mappings successfully created'
            : 'Fetching and processing depot information from Steam...'}
        </p>
      </div>

      {/* Progress Section */}
      {isComplete ? (
        <div
          className="p-4 rounded-lg text-center"
          style={{ backgroundColor: 'var(--theme-success-bg)' }}
        >
          <p className="text-sm font-medium" style={{ color: 'var(--theme-success-text)' }}>
            {progress?.depotMappingsFound
              ? `${progress.depotMappingsFound.toLocaleString()} depot mappings ready`
              : 'Depot mappings are ready'}
          </p>
        </div>
      ) : (
        <div className="space-y-4">
          {/* Status */}
          <div
            className="p-4 rounded-lg text-center"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
          >
            <p className="text-base font-medium text-themed-primary mb-1">
              {getStatusMessage()}
            </p>
            {!isInitializing() && progress?.processedBatches !== undefined && progress?.totalBatches !== undefined && progress.totalBatches > 0 && (
              <p className="text-sm text-themed-secondary">
                {progress.processedBatches.toLocaleString()} / {progress.totalBatches.toLocaleString()} batches
              </p>
            )}
            {!isInitializing() && progress?.depotMappingsFound !== undefined && progress.depotMappingsFound > 0 && (
              <p className="text-xs text-themed-muted mt-1">
                {progress.depotMappingsFound.toLocaleString()} depot mappings found
              </p>
            )}
          </div>

          {/* Progress Bar */}
          <div>
            <div
              className="w-full rounded-full h-2.5 overflow-hidden"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-full transition-all duration-300 ease-out rounded-full"
                style={{
                  width: `${progressPercent}%`,
                  backgroundColor: 'var(--theme-primary)'
                }}
              />
            </div>
            <p className="text-sm text-themed-secondary text-center mt-2">
              {Math.round(progressPercent)}%
              {isInitializing() && ' - Preparing...'}
            </p>
          </div>

          {/* Info */}
          <p className="text-xs text-themed-muted text-center">
            This typically takes 1-5 minutes depending on your connection.
          </p>
        </div>
      )}

      {/* Action Buttons */}
      <div className="pt-2">
        {isComplete ? (
          <Button variant="filled" color="green" onClick={onComplete} fullWidth>
            Continue
          </Button>
        ) : onCancel ? (
          <Button
            variant="outline"
            color="red"
            onClick={handleCancel}
            disabled={isCancelling}
            fullWidth
          >
            {isCancelling && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
            {isCancelling ? 'Cancelling...' : 'Cancel and Use GitHub Instead'}
          </Button>
        ) : null}
      </div>
    </div>
  );
};
