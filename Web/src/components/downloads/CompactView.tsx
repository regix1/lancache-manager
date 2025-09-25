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

interface CompactViewSectionLabels {
  grouped: string;
  individual: string;
  banner: string;
  downloadList: string;
}

const DEFAULT_SECTION_LABELS: CompactViewSectionLabels = {
  grouped: 'Grouped Downloads',
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

const CompactView: React.FC<CompactViewProps> = ({
  items,
  expandedItem,
  onItemClick,
  sectionLabels = DEFAULT_SECTION_LABELS
}) => {
  const renderCompactRow = (item: Download | DownloadGroup) => {
    const isGroup = 'downloads' in item;
    const isExpanded = expandedItem === (isGroup ? (item as DownloadGroup).id : `download-${(item as Download).id}`);

    if (isGroup) {
      const group = item as DownloadGroup;
      const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
      const avgCacheHit = hitPercent > 0 ? formatPercent(hitPercent) : 'N/A';
      const primaryDownload = group.downloads[0];
      const storeLink = group.type === 'game' &&
                        group.service.toLowerCase() === 'steam' &&
                        primaryDownload?.gameAppId
        ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
        : null;
      const showGameImage =
        group.type === 'game' &&
        group.service.toLowerCase() === 'steam' &&
        primaryDownload?.gameAppId &&
        primaryDownload?.gameName &&
        primaryDownload.gameName !== 'Unknown Steam Game' &&
        !primaryDownload.gameName.match(/^Steam App \d+$/);

      return (
        <div
          key={group.id}
          className="group hover:bg-[var(--theme-bg-tertiary)] transition-colors cursor-pointer"
          onClick={() => onItemClick(group.id)}
        >
          {/* Main row */}
          <div className="flex items-center px-4 py-2 border-b border-[var(--theme-border-primary)]">
            {/* Service */}
            <div className="w-28 flex items-center gap-2">
              <ChevronRight
                size={14}
                className={`transition-transform text-[var(--theme-text-secondary)] ${isExpanded ? 'rotate-90' : ''}`}
              />
              <span className={`px-2 py-1 text-xs font-bold rounded shadow-sm ${getServiceBadgeClasses(group.service)}`}>
                {group.service.toUpperCase()}
              </span>
            </div>

            {/* Status */}
            <div className="w-28">
              <span className="text-xs bg-themed-accent text-themed-button px-2 py-0.5 rounded-full font-semibold">
                {group.count} downloads
              </span>
            </div>

            {/* Game */}
            <div className="flex-1 px-2">
              <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate block">
                {group.name}
              </span>
            </div>

            {/* Client */}
            <div className="w-32">
              <span className="text-sm text-[var(--theme-text-secondary)]">
                {group.clientsSet.size} clients
              </span>
            </div>

            {/* Time */}
            <div className="w-24">
              <span className="text-sm text-[var(--theme-text-secondary)]">
                {formatRelativeTime(group.lastSeen)}
              </span>
            </div>

            {/* Size */}
            <div className="w-32 text-right">
              <div className="text-sm font-semibold text-[var(--theme-text-primary)]">
                {formatBytes(group.totalBytes)}
              </div>
              {group.cacheHitBytes > 0 && (
                <div className="text-xs cache-hit">
                  {avgCacheHit}
                </div>
              )}
            </div>
          </div>

          {/* Expanded section */}
          {isExpanded && (
            <div className="bg-[var(--theme-bg-tertiary)]/50">
              <div
                className="px-4 sm:px-6 py-4 border-b"
                style={{ borderColor: 'var(--theme-border-primary)' }}
              >
                <div className={`flex flex-col sm:flex-row ${showGameImage ? 'gap-4 sm:gap-6' : 'gap-3'}`}>
                  {showGameImage && primaryDownload?.gameAppId && (
                    <div className="flex-shrink-0 flex flex-col gap-2">
                      <span className="text-[10px] font-semibold uppercase tracking-wide text-themed-muted">
                        {sectionLabels.banner}
                      </span>
                      <ImageWithFallback
                        src={`${API_BASE}/gameimages/${primaryDownload.gameAppId}/header/`}
                        alt={primaryDownload.gameName || group.name}
                        className="w-full sm:w-[260px] h-[120px] sm:h-[130px] rounded-lg object-cover"
                      />
                    </div>
                  )}
                  <div className="flex-1 flex flex-col gap-3">
                    <div className="flex flex-wrap items-center gap-3 justify-between">
                      <div className="flex flex-wrap items-center gap-2 text-sm">
                        <h4 className="font-semibold text-themed-primary">
                          {group.name}
                        </h4>
                        <span className="px-2 py-0.5 text-xs rounded-full bg-themed-hover text-themed-muted">
                          {group.type === 'game' ? 'Game' : group.type.charAt(0).toUpperCase() + group.type.slice(1)} group
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
                    <div className="flex flex-wrap gap-4 text-xs text-themed-muted">
                      <div className="flex items-center gap-1">
                        <CheckCircle size={12} className="text-[var(--theme-success-text)]" />
                        <span>Average cache hit: {avgCacheHit}</span>
                      </div>
                      <div className="flex items-center gap-1">
                        <AlertCircle size={12} className="text-[var(--theme-text-secondary)]" />
                        <span>Total size: {formatBytes(group.totalBytes)}</span>
                      </div>
                      {primaryDownload?.gameName && (
                        <div className="flex items-center gap-1">
                          <span className="w-2 h-2 rounded-full bg-[var(--theme-primary)]" />
                          <span>Latest download: {primaryDownload.gameName}</span>
                        </div>
                      )}
                    </div>
                  </div>
                </div>
              </div>
              {group.downloads.length > 0 && (
                <>
                  <div className="px-12 pt-4 text-[10px] font-semibold uppercase tracking-wide text-themed-muted">
                    {sectionLabels.downloadList}
                  </div>
                  <div className="px-12 py-2 text-[10px] uppercase tracking-wide text-themed-muted flex justify-between">
                    <span className="w-32">Client</span>
                    <span className="flex-1">Last Seen</span>
                    <span className="w-24 text-right">Size</span>
                    <span className="w-20 text-right">Cache Hit</span>
                  </div>
                </>
              )}
              {group.downloads.map((download, idx) => (
                <div
                  key={download.id}
                  className={`px-12 py-1.5 text-xs ${idx % 2 === 0 ? 'bg-[var(--theme-bg-secondary)]/30' : ''}`}
                >
                  <div className="flex items-center">
                    <div className="w-32 text-[var(--theme-text-muted)]">
                      {download.clientIp}
                    </div>
                    <div className="flex-1 text-[var(--theme-text-muted)]">
                      {formatRelativeTime(download.startTime)}
                    </div>
                    <div className="w-24 text-right font-medium text-[var(--theme-text-primary)]">
                      {formatBytes(download.totalBytes || 0)}
                    </div>
                    <div className="w-20 text-right">
                      {download.cacheHitBytes > 0 && (
                        <span className="cache-hit font-semibold">
                          {formatPercent((download.cacheHitBytes / (download.totalBytes || 1)) * 100)}
                        </span>
                      )}
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      );
    } else {
      // Individual download
      const download = item as Download;
      const hitPercent = download.totalBytes > 0 ? ((download.cacheHitBytes || 0) / download.totalBytes) * 100 : 0;
      const downloadStatus = download.totalBytes === 0 ? 'Metadata' :
        download.cacheHitBytes === download.totalBytes ? 'Cached' :
        download.cacheHitBytes > 0 ? 'Partial Cache Hit' : 'New Download';
      const storeLink = download.service.toLowerCase() === 'steam' && download.gameAppId
        ? `https://store.steampowered.com/app/${download.gameAppId}`
        : null;
      const showGameImage = download.service.toLowerCase() === 'steam' &&
        download.gameAppId &&
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
          className="group hover:bg-[var(--theme-bg-tertiary)] transition-colors cursor-pointer"
          onClick={() => onItemClick(`download-${download.id}`)}
        >
          <div className="flex items-center px-4 py-2 border-b border-[var(--theme-border-primary)]">
            {/* Service */}
            <div className="w-28 flex items-center gap-2">
              <ChevronRight
                size={14}
                className={`transition-transform text-[var(--theme-text-secondary)] ${isExpanded ? 'rotate-90' : ''}`}
              />
              <span className={`px-2 py-1 text-xs font-bold rounded shadow-sm ${getServiceBadgeClasses(download.service)}`}>
                {download.service.toUpperCase()}
              </span>
            </div>

            {/* Summary */}
            <div className="w-28">
              <span className="px-2 py-0.5 text-xs rounded-full bg-themed-hover text-themed-muted">
                1 download
              </span>
            </div>

            {/* Game */}
            <div className="flex-1 px-2">
              <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate block">
                {download.gameName || 'Unknown Game'}
              </span>
            </div>

            {/* Client */}
            <div className="w-32">
              <span className="text-sm text-[var(--theme-text-secondary)]">
                {download.clientIp}
              </span>
            </div>

            {/* Time */}
            <div className="w-24">
              <span className="text-sm text-[var(--theme-text-secondary)]">
                {formatRelativeTime(download.startTime)}
              </span>
            </div>

            {/* Size */}
            <div className="w-32 text-right">
              <div className="text-sm font-semibold text-[var(--theme-text-primary)]">
                {formatBytes(download.totalBytes || 0)}
              </div>
              {download.cacheHitBytes > 0 && (
                <div className="text-xs cache-hit">
                  {formatPercent(hitPercent)}
                </div>
              )}
            </div>
          </div>

          {isExpanded && (
            <div className="bg-[var(--theme-bg-tertiary)]/50">
              <div
                className="px-4 sm:px-6 py-4 border-b"
                style={{ borderColor: 'var(--theme-border-primary)' }}
              >
                <div className={`flex flex-col sm:flex-row ${showGameImage ? 'gap-4 sm:gap-6' : 'gap-3'}`}>
                  {showGameImage && download.gameAppId && (
                    <div className="flex-shrink-0 flex flex-col gap-2">
                      <span className="text-[10px] font-semibold uppercase tracking-wide text-themed-muted">
                        {sectionLabels.banner}
                      </span>
                      <ImageWithFallback
                        src={`${API_BASE}/gameimages/${download.gameAppId}/header/`}
                        alt={download.gameName || 'Game'}
                        className="w-full sm:w-[260px] h-[120px] sm:h-[130px] rounded-lg object-cover"
                      />
                    </div>
                  )}
                  <div className="flex-1 flex flex-col gap-3">
                    <div className="flex flex-wrap items-center gap-3 justify-between">
                      <div className="flex flex-wrap items-center gap-2 text-sm">
                        <h4 className="font-semibold text-themed-primary">
                          {download.gameName || 'Unknown Game'}
                        </h4>
                        <span className={`px-2 py-0.5 text-xs rounded-full ${statusBadgeClasses}`}>
                          {downloadStatus}
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
                    <div className="flex flex-wrap gap-4 text-xs text-themed-muted">
                      <div className="flex items-center gap-1">
                        <CheckCircle size={12} className="text-[var(--theme-success-text)]" />
                        <span>Cache hit: {download.cacheHitBytes > 0 ? formatPercent(hitPercent) : 'No cache hit data'}</span>
                      </div>
                      <div className="flex items-center gap-1">
                        <AlertCircle size={12} className="text-[var(--theme-text-secondary)]" />
                        <span>Total size: {formatBytes(download.totalBytes || 0)}</span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
              <div className="px-12 pt-4 text-[10px] font-semibold uppercase tracking-wide text-themed-muted">
                {sectionLabels.downloadList}
              </div>
              <div className="px-12 py-2 text-[10px] uppercase tracking-wide text-themed-muted flex justify-between">
                <span className="w-32">Client</span>
                <span className="flex-1">Last Seen</span>
                <span className="w-24 text-right">Size</span>
                <span className="w-20 text-right">Cache Hit</span>
              </div>
              <div className="px-12 pb-1.5 text-xs">
                <div className="flex items-center">
                  <div className="w-32 text-[var(--theme-text-muted)]">
                    {download.clientIp}
                  </div>
                  <div className="flex-1 text-[var(--theme-text-muted)]">
                    {formatRelativeTime(download.startTime)}
                  </div>
                  <div className="w-24 text-right font-medium text-[var(--theme-text-primary)]">
                    {formatBytes(download.totalBytes || 0)}
                  </div>
                  <div className="w-20 text-right">
                    {download.cacheHitBytes > 0 && (
                      <span className="cache-hit font-semibold">
                        {formatPercent(hitPercent)}
                      </span>
                    )}
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>
      );
    }
  };

  let groupHeaderRendered = false;
  let individualHeaderRendered = false;

  return (
    <div className="rounded-lg overflow-hidden border border-[var(--theme-border-primary)] bg-[var(--theme-bg-secondary)]">
      {/* Table Header - Desktop */}
      <div className="hidden md:flex items-center px-4 py-2 text-xs font-medium uppercase tracking-wider text-themed-muted border-b bg-[var(--theme-bg-tertiary)]/50"
           style={{ borderColor: 'var(--theme-border-primary)' }}>
        <div className="w-28">Service</div>
        <div className="w-28">Status</div>
        <div className="flex-1 px-2">Game</div>
        <div className="w-32">Client</div>
        <div className="w-24">Time</div>
        <div className="w-32 text-right">Size</div>
      </div>

      {/* Mobile Header */}
      <div className="md:hidden px-3 py-2 text-xs font-medium uppercase tracking-wider text-themed-muted border-b bg-[var(--theme-bg-tertiary)]/50"
           style={{ borderColor: 'var(--theme-border-primary)' }}>
        Downloads
      </div>

      {/* Content */}
      <div className="divide-y-0">
        {items.map((item) => {
          const isGroup = 'downloads' in item;
          let header: React.ReactNode = null;

          if (isGroup && !groupHeaderRendered) {
            groupHeaderRendered = true;
            header = (
              <div className="px-4 py-2 text-[11px] font-semibold uppercase tracking-wide text-themed-muted bg-[var(--theme-bg-tertiary)]/60">
                {sectionLabels.grouped}
              </div>
            );
          } else if (!isGroup && !individualHeaderRendered) {
            individualHeaderRendered = true;
            header = (
              <div className="px-4 py-2 text-[11px] font-semibold uppercase tracking-wide text-themed-muted bg-[var(--theme-bg-tertiary)]/60">
                {sectionLabels.individual}
              </div>
            );
          }

          const key = isGroup
            ? (item as DownloadGroup).id
            : `download-${(item as Download).id}`;

          return (
            <React.Fragment key={key}>
              {header}
              {renderCompactRow(item)}
            </React.Fragment>
          );
        })}
      </div>
    </div>
  );
};

export default CompactView;
