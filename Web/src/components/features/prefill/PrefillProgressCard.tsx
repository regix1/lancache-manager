import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { Download, XCircle, Loader2 } from 'lucide-react';
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
  const { t } = useTranslation();

  const getStateLabel = () => {
    switch (progress.state) {
      case 'reconnecting':
        return t('prefill.progress.reconnecting', 'Reconnecting...');
      case 'loading-metadata':
        return t('prefill.progress.loadingGameData');
      case 'metadata-loaded':
        return t('prefill.progress.preparingDownload');
      case 'starting':
        return t('prefill.progress.starting');
      case 'preparing':
        return t('prefill.progress.preparing');
      case 'app_completed':
        return t('prefill.progress.loadingNextGame');
      case 'already_cached':
        return t('prefill.progress.alreadyCached');
      default:
        return t('prefill.progress.downloading');
    }
  };

  const showAppInfo =
    progress.state === 'downloading' ||
    progress.state === 'app_completed' ||
    progress.state === 'already_cached';
  const isReconnecting = progress.state === 'reconnecting';

  return (
    <Card
      padding="md"
      className="overflow-hidden border-[color-mix(in_srgb,var(--theme-primary)_50%,transparent)]"
    >
      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-primary)_15%,transparent)]">
              {isReconnecting ? (
                <Loader2 className="h-5 w-5 animate-spin text-[var(--theme-primary)]" />
              ) : (
                <Download className="h-5 w-5 animate-pulse text-[var(--theme-primary)]" />
              )}
            </div>
            <div>
              <p className="font-medium text-themed-primary">{getStateLabel()}</p>
              {showAppInfo && (
                <p className="text-sm text-themed-muted truncate max-w-[300px]">
                  {progress.currentAppName ||
                    t('prefill.progress.appId', { id: progress.currentAppId })}
                  {progress.state === 'app_completed' && ` - ${t('prefill.progress.complete')}`}
                  {progress.state === 'already_cached' && ` - ${t('prefill.progress.upToDate')}`}
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
                  {formatTimeRemaining(Math.floor(progress.elapsedSeconds))}{' '}
                  {t('prefill.progress.elapsed')}
                </p>
              </div>
            )}
            <Button variant="outline" size="sm" onClick={onCancel}>
              <XCircle className="h-4 w-4" />
              {t('common.cancel')}
            </Button>
          </div>
        </div>

        {/* Progress Bar */}
        <div className="space-y-2">
          <div className="h-3 rounded-full overflow-hidden bg-[var(--theme-progress-bg)]">
            {isReconnecting ? (
              <div className="h-full rounded-full animate-pulse w-full opacity-50 bg-gradient-to-r from-[var(--theme-warning)] to-[var(--theme-primary)]" />
            ) : progress.state === 'already_cached' ? (
              <div
                key={`cached-${progress.currentAppId}`}
                className="h-full rounded-full bg-[var(--theme-info)] prefill-progress-bar"
                style={{ '--progress-width': `${progress.percentComplete}%` } as React.CSSProperties}
              />
            ) : progress.state === 'downloading' || progress.state === 'app_completed' ? (
              <div
                key={`download-${progress.currentAppId}`}
                className="h-full rounded-full transition-all duration-300 ease-out bg-gradient-to-r from-[var(--theme-primary)] to-[var(--theme-accent)] prefill-progress-bar"
                style={{ '--progress-width': `${Math.min(100, progress.percentComplete)}%` } as React.CSSProperties}
              />
            ) : (
              <div className="h-full rounded-full animate-pulse w-full opacity-50 bg-gradient-to-r from-[var(--theme-primary)] to-[var(--theme-accent)]" />
            )}
          </div>

          {progress.state === 'reconnecting' ? (
            <p className="text-sm text-themed-muted text-center">
              {t(
                'prefill.progress.reconnectingMessage',
                'Prefill in progress. Reconnecting to get current status...'
              )}
            </p>
          ) : progress.state === 'downloading' ? (
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
              <span className="text-[var(--theme-info)]">{t('prefill.progress.gameUpToDate')}</span>
              <span className="font-medium text-[var(--theme-info)]">
                {progress.percentComplete.toFixed(0)}%
              </span>
            </div>
          ) : progress.state === 'app_completed' ? (
            <p className="text-sm text-themed-muted text-center">
              {t('prefill.progress.loadingNextGame')}...
            </p>
          ) : (
            <p className="text-sm text-themed-muted text-center">
              {progress.message || t('prefill.progress.preparingOperation')}
            </p>
          )}
        </div>
      </div>
    </Card>
  );
}
