import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
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
  const { t } = useTranslation();
  const { progress } = usePicsProgress();
  const [isComplete, setIsComplete] = useState(false);
  const [isCancelling, setIsCancelling] = useState(false);

  const handleCancel = async () => {
    try {
      setIsCancelling(true);
      await ApiService.cancelSteamKitRebuild();
      onCancel?.();
    } catch (error: unknown) {
      console.error('Failed to cancel PICS rebuild:', error);
      onCancel?.();
    } finally {
      setIsCancelling(false);
    }
  };

  useEffect(() => {
    const isProcessing = progress?.isProcessing || false;
    onProcessingStateChange?.(isProcessing);
  }, [progress?.isProcessing, onProcessingStateChange]);

  const isInitializing = () => {
    if (!progress) return true;
    if (progress.isProcessing && (progress.progressPercent === 0 || !progress.progressPercent)) {
      return true;
    }
    return false;
  };

  const getStatusMessage = () => {
    if (!progress) return t('initialization.picsProgress.initializing');
    if (progress.status === 'Idle' && progress.isProcessing) {
      return t('initialization.picsProgress.connecting');
    }
    return progress.status || t('initialization.picsProgress.processing');
  };

  useEffect(() => {
    const status = progress?.status?.toLowerCase() || '';
    const isFinished = status === 'completed';

    if (progress && !progress.isProcessing && (isFinished || progress.progressPercent >= 100)) {
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
          className={`w-16 h-16 rounded-full flex items-center justify-center mb-4 ${
            isComplete ? 'bg-themed-success' : 'bg-themed-primary-subtle'
          }`}
        >
          {isComplete ? (
            <CheckCircle className="w-8 h-8 icon-success" />
          ) : (
            <Database className="w-8 h-8 animate-pulse icon-primary" />
          )}
        </div>
        <h3 className="text-xl font-semibold text-themed-primary mb-1">
          {isComplete
            ? t('initialization.picsProgress.complete')
            : t('initialization.picsProgress.building')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {isComplete
            ? t('initialization.picsProgress.completeDesc')
            : t('initialization.picsProgress.buildingDesc')}
        </p>
      </div>

      {/* Progress Section */}
      {isComplete ? (
        <div className="p-4 rounded-lg text-center bg-themed-success">
          <p className="text-sm font-medium text-themed-success">
            {progress?.depotMappingsFound
              ? t('initialization.picsProgress.mappingsReady', {
                  count: progress.depotMappingsFound
                })
              : t('initialization.picsProgress.mappingsReadyGeneric')}
          </p>
        </div>
      ) : (
        <div className="space-y-4">
          {/* Status */}
          <div className="p-4 rounded-lg text-center bg-themed-tertiary">
            <p className="text-base font-medium text-themed-primary mb-1">{getStatusMessage()}</p>
            {!isInitializing() &&
              progress?.processedBatches !== undefined &&
              progress?.totalBatches !== undefined &&
              progress.totalBatches > 0 && (
                <p className="text-sm text-themed-secondary">
                  {t('initialization.picsProgress.batches', {
                    processed: progress.processedBatches,
                    total: progress.totalBatches
                  })}
                </p>
              )}
            {!isInitializing() &&
              progress?.depotMappingsFound !== undefined &&
              progress.depotMappingsFound > 0 && (
                <p className="text-xs text-themed-muted mt-1">
                  {t('initialization.picsProgress.mappingsFound', {
                    count: progress.depotMappingsFound
                  })}
                </p>
              )}
          </div>

          {/* Progress Bar */}
          <div>
            <div className="w-full rounded-full h-2.5 overflow-hidden bg-themed-tertiary">
              <div
                className="h-full transition-all duration-300 ease-out rounded-full bg-primary"
                style={{ width: `${progressPercent}%` }}
              />
            </div>
            <p className="text-sm text-themed-secondary text-center mt-2">
              {Math.round(progressPercent)}%
              {isInitializing() && ` - ${t('initialization.picsProgress.preparing')}`}
            </p>
          </div>

          {/* Info */}
          <p className="text-xs text-themed-muted text-center">
            {t('initialization.picsProgress.timeEstimate')}
          </p>
        </div>
      )}

      {/* Action Buttons */}
      <div className="pt-2">
        {isComplete ? (
          <Button variant="filled" color="green" onClick={onComplete} fullWidth>
            {t('initialization.picsProgress.continue')}
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
            {isCancelling
              ? t('initialization.picsProgress.cancelling')
              : t('initialization.picsProgress.cancelUseGithub')}
          </Button>
        ) : null}
      </div>
    </div>
  );
};
