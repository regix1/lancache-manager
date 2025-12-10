import React from 'react';
import { formatBytes, formatPercent, formatDateTime } from '@utils/formatters';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EAIcon } from '@components/ui/EAIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
import type { Download, DownloadGroup } from '../../../types';

const API_BASE = '/api';

interface RetroViewProps {
  items: (Download | DownloadGroup)[];
}

// Flatten groups into individual downloads for the retro view
const flattenItems = (items: (Download | DownloadGroup)[]): Download[] => {
  const downloads: Download[] = [];

  for (const item of items) {
    if ('downloads' in item) {
      // It's a group - add all its downloads
      downloads.push(...item.downloads);
    } else {
      // It's an individual download
      downloads.push(item);
    }
  }

  // Sort by start time, most recent first
  return downloads.sort(
    (a, b) => new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
  );
};

const getServiceIcon = (service: string, size: number = 24) => {
  const serviceLower = service.toLowerCase();
  const style = { opacity: 0.8 };

  switch (serviceLower) {
    case 'steam':
      return <SteamIcon size={size} style={{ ...style, color: 'var(--theme-steam)' }} />;
    case 'wsus':
    case 'windows':
      return <WsusIcon size={size} style={{ ...style, color: 'var(--theme-wsus)' }} />;
    case 'riot':
    case 'riotgames':
      return <RiotIcon size={size} style={{ ...style, color: 'var(--theme-riot)' }} />;
    case 'epic':
    case 'epicgames':
      return <EpicIcon size={size} style={style} />;
    case 'origin':
    case 'ea':
      return <EAIcon size={size} style={{ ...style, color: 'var(--theme-origin)' }} />;
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return <BlizzardIcon size={size} style={{ ...style, color: 'var(--theme-blizzard)' }} />;
    case 'xbox':
    case 'xboxlive':
      return <XboxIcon size={size} style={{ ...style, color: 'var(--theme-xbox)' }} />;
    default:
      return <UnknownServiceIcon size={size} style={{ ...style, color: 'var(--theme-text-secondary)' }} />;
  }
};

const RetroView: React.FC<RetroViewProps> = ({ items }) => {
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());

  const downloads = React.useMemo(() => flattenItems(items), [items]);

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
  };

  return (
    <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--theme-border-primary)' }}>
      {/* Desktop Table Header - hidden on mobile */}
      <div
        className="hidden lg:grid grid-cols-[minmax(180px,1fr)_minmax(200px,2fr)_100px_120px_minmax(150px,1fr)_minmax(150px,1fr)] gap-2 px-4 py-3 text-xs font-semibold uppercase tracking-wide border-b"
        style={{
          backgroundColor: 'var(--theme-bg-tertiary)',
          borderColor: 'var(--theme-border-secondary)',
          color: 'var(--theme-text-secondary)'
        }}
      >
        <div>Timestamp</div>
        <div>App</div>
        <div>Depot</div>
        <div>Client</div>
        <div>Cache Hit</div>
        <div>Cache Miss</div>
      </div>

      {/* Table Body */}
      <div className="divide-y" style={{ borderColor: 'var(--theme-border-secondary)' }}>
        {downloads.map((download, index) => {
          const serviceLower = download.service.toLowerCase();
          const isSteam = serviceLower === 'steam';
          const hasGameImage = isSteam &&
            download.gameAppId &&
            download.gameName &&
            download.gameName !== 'Unknown Steam Game' &&
            !download.gameName.match(/^Steam App \d+$/) &&
            !imageErrors.has(String(download.gameAppId));

          const totalBytes = download.totalBytes || 0;
          const cacheHitBytes = download.cacheHitBytes || 0;
          const cacheMissBytes = download.cacheMissBytes || 0;
          const hitPercent = totalBytes > 0 ? (cacheHitBytes / totalBytes) * 100 : 0;
          const missPercent = totalBytes > 0 ? (cacheMissBytes / totalBytes) * 100 : 0;

          // Format timestamp range
          const startTime = formatDateTime(download.startTimeUtc);
          const endTime = download.endTimeUtc ? formatDateTime(download.endTimeUtc) : startTime;
          const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;

          return (
            <div
              key={download.id}
              className="transition-colors hover:bg-[var(--theme-bg-tertiary)]/30"
              style={{
                backgroundColor: index % 2 === 0 ? 'var(--theme-bg-secondary)' : 'var(--theme-bg-primary)'
              }}
            >
              {/* Mobile Layout */}
              <div className="lg:hidden p-3 space-y-3">
                {/* App image and name */}
                <div className="flex items-center gap-3">
                  {hasGameImage && download.gameAppId ? (
                    <img
                      src={`${API_BASE}/game-images/${download.gameAppId}/header/`}
                      alt={download.gameName || 'Game'}
                      className="w-[100px] h-[38px] rounded object-cover flex-shrink-0"
                      loading="lazy"
                      onError={() => handleImageError(String(download.gameAppId))}
                    />
                  ) : (
                    <div
                      className="w-[100px] h-[38px] rounded flex items-center justify-center flex-shrink-0"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      {getServiceIcon(download.service, 24)}
                    </div>
                  )}
                  <div className="flex-1 min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {download.gameName || download.service}
                    </div>
                    <div className="text-xs text-[var(--theme-text-muted)]">
                      {download.clientIp}
                      {download.depotId && (
                        <>
                          {' • '}
                          <a
                            href={`https://steamdb.info/depot/${download.depotId}/`}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="text-[var(--theme-primary)] hover:underline"
                          >
                            {download.depotId}
                          </a>
                        </>
                      )}
                    </div>
                  </div>
                </div>

                {/* Timestamp */}
                <div className="text-xs text-[var(--theme-text-secondary)]">
                  {timeRange}
                </div>

                {/* Cache bars */}
                <div className="grid grid-cols-2 gap-3">
                  {/* Cache Hit */}
                  <div className="flex flex-col gap-1">
                    <div className="text-xs text-[var(--theme-text-muted)]">Hit</div>
                    <div
                      className="h-3 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${hitPercent}%`,
                          backgroundColor: hitPercent > 0 ? 'var(--theme-success)' : 'transparent'
                        }}
                      />
                    </div>
                    <div className="text-xs text-[var(--theme-text-secondary)]">
                      {formatBytes(cacheHitBytes)} • {formatPercent(hitPercent)}
                    </div>
                  </div>

                  {/* Cache Miss */}
                  <div className="flex flex-col gap-1">
                    <div className="text-xs text-[var(--theme-text-muted)]">Miss</div>
                    <div
                      className="h-3 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${missPercent}%`,
                          backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                        }}
                      />
                    </div>
                    <div className="text-xs text-[var(--theme-text-secondary)]">
                      {formatBytes(cacheMissBytes)} • {formatPercent(missPercent)}
                    </div>
                  </div>
                </div>
              </div>

              {/* Desktop Layout */}
              <div className="hidden lg:grid grid-cols-[minmax(180px,1fr)_minmax(200px,2fr)_100px_120px_minmax(150px,1fr)_minmax(150px,1fr)] gap-2 px-4 py-3 items-center">
                {/* Timestamp */}
                <div className="text-xs text-[var(--theme-text-secondary)]">
                  {timeRange}
                </div>

                {/* App - with game image */}
                <div className="flex items-center gap-3">
                  {hasGameImage && download.gameAppId ? (
                    <img
                      src={`${API_BASE}/game-images/${download.gameAppId}/header/`}
                      alt={download.gameName || 'Game'}
                      className="w-[120px] h-[45px] rounded object-cover flex-shrink-0"
                      loading="lazy"
                      onError={() => handleImageError(String(download.gameAppId))}
                    />
                  ) : (
                    <div
                      className="w-[120px] h-[45px] rounded flex items-center justify-center flex-shrink-0"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      {getServiceIcon(download.service, 28)}
                    </div>
                  )}
                  <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                    {download.gameName || download.service}
                  </span>
                </div>

                {/* Depot */}
                <div>
                  {download.depotId ? (
                    <a
                      href={`https://steamdb.info/depot/${download.depotId}/`}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="text-sm font-mono text-[var(--theme-primary)] hover:underline"
                    >
                      {download.depotId}
                    </a>
                  ) : (
                    <span className="text-sm text-[var(--theme-text-muted)]">N/A</span>
                  )}
                </div>

                {/* Client IP */}
                <div className="text-sm font-mono text-[var(--theme-text-primary)]">
                  {download.clientIp}
                </div>

                {/* Cache Hit - Progress bar style */}
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <div
                      className="flex-1 h-4 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${hitPercent}%`,
                          backgroundColor: hitPercent > 0 ? 'var(--theme-success)' : 'transparent'
                        }}
                      />
                    </div>
                  </div>
                  <div className="text-xs text-[var(--theme-text-secondary)]">
                    {formatBytes(cacheHitBytes)} • {formatPercent(hitPercent)}
                  </div>
                </div>

                {/* Cache Miss - Progress bar style */}
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <div
                      className="flex-1 h-4 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${missPercent}%`,
                          backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                        }}
                      />
                    </div>
                  </div>
                  <div className="text-xs text-[var(--theme-text-secondary)]">
                    {formatBytes(cacheMissBytes)} • {formatPercent(missPercent)}
                  </div>
                </div>
              </div>
            </div>
          );
        })}
      </div>

      {downloads.length === 0 && (
        <div className="px-4 py-8 text-center text-[var(--theme-text-muted)]">
          No downloads to display
        </div>
      )}
    </div>
  );
};

export default RetroView;
