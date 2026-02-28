import React from 'react';
import { AlertTriangle, Download, Scan, Github, Clock, Database, RefreshCw } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { useTranslation } from 'react-i18next';

interface FullScanRequiredModalProps {
  changeGap?: number;
  estimatedApps?: number;
  onConfirm?: () => void;
  onCancel: () => void;
  onDownloadFromGitHub: () => void;
  showDownloadOption?: boolean;
  hasSteamApiKey?: boolean;
  title?: string;
  isDownloading?: boolean;
}

export const FullScanRequiredModal: React.FC<FullScanRequiredModalProps> = ({
  changeGap,
  estimatedApps,
  onConfirm,
  onCancel,
  onDownloadFromGitHub,
  showDownloadOption = true,
  hasSteamApiKey = false,
  title = 'Data Update Required',
  isDownloading = false
}) => {
  const { t } = useTranslation();

  const formatNumber = (num: number) => {
    if (num >= 1000000) return `${(num / 1000000).toFixed(1)}M`;
    if (num >= 1000) return `${(num / 1000).toFixed(0)}K`;
    return num.toLocaleString();
  };

  return (
    <Modal
      opened={true}
      onClose={onCancel}
      title={
        <div className="flex items-center gap-3">
          <div className="full-scan-modal-icon">
            <AlertTriangle className="w-5 h-5" />
          </div>
          <span>{title}</span>
        </div>
      }
      size="md"
    >
      <div className="full-scan-modal-content">
        {/* Stats Display */}
        <div className="full-scan-modal-stats">
          {changeGap && (
            <div className="full-scan-modal-stat">
              <div className="full-scan-modal-stat-icon">
                <RefreshCw className="w-4 h-4" />
              </div>
              <div className="full-scan-modal-stat-content">
                <span className="full-scan-modal-stat-value">{formatNumber(changeGap)}</span>
                <span className="full-scan-modal-stat-label">
                  {t('modals.fullScan.stats.updatesBehind')}
                </span>
              </div>
            </div>
          )}
          <div className="full-scan-modal-stat">
            <div className="full-scan-modal-stat-icon">
              <Database className="w-4 h-4" />
            </div>
            <div className="full-scan-modal-stat-content">
              <span className="full-scan-modal-stat-value">
                {estimatedApps ? `~${formatNumber(estimatedApps)}` : '300K+'}
              </span>
              <span className="full-scan-modal-stat-label">
                {t('modals.fullScan.stats.appsToScan')}
              </span>
            </div>
          </div>
        </div>

        {/* Description */}
        <p className="full-scan-modal-description">{t('modals.fullScan.description')}</p>

        {/* GitHub Option */}
        {showDownloadOption && (
          <div className="full-scan-modal-option full-scan-modal-option-primary">
            <div className="full-scan-modal-option-header">
              <Github className="w-4 h-4" />
              <span>{t('modals.fullScan.github.title')}</span>
              <span className="full-scan-modal-badge">
                {t('modals.fullScan.github.recommended')}
              </span>
            </div>
            <div className="full-scan-modal-option-features">
              <div className="full-scan-modal-feature">
                <Clock className="w-3.5 h-3.5" />
                <span>{t('modals.fullScan.github.duration')}</span>
              </div>
              <div className="full-scan-modal-feature">
                <Database className="w-3.5 h-3.5" />
                <span>{t('modals.fullScan.github.depots')}</span>
              </div>
            </div>
            <Button
              onClick={onDownloadFromGitHub}
              variant="filled"
              color="blue"
              fullWidth
              loading={isDownloading}
              leftSection={!isDownloading ? <Download className="w-4 h-4" /> : undefined}
            >
              {isDownloading
                ? t('modals.fullScan.github.downloading')
                : t('modals.fullScan.github.downloadButton')}
            </Button>
          </div>
        )}

        {/* Full Scan Option - only show if Steam API key available */}
        {hasSteamApiKey && onConfirm && (
          <>
            {showDownloadOption && (
              <div className="full-scan-modal-divider">
                <span>{t('modals.fullScan.or')}</span>
              </div>
            )}
            <div className="full-scan-modal-option full-scan-modal-option-secondary">
              <Button
                onClick={onConfirm}
                variant="outline"
                fullWidth
                leftSection={<Scan className="w-4 h-4" />}
              >
                {t('modals.fullScan.fullScanButton')}
              </Button>
            </div>
          </>
        )}

        {/* Cancel */}
        <Button onClick={onCancel} variant="subtle" fullWidth className="mt-2">
          {t('common.cancel')}
        </Button>
      </div>
    </Modal>
  );
};
