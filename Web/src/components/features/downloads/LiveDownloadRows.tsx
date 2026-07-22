import React from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatSpeed } from '@utils/formatters';
import BadgesRow from './BadgesRow';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { Tooltip } from '@components/ui/Tooltip';
import type { LiveDownloadPreview } from './liveDownloadPreviews';

interface LiveDownloadRowsProps {
  previews: LiveDownloadPreview[];
  variant: 'panel' | 'downloads';
}

/**
 * Renders the separate "In progress" region for live traffic that has no recorded row yet.
 * Rows are purely informational: no click actions, no associations, no export, and window
 * bytes are always labeled as window traffic, never presented as a session total.
 */
const LiveDownloadRows: React.FC<LiveDownloadRowsProps> = ({ previews, variant }) => {
  const { t } = useTranslation();

  if (previews.length === 0) {
    return null;
  }

  if (variant === 'panel') {
    return (
      <>
        {previews.map((preview) => (
          <div className="rdl-row rdl-row-active" key={preview.key}>
            <div className="rdl-row-main">
              <div className="rdl-active-indicator">
                <div className="rdl-pulse-ring" />
                <div className="rdl-pulse-dot" />
              </div>
              <div className="rdl-row-info">
                <div className="rdl-row-name">
                  <span className="rdl-name-text">{preview.displayName}</span>
                  <span className="themed-badge status-badge-neutral rdl-live-badge">
                    {t('dashboard.downloadsPanel.inProgress')}
                  </span>
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
  }

  return (
    <div className="dl-live-region">
      {previews.map((preview) => (
        <div className="dl-live-row themed-border-radius-sm" key={preview.key}>
          <div className="rdl-active-indicator">
            <div className="rdl-pulse-ring" />
            <div className="rdl-pulse-dot" />
          </div>
          <div className="dl-live-info">
            <div className="dl-live-name">
              <BadgesRow service={preview.service} showDatasource={false} />
              <span className="dl-live-name-text">{preview.displayName}</span>
              <span className="themed-badge status-badge-neutral dl-live-badge">
                {t('downloads.provisional.inProgress')}
              </span>
            </div>
            {preview.clientIp && (
              <div className="dl-live-meta">
                <ClientIpDisplay clientIp={preview.clientIp} />
              </div>
            )}
          </div>
          <div className="dl-live-figures">
            <span className="dl-live-speed tabular-nums">
              {formatSpeed(preview.bytesPerSecond)}
              <span className="dl-live-speed-tag">{t('downloads.provisional.liveSpeed')}</span>
            </span>
            <Tooltip
              content={t('downloads.provisional.windowTooltip', {
                seconds: preview.windowSeconds
              })}
              className="tabular-nums dl-live-window"
            >
              {t('downloads.provisional.lastSeconds', { seconds: preview.windowSeconds })} ·{' '}
              {formatBytes(preview.windowBytes)}
            </Tooltip>
          </div>
        </div>
      ))}
    </div>
  );
};

export default LiveDownloadRows;
