import React from 'react';
import { ChevronRight, ExternalLink, ChevronLeft } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import { Tooltip } from '@components/common/Tooltip';
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

interface CompactViewSectionLabels {
  multipleDownloads: string;
  singleDownloads: string;
  individual: string;
  banner: string;
  downloadList: string;
}

const DEFAULT_SECTION_LABELS: CompactViewSectionLabels = {
  multipleDownloads: 'Frequently Downloaded Games (2+ sessions)',
  singleDownloads: 'Single Session Downloads',
  individual: 'Uncategorized Downloads',
  banner: 'Game Banner',
  downloadList: 'Download Sessions'
};

interface CompactViewProps {
  items: (Download | DownloadGroup)[];
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  sectionLabels?: CompactViewSectionLabels;
  aestheticMode?: boolean;
  groupByFrequency?: boolean;
  enableScrollIntoView?: boolean;
}

interface GroupRowProps {
  group: DownloadGroup;
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  aestheticMode: boolean;
  imageErrors: Set<string>;
  handleImageError: (gameAppId: string) => void;
  groupPages: Record<string, number>;
  setGroupPages: React.Dispatch<React.SetStateAction<Record<string, number>>>;
  startHoldTimer: (callback: () => void) => void;
  stopHoldTimer: () => void;
  SESSIONS_PER_PAGE: number;
  labels: CompactViewSectionLabels;
  enableScrollIntoView: boolean;
}

const GroupRow: React.FC<GroupRowProps> = ({
  group,
  expandedItem,
  onItemClick,
  aestheticMode,
  imageErrors,
  handleImageError,
  groupPages,
  setGroupPages,
  startHoldTimer,
  stopHoldTimer,
  SESSIONS_PER_PAGE,
  labels,
  enableScrollIntoView
}) => {
  const isExpanded = expandedItem === group.id;
  const rowRef = React.useRef<HTMLDivElement>(null);
  const prevExpandedRef = React.useRef<boolean>(false);

  React.useEffect(() => {
    if (!enableScrollIntoView) return;

    const wasExpanded = prevExpandedRef.current;
    prevExpandedRef.current = isExpanded;

    if (isExpanded && !wasExpanded && rowRef.current) {
      const timeoutId = setTimeout(() => {
        rowRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }, 450);
      return () => clearTimeout(timeoutId);
    }
  }, [isExpanded, enableScrollIntoView]);

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
      ref={rowRef}
      className={`${
        isExpanded
          ? 'bg-[var(--theme-bg-tertiary)]/10 shadow-sm'
          : 'hover:bg-[var(--theme-bg-tertiary)]/5'
      }`}
      style={{
        animation: 'gentleFadeIn 0.3s ease-out',
        transition: 'background-color 0.4s cubic-bezier(0.4, 0, 0.2, 1), box-shadow 0.4s cubic-bezier(0.4, 0, 0.2, 1)',
      }}
    >
      <button
        type="button"
        onClick={() => onItemClick(group.id)}
        className="w-full text-left px-3 py-2 flex items-center gap-3 no-click-outline transition-all duration-200 focus:outline-none focus:ring-0 focus:border-transparent focus-visible:outline-none focus-visible:ring-0 active:outline-none active:ring-0"
        style={{
          WebkitTapHighlightColor: 'transparent',
          outline: 'none !important',
          boxShadow: 'none !important'
        }}
      >
        <ChevronRight
          size={14}
          className={`flex-shrink-0 text-[var(--theme-text-secondary)] transition-transform duration-300 ease-in-out ${
            isExpanded ? 'rotate-90' : ''
          }`}
        />
        <div className="flex items-center gap-2 min-w-0 flex-1">
          <span
            className="px-2 py-0.5 text-xs font-bold rounded flex-shrink-0"
            style={getServiceBadgeStyles(group.service)}
          >
            {group.service.toUpperCase()}
          </span>
          <span className="text-sm sm:text-base font-medium text-[var(--theme-text-primary)] truncate">
            {group.name}
          </span>
          <span className="hidden sm:inline text-sm text-themed-muted flex-shrink-0">
            {group.count} download{group.count !== 1 ? 's' : ''}
          </span>
        </div>
        <div className="flex items-center gap-2 sm:gap-4 flex-shrink-0">
          <span className="text-sm sm:text-base font-semibold text-[var(--theme-text-primary)] font-mono text-right min-w-[60px] sm:min-w-[80px]">
            {formatBytes(group.totalBytes)}
          </span>
          {group.cacheHitBytes > 0 ? (
            <span className="cache-hit font-medium text-xs sm:text-sm font-mono text-right min-w-[40px] sm:min-w-[50px]">
              {formatPercent(hitPercent)}
            </span>
          ) : (
            <span className="font-medium text-xs sm:text-sm font-mono text-right min-w-[40px] sm:min-w-[50px]" style={{ color: 'var(--theme-error-text)' }}>
              0%
            </span>
          )}
          <span className="hidden sm:inline text-sm text-themed-muted font-mono text-right min-w-[70px]">
            {group.clientsSet.size} clients
          </span>
        </div>
      </button>

      {isExpanded && (
        <div
          className="px-4 pb-3 pt-2 border-t overflow-hidden"
          style={{
            borderColor: 'var(--theme-border-secondary)',
            animation: 'expandDown 0.4s cubic-bezier(0.4, 0, 0.2, 1)',
            transformOrigin: 'top'
          }}
          onClick={(event) => event.stopPropagation()}
        >
          <div className="flex flex-col gap-3" style={{ animation: 'slideUp 0.35s cubic-bezier(0.4, 0, 0.2, 1) 0.08s both' }}>
            {showGameImage && primaryDownload?.gameAppId && (
              <div className="flex flex-col gap-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-themed-muted">
                  {aestheticMode || imageErrors.has(String(primaryDownload.gameAppId)) ? 'Game Icon' : labels.banner}
                </span>
                {aestheticMode || imageErrors.has(String(primaryDownload.gameAppId)) ? (
                  <div
                    className="w-full sm:w-[240px] h-[110px] sm:h-[120px] rounded-lg border flex items-center justify-center"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      borderColor: 'var(--theme-border-primary)'
                    }}
                  >
                    <SteamIcon
                      size={64}
                      style={{ color: 'var(--theme-steam)', opacity: 0.6 }}
                    />
                  </div>
                ) : (
                  <img
                    src={`${API_BASE}/gameimages/${primaryDownload.gameAppId}/header/`}
                    alt={primaryDownload.gameName || group.name}
                    className="w-full sm:w-[240px] h-[110px] sm:h-[120px] rounded-lg object-cover"
                    loading="lazy"
                    onError={() => handleImageError(String(primaryDownload.gameAppId))}
                  />
                )}
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
                  className="inline-flex items-center gap-1 text-xs px-2 py-1 rounded text-[var(--theme-primary)] hover:text-[var(--theme-primary-hover)] hover:bg-[var(--theme-primary)]/10 transition-all duration-200 font-medium border border-transparent hover:border-[var(--theme-primary)]/20"
                  title="View in Steam Store"
                >
                  <ExternalLink size={12} />
                  <span>Store</span>
                </a>
              )}
            </div>

            {(() => {
              const currentPage = groupPages[group.id] || 1;
              const sortedDownloads = group.downloads
                .sort((a, b) => new Date(b.startTimeLocal).getTime() - new Date(a.startTimeLocal).getTime());
              const totalPages = Math.ceil(sortedDownloads.length / SESSIONS_PER_PAGE);
              const startIndex = (currentPage - 1) * SESSIONS_PER_PAGE;
              const endIndex = startIndex + SESSIONS_PER_PAGE;
              const paginatedDownloads = sortedDownloads.slice(startIndex, endIndex);

              const handlePageChange = (newPage: number) => {
                setGroupPages(prev => ({ ...prev, [group.id]: newPage }));
              };

              const handlePointerHoldStart = (
                event: React.PointerEvent<HTMLButtonElement>,
                direction: 'prev' | 'next'
              ) => {
                const isPrevious = direction === 'prev';
                if ((isPrevious && currentPage === 1) || (!isPrevious && currentPage === totalPages)) {
                  return;
                }

                event.currentTarget.setPointerCapture?.(event.pointerId);
                startHoldTimer(() => {
                  setGroupPages(prev => {
                    const current = prev[group.id] || 1;
                    const nextPage = isPrevious
                      ? Math.max(1, current - 1)
                      : Math.min(totalPages, current + 1);
                    if (nextPage === current) {
                      return prev;
                    }
                    return { ...prev, [group.id]: nextPage };
                  });
                });
              };

              const handlePointerHoldEnd = (event: React.PointerEvent<HTMLButtonElement>) => {
                event.currentTarget.releasePointerCapture?.(event.pointerId);
                stopHoldTimer();
              };

              return (
                <div className="space-y-1">
                  <div className="text-xs text-themed-muted">
                    {labels.downloadList} ({group.downloads.length} total{totalPages > 1 && ` • Page ${currentPage}/${totalPages}`})
                  </div>
                  {/* Sessions list with min height to prevent layout shift */}
                  <div style={{ minHeight: '300px' }}>
                  {paginatedDownloads.map((download) => {
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
                            {formatRelativeTime(download.startTimeLocal)}
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

                  {/* Pagination Controls */}
                  {totalPages > 1 && (
                    <div className="flex items-center justify-center gap-2 pt-2 mt-2 border-t" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                      <Tooltip content="Previous page (hold to skip multiple)">
                        <button
                          onClick={() => handlePageChange(currentPage - 1)}
                          onPointerDown={(event) => handlePointerHoldStart(event, 'prev')}
                          onPointerUp={handlePointerHoldEnd}
                          onPointerCancel={handlePointerHoldEnd}
                          onLostPointerCapture={stopHoldTimer}
                          disabled={currentPage === 1}
                          className="p-1 rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)]/80"
                          style={{
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            color: 'var(--theme-text-primary)'
                          }}
                        >
                          <ChevronLeft size={12} />
                        </button>
                      </Tooltip>

                      <span className="text-xs text-[var(--theme-text-secondary)] font-medium font-mono px-2" style={{ minWidth: '45px', textAlign: 'center' }}>
                        {currentPage} / {totalPages}
                      </span>

                      <Tooltip content="Next page (hold to skip multiple)">
                        <button
                          onClick={() => handlePageChange(currentPage + 1)}
                          onPointerDown={(event) => handlePointerHoldStart(event, 'next')}
                          onPointerUp={handlePointerHoldEnd}
                          onPointerCancel={handlePointerHoldEnd}
                          onLostPointerCapture={stopHoldTimer}
                          disabled={currentPage === totalPages}
                          className="p-1 rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)]/80"
                          style={{
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            color: 'var(--theme-text-primary)'
                          }}
                        >
                          <ChevronRight size={12} />
                        </button>
                      </Tooltip>
                    </div>
                  )}
                </div>
              );
            })()}
          </div>
        </div>
      )}
    </div>
  );
};

const CompactView: React.FC<CompactViewProps> = ({
  items,
  expandedItem,
  onItemClick,
  sectionLabels,
  aestheticMode = false,
  groupByFrequency = true,
  enableScrollIntoView = true
}) => {
  const labels = { ...DEFAULT_SECTION_LABELS, ...sectionLabels };
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());
  const [groupPages, setGroupPages] = React.useState<Record<string, number>>({});
  const holdTimerRef = React.useRef<NodeJS.Timeout | null>(null);
  const holdTimeoutRef = React.useRef<NodeJS.Timeout | null>(null);

  const SESSIONS_PER_PAGE = 10;

  const handleImageError = (gameAppId: string) => {
    setImageErrors(prev => new Set(prev).add(gameAppId));
  };

  const stopHoldTimer = React.useCallback(() => {
    if (holdTimeoutRef.current) {
      clearTimeout(holdTimeoutRef.current);
      holdTimeoutRef.current = null;
    }
    if (holdTimerRef.current) {
      clearInterval(holdTimerRef.current);
      holdTimerRef.current = null;
    }
  }, []);

  const startHoldTimer = React.useCallback(
    (callback: () => void) => {
      stopHoldTimer();
      holdTimeoutRef.current = setTimeout(() => {
        holdTimerRef.current = setInterval(callback, 150);
      }, 400);
    },
    [stopHoldTimer]
  );

  React.useEffect(() => () => stopHoldTimer(), [stopHoldTimer]);

  const renderGroupRow = (group: DownloadGroup) => (
    <GroupRow
      group={group}
      expandedItem={expandedItem}
      onItemClick={onItemClick}
      aestheticMode={aestheticMode}
      imageErrors={imageErrors}
      handleImageError={handleImageError}
      groupPages={groupPages}
      setGroupPages={setGroupPages}
      startHoldTimer={startHoldTimer}
      stopHoldTimer={stopHoldTimer}
      SESSIONS_PER_PAGE={SESSIONS_PER_PAGE}
      labels={labels}
      enableScrollIntoView={enableScrollIntoView}
    />
  );

  const renderDownloadRow = (download: Download) => {
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

    return renderGroupRow(fakeGroup);
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

          // Only show section headers if groupByFrequency is enabled
          if (groupByFrequency) {
            if (isGroup) {
              const group = item as DownloadGroup;
              if (group.count > 1 && !multipleDownloadsHeaderRendered) {
                multipleDownloadsHeaderRendered = true;
                header = (
                  <div className="px-3 py-2 mt-2">
                    <div className="text-sm font-bold text-themed-primary uppercase tracking-wide">
                      {labels.multipleDownloads}
                    </div>
                    <div className="text-xs text-themed-muted mt-0.5">Games downloaded multiple times</div>
                  </div>
                );
              } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
                singleDownloadsHeaderRendered = true;
                header = (
                  <div className="px-3 py-2 mt-2">
                    <div className="text-sm font-bold text-themed-primary uppercase tracking-wide">
                      {labels.singleDownloads}
                    </div>
                    <div className="text-xs text-themed-muted mt-0.5">Games downloaded once</div>
                  </div>
                );
              }
            } else if (!isGroup && !individualHeaderRendered) {
              individualHeaderRendered = true;
              header = (
                <div className="px-3 py-2 mt-2">
                  <div className="text-sm font-bold text-themed-primary uppercase tracking-wide">
                    {labels.individual}
                  </div>
                  <div className="text-xs text-themed-muted mt-0.5">Ungrouped downloads</div>
                </div>
              );
            }
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
