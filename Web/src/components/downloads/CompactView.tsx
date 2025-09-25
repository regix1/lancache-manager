import React from 'react';
import { ChevronRight, ExternalLink } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import ImageWithFallback from '@components/ui/ImageWithFallback';
import type { Download, DownloadGroup } from '../../types';

const API_BASE = '/api';

const getServiceBadgeStyles = (service: string): { backgroundColor: string; color: string } => {
  const serviceLower = service.toLowerCase();
  switch (serviceLower) {
    case 'steam':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-steam)'
      };
    case 'epic':
    case 'epicgames':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-epic)'
      };
    case 'origin':
    case 'ea':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-origin)'
      };
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-blizzard)'
      };
    case 'wsus':
    case 'windows':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-wsus)'
      };
    case 'riot':
    case 'riotgames':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-riot)'
      };
    default:
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-text-secondary)'
      };
  }
};

interface CompactViewSectionLabels {
  multipleDownloads: string;
  singleDownloads: string;
  individual: string;
  banner: string;
  downloadList: string;
}

const DEFAULT_SECTION_LABELS: CompactViewSectionLabels = {
  multipleDownloads: 'Multiple Downloads',
  singleDownloads: 'Single Downloads',
  individual: 'Individual Downloads',
  banner: 'Game Banner',
  downloadList: 'Download Sessions'
};

interface CompactViewProps {
  items: (Download | DownloadGroup)[];
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  sectionLabels?: CompactViewSectionLabels;
}

const getCacheStatusPill = (download: Download) => {
  const totalBytes = download.totalBytes || 0;
  const cacheBytes = download.cacheHitBytes || 0;

  if (totalBytes === 0) {
    return {
      label: 'Metadata',
      className: 'bg-themed-hover text-themed-muted'
    };
  }

  if (cacheBytes === totalBytes && totalBytes > 0) {
    return {
      label: 'Cached',
      className: 'bg-[var(--theme-success-bg)]/40 text-[var(--theme-success-text)]'
    };
  }

  if (cacheBytes > 0) {
    return {
      label: 'Partial Hit',
      className: 'bg-[var(--theme-warning-bg)]/40 text-[var(--theme-warning-text)]'
    };
  }

  return {
    label: 'Origin Fetch',
    className: 'bg-themed-hover text-themed-muted'
  };
};

const getActivityStatusPill = (download: Download) =>
  download.isActive
    ? {
        label: 'In Progress',
        className: 'bg-[var(--theme-info-bg)]/40 text-[var(--theme-info-text)]'
      }
    : {
        label: 'Completed',
        className: 'bg-themed-hover text-themed-muted'
      };

const CompactView: React.FC<CompactViewProps> = ({
  items,
  expandedItem,
  onItemClick,
  sectionLabels
}) => {
  const labels = { ...DEFAULT_SECTION_LABELS, ...sectionLabels };

  const renderGroupRow = (group: DownloadGroup) => {
    const isExpanded = expandedItem === group.id;
    const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    const primaryDownload = group.downloads[0];
    const showGameImage =
      group.type === 'game' &&
      group.service.toLowerCase() === 'steam' &&
      primaryDownload?.gameAppId &&
      primaryDownload?.gameName &&
      primaryDownload.gameName !== 'Unknown Steam Game' &&
      !primaryDownload.gameName.match(/^Steam App \d+$/);
    const storeLink = showGameImage && primaryDownload?.gameAppId
      ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
      : null;

    return (
      <div
        key={group.id}
        className={`transition-all duration-300 ease-in-out ${
          isExpanded
            ? 'bg-[var(--theme-bg-tertiary)]/10 shadow-sm'
            : 'hover:bg-[var(--theme-bg-tertiary)]/5'
        }`}
        style={{
          animation: 'gentleFadeIn 0.3s ease-out',
        }}
      >
        <button
          type="button"
          onClick={() => onItemClick(group.id)}
          className="w-full text-left px-3 py-2 flex items-center gap-3 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-primary)] transition-all duration-200"
        >
          <ChevronRight
            size={14}
            className={`flex-shrink-0 text-[var(--theme-text-secondary)] transition-transform duration-300 ease-in-out ${
              isExpanded ? 'rotate-90' : ''
            }`}
          />
          <div className="flex items-center gap-2 min-w-0 flex-1">
            <span
              className="px-2 py-0.5 text-xs font-bold rounded"
              style={getServiceBadgeStyles(group.service)}
            >
              {group.service.toUpperCase()}
            </span>
            <span className="text-base font-medium text-[var(--theme-text-primary)] truncate">
              {group.name}
            </span>
            <span className="text-sm text-themed-muted">
              {group.count} download{group.count !== 1 ? 's' : ''}
            </span>
          </div>
          <div className="flex items-center gap-4 flex-shrink-0">
            <span className="text-base font-semibold text-[var(--theme-text-primary)] font-mono text-right min-w-[80px]">
              {formatBytes(group.totalBytes)}
            </span>
            {group.cacheHitBytes > 0 ? (
              <span className="cache-hit font-medium text-sm font-mono text-right min-w-[50px]">
                {formatPercent(hitPercent)}
              </span>
            ) : (
              <span className="font-medium text-sm font-mono text-right min-w-[50px]" style={{ color: 'var(--theme-error-text)' }}>
                0%
              </span>
            )}
            <span className="text-sm text-themed-muted font-mono text-right min-w-[70px]">
              {group.clientsSet.size} clients
            </span>
          </div>
        </button>

        {isExpanded && (
          <div
            className="px-4 pb-3 pt-2 border-t overflow-hidden"
            style={{
              borderColor: 'var(--theme-border-secondary)',
              animation: 'expandDown 0.3s ease-out',
              transformOrigin: 'top'
            }}
            onClick={(event) => event.stopPropagation()}
          >
            <div className="flex flex-col gap-3" style={{ animation: 'slideUp 0.3s ease-out 0.1s both' }}>
              {showGameImage && primaryDownload?.gameAppId && (
                <div className="flex flex-col gap-2">
                  <span className="text-xs font-semibold uppercase tracking-wide text-themed-muted">
                    {labels.banner}
                  </span>
                  <ImageWithFallback
                    src={`${API_BASE}/gameimages/${primaryDownload.gameAppId}/header/`}
                    alt={primaryDownload.gameName || group.name}
                    className="w-full sm:w-[240px] h-[110px] sm:h-[120px] rounded-lg object-cover"
                  />
                </div>
              )}

              <div className="flex flex-wrap items-center gap-3 text-xs text-themed-muted">
                <span>Cache Hit: {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : 'None'}</span>
                <span>Miss: {formatBytes(group.cacheMissBytes || 0)}</span>
                {hitPercent > 0 && <span>Efficiency: {formatPercent(hitPercent)}</span>}
                <span>First: {formatRelativeTime(group.firstSeen)}</span>
                <span>Last: {formatRelativeTime(group.lastSeen)}</span>
                {storeLink && (
                  <a
                    href={storeLink}
                    target="_blank"
                    rel="noopener noreferrer"
                    onClick={(event) => event.stopPropagation()}
                    className="inline-flex items-center gap-1 text-xs text-themed-muted hover:text-themed-accent transition-colors"
                    title="View in Steam Store"
                  >
                    <ExternalLink size={12} />
                    <span>Store</span>
                  </a>
                )}
              </div>

              <div className="space-y-1">
                <div className="text-xs text-themed-muted">
                  {labels.downloadList} ({group.downloads.length > 100
                    ? `Showing 100 of ${group.downloads.length}`
                    : group.downloads.length})
                </div>
                {group.downloads
                  .sort((a, b) => new Date(b.startTime).getTime() - new Date(a.startTime).getTime())
                  .slice(0, 100)
                  .map((download) => {
                    const totalBytes = download.totalBytes || 0;
                    const cachePercent = totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;

                    return (
                      <div
                        key={download.id}
                        className="flex items-center justify-between text-xs py-0.5 hover:bg-[var(--theme-bg-tertiary)]/10 transition-colors"
                      >
                        <div className="flex items-center gap-3">
                          <span className="font-mono text-[var(--theme-text-primary)]">
                            {download.clientIp}
                          </span>
                          <span className="text-themed-muted">
                            {formatRelativeTime(download.startTime)}
                          </span>
                        </div>
                        <div className="flex items-center gap-4">
                          <span className="font-medium text-[var(--theme-text-primary)] font-mono text-right min-w-[70px]">
                            {formatBytes(totalBytes)}
                          </span>
                          {download.cacheHitBytes > 0 ? (
                            <span className="cache-hit font-medium font-mono text-right min-w-[45px]">
                              {formatPercent(cachePercent)}
                            </span>
                          ) : (
                            <span className="font-medium font-mono text-right min-w-[45px]" style={{ color: 'var(--theme-error-text)' }}>0%</span>
                          )}
                        </div>
                      </div>
                    );
                  })}
              </div>
            </div>
          </div>
        )}
      </div>
    );
  };

  const renderDownloadRow = (download: Download) => {
    const totalBytes = download.totalBytes || 0;
    const hitPercent = totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;
    const cacheStatus = getCacheStatusPill(download);
    const activityStatus = getActivityStatusPill(download);
    const storeLink = download.service.toLowerCase() === 'steam' && download.gameAppId
      ? `https://store.steampowered.com/app/${download.gameAppId}`
      : null;

    return (
      <div
        key={`download-${download.id}`}
        className="hover:bg-[var(--theme-bg-tertiary)]/5 transition-all duration-200 ease-in-out"
        style={{ animation: 'gentleFadeIn 0.3s ease-out' }}
      >
        <div className="px-3 py-2 flex items-center gap-3">
          <span
            className="px-2 py-0.5 text-xs font-bold rounded"
            style={getServiceBadgeStyles(download.service)}
          >
            {download.service.toUpperCase()}
          </span>
          <div className="flex items-center gap-2 min-w-0 flex-1">
            <span className="text-base font-medium text-[var(--theme-text-primary)] truncate">
              {download.gameName || 'Unknown Game'}
            </span>
            <span className="text-sm text-themed-muted">
              {download.clientIp}
            </span>
            <span className="text-sm text-themed-muted">
              {formatRelativeTime(download.startTime)}
            </span>
            {storeLink && (
              <a
                href={storeLink}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 text-sm text-themed-muted hover:text-themed-accent transition-colors"
                title="View in Steam Store"
              >
                <ExternalLink size={12} />
              </a>
            )}
          </div>
          <div className="flex items-center gap-4 flex-shrink-0">
            <span className="text-base font-semibold text-[var(--theme-text-primary)] font-mono text-right min-w-[80px]">
              {formatBytes(totalBytes)}
            </span>
            {download.cacheHitBytes > 0 ? (
              <span className="cache-hit font-medium text-sm font-mono text-right min-w-[50px]">
                {formatPercent(hitPercent)}
              </span>
            ) : (
              <span className="font-medium text-sm font-mono text-right min-w-[50px]" style={{ color: 'var(--theme-error-text)' }}>0%</span>
            )}
            <span className={`text-sm min-w-[90px] text-center ${cacheStatus.className}`}>
              {cacheStatus.label}
            </span>
            <span className={`text-sm min-w-[80px] text-center ${activityStatus.className}`}>
              {activityStatus.label}
            </span>
          </div>
        </div>
      </div>
    );
  };

  let multipleDownloadsHeaderRendered = false;
  let singleDownloadsHeaderRendered = false;
  let individualHeaderRendered = false;

  return (
    <div className="space-y-2" style={{ animation: 'gentleFadeIn 0.4s ease-out' }}>
      <div className="px-3 py-2 text-sm font-semibold text-themed-primary"
           style={{ animation: 'slideUp 0.3s ease-out' }}>
        Downloads Overview
      </div>
      <div className="transition-all duration-300">
        {items.map((item) => {
          const isGroup = 'downloads' in item;
          const key = isGroup ? (item as DownloadGroup).id : `download-${(item as Download).id}`;
          let header: React.ReactNode = null;

          if (isGroup) {
            const group = item as DownloadGroup;
            if (group.count > 1 && !multipleDownloadsHeaderRendered) {
              multipleDownloadsHeaderRendered = true;
              header = (
                <div className="px-3 py-1 text-xs font-semibold text-themed-primary uppercase tracking-wide">
                  {labels.multipleDownloads}
                </div>
              );
            } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
              singleDownloadsHeaderRendered = true;
              header = (
                <div className="px-3 py-1 text-xs font-semibold text-themed-primary uppercase tracking-wide">
                  {labels.singleDownloads}
                </div>
              );
            }
          } else if (!isGroup && !individualHeaderRendered) {
            individualHeaderRendered = true;
            header = (
              <div className="px-3 py-1 text-xs font-semibold text-themed-primary uppercase tracking-wide">
                {labels.individual}
              </div>
            );
          }

          return (
            <React.Fragment key={key}>
              {header}
              {isGroup ? renderGroupRow(item as DownloadGroup) : renderDownloadRow(item as Download)}
            </React.Fragment>
          );
        })}
      </div>
    </div>
  );
};

export default CompactView;

