import React from 'react';
import { ChevronRight, Clock, ExternalLink, CheckCircle, AlertCircle } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
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
  fullHeightBanners?: boolean;
  groupByFrequency?: boolean;
}

const NormalView: React.FC<NormalViewProps> = ({ items, expandedItem, onItemClick, sectionLabels, aestheticMode = false, fullHeightBanners = false, groupByFrequency = true }) => {
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
      firstSeen: download.startTimeLocal,
      lastSeen: download.startTimeLocal,
      count: 1
    };

    return renderGroupCard(fakeGroup);
  };

  const renderGroupCard = (group: DownloadGroup) => {
    const isExpanded = expandedItem === group.id;
    const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    const primaryDownload = group.downloads[0];
    const serviceLower = group.service.toLowerCase();
    const isSteam = serviceLower === 'steam';
    const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
    const isOtherService = !isSteam && !isWsus;
    const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
    const primaryName = primaryDownload?.gameName ?? '';
    const isGenericSteamTitle =
      primaryName === 'Unknown Steam Game' || /^Steam App \d+$/.test(primaryName);
    const showGameImage =
      group.type === 'game' && isSteam && Boolean(steamAppId) && !!primaryName && !isGenericSteamTitle;
    const storeLink = primaryDownload?.gameAppId
      ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
      : null;
    const shouldRenderBanner = !aestheticMode && (isSteam || isWsus || isOtherService);
    const hasSteamArtwork =
      showGameImage && steamAppId !== null && !imageErrors.has(steamAppId);
    const placeholderBaseClasses = 'min-h-[130px] sm:min-h-[130px]';
    const placeholderIconColor = isSteam
      ? 'var(--theme-steam)'
      : isWsus
      ? 'var(--theme-wsus)'
      : 'var(--theme-text-secondary)';
    const placeholderIconSize = fullHeightBanners ? 80 : 72;
    const bannerWrapperClasses = fullHeightBanners
      ? 'w-full h-[130px] sm:w-[280px] sm:h-[130px]'
      : 'w-full h-[130px] sm:w-[280px] sm:h-[130px] sm:self-start';

    let bannerContent: React.ReactNode | null = null;

    if (shouldRenderBanner) {
      if (hasSteamArtwork && steamAppId) {
        bannerContent = (
          <img
            src={`${API_BASE}/gameimages/${steamAppId}/header/`}
            alt={primaryName || group.name}
            className="h-full w-full object-cover transition-transform duration-300 hover:scale-105"
            loading="lazy"
            onError={() => handleImageError(steamAppId)}
          />
        );
      } else {
        bannerContent = (
          <div
            className={`flex h-full w-full flex-col items-center justify-center px-4 text-center ${placeholderBaseClasses}`}
          >
            {isSteam ? (
              <SteamIcon size={placeholderIconSize} style={{ color: placeholderIconColor, opacity: 0.75 }} />
            ) : isWsus ? (
              <WsusIcon size={placeholderIconSize} style={{ color: placeholderIconColor, opacity: 0.75 }} />
            ) : (
              <UnknownServiceIcon size={placeholderIconSize + 12} style={{ color: placeholderIconColor, opacity: 0.75 }} />
            )}
          </div>
        );
      }
    }

    const cardContent = (
      <div className={`flex flex-col ${fullHeightBanners ? 'sm:flex-row sm:items-stretch' : 'sm:flex-row'}`}>
        {bannerContent && (
          <div
            className={`flex-shrink-0 overflow-hidden ${bannerWrapperClasses}`}
          >
            {bannerContent}
          </div>
        )}
        <div
          className={`flex-1 ${
            fullHeightBanners
              ? 'px-3 pt-3 pb-1 sm:px-3 sm:pt-3 sm:pb-2'
              : 'px-4 pt-4 pb-2 sm:px-5 sm:pt-5 sm:pb-3'
          }`}
        >
          <div className="flex items-start gap-3 sm:gap-4">
            <ChevronRight
              size={18}
              className={`mt-0.5 sm:mt-1 text-[var(--theme-primary)] transition-all duration-300 flex-shrink-0 ${isExpanded ? 'rotate-90' : ''}`}
              style={{ opacity: isExpanded ? 1 : 0.6 }}
            />
            <div className="flex-1 min-w-0">
              {/* Title Row */}
              <div className={`flex flex-col sm:flex-row sm:items-center gap-2 ${fullHeightBanners ? 'sm:gap-2 mb-1.5 sm:mb-2' : 'sm:gap-3 mb-2 sm:mb-3'}`}>
                <div className="flex items-center gap-2">
                  <span
                    className={`${fullHeightBanners ? 'px-1.5 py-0.5 text-xs' : 'px-2 sm:px-2.5 py-0.5 sm:py-1 text-xs'} font-extrabold rounded-md shadow-sm`}
                    style={getServiceBadgeStyles(group.service)}
                  >
                    {group.service.toUpperCase()}
                  </span>
                  {group.count > 1 && (
                    <span className={`${fullHeightBanners ? 'px-1.5 py-0.5 text-xs' : 'px-2 sm:px-2.5 py-0.5 sm:py-1 text-xs'} font-semibold rounded-full bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]`}>
                      {group.count}× downloads
                    </span>
                  )}
                </div>
                <h3 className={`${fullHeightBanners ? 'text-base sm:text-lg' : 'text-lg sm:text-xl'} font-bold text-[var(--theme-text-primary)] truncate flex-1`}>
                  {group.name}
                </h3>
              </div>

              {/* Stats Grid - Better aligned */}
              <div className={`grid grid-cols-1 sm:grid-cols-2 ${fullHeightBanners ? 'gap-x-4 gap-y-1' : 'gap-x-8 gap-y-1.5 sm:gap-y-2'}`}>
                <div className="flex items-baseline gap-2">
                  <span className={`${fullHeightBanners ? 'text-xs' : 'text-xs sm:text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[70px] sm:min-w-[80px]'}`}>Total Downloaded</span>
                  <span className={`${fullHeightBanners ? 'text-xs sm:text-sm' : 'text-sm sm:text-base'} font-bold text-[var(--theme-text-primary)]`}>
                    {formatBytes(group.totalBytes)}
                  </span>
                </div>
                <div className="flex items-baseline gap-2">
                  <span className={`${fullHeightBanners ? 'text-xs' : 'text-xs sm:text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[70px] sm:min-w-[80px]'}`}>Clients</span>
                  <span className={`${fullHeightBanners ? 'text-xs sm:text-sm' : 'text-sm sm:text-base'} font-bold text-[var(--theme-text-primary)]`}>
                    {group.clientsSet.size}
                  </span>
                </div>
                <div className="flex items-baseline gap-2">
                  <span className={`${fullHeightBanners ? 'text-xs' : 'text-xs sm:text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[70px] sm:min-w-[80px]'}`}>Cache Saved</span>
                  <span className={`${fullHeightBanners ? 'text-xs sm:text-sm' : 'text-sm sm:text-base'} font-bold text-[var(--theme-success-text)]`}>
                    {formatBytes(group.cacheHitBytes)}
                  </span>
                </div>
                <div className="flex items-baseline gap-2">
                  <span className={`${fullHeightBanners ? 'text-xs' : 'text-xs sm:text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[70px] sm:min-w-[80px]'}`}>Last Active</span>
                  <span className={`text-xs ${fullHeightBanners ? '' : 'sm:text-sm'} font-medium text-[var(--theme-text-secondary)] inline-flex items-center gap-1.5`}>
                    <Clock size={12} className={fullHeightBanners ? '' : 'sm:hidden'} />
                    {!fullHeightBanners && <Clock size={14} className="hidden sm:block" />}
                    {formatRelativeTime(group.lastSeen)}
                  </span>
                </div>
                <div className="flex items-baseline gap-2">
                  <span className={`${fullHeightBanners ? 'text-xs' : 'text-xs sm:text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[70px] sm:min-w-[80px]'}`}>Efficiency</span>
                  <span
                    className={`text-xs ${fullHeightBanners ? '' : 'sm:text-sm'} font-bold inline-flex items-center gap-1.5 ${
                      hitPercent > 0 ? 'cache-hit' : 'text-[var(--theme-text-secondary)]'
                    }`}
                  >
                    {hitPercent > 0 ? formatPercent(hitPercent) : 'N/A'}
                  </span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    );

    return (
      <div
        className={`rounded-lg border overflow-hidden shadow-sm transition-all duration-300 ${
          isExpanded ? 'ring-2' : 'hover:shadow-md'
        }`}
        style={{
          borderColor: isExpanded ? 'var(--theme-primary)' : 'var(--theme-border-primary)'
        }}
      >
        {fullHeightBanners ? (
          <div
            onClick={() => onItemClick(group.id)}
            className="w-full text-left cursor-pointer bg-[var(--theme-bg-secondary)] transition-all duration-300 hover:bg-[var(--theme-bg-tertiary)]/30"
          >
            {cardContent}
          </div>
        ) : (
          <button
            onClick={() => onItemClick(group.id)}
            className="w-full text-left transition-all duration-300 hover:bg-[var(--theme-bg-tertiary)]/30 bg-[var(--theme-bg-secondary)]"
          >
            {cardContent}
          </button>
        )}

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
                                  Started {formatRelativeTime(download.startTimeLocal)}
                                </span>
                                {download.endTimeLocal ? (
                                  <span className="flex items-center gap-1.5 text-[var(--theme-success-text)]">
                                    <CheckCircle size={12} />
                                    Completed {formatRelativeTime(download.endTimeLocal)}
                                  </span>
                                ) : (
                                  <span className="flex items-center gap-1.5 text-[var(--theme-info-text)]">
                                    <AlertCircle size={12} />
                                    In progress
                                  </span>
                                )}
                                {download.depotId && (
                                  <span className="flex items-center gap-1.5 text-[var(--theme-text-muted)] font-mono">
                                    Depot: {download.depotId}
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
