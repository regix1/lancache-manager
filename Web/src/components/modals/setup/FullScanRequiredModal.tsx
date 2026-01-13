import React from 'react';
import { AlertTriangle, Download, Scan, Github, Clock, Database, RefreshCw } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';

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
                <span className="full-scan-modal-stat-label">updates behind</span>
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
              <span className="full-scan-modal-stat-label">apps to scan</span>
            </div>
          </div>
        </div>

        {/* Description */}
        <p className="full-scan-modal-description">
          Your depot data is too outdated for incremental updates. Download the latest mappings from GitHub to continue.
        </p>

        {/* GitHub Option */}
        {showDownloadOption && (
          <div className="full-scan-modal-option full-scan-modal-option-primary">
            <div className="full-scan-modal-option-header">
              <Github className="w-4 h-4" />
              <span>Download from GitHub</span>
              <span className="full-scan-modal-badge">Recommended</span>
            </div>
            <div className="full-scan-modal-option-features">
              <div className="full-scan-modal-feature">
                <Clock className="w-3.5 h-3.5" />
                <span>1-2 minutes</span>
              </div>
              <div className="full-scan-modal-feature">
                <Database className="w-3.5 h-3.5" />
                <span>300K+ depot mappings</span>
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
              {isDownloading ? 'Downloading...' : 'Download from GitHub'}
            </Button>
          </div>
        )}

        {/* Full Scan Option - only show if Steam API key available */}
        {hasSteamApiKey && onConfirm && (
          <>
            {showDownloadOption && (
              <div className="full-scan-modal-divider">
                <span>or</span>
              </div>
            )}
            <div className="full-scan-modal-option full-scan-modal-option-secondary">
              <Button
                onClick={onConfirm}
                variant="outline"
                fullWidth
                leftSection={<Scan className="w-4 h-4" />}
              >
                Full Scan (Slower)
              </Button>
            </div>
          </>
        )}

        {/* Cancel */}
        <Button onClick={onCancel} variant="subtle" fullWidth className="mt-2">
          Cancel
        </Button>
      </div>
    </Modal>
  );
};
