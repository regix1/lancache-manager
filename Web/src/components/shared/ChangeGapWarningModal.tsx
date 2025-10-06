import React from 'react';
import { AlertTriangle, Download, X } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface ChangeGapWarningModalProps {
  changeGap: number;
  estimatedApps: number;
  onConfirm: () => void;
  onCancel: () => void;
  onDownloadFromGitHub: () => void;
  showDownloadOption?: boolean;
}

export const ChangeGapWarningModal: React.FC<ChangeGapWarningModalProps> = ({
  changeGap,
  estimatedApps,
  onConfirm,
  onCancel,
  onDownloadFromGitHub,
  showDownloadOption = true
}) => {

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-[var(--theme-bg-secondary)] rounded-lg shadow-xl max-w-2xl w-full mx-4 border-2 border-red-500">
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b border-[var(--theme-border)]">
          <div className="flex items-center gap-3">
            <div className="p-2 bg-red-500/10 rounded-lg">
              <AlertTriangle className="w-6 h-6 text-red-500" />
            </div>
            <h2 className="text-xl font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
              Large Change Gap Detected
            </h2>
          </div>
          <button
            onClick={onCancel}
            className="p-1 hover:bg-[var(--theme-bg-tertiary)] rounded transition-colors"
            style={{ color: 'var(--theme-text-secondary)' }}
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Content */}
        <div className="p-6 space-y-4">
          <div className="bg-red-500/10 border border-red-500/30 rounded-lg p-4">
            <p className="text-red-400 font-medium mb-2">
              Your depot data is too far behind Steam's current state
            </p>
            <div className="space-y-1 text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
              <p>• Change gap: <span className="font-mono text-red-400">{changeGap.toLocaleString()}</span> updates behind</p>
              <p>• Estimated apps to scan: <span className="font-mono text-red-400">~{estimatedApps.toLocaleString()}</span> apps</p>
              <p>• Steam's PICS API will force a <span className="font-bold text-red-400">FULL SCAN</span> instead of incremental</p>
            </div>
          </div>

          <div className="space-y-3">
            <p style={{ color: 'var(--theme-text-primary)' }}>
              <strong>Why this happens:</strong> When depot data becomes too outdated (typically &gt;24 hours),
              Steam's API refuses incremental updates and requires a full rescan for data integrity.
            </p>

            {showDownloadOption && (
              <div className="bg-blue-500/10 border border-blue-500/30 rounded-lg p-4">
                <p className="text-blue-400 font-medium mb-2">Recommended: Download from GitHub</p>
                <ul className="space-y-1 text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  <li>✓ Instant: Get pre-generated depot mappings in seconds</li>
                  <li>✓ Complete: Contains all current Steam depot data</li>
                  <li>✓ Efficient: Avoids scanning 270,000+ Steam apps</li>
                </ul>
              </div>
            )}
          </div>
        </div>

        {/* Actions */}
        <div className="flex gap-3 p-6 border-t border-[var(--theme-border)]">
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
