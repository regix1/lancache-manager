import React from 'react';
import { ChevronRight, Clock, ExternalLink, CheckCircle, AlertCircle } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import type { Download, DownloadGroup } from '../../types';

const SteamIcon: React.FC<{ size?: number; className?: string; style?: React.CSSProperties }> = ({ size = 24, className = '', style = {} }) => (
  <svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg" width={size} height={size} className={className} style={style}>
    <g fill="currentColor">
      <circle cx="15.5" cy="9.5" r="2.5"></circle>
      <path d="m8.67 18.34a1.49 1.49 0 0 1 -1.67-.21.5.5 0 0 0 -.66.75 2.5 2.5 0 1 0 2-4.35.49.49 0 0 0 -.56.43.5.5 0 0 0 .43.56 1.5 1.5 0 0 1 .47 2.83z"></path>
      <path d="m12 0a12 12 0 0 0 -12 11.5.5.5 0 0 0 .14.37.5.5 0 0 0 .26.13c.34.11 3 1.26 4.55 2a.51.51 0 0 0 .52-.07 3.84 3.84 0 0 1 2.86-.93.5.5 0 0 0 .45-.19l2.11-2.76a.5.5 0 0 0 .1-.35c0-.08 0-.15 0-.22a4.5 4.5 0 1 1 4.81 4.52.5.5 0 0 0 -.28.11l-3.35 2.75a.5.5 0 0 0 -.18.36 4 4 0 0 1 -3.99 3.78 3.94 3.94 0 0 1 -3.84-2.93.5.5 0 0 0 -.26-.32l-1.9-.93a.5.5 0 0 0 -.67.68 12 12 0 1 0 10.67-17.5z"></path>
    </g>
  </svg>
);

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
  multipleDownloads: 'Frequently Downloaded Games (2+ sessions)',
  singleDownloads: 'Single Session Downloads',
  individual: 'Uncategorized Downloads'
};

interface NormalViewProps {
  items: (Download | DownloadGroup)[];
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  sectionLabels?: NormalViewSectionLabels;
  aestheticMode?: boolean;
  groupByFrequency?: boolean;
}

const NormalView: React.FC<NormalViewProps> = ({ items, expandedItem, onItemClick, sectionLabels, aestheticMode = false, groupByFrequency = true }) => {
  const labels = { ...DEFAULT_SECTION_LABELS, ...sectionLabels };
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());

  const handleImageError = (gameAppId: string) => {
    setImageErrors(prev => new Set(prev).add(gameAppId));
  };

  const renderDownloadCard = (download: Download) => {
    const totalBytes = download.totalBytes || 0;

    // Create a fake group-like structure for individual downloads to match grouped style
    const fakeGroup = {
      id: `individual-${download.id}`,
      name: download.gameName || 'Unknown Game',
      type: 'game' as const,
      service: download.service,
      downloads: [download],
      totalBytes: totalBytes,
      totalDownloaded: totalBytes,
      cacheHitBytes: download.cacheHitBytes || 0,
      cacheMissBytes: download.cacheMissBytes || 0,
      clientsSet: new Set([download.clientIp]),
      firstSeen: download.startTime,
      lastSeen: download.startTime,
      count: 1
    };

    return renderGroupCard(fakeGroup);
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
        className="rounded-xl border bg-[var(--theme-bg-secondary)] overflow-hidden"
        style={{
          borderColor: isExpanded ? 'var(--theme-card-outline)' : 'var(--theme-border-primary)',
          boxShadow: isExpanded
            ? '0 25px 50px -12px rgba(0, 0, 0, 0.25), 0 0 0 2px var(--theme-card-ring)'
            : '0 1px 2px 0 rgba(0, 0, 0, 0.05)',
          transition: 'border-color 0.4s cubic-bezier(0.4, 0, 0.2, 1), box-shadow 0.4s cubic-bezier(0.4, 0, 0.2, 1)',
          WebkitTapHighlightColor: 'transparent',
        }}
      >
        <button
          type="button"
          onClick={() => onItemClick(group.id)}
          className="w-full text-left no-click-outline focus:outline-none focus:ring-0 focus:border-transparent focus-visible:outline-none focus-visible:ring-0 active:outline-none active:ring-0"
          style={{
            WebkitTapHighlightColor: 'transparent',
            outline: 'none !important',
            boxShadow: 'none !important'
          }}
        >
          <div className="flex flex-col sm:flex-row sm:items-stretch">
            {showGameImage && primaryDownload?.gameAppId && (
              <div className="flex-shrink-0 overflow-hidden w-full sm:w-auto">
                {aestheticMode || imageErrors.has(String(primaryDownload.gameAppId)) ? (
                  <div
                    className="w-full sm:w-[280px] h-[130px] flex items-center justify-center"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                    }}
                  >
                    <SteamIcon
                      size={80}
                      style={{ color: 'var(--theme-steam)', opacity: 0.5 }}
                    />
                  </div>
                ) : (
                  <img
                    src={`${API_BASE}/gameimages/${primaryDownload.gameAppId}/header/`}
                    alt={primaryDownload.gameName || group.name}
                    className="w-full sm:w-[280px] h-[130px] object-cover transition-transform duration-300 hover:scale-105"
                    loading="lazy"
                    onError={() => handleImageError(String(primaryDownload.gameAppId))}
                  />
                )}
              </div>
            )}
            <div className="flex-1 p-4 sm:p-5">
              <div className="flex items-start gap-3 sm:gap-4">
                <ChevronRight
                  size={18}
                  className={`mt-0.5 sm:mt-1 text-[var(--theme-primary)] transition-all duration-300 flex-shrink-0 ${isExpanded ? 'rotate-90' : ''}`}
                  style={{ opacity: isExpanded ? 1 : 0.6 }}
                />
                <div className="flex-1 min-w-0">
                  {/* Title Row */}
                  <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-3 mb-2 sm:mb-3">
                    <div className="flex items-center gap-2">
                      <span
                        className="px-2 sm:px-2.5 py-0.5 sm:py-1 text-xs font-extrabold rounded-md shadow-sm"
                        style={getServiceBadgeStyles(group.service)}
                      >
                        {group.service.toUpperCase()}
                      </span>
                      {group.count > 1 && (
                        <span className="px-2 sm:px-2.5 py-0.5 sm:py-1 text-xs font-semibold rounded-full bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]">
                          {group.count}× downloads
                        </span>
                      )}
                    </div>
                    <h3 className="text-lg sm:text-xl font-bold text-[var(--theme-text-primary)] truncate flex-1">
                      {group.name}
                    </h3>
                  </div>

                  {/* Stats Grid - Better aligned */}
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-x-8 gap-y-1.5 sm:gap-y-2">
                    <div className="flex items-baseline gap-2">
                      <span className="text-xs sm:text-sm text-themed-muted font-medium min-w-[70px] sm:min-w-[80px]">Total Downloaded</span>
                      <span className="text-sm sm:text-base font-bold text-[var(--theme-text-primary)]">
                        {formatBytes(group.totalBytes)}
                      </span>
                    </div>
                    <div className="flex items-baseline gap-2">
                      <span className="text-xs sm:text-sm text-themed-muted font-medium min-w-[70px] sm:min-w-[80px]">Clients</span>
                      <span className="text-sm sm:text-base font-bold text-[var(--theme-text-primary)]">
                        {group.clientsSet.size}
                      </span>
                    </div>
                    <div className="flex items-baseline gap-2">
                      <span className="text-xs sm:text-sm text-themed-muted font-medium min-w-[70px] sm:min-w-[80px]">Cache Saved</span>
                      <span className="text-sm sm:text-base font-bold text-[var(--theme-success-text)]">
                        {formatBytes(group.cacheHitBytes)}
                      </span>
                    </div>
                    <div className="flex items-baseline gap-2">
                      <span className="text-xs sm:text-sm text-themed-muted font-medium min-w-[70px] sm:min-w-[80px]">Last Active</span>
                      <span className="text-xs sm:text-sm font-medium text-[var(--theme-text-secondary)] inline-flex items-center gap-1.5">
                        <Clock size={12} className="sm:hidden" />
                        <Clock size={14} className="hidden sm:block" />
                        {formatRelativeTime(group.lastSeen)}
                      </span>
                    </div>
                    {hitPercent > 0 && (
                      <div className="flex items-baseline gap-2">
                        <span className="text-xs sm:text-sm text-themed-muted font-medium min-w-[70px] sm:min-w-[80px]">Efficiency</span>
                        <span className="text-xs sm:text-sm font-bold cache-hit inline-flex items-center gap-1.5">
                          {formatPercent(hitPercent)}
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
            className="border-t bg-gradient-to-b from-[var(--theme-bg-secondary)] to-[var(--theme-bg-primary)] px-6 pb-6 pt-5"
            style={{
              borderColor: 'var(--theme-primary)',
              animation: 'expandDown 0.4s cubic-bezier(0.4, 0, 0.2, 1)'
            }}
            onClick={(event) => event.stopPropagation()}
          >
            <div className="flex flex-col gap-6">
              {/* Quick Actions Bar */}
              {storeLink && (
                <div className="flex justify-end">
                  <a
                    href={storeLink}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-2 text-sm px-4 py-2 rounded-lg font-semibold transition-all duration-200 shadow-sm hover:shadow-md"
                    style={{
                      backgroundColor: 'var(--theme-primary)',
                      color: 'var(--theme-button-text)'
                    }}
                    title="View in Steam Store"
                  >
                    <ExternalLink size={18} />
                    <span>View Store Page</span>
                  </a>
                </div>
              )}

              {/* Summary Stats Banner */}
              <div className="rounded-xl border p-4 bg-[var(--theme-bg-tertiary)]/50" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
                  <div className="text-center">
                    <div className="text-xs text-themed-muted mb-1 font-medium">Total Downloaded</div>
                    <div className="text-lg font-bold text-[var(--theme-text-primary)]">{formatBytes(group.totalBytes)}</div>
                  </div>
                  <div className="text-center">
                    <div className="text-xs text-themed-muted mb-1 font-medium">Cache Saved</div>
                    <div className="text-lg font-bold text-[var(--theme-success-text)]">
                      {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '—'}
                    </div>
                  </div>
                  <div className="text-center">
                    <div className="text-xs text-themed-muted mb-1 font-medium">Efficiency</div>
                    <div className="text-lg font-bold cache-hit">
                      {hitPercent > 0 ? formatPercent(hitPercent) : '—'}
                    </div>
                  </div>
                  <div className="text-center">
                    <div className="text-xs text-themed-muted mb-1 font-medium">Downloads</div>
                    <div className="text-lg font-bold text-[var(--theme-text-primary)]">{group.count}</div>
                  </div>
                </div>
              </div>

              {/* Detailed Statistics */}
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-5">
                {/* Cache Performance Card */}
                <div className="rounded-xl border p-5 bg-[var(--theme-bg-secondary)]" style={{ borderColor: 'var(--theme-border-primary)' }}>
                  <h4 className="text-base font-bold text-[var(--theme-text-primary)] mb-4 pb-2 border-b" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                    Cache Performance
                  </h4>
                  <div className="space-y-3">
                    <div className="flex justify-between items-center">
                      <span className="text-sm text-themed-muted font-medium">Cache Hit</span>
                      <span className="text-sm font-bold text-[var(--theme-success-text)]">
                        {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : 'None'}
                      </span>
                    </div>
                    <div className="flex justify-between items-center">
                      <span className="text-sm text-themed-muted font-medium">Cache Miss</span>
                      <span className="text-sm font-semibold text-[var(--theme-text-secondary)]">
                        {formatBytes(group.cacheMissBytes || 0)}
                      </span>
                    </div>
                    <div className="flex justify-between items-center pt-2 border-t" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                      <span className="text-sm text-themed-muted font-medium">Efficiency Rate</span>
                      <span className="text-base font-bold cache-hit">
                        {hitPercent > 0 ? formatPercent(hitPercent) : 'N/A'}
                      </span>
                    </div>
                  </div>
                </div>

                {/* Activity Card */}
                <div className="rounded-xl border p-5 bg-[var(--theme-bg-secondary)]" style={{ borderColor: 'var(--theme-border-primary)' }}>
                  <h4 className="text-base font-bold text-[var(--theme-text-primary)] mb-4 pb-2 border-b" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                    Activity Timeline
                  </h4>
                  <div className="space-y-3">
                    <div className="flex justify-between items-center">
                      <span className="text-sm text-themed-muted font-medium">Download Sessions</span>
                      <span className="text-sm font-bold text-[var(--theme-text-primary)]">{group.count}</span>
                    </div>
                    <div className="flex justify-between items-center">
                      <span className="text-sm text-themed-muted font-medium">Unique Clients</span>
                      <span className="text-sm font-bold text-[var(--theme-text-primary)]">{group.clientsSet.size}</span>
                    </div>
                    <div className="flex justify-between items-center">
                      <span className="text-sm text-themed-muted font-medium">First Seen</span>
                      <span className="text-sm font-semibold text-[var(--theme-text-secondary)]">{formatRelativeTime(group.firstSeen)}</span>
                    </div>
                    <div className="flex justify-between items-center pt-2 border-t" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                      <span className="text-sm text-themed-muted font-medium">Last Activity</span>
                      <span className="text-sm font-bold text-[var(--theme-text-primary)]">{formatRelativeTime(group.lastSeen)}</span>
                    </div>
                  </div>
                </div>
              </div>

              {/* Download Sessions List */}
              {group.downloads.length > 0 && (
                <div className="space-y-3">
                  <div className="flex items-center justify-between">
                    <h4 className="text-base font-bold text-[var(--theme-text-primary)]">
                      Download Sessions
                    </h4>
                    <span className="text-xs font-semibold bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] px-3 py-1.5 rounded-full">
                      {group.downloads.length} session{group.downloads.length !== 1 ? 's' : ''}
                    </span>
                  </div>
                  {/* Group sessions by client IP */}
                  {Object.entries(
                    group.downloads.reduce((acc, d) => {
                      if (!acc[d.clientIp]) acc[d.clientIp] = [];
                      acc[d.clientIp].push(d);
                      return acc;
                    }, {} as Record<string, typeof group.downloads>)
                  ).map(([clientIp, clientDownloads]) => {
                    const clientTotal = clientDownloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
                    const clientCacheHit = clientDownloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);

                    return (
                      <div key={clientIp} className="space-y-2">
                        {/* Client IP Header with totals */}
                        <div className="flex items-center justify-between p-3 rounded-lg bg-[var(--theme-bg-tertiary)]/50">
                          <div className="flex items-center gap-2">
                            <span className="font-mono text-sm font-bold text-[var(--theme-text-primary)]">
                              {clientIp}
                            </span>
                            <span className="text-xs text-themed-muted">
                              ({clientDownloads.length} session{clientDownloads.length !== 1 ? 's' : ''})
                            </span>
                          </div>
                          <div className="flex items-center gap-3">
                            <span className="text-sm font-bold text-[var(--theme-text-primary)]">
                              {formatBytes(clientTotal)}
                            </span>
                            {clientCacheHit > 0 && (
                              <span className="text-xs px-2 py-1 rounded-full bg-[var(--theme-success-bg)] text-[var(--theme-success-text)] font-semibold">
                                {formatPercent(clientTotal > 0 ? (clientCacheHit / clientTotal) * 100 : 0)}
                              </span>
                            )}
                          </div>
                        </div>
                        {/* Individual sessions for this client */}
                        {clientDownloads.map((download) => {
                      const totalBytes = download.totalBytes || 0;
                      const cachePercent = totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;

                      return (
                        <div
                          key={download.id}
                          className="rounded-lg border p-4 hover:bg-[var(--theme-bg-tertiary)]/30 transition-all duration-200 ml-4"
                          style={{ borderColor: 'var(--theme-border-secondary)' }}
                        >
                          <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 items-center">
                            {/* Time Info */}
                            <div>
                              <div className="text-xs text-themed-muted mb-1 font-medium">Timeline</div>
                              <div className="flex flex-col gap-1 text-xs">
                                <span className="flex items-center gap-1.5 text-[var(--theme-text-secondary)]">
                                  <Clock size={12} />
                                  Started {formatRelativeTime(download.startTime)}
                                </span>
                                {download.endTime ? (
                                  <span className="flex items-center gap-1.5 text-[var(--theme-success-text)]">
                                    <CheckCircle size={12} />
                                    Completed {formatRelativeTime(download.endTime)}
                                  </span>
                                ) : (
                                  <span className="flex items-center gap-1.5 text-[var(--theme-info-text)]">
                                    <AlertCircle size={12} />
                                    In progress
                                  </span>
                                )}
                              </div>
                            </div>

                            {/* Size & Cache */}
                            <div className="flex items-center justify-between lg:justify-end gap-4">
                              <div>
                                <div className="text-xs text-themed-muted mb-1 font-medium">Size</div>
                                <span className="text-base font-bold text-[var(--theme-text-primary)]">
                                  {formatBytes(totalBytes)}
                                </span>
                              </div>
                              {download.cacheHitBytes > 0 ? (
                                <div className="text-center">
                                  <div className="text-xs text-themed-muted mb-1 font-medium">Cache</div>
                                  <span className="cache-hit font-bold text-sm px-3 py-1.5 rounded-full bg-[var(--theme-success-bg)] inline-block">
                                    {formatPercent(cachePercent)}
                                  </span>
                                </div>
                              ) : (
                                <div className="text-center">
                                  <div className="text-xs text-themed-muted mb-1 font-medium">Cache</div>
                                  <span className="text-xs px-3 py-1.5 rounded-full bg-[var(--theme-bg-tertiary)] text-themed-muted inline-block font-medium">
                                    No hits
                                  </span>
                                </div>
                              )}
                            </div>
                          </div>
                        </div>
                      );
                    })}
                      </div>
                    );
                  })}
                </div>
              )}
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

        // Only show section headers if groupByFrequency is enabled
        if (groupByFrequency) {
          if (isGroup) {
            const group = item as DownloadGroup;
            if (group.count > 1 && !multipleDownloadsHeaderRendered) {
              multipleDownloadsHeaderRendered = true;
              header = (
                <div className="mb-4 mt-6 first:mt-0">
                  <h2 className="text-lg font-bold text-themed-primary border-b pb-2" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                    {labels.multipleDownloads}
                  </h2>
                  <p className="text-xs text-themed-muted mt-1">Games that have been downloaded multiple times</p>
                </div>
              );
            } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
              singleDownloadsHeaderRendered = true;
              header = (
                <div className="mb-4 mt-6 first:mt-0">
                  <h2 className="text-lg font-bold text-themed-primary border-b pb-2" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                    {labels.singleDownloads}
                  </h2>
                  <p className="text-xs text-themed-muted mt-1">Games downloaded once in a single session</p>
                </div>
              );
            }
          } else if (!isGroup && !individualHeaderRendered) {
            individualHeaderRendered = true;
            header = (
              <div className="mb-4 mt-6 first:mt-0">
                <h2 className="text-lg font-bold text-themed-primary border-b pb-2" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                  {labels.individual}
                </h2>
                <p className="text-xs text-themed-muted mt-1">Downloads that couldn't be grouped by game name</p>
              </div>
            );
          }
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
