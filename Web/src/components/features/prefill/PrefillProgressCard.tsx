import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { Download, XCircle } from 'lucide-react';
import { formatSpeed } from '@utils/formatters';
import { formatBytes, formatTimeRemaining } from './types';

interface PrefillProgressState {
  state: string;
  message?: string;
  currentAppId: number;
  currentAppName?: string;
  percentComplete: number;
  bytesDownloaded: number;
  totalBytes: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
}

interface PrefillProgressCardProps {
  progress: PrefillProgressState;
  onCancel: () => void;
}

export function PrefillProgressCard({ progress, onCancel }: PrefillProgressCardProps) {
  const getStateLabel = () => {
    switch (progress.state) {
      case 'loading-metadata':
        return 'Loading Game Data';
      case 'metadata-loaded':
        return 'Preparing Download';
      case 'starting':
        return 'Starting';
      case 'preparing':
        return 'Preparing';
      case 'app_completed':
        return 'Loading Next Game';
      case 'already_cached':
        return 'Already Cached';
      default:
        return 'Downloading';
    }
  };

  const showAppInfo = progress.state === 'downloading' || progress.state === 'app_completed' || progress.state === 'already_cached';

  return (
    <Card
      padding="md"
      className="overflow-hidden border-[color-mix(in_srgb,var(--theme-primary)_50%,transparent)]"
    >
      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-primary)_15%,transparent)]">
              <Download className="h-5 w-5 animate-pulse text-[var(--theme-primary)]" />
            </div>
            <div>
              <p className="font-medium text-themed-primary">{getStateLabel()}</p>
              {showAppInfo && (
                <p className="text-sm text-themed-muted truncate max-w-[300px]">
                  {progress.currentAppName || `App ${progress.currentAppId}`}
                  {progress.state === 'app_completed' && ' - Complete'}
                  {progress.state === 'already_cached' && ' - Up to Date'}
                </p>
              )}
            </div>
          </div>

          <div className="flex items-center gap-3">
            {progress.state === 'downloading' && (
              <div className="text-right hidden sm:block">
                <p className="text-sm font-medium text-themed-primary">
                  {formatSpeed(progress.bytesPerSecond)}
                </p>
                <p className="text-xs text-themed-muted">
                  {formatTimeRemaining(Math.floor(progress.elapsedSeconds))} elapsed
                </p>
              </div>
            )}
            <Button variant="outline" size="sm" onClick={onCancel}>
              <XCircle className="h-4 w-4" />
              Cancel
            </Button>
          </div>
        </div>

        {/* Progress Bar */}
        <div className="space-y-2">
          <div className="h-3 rounded-full overflow-hidden bg-[var(--theme-progress-bg)]">
            {progress.state === 'already_cached' ? (
              <div
                key={`cached-${progress.currentAppId}`}
                className="h-full rounded-full bg-[var(--theme-info)]"
                style={{ width: `${progress.percentComplete}%` }}
              />
            ) : progress.state === 'downloading' || progress.state === 'app_completed' ? (
              <div
                key={`download-${progress.currentAppId}`}
                className="h-full rounded-full transition-all duration-300 ease-out bg-gradient-to-r from-[var(--theme-primary)] to-[var(--theme-accent)]"
                style={{ width: `${Math.min(100, progress.percentComplete)}%` }}
              />
            ) : (
              <div className="h-full rounded-full animate-pulse w-full opacity-50 bg-gradient-to-r from-[var(--theme-primary)] to-[var(--theme-accent)]" />
            )}
          </div>

          {progress.state === 'downloading' ? (
            <div className="flex items-center justify-between text-xs text-themed-muted">
              <span>
                {formatBytes(progress.bytesDownloaded)} / {formatBytes(progress.totalBytes)}
              </span>
              <span className="font-medium text-[var(--theme-primary)]">
                {progress.percentComplete.toFixed(1)}%
              </span>
            </div>
          ) : progress.state === 'already_cached' ? (
            <div className="flex items-center justify-between text-xs text-themed-muted">
              <span className="text-[var(--theme-info)]">Game is already up to date in cache</span>
              <span className="font-medium text-[var(--theme-info)]">
                {progress.percentComplete.toFixed(0)}%
              </span>
            </div>
          ) : progress.state === 'app_completed' ? (
            <p className="text-sm text-themed-muted text-center">Loading next game...</p>
          ) : (
            <p className="text-sm text-themed-muted text-center">
              {progress.message || 'Preparing prefill operation...'}
            </p>
          )}
        </div>
      </div>
    </Card>
  );
}
