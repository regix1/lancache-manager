import React from 'react';
import { AlertTriangle, Download, X } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface FullScanRequiredModalProps {
  changeGap: number;
  estimatedApps: number;
  onConfirm: () => void;
  onCancel: () => void;
  onDownloadFromGitHub: () => void;
  showDownloadOption?: boolean;
}

export const FullScanRequiredModal: React.FC<FullScanRequiredModalProps> = ({
  changeGap,
  estimatedApps,
  onConfirm,
  onCancel,
  onDownloadFromGitHub,
  showDownloadOption = true
}) => {

  return (
    <div className="fixed inset-0 flex items-center justify-center z-50" style={{ backgroundColor: 'rgba(0, 0, 0, 0.5)' }}>
      <div className="rounded-lg shadow-xl max-w-2xl w-full mx-4 border-2"
           style={{
             backgroundColor: 'var(--theme-bg-secondary)',
             borderColor: 'var(--theme-error)'
           }}>
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b"
             style={{ borderColor: 'var(--theme-border-primary)' }}>
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg" style={{ backgroundColor: 'var(--theme-error-bg)' }}>
              <AlertTriangle className="w-6 h-6" style={{ color: 'var(--theme-error)' }} />
            </div>
            <h2 className="text-xl font-semibold text-themed-primary">
              Full Scan Required
            </h2>
          </div>
          <button
            onClick={onCancel}
            className="p-1 rounded transition-colors text-themed-secondary hover:bg-themed-hover"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Content */}
        <div className="p-6 space-y-4">
          <div className="rounded-lg p-4 border"
               style={{
                 backgroundColor: 'var(--theme-error-bg)',
                 borderColor: 'var(--theme-error)'
               }}>
            <p className="font-medium mb-2" style={{ color: 'var(--theme-error-text)' }}>
              Steam requires a full scan - incremental update not possible
            </p>
            <div className="space-y-1 text-sm text-themed-secondary">
              <p>• Change gap: <span className="font-mono" style={{ color: 'var(--theme-error-text)' }}>{changeGap.toLocaleString()}</span> updates behind</p>
              <p>• Estimated apps to scan: <span className="font-mono" style={{ color: 'var(--theme-error-text)' }}>~{estimatedApps.toLocaleString()}</span> apps</p>
              <p>• Steam's PICS API will force a <span className="font-bold" style={{ color: 'var(--theme-error-text)' }}>FULL SCAN</span> via Web API</p>
            </div>
          </div>

          <div className="space-y-3">
            <p className="text-themed-primary">
              <strong>Why this happens:</strong> When depot data becomes too outdated (change gap &gt;20,000),
              Steam's PICS API refuses incremental updates and requires a full rescan for data integrity.
            </p>

            {showDownloadOption && (
              <div className="rounded-lg p-4 border"
                   style={{
                     backgroundColor: 'var(--theme-info-bg)',
                     borderColor: 'var(--theme-info)'
                   }}>
                <p className="font-medium mb-2" style={{ color: 'var(--theme-info-text)' }}>Recommended: Download from GitHub</p>
                <ul className="space-y-1 text-sm text-themed-secondary">
                  <li>✓ Instant: Get pre-generated depot mappings in 1-2 minutes</li>
                  <li>✓ Complete: Contains 299,000+ current Steam depot mappings</li>
                  <li>✓ Efficient: Avoids scanning 270,000+ Steam apps (15-30 min)</li>
                </ul>
              </div>
            )}
          </div>
        </div>

        {/* Actions */}
        <div className="flex flex-col sm:flex-row gap-3 p-6 border-t"
             style={{ borderColor: 'var(--theme-border-primary)' }}>
          {showDownloadOption && (
            <Button
              onClick={onDownloadFromGitHub}
              variant="filled"
              color="blue"
              className="flex-1"
            >
              <Download className="w-4 h-4 mr-2" />
              Download from GitHub (Recommended)
            </Button>
          )}

          <Button
            onClick={onConfirm}
            variant="filled"
            color="red"
            className="flex-1"
          >
            Run Full Scan Anyway
          </Button>

          <Button
            onClick={onCancel}
            variant="default"
          >
            Cancel
          </Button>
        </div>
      </div>
    </div>
  );
};
