import React from 'react';
import { ChevronRight, Clock, Users, ExternalLink, CheckCircle, AlertCircle } from 'lucide-react';
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

interface NormalViewSectionLabels {
  multipleDownloads: string;
  singleDownloads: string;
  individual: string;
}

const DEFAULT_SECTION_LABELS: NormalViewSectionLabels = {
  multipleDownloads: 'Multiple Downloads',
  singleDownloads: 'Single Downloads',
  individual: 'Individual Downloads'
};

interface NormalViewProps {
  items: (Download | DownloadGroup)[];
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  sectionLabels?: NormalViewSectionLabels;
}

interface InfoRowProps {
  label: string;
  value: React.ReactNode;
  highlight?: boolean;
}

const InfoRow: React.FC<InfoRowProps> = ({ label, value, highlight }) => (
  <div className="flex justify-between items-center py-1">
    <span className="text-sm text-themed-muted">{label}</span>
    <span className={`text-sm font-medium ${highlight ? 'text-themed-primary' : 'text-[var(--theme-text-primary)]'}`}>
      {value}
    </span>
  </div>
);

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

const NormalView: React.FC<NormalViewProps> = ({ items, expandedItem, onItemClick, sectionLabels }) => {
  const labels = { ...DEFAULT_SECTION_LABELS, ...sectionLabels };
  const renderDownloadCard = (download: Download) => {
    const totalBytes = download.totalBytes || 0;
    const cachePercent = totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;
    const cacheStatus = getCacheStatusPill(download);
    const activityStatus = getActivityStatusPill(download);
    const storeLink = download.service.toLowerCase() === 'steam' && download.gameAppId
      ? `https://store.steampowered.com/app/${download.gameAppId}`
      : null;

    return (
      <div
        key={download.id}
        className="rounded-xl border bg-[var(--theme-bg-secondary)] shadow-sm transition-all duration-300 hover:shadow-xl"
        style={{ borderColor: 'var(--theme-border-primary)' }}
      >
        <div className="flex flex-col gap-5 p-5">
          <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
            <div className="flex items-start gap-3 min-w-0">
              <span
                className="px-3 py-1 text-xs font-bold rounded shadow-sm"
                style={getServiceBadgeStyles(download.service)}
              >
                {download.service.toUpperCase()}
              </span>
              <div className="min-w-0 flex flex-col gap-2">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="text-lg font-semibold text-themed-primary truncate">
                    {download.gameName || 'Unknown Game'}
                  </h3>
                  {storeLink && (
                    <a
                      href={storeLink}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="inline-flex items-center gap-1 text-xs text-themed-muted hover:text-themed-accent transition-colors"
                      title="View in Steam Store"
                    >
                      <ExternalLink size={16} />
                      <span>Store Page</span>
                    </a>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-2 text-[11px] uppercase tracking-wide">
                  <span className={`px-2.5 py-1 rounded-full ${cacheStatus.className}`}>
                    {cacheStatus.label}
                  </span>
                  <span className={`px-2.5 py-1 rounded-full ${activityStatus.className}`}>
                    {activityStatus.label}
                  </span>
                </div>
                <div className="flex flex-wrap items-center gap-3 text-sm text-themed-muted">
                  <span className="inline-flex items-center gap-1">
                    <Users size={14} />
                    {download.clientIp}
                  </span>
                  <span className="inline-flex items-center gap-1">
                    <Clock size={14} />
                    Started {formatRelativeTime(download.startTime)}
                  </span>
                  {download.endTime && (
                    <span className="inline-flex items-center gap-1">
                      <Clock size={14} />
                      Finished {formatRelativeTime(download.endTime)}
                    </span>
                  )}
                </div>
              </div>
            </div>
            <div className="flex flex-col items-end gap-2 text-sm text-themed-muted">
              <span className="text-2xl font-semibold text-themed-primary">
                {formatBytes(totalBytes)}
              </span>
              {download.cacheHitBytes > 0 ? (
                <span className="cache-hit font-semibold">
                  {formatPercent(cachePercent)} cache hit
                </span>
              ) : (
                <span>No cache hit</span>
              )}
            </div>
          </div>

          {/* Statistics Section */}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">
            <div className="space-y-3">
              <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Cache Performance</h4>
              <div className="space-y-2">
                <InfoRow
                  label="Cache Hit"
                  value={download.cacheHitBytes > 0 ? formatBytes(download.cacheHitBytes) : 'No cache hits'}
                  highlight={download.cacheHitBytes > 0}
                />
                <InfoRow
                  label="Cache Miss"
                  value={formatBytes(download.cacheMissBytes || 0)}
                />
                <InfoRow
                  label="Cache Efficiency"
                  value={cachePercent > 0 ? formatPercent(cachePercent) : 'N/A'}
                  highlight={cachePercent > 0}
                />
              </div>
            </div>
            <div className="space-y-3">
              <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Details</h4>
              <div className="space-y-2">
                <InfoRow label="Download ID" value={download.id} />
                <InfoRow label="Service" value={download.service.toUpperCase()} />
                <InfoRow label="Status" value={download.isActive ? 'In Progress' : 'Completed'} />
              </div>
            </div>
          </div>

          <div
            className="rounded-lg border bg-[var(--theme-bg-tertiary)]/40 px-4 py-3 text-xs text-themed-muted"
            style={{ borderColor: 'var(--theme-border-primary)' }}
          >
            <div className="flex flex-wrap items-center gap-4">
              <span className="inline-flex items-center gap-1 text-[var(--theme-success-text)]">
                <CheckCircle size={14} />
                Served {download.cacheHitBytes > 0 ? formatBytes(download.cacheHitBytes) : '0 bytes'} from cache
              </span>
              <span className="inline-flex items-center gap-1">
                <AlertCircle size={14} className="text-[var(--theme-text-secondary)]" />
                {formatBytes(download.cacheMissBytes || 0)} fetched from origin
              </span>
            </div>
          </div>
        </div>
      </div>
    );
  };

  const renderGroupCard = (group: DownloadGroup) => {
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
        className={`rounded-xl border bg-[var(--theme-bg-secondary)] shadow-sm transition-all duration-300 overflow-hidden ${isExpanded ? 'border-[var(--theme-primary)]/70 shadow-xl' : 'hover:shadow-xl'}`}
        style={{ borderColor: 'var(--theme-border-primary)' }}
      >
        <button
          type="button"
          onClick={() => onItemClick(group.id)}
          className="w-full text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-primary)]"
        >
          <div className="flex items-center">
            {showGameImage && primaryDownload?.gameAppId && (
              <div className="flex-shrink-0">
                <ImageWithFallback
                  src={`${API_BASE}/gameimages/${primaryDownload.gameAppId}/header/`}
                  alt={primaryDownload.gameName || group.name}
                  className="w-[230px] h-[107px] object-cover"
                />
              </div>
            )}
            <div className="flex-1 px-4 py-3">
              <div className="flex items-start justify-between">
                <div className="flex items-start gap-3 flex-1">
                  <ChevronRight
                    size={14}
                    className={`mt-0.5 text-[var(--theme-text-secondary)] transition-transform flex-shrink-0 ${isExpanded ? 'rotate-90' : ''}`}
                  />
                  <div className="flex flex-col gap-1">
                    <div className="flex items-center gap-2">
                      <span
                        className="px-2 py-0.5 text-xs font-bold rounded"
                        style={getServiceBadgeStyles(group.service)}
                      >
                        {group.service.toUpperCase()}
                      </span>
                      <h3 className="text-base font-semibold text-[var(--theme-text-primary)]">
                        {group.name}
                      </h3>
                      <span className="text-xs text-themed-muted">
                        {group.count} download{group.count !== 1 ? 's' : ''}
                      </span>
                    </div>
                    <div className="flex items-center gap-4 text-xs text-themed-muted">
                      <span className="inline-flex items-center gap-1">
                        <Clock size={10} />
                        {formatRelativeTime(group.lastSeen)}
                      </span>
                      <span>
                        Total Size
                      </span>
                      <span className="font-semibold text-[var(--theme-text-primary)]">
                        {formatBytes(group.totalBytes)}
                      </span>
                      <span>
                        Clients
                      </span>
                      <span className="font-semibold text-[var(--theme-text-primary)]">
                        {group.clientsSet.size}
                      </span>
                    </div>
                    {group.cacheHitBytes > 0 && (
                      <div className="flex items-center gap-3 text-xs">
                        <span>Cache Hit</span>
                        <span className="cache-hit font-semibold">
                          {formatPercent(hitPercent)}
                        </span>
                        <span>Saved</span>
                        <span className="font-semibold text-[var(--theme-success-text)]">
                          {formatBytes(group.cacheHitBytes)}
                        </span>
                      </div>
                    )}
                  </div>
                </div>
              </div>
            </div>
          </div>
        </button>

        {isExpanded && (
          <div
            className="border-t bg-[var(--theme-bg-secondary)]/70 px-5 pb-5 pt-4 overflow-hidden"
            style={{
              borderColor: 'var(--theme-border-primary)',
              animation: 'expandDown 0.4s ease-out',
              transformOrigin: 'top'
            }}
            onClick={(event) => event.stopPropagation()}
          >
            <div className="flex flex-col gap-4">
              <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                <div className="flex flex-wrap items-center gap-2 text-xs">
                  <span
                    className="px-3 py-1 font-bold rounded shadow-sm"
                    style={getServiceBadgeStyles(group.service)}
                  >
                    {group.service.toUpperCase()}
                  </span>
                </div>
                {storeLink && (
                  <a
                    href={storeLink}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 text-xs text-themed-muted hover:text-themed-accent transition-colors"
                    title="View in Steam Store"
                  >
                    <ExternalLink size={16} />
                    <span>Store Page</span>
                  </a>
                )}
              </div>

              {/* Group Statistics */}
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">
                <div className="space-y-3">
                  <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Cache Performance</h4>
                  <div className="space-y-2">
                    <InfoRow label="Total Size" value={formatBytes(group.totalBytes)} highlight />
                    <InfoRow
                      label="Cache Hit"
                      value={group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : 'No cache hits yet'}
                      highlight={group.cacheHitBytes > 0}
                    />
                    <InfoRow label="Cache Miss" value={formatBytes(group.cacheMissBytes || 0)} />
                    <InfoRow
                      label="Cache Efficiency"
                      value={hitPercent > 0 ? formatPercent(hitPercent) : 'N/A'}
                      highlight={hitPercent > 0}
                    />
                  </div>
                </div>
                <div className="space-y-3">
                  <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">Activity</h4>
                  <div className="space-y-2">
                    <InfoRow label={`Download${group.count !== 1 ? 's' : ''}`} value={group.count} />
                    <InfoRow label="Unique Clients" value={group.clientsSet.size} />
                    <InfoRow label="First Seen" value={formatRelativeTime(group.firstSeen)} />
                    <InfoRow label="Last Seen" value={formatRelativeTime(group.lastSeen)} />
                  </div>
                </div>
              </div>

              <div className="rounded-lg border bg-[var(--theme-bg-tertiary)]/40 px-4 py-3 text-xs text-themed-muted" style={{ borderColor: 'var(--theme-border-primary)' }}>
                <div className="flex flex-wrap items-center gap-4">
                  <span className="inline-flex items-center gap-1 text-[var(--theme-success-text)]">
                    <CheckCircle size={14} />
                    {group.cacheHitBytes > 0 ? `${formatBytes(group.cacheHitBytes)} served from cache` : 'No cache hits yet'}
                  </span>
                  <span className="inline-flex items-center gap-1">
                    <AlertCircle size={14} className="text-[var(--theme-text-secondary)]" />
                    {formatBytes(group.cacheMissBytes || 0)} fetched from origin
                  </span>
                </div>
              </div>

              <div className="space-y-4">
                <div className="flex items-center justify-between">
                  <h4 className="text-sm font-semibold text-themed-primary uppercase tracking-wide">
                    Download Sessions
                  </h4>
                  <span className="text-xs text-themed-muted bg-[var(--theme-bg-tertiary)] px-2 py-1 rounded-full">
                    {group.downloads.length} {group.downloads.length !== 1 ? 'entries' : 'entry'}
                  </span>
                </div>
                <div className="space-y-2">
                  {group.downloads.map((download) => {
                    const totalBytes = download.totalBytes || 0;
                    const cachePercent = totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;

                    return (
                      <div
                        key={download.id}
                        className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 px-4 py-3 rounded-lg border hover:bg-[var(--theme-bg-tertiary)]/20 transition-colors"
                        style={{ borderColor: 'var(--theme-border-secondary)' }}
                      >
                        <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
                          <span className="font-mono text-sm text-[var(--theme-text-primary)] bg-[var(--theme-bg-tertiary)] px-2 py-1 rounded">
                            {download.clientIp}
                          </span>
                          <div className="flex items-center gap-4 text-xs text-themed-muted">
                            <span className="flex items-center gap-1">
                              <Clock size={12} />
                              Started {formatRelativeTime(download.startTime)}
                            </span>
                            {download.endTime && (
                              <span className="flex items-center gap-1">
                                <CheckCircle size={12} />
                                Finished {formatRelativeTime(download.endTime)}
                              </span>
                            )}
                            {!download.endTime && (
                              <span className="flex items-center gap-1 text-[var(--theme-info-text)]">
                                <AlertCircle size={12} />
                                In progress
                              </span>
                            )}
                          </div>
                        </div>
                        <div className="flex items-center gap-4 text-sm">
                          <span className="font-semibold text-[var(--theme-text-primary)]">
                            {formatBytes(totalBytes)}
                          </span>
                          {download.cacheHitBytes > 0 ? (
                            <span className="cache-hit font-semibold text-xs px-2 py-1 rounded-full bg-[var(--theme-success-bg)] text-[var(--theme-success-text)]">
                              {formatPercent(cachePercent)}
                            </span>
                          ) : (
                            <span className="text-xs px-2 py-1 rounded-full bg-[var(--theme-bg-tertiary)] text-themed-muted">
                              No cache
                            </span>
                          )}
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    );
  };

  let multipleDownloadsHeaderRendered = false;
  let singleDownloadsHeaderRendered = false;
  let individualHeaderRendered = false;

  return (
    <div className="space-y-4">
      {items.map((item) => {
        const isGroup = 'downloads' in item;
        const key = isGroup ? (item as DownloadGroup).id : `download-${(item as Download).id}`;
        let header: React.ReactNode = null;

        if (isGroup) {
          const group = item as DownloadGroup;
          if (group.count > 1 && !multipleDownloadsHeaderRendered) {
            multipleDownloadsHeaderRendered = true;
            header = (
              <div className="mb-4 mt-6 first:mt-0">
                <h2 className="text-lg font-semibold text-themed-primary border-b pb-2" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                  {labels.multipleDownloads}
                </h2>
              </div>
            );
          } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
            singleDownloadsHeaderRendered = true;
            header = (
              <div className="mb-4 mt-6 first:mt-0">
                <h2 className="text-lg font-semibold text-themed-primary border-b pb-2" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                  {labels.singleDownloads}
                </h2>
              </div>
            );
          }
        } else if (!isGroup && !individualHeaderRendered) {
          individualHeaderRendered = true;
          header = (
            <div className="mb-4 mt-6 first:mt-0">
              <h2 className="text-lg font-semibold text-themed-primary border-b pb-2" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                {labels.individual}
              </h2>
            </div>
          );
        }

        return (
          <React.Fragment key={key}>
            {header}
            {isGroup ? renderGroupCard(item as DownloadGroup) : renderDownloadCard(item as Download)}
          </React.Fragment>
        );
      })}
    </div>
  );
};

export default NormalView;
