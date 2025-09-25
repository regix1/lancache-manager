import React from 'react';
import { ChevronRight, Clock, Users, ExternalLink, CheckCircle, AlertCircle } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import ImageWithFallback from '@components/ui/ImageWithFallback';

// API base URL
const API_BASE = '/api';

// Service badge styling helper
const getServiceBadgeClasses = (service: string): string => {
  const serviceLower = service.toLowerCase();
  switch (serviceLower) {
    case 'steam':
      return 'bg-[#1e40af]/20 text-[#1e40af]';
    case 'epic':
    case 'epicgames':
      return 'bg-[#7c3aed]/20 text-[#7c3aed]';
    case 'origin':
    case 'ea':
      return 'bg-[#ea580c]/20 text-[#ea580c]';
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return 'bg-[#0891b2]/20 text-[#0891b2]';
    case 'wsus':
    case 'windows':
      return 'bg-[#16a34a]/20 text-[#16a34a]';
    case 'riot':
    case 'riotgames':
      return 'bg-[#dc2626]/20 text-[#dc2626]';
    default:
      return 'bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]';
  }
};
import type { Download, DownloadGroup } from '../../types';

interface NormalViewProps {
  items: (Download | DownloadGroup)[];
  expandedItem: string | null;
  onItemClick: (id: string) => void;
}

const NormalView: React.FC<NormalViewProps> = ({ items, expandedItem, onItemClick }) => {
  const renderDownloadCard = (download: Download) => {
    const hitPercent = download.totalBytes > 0 ? ((download.cacheHitBytes || 0) / download.totalBytes) * 100 : 0;
    const downloadStatus = download.totalBytes === 0
      ? 'Metadata'
      : download.cacheHitBytes === download.totalBytes
        ? 'Cached'
        : download.cacheHitBytes > 0
          ? 'Partial Cache Hit'
          : 'New Download';
    const storeLink = download.service.toLowerCase() === 'steam' && download.gameAppId
      ? `https://store.steampowered.com/app/${download.gameAppId}`
      : null;
    const showGameImage = download.service.toLowerCase() === 'steam' &&
                          download.gameName &&
                          download.gameName !== 'Unknown Steam Game' &&
                          !download.gameName.match(/^Steam App \d+$/);

    const statusBadgeClasses = (() => {
      switch (downloadStatus) {
        case 'Cached':
          return 'bg-[var(--theme-success-bg)]/40 text-[var(--theme-success-text)]';
        case 'Partial Cache Hit':
          return 'bg-[var(--theme-warning-bg)]/40 text-[var(--theme-warning-text)]';
        case 'New Download':
          return 'bg-[var(--theme-info-bg)]/40 text-[var(--theme-info-text)]';
        default:
          return 'bg-themed-hover text-themed-muted';
      }
    })();

    return (
      <div
        key={download.id}
        className="rounded-xl border border-[var(--theme-border-primary)] bg-themed-secondary transition-all duration-300 hover:shadow-xl hover:-translate-y-0.5"
      >
        <div className="p-4 md:p-5 flex flex-col gap-4">
          <div className={`flex flex-col lg:flex-row ${showGameImage ? 'gap-4 lg:gap-6' : 'gap-4'}`}>
            {showGameImage && download.gameAppId && (
              <div className="flex-shrink-0">
                <ImageWithFallback
                  src={`${API_BASE}/gameimages/${download.gameAppId}/header/`}
                  alt={download.gameName || 'Game'}
                  className="w-full lg:w-[260px] h-[140px] lg:h-[150px] rounded-lg object-cover"
                />
              </div>
            )}
            <div className="flex-1 flex flex-col gap-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className={`px-2.5 py-1 text-xs font-bold rounded-lg shadow-sm ${getServiceBadgeClasses(download.service)}`}>
                    {download.service.toUpperCase()}
                  </span>
                  <h3 className="text-base font-semibold text-themed-primary">
                    {download.gameName || 'Unknown Game'}
                  </h3>
                  <span className={`px-2 py-0.5 text-xs rounded-full ${statusBadgeClasses}`}>
                    {downloadStatus}
                  </span>
                  <span className={`px-2 py-0.5 text-xs rounded-full ${download.isActive ? 'bg-[var(--theme-info-bg)]/40 text-[var(--theme-info-text)]' : 'bg-themed-hover text-themed-muted'}`}>
                    {download.isActive ? 'In Progress' : 'Completed'}
                  </span>
                  {storeLink && (
                    <a
                      href={storeLink}
                      target="_blank"
                      rel="noopener noreferrer"
                      onClick={(e) => e.stopPropagation()}
                      className="flex items-center gap-1 text-xs text-themed-muted hover:text-themed-accent transition-colors"
                      title="View in Steam Store"
                    >
                      <ExternalLink size={14} />
                      <span>Store Page</span>
                    </a>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-3 text-xs text-themed-muted whitespace-nowrap">
                  <div className="flex items-center gap-1">
                    <Users size={12} />
                    <span>{download.clientIp}</span>
                  </div>
                  <div className="flex items-center gap-1">
                    <Clock size={12} />
                    <span>{formatRelativeTime(download.startTime)}</span>
                  </div>
                </div>
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-3 gap-3 text-xs text-themed-muted">
                <div className="flex flex-col gap-1">
                  <span>Total Size</span>
                  <span className="text-sm font-semibold text-themed-primary">
                    {formatBytes(download.totalBytes || 0)}
                  </span>
                </div>
                <div className="flex flex-col gap-1">
                  <span>Cache Hit Bytes</span>
                  <span className="text-sm font-semibold text-themed-primary">
                    {download.cacheHitBytes > 0 ? formatBytes(download.cacheHitBytes) : 'No cache hits yet'}
                  </span>
                </div>
                <div className="flex flex-col gap-1">
                  <span>Cache Miss Bytes</span>
                  <span className="text-sm font-semibold text-themed-primary">
                    {formatBytes(download.cacheMissBytes || 0)}
                  </span>
                </div>
              </div>

              <div className="flex flex-col gap-2">
                <div className="flex items-center justify-between text-xs text-themed-muted">
                  <span>Cache efficiency</span>
                  <span className="font-semibold text-themed-primary">{formatPercent(hitPercent)}</span>
                </div>
                <div className="h-2 rounded-full bg-[var(--theme-progress-bg)] overflow-hidden">
                  <div
                    className="h-full bg-[var(--theme-progress-bar)] transition-all duration-500"
                    style={{ width: `${Math.min(hitPercent, 100)}%` }}
                  />
                </div>
                <div className="text-[11px] text-themed-muted">
                  {download.cacheHitBytes > 0
                    ? `Served ${formatBytes(download.cacheHitBytes)} from cache`
                    : 'This download has not hit the cache yet'}
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    );
  };

  const renderGroupCard = (group: DownloadGroup) => {
    const isExpanded = expandedItem === group.id;
    const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    const showGameImage = group.type === 'game' &&
                         group.service.toLowerCase() === 'steam' &&
                         group.downloads[0]?.gameAppId;
    const primaryDownload = group.downloads[0];
    const storeLink = showGameImage && primaryDownload?.gameAppId
      ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
      : null;
    const groupTypeLabel = group.type === 'game' ? 'Game group' : group.type === 'metadata' ? 'Metadata group' : 'Content group';

    return (
      <div
        key={group.id}
        className="rounded-xl border border-[var(--theme-border-primary)] bg-themed-secondary transition-all duration-300 hover:shadow-xl hover:-translate-y-0.5 cursor-pointer"
        onClick={() => onItemClick(group.id)}
      >
        <div className="p-4 md:p-5 flex flex-col gap-4">
          <div className={`flex flex-col lg:flex-row ${showGameImage ? 'gap-4 lg:gap-6' : 'gap-4'}`}>
            {showGameImage && primaryDownload?.gameAppId && (
              <div className="flex-shrink-0">
                <ImageWithFallback
                  src={`${API_BASE}/gameimages/${primaryDownload.gameAppId}/header/`}
                  alt={group.name}
                  className="w-full lg:w-[260px] h-[140px] lg:h-[150px] rounded-lg object-cover"
                />
              </div>
            )}
            <div className="flex-1 flex flex-col gap-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <ChevronRight
                    size={18}
                    className={`transition-transform text-themed-secondary ${isExpanded ? 'rotate-90' : ''}`}
                  />
                  <span className={`px-2.5 py-1 text-xs font-bold rounded-lg shadow-sm ${getServiceBadgeClasses(group.service)}`}>
                    {group.service.toUpperCase()}
                  </span>
                  <h3 className="text-base font-semibold text-themed-primary">
                    {group.name}
                  </h3>
                  <span className="px-2 py-0.5 text-xs rounded-full bg-themed-hover text-themed-muted">
                    {groupTypeLabel}
                  </span>
                  <span className="px-2 py-0.5 text-xs rounded-full bg-themed-accent text-themed-button font-semibold">
                    {group.count} downloads
                  </span>
                  {storeLink && (
                    <a
                      href={storeLink}
                      target="_blank"
                      rel="noopener noreferrer"
                      onClick={(e) => e.stopPropagation()}
                      className="flex items-center gap-1 text-xs text-themed-muted hover:text-themed-accent transition-colors"
                      title="View in Steam Store"
                    >
                      <ExternalLink size={14} />
                      <span>Store Page</span>
                    </a>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-3 text-xs text-themed-muted whitespace-nowrap">
                  <div className="flex items-center gap-1">
                    <Users size={12} />
                    <span>{group.clientsSet.size} clients</span>
                  </div>
                  <div className="flex items-center gap-1">
                    <Clock size={12} />
                    <span>Last seen {formatRelativeTime(group.lastSeen)}</span>
                  </div>
                </div>
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-4 gap-3 text-xs text-themed-muted">
                <div className="flex flex-col gap-1">
                  <span>Total Size</span>
                  <span className="text-sm font-semibold text-themed-primary">{formatBytes(group.totalBytes)}</span>
                </div>
                <div className="flex flex-col gap-1">
                  <span>Cache Hit Bytes</span>
                  <span className="text-sm font-semibold text-themed-primary">{group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : 'No cache hits yet'}</span>
                </div>
                <div className="flex flex-col gap-1">
                  <span>Cache Miss Bytes</span>
                  <span className="text-sm font-semibold text-themed-primary">{formatBytes(group.cacheMissBytes || 0)}</span>
                </div>
                <div className="flex flex-col gap-1">
                  <span>First Seen</span>
                  <span className="text-sm font-semibold text-themed-primary">{formatRelativeTime(group.firstSeen)}</span>
                </div>
              </div>

              <div className="flex flex-col gap-2">
                <div className="flex items-center justify-between text-xs text-themed-muted">
                  <span>Average cache efficiency</span>
                  <span className="font-semibold text-themed-primary">{formatPercent(hitPercent)}</span>
                </div>
                <div className="h-2 rounded-full bg-[var(--theme-progress-bg)] overflow-hidden">
                  <div
                    className="h-full bg-[var(--theme-progress-bar)] transition-all duration-500"
                    style={{ width: `${Math.min(hitPercent, 100)}%` }}
                  />
                </div>
                <div className="flex flex-wrap gap-4 text-xs text-themed-muted">
                  <div className="flex items-center gap-1">
                    <CheckCircle size={12} className="text-[var(--theme-success-text)]" />
                    <span>{group.cacheHitBytes > 0 ? `${formatBytes(group.cacheHitBytes)} served from cache` : 'No cache hits yet'}</span>
                  </div>
                  <div className="flex items-center gap-1">
                    <AlertCircle size={12} className="text-[var(--theme-text-secondary)]" />
                    <span>{formatBytes(group.cacheMissBytes || 0)} fetched from origin</span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        {isExpanded && (
          <div className="border-t border-[var(--theme-border-primary)] max-h-96 overflow-y-auto">
            <div className="p-4 bg-[var(--theme-bg-tertiary)]/30">
              <h4 className="text-xs font-semibold text-themed-muted uppercase mb-3">Individual Downloads</h4>
              <div className="space-y-2">
                {group.downloads.map((download) => {
                  const dlHitPercent = download.totalBytes && download.totalBytes > 0
                    ? ((download.cacheHitBytes || 0) / download.totalBytes) * 100
                    : 0;

                  return (
                    <div
                      key={download.id}
                      className="flex items-center justify-between p-2 rounded bg-themed-secondary border"
                      style={{ borderColor: 'var(--theme-border-primary)' }}
                    >
                      <div className="flex items-center gap-3 text-xs">
                        <span className="text-themed-muted">{download.clientIp}</span>
                        <span className="text-themed-muted">{formatRelativeTime(download.startTime)}</span>
                      </div>
                      <div className="flex items-center gap-3 text-xs">
                        <span className="font-medium text-themed-primary">
                          {formatBytes(download.totalBytes || 0)}
                        </span>
                        {download.cacheHitBytes > 0 && (
                          <span className="cache-hit font-bold">
                            {formatPercent(dlHitPercent)}
                          </span>
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

  return (
    <div className="space-y-3">
      {items.map((item) => {
        const isGroup = 'downloads' in item;
        if (isGroup) {
          return renderGroupCard(item as DownloadGroup);
        } else {
          return renderDownloadCard(item as Download);
        }
      })}
    </div>
  );
};

export default NormalView;
