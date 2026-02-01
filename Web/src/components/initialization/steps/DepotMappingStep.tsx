import React, { useState, useEffect } from 'react';
import { Map, Loader2, CheckCircle, Home } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import { FullScanRequiredModal } from '@components/modals/setup/FullScanRequiredModal';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';

interface DepotMappingStepProps {
  onComplete: () => void;
  onSkip?: () => void;
}

export const DepotMappingStep: React.FC<DepotMappingStepProps> = ({ onComplete, onSkip }) => {
  const { t } = useTranslation();
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
  const isRunning = picsProgress?.isProcessing || false;

  useEffect(() => {
    if (picsProgress?.isProcessing && picsProgress.progressPercent < 100) {
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
        } catch (err: unknown) {
          console.error('[DepotMapping] Error applying mappings:', err);
          setError((err instanceof Error ? err.message : String(err)) || t('initialization.depotMapping.failedToApply'));
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
    } catch (err: unknown) {
      console.error('[DepotMapping] Error:', err);
      setError((err instanceof Error ? err.message : String(err)) || t('initialization.depotMapping.failedToStart'));
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
    } catch (err: unknown) {
      // Don't show error for user-initiated cancellation
      if (!isAbortError(err)) {
        console.error('[DepotMapping] Error downloading from GitHub:', err);
        setError((err instanceof Error ? err.message : String(err)) || t('initialization.depotMapping.failedToDownload'));
      }
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
          className={`w-16 h-16 rounded-full flex items-center justify-center mb-4 ${
            complete
              ? 'bg-themed-success'
              : mapping
                ? 'bg-themed-primary-subtle'
                : 'bg-themed-info'
          }`}
        >
          {complete ? (
            <CheckCircle className="w-8 h-8 icon-success" />
          ) : mapping ? (
            <Loader2 className="w-8 h-8 animate-spin icon-primary" />
          ) : (
            <Map className="w-8 h-8 icon-info" />
          )}
        </div>
        <h3 className="text-xl font-semibold text-themed-primary mb-1">
          {complete ? t('initialization.depotMapping.titleComplete') : t('initialization.depotMapping.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {complete
            ? t('initialization.depotMapping.subtitleComplete')
            : t('initialization.depotMapping.subtitle')}
        </p>
      </div>

      {/* Content */}
      {!mapping && !complete && (
        <div className="p-4 rounded-lg bg-themed-tertiary">
          <p className="text-sm text-themed-secondary mb-2">
            {t('initialization.depotMapping.description')}
          </p>
          <p className="text-sm text-themed-muted">
            {t('initialization.depotMapping.canSkip')}
          </p>
        </div>
      )}

      {/* Progress Display */}
      {mapping && !complete && (
        <div className="p-4 rounded-lg text-center bg-themed-tertiary">
          {phase === 'scanning' ? (
            <>
              <p className="text-base font-medium text-themed-primary mb-1">
                {statusMessage || t('initialization.depotMapping.scanning')}
              </p>
              <p className="text-sm text-themed-secondary">{t('initialization.depotMapping.percentComplete', { percent: progress.toFixed(0) })}</p>
            </>
          ) : phase === 'applying' ? (
            <>
              <p className="text-base font-medium text-themed-primary mb-1">
                {t('initialization.depotMapping.applyingMappings')}
              </p>
              <p className="text-sm text-themed-secondary">{t('initialization.depotMapping.applyingNote')}</p>
            </>
          ) : (
            <p className="text-base font-medium text-themed-primary">{t('initialization.depotMapping.processing')}</p>
          )}
        </div>
      )}

      {/* Complete Summary */}
      {complete && (
        <div className="space-y-4">
          <div className="p-4 rounded-lg bg-themed-success">
            <p className="text-sm text-center text-themed-success">
              {t('initialization.depotMapping.setupComplete')}
            </p>
          </div>

          <div className="p-4 rounded-lg bg-themed-tertiary">
            <h4 className="text-sm font-semibold text-themed-primary mb-3 text-center">{t('initialization.depotMapping.whatsNext')}</h4>
            <ul className="text-sm text-themed-secondary space-y-2">
              <li className="flex items-start gap-2">
                <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5 icon-success" />
                <span>{t('initialization.depotMapping.picsUpdated')}</span>
              </li>
              <li className="flex items-start gap-2">
                <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5 icon-success" />
                <span>{t('initialization.depotMapping.logsProcessed')}</span>
              </li>
              <li className="flex items-start gap-2">
                <CheckCircle className="w-4 h-4 flex-shrink-0 mt-0.5 icon-success" />
                <span>{t('initialization.depotMapping.downloadsMapped')}</span>
              </li>
              <li className="flex items-start gap-2">
                <Home className="w-4 h-4 flex-shrink-0 mt-0.5 icon-primary" />
                <span>{t('initialization.depotMapping.viewDashboard')}</span>
              </li>
            </ul>
          </div>
        </div>
      )}

      {/* Error */}
      {error && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{error}</p>
        </div>
      )}

      {/* Action Buttons */}
      <div className="pt-2">
        {!mapping && !complete && (
          <div className="flex gap-3">
            <Button variant="filled" color="blue" onClick={startDepotMapping} className="flex-1">
              {t('initialization.depotMapping.scanAndMap')}
            </Button>
            <Button variant="default" onClick={handleSkip} className="flex-1">
              {t('initialization.depotMapping.skipForNow')}
            </Button>
          </div>
        )}

        {complete && (
          <Button variant="filled" color="green" size="lg" onClick={onComplete} fullWidth>
            {t('initialization.depotMapping.goToDashboard')}
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
