import React from 'react';
import { AlertTriangle, Download, Scan } from 'lucide-react';
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
  subtitle?: string;
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
  subtitle
}) => {
  const defaultSubtitle = 'Change gap too large - please download latest data from GitHub';

  return (
    <Modal
      opened={true}
      onClose={onCancel}
      title={
        <div className="flex items-center gap-3">
          <div className="p-2 rounded-lg" style={{ backgroundColor: 'var(--theme-error-bg)' }}>
            <AlertTriangle className="w-6 h-6" style={{ color: 'var(--theme-error)' }} />
          </div>
          <span>{title}</span>
        </div>
      }
      size="lg"
    >
      <div className="space-y-4">
        <div
          className="rounded-lg p-4 border"
          style={{
            backgroundColor: 'var(--theme-error-bg)',
            borderColor: 'var(--theme-error)'
          }}
        >
          <p className="font-medium mb-2" style={{ color: 'var(--theme-error-text)' }}>
            {subtitle || defaultSubtitle}
          </p>
          <div className="space-y-1 text-sm text-themed-secondary">
            {changeGap && (
              <p>
                • Change gap:{' '}
                <span className="font-mono" style={{ color: 'var(--theme-error-text)' }}>
                  {changeGap.toLocaleString()}
                </span>{' '}
                updates behind
              </p>
            )}
            <p>
              •{' '}
              {estimatedApps ? (
                <>
                  Estimated apps to scan:{' '}
                  <span className="font-mono" style={{ color: 'var(--theme-error-text)' }}>
                    ~{estimatedApps.toLocaleString()}
                  </span>{' '}
                  apps
                </>
              ) : (
                <>
                  Will need to scan{' '}
                  <span className="font-bold" style={{ color: 'var(--theme-error-text)' }}>
                    ALL
                  </span>{' '}
                  Steam apps (currently 300,000+)
                </>
              )}
            </p>
            <p>
              • Steam's PICS API{' '}
              <span className="font-bold" style={{ color: 'var(--theme-error-text)' }}>
                cannot process
              </span>{' '}
              incremental updates with this large gap
            </p>
          </div>
        </div>

        <div className="space-y-3">
          <p className="text-themed-primary">
            <strong>Why this happens:</strong> When depot data becomes too outdated (change gap
            &gt;20,000), Steam's PICS API refuses incremental updates. Full scans are no longer
            supported - use GitHub downloads to reset your baseline.
          </p>

          {showDownloadOption && (
            <div
              className="rounded-lg p-4 border"
              style={{
                backgroundColor: 'var(--theme-info-bg)',
                borderColor: 'var(--theme-info)'
              }}
            >
              <p className="font-medium mb-2" style={{ color: 'var(--theme-info-text)' }}>
                Solution: Download from GitHub
              </p>
              <ul className="space-y-1 text-sm text-themed-secondary">
                <li>✓ Fast: Get pre-generated depot mappings in 1-2 minutes</li>
                <li>✓ Complete: Contains 300,000+ current Steam depot mappings</li>
                <li>✓ Up-to-date: Updated daily from Steam's PICS data</li>
                <li>✓ Resets baseline: Incremental scans will work again after download</li>
              </ul>
            </div>
          )}
        </div>

        <div className="flex flex-col sm:flex-row gap-3 pt-2">
          {showDownloadOption && (
            <Button onClick={onDownloadFromGitHub} variant="filled" color="blue" className="flex-1">
              <Download className="w-4 h-4 mr-2" />
              Download from GitHub
            </Button>
          )}

          {hasSteamApiKey && onConfirm && (
            <>
              {showDownloadOption && (
                <div className="flex items-center gap-2 sm:mx-2">
                  <div className="flex-1 h-px bg-themed-border"></div>
                  <span className="text-xs text-themed-muted">OR</span>
                  <div className="flex-1 h-px bg-themed-border"></div>
                </div>
              )}
              <Button onClick={onConfirm} variant="filled" color="orange" className="flex-1">
                <Scan className="w-4 h-4 mr-2" />
                Full Scan (Slow)
              </Button>
            </>
          )}

          <Button onClick={onCancel} variant="default">
            Cancel
          </Button>
        </div>
      </div>
    </Modal>
  );
};
