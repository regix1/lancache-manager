import React from 'react';
import { AlertTriangle, Github } from 'lucide-react';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { useTranslation } from 'react-i18next';
import { formatCount } from '@utils/formatters';

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
  title,
  isDownloading = false
}) => {
  const { t } = useTranslation();

  const formatNumber = (num: number) => {
    if (num >= 1000000) return `${(num / 1000000).toFixed(1)}M`;
    if (num >= 1000) return `${(num / 1000).toFixed(0)}K`;
    return formatCount(num);
  };

  return (
    <Modal
      opened={true}
      onClose={onCancel}
      title={
        <div className="flex items-center gap-3">
          <div className="icon-box icon-box--sm full-scan-modal-icon">
            <AlertTriangle className="w-5 h-5" />
          </div>
          <span>{title ?? t('app.fullScanRequired.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="full-scan-modal-content">
        <p className="full-scan-modal-description">{t('modals.fullScan.description')}</p>

        {/* Both counts read as one figure pair, so they share a single well and a hairline
            rather than two separate boxes. `changeGap` is checked rather than short-circuited
            so a zero gap renders nothing instead of the digit 0. */}
        <div className="well-surface full-scan-modal-stats">
          {changeGap ? (
            <div className="full-scan-modal-stat">
              <span className="tabular-nums full-scan-modal-stat-value">
                {formatNumber(changeGap)}
              </span>
              <span className="caps-label">{t('modals.fullScan.stats.updatesBehind')}</span>
            </div>
          ) : null}
          <div className="full-scan-modal-stat">
            <span className="tabular-nums full-scan-modal-stat-value">
              {estimatedApps ? `~${formatNumber(estimatedApps)}` : '300K+'}
            </span>
            <span className="caps-label">{t('modals.fullScan.stats.appsToScan')}</span>
          </div>
        </div>

        {showDownloadOption && (
          <div className="full-scan-modal-option full-scan-modal-option-primary">
            <div className="full-scan-modal-option-header">
              <Github className="w-4 h-4" />
              <span>{t('modals.fullScan.github.title')}</span>
              <Badge variant="success" className="full-scan-modal-badge">
                {t('modals.fullScan.github.recommended')}
              </Badge>
            </div>
            <div className="full-scan-modal-option-features">
              <span className="full-scan-modal-feature">
                {t('modals.fullScan.github.duration')}
              </span>
              <span className="full-scan-modal-feature">{t('modals.fullScan.github.depots')}</span>
            </div>
            <Button
              onClick={onDownloadFromGitHub}
              variant="filled"
              color="blue"
              fullWidth
              loading={isDownloading}
              aria-busy={isDownloading}
            >
              {isDownloading
                ? t('modals.fullScan.github.downloading')
                : t('modals.fullScan.github.downloadButton')}
            </Button>
          </div>
        )}

        {/* Only offered when a Steam API key is configured; without one the scan cannot run. */}
        {hasSteamApiKey && onConfirm && (
          <>
            {showDownloadOption && (
              <div className="full-scan-modal-divider">
                <span>{t('modals.fullScan.or')}</span>
              </div>
            )}
            <Button onClick={onConfirm} variant="default" fullWidth disabled={isDownloading}>
              {t('modals.fullScan.fullScanButton')}
            </Button>
          </>
        )}

        <div className="full-scan-modal-footer">
          <Button onClick={onCancel} variant="default">
            {t('common.cancel')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};
