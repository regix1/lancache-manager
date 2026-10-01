import React from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatSpeed } from '@utils/formatters';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import BadgesRow from './BadgesRow';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import type { LiveDownloadPreview } from './liveDownloadPreviews';

interface LiveDownloadRowsProps {
  previews: LiveDownloadPreview[];
}

/**
 * Renders the separate "In progress" region for live traffic that has no recorded row yet.
 * Rows are purely informational: no click actions, no associations, no export, and window
 * bytes are always labeled as window traffic, never presented as a session total.
 */
const LiveDownloadRows: React.FC<LiveDownloadRowsProps> = ({ previews }) => {
  const { t } = useTranslation();
  // Download dots read the same rendered speed snapshot that owns these preview rows.
  const activity = useActivityStatus();
  const isDownloading = (preview: LiveDownloadPreview): boolean =>
    activity.isActive('download', preview.key, 'downloading');
  // displayName carries the backend's own game name, which is never translated; only the
  // placeholder labels this app supplies for unidentified traffic have a key to render.
  const displayLabel = (preview: LiveDownloadPreview): string =>
    preview.displayNameKey
      ? t(preview.displayNameKey, { depotId: preview.depotId })
      : preview.displayName;

  if (previews.length === 0) {
    return null;
  }

  return (
    <>
      {previews.map((preview) => (
        <div className="rdl-row rdl-row-active" key={preview.key}>
          <div className="rdl-row-main">
            {isDownloading(preview) && (
              <div className="rdl-active-indicator">
                <div className="rdl-pulse-ring" />
                <div className="rdl-pulse-dot" />
              </div>
            )}
            <div className="rdl-row-info">
              <div className="rdl-row-name">
                <span className="rdl-name-text">{displayLabel(preview)}</span>
                <Badge variant="neutral">{t('dashboard.downloadsPanel.inProgress')}</Badge>
              </div>
              <div className="rdl-row-meta">
                <BadgesRow service={preview.service} showDatasource={false} />
                {preview.clientIp && (
                  <>
                    <span className="rdl-meta-sep">•</span>
                    <span>
                      <ClientIpDisplay clientIp={preview.clientIp} />
                    </span>
                  </>
                )}
              </div>
            </div>
          </div>
          <div className="rdl-row-stats">
            <div className="rdl-row-figures">
              <span className="rdl-row-speed tabular-nums">
                {formatSpeed(preview.bytesPerSecond)}
              </span>
              <Tooltip
                content={t('downloads.provisional.windowTooltip', {
                  seconds: preview.windowSeconds
                })}
                className="tabular-nums rdl-window-bytes"
              >
                {t('downloads.provisional.lastSeconds', { seconds: preview.windowSeconds })} ·{' '}
                {formatBytes(preview.windowBytes)}
              </Tooltip>
            </div>
          </div>
        </div>
      ))}
    </>
  );
};

export default LiveDownloadRows;
