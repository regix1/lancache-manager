import React from 'react';
import { ChevronRight, ExternalLink, ChevronLeft } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import { getServiceBadgeStyles } from '@utils/serviceColors';
import { Tooltip } from '@components/ui/Tooltip';
import { SteamIcon } from '@components/ui/SteamIcon';
import { useHoldTimer } from '@hooks/useHoldTimer';
import { useDownloadAssociations } from '@contexts/DownloadAssociationsContext';
import DownloadBadges from './DownloadBadges';
import type { Download, DownloadGroup } from '../../../types';

const API_BASE = '/api';

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
  showDatasourceLabels?: boolean;
  hasMultipleDatasources?: boolean;
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
  showDatasourceLabels: boolean;
  hasMultipleDatasources: boolean;
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
  enableScrollIntoView,
  showDatasourceLabels,
  hasMultipleDatasources
}) => {
  const { fetchAssociations, getAssociations } = useDownloadAssociations();
  const isExpanded = expandedItem === group.id;
  const rowRef = React.useRef<HTMLDivElement>(null);
  const prevExpandedRef = React.useRef<boolean>(false);

  // Fetch associations when group is expanded
  React.useEffect(() => {
    if (isExpanded) {
      const downloadIds = group.downloads.map(d => d.id);
      fetchAssociations(downloadIds);
    }
  }, [isExpanded, group.downloads, fetchAssociations]);

  React.useEffect(() => {
    if (!enableScrollIntoView) return;

    const wasExpanded = prevExpandedRef.current;
    prevExpandedRef.current = isExpanded;

    if (isExpanded && !wasExpanded && rowRef.current) {
      const timeoutId = setTimeout(() => {
        rowRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }, 250);
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
  const storeLink =
    showGameImage && primaryDownload?.gameAppId
      ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
      : null;

  return (
    <div
      ref={rowRef}
      className={`rounded-lg transition-colors duration-200 ${
        isExpanded
          ? 'bg-[var(--theme-bg-secondary)]'
          : 'hover:bg-[var(--theme-bg-tertiary)]/30'
      }`}
    >
      <button
        type="button"
        onClick={() => onItemClick(group.id)}
        className="w-full text-left px-3 py-3 focus:outline-none"
        style={{ WebkitTapHighlightColor: 'transparent' }}
      >
        {/* Get datasource from primary download */}
        {(() => {
          const primaryDatasource = group.downloads[0]?.datasource;
          const shouldShowDatasource = hasMultipleDatasources && showDatasourceLabels && primaryDatasource;

          return (
            <>
              {/* Mobile Layout */}
              <div className="sm:hidden">
                <div className="flex items-center gap-2 mb-1">
                  <ChevronRight
                    size={14}
                    className={`flex-shrink-0 text-[var(--theme-text-secondary)] transition-transform duration-200 ${
                      isExpanded ? 'rotate-90' : ''
                    }`}
                  />
                  <span
                    className="px-2 py-0.5 text-xs font-bold rounded flex-shrink-0"
                    style={getServiceBadgeStyles(group.service)}
                  >
                    {group.service.toUpperCase()}
                  </span>
                  {group.downloads.some((d: Download) => d.gameName && d.gameName !== 'Unknown Steam Game' && !d.gameName.match(/^Steam App \d+$/)) && (
                    <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate flex-1">
                      {group.name}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between pl-6 text-xs">
                  <div className="flex items-center gap-2">
                    <span className="text-themed-muted">
                      {group.clientsSet.size} client{group.clientsSet.size !== 1 ? 's' : ''} · {group.count} request{group.count !== 1 ? 's' : ''}
                    </span>
                    {shouldShowDatasource && (
                      <Tooltip content={`Datasource: ${primaryDatasource}`}>
                        <span
                          className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0"
                          style={{
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            color: 'var(--theme-text-secondary)',
                            border: '1px solid var(--theme-border-secondary)'
                          }}
                        >
                          {primaryDatasource}
                        </span>
                      </Tooltip>
                    )}
                  </div>
                  <div className="flex items-center gap-2">
                    <span className="font-semibold text-[var(--theme-text-primary)] font-mono">
                      {formatBytes(group.totalBytes)}
                    </span>
                    {group.cacheHitBytes > 0 ? (
                      <span className="cache-hit font-medium font-mono">
                        {formatPercent(hitPercent)}
                      </span>
                    ) : (
                      <span className="font-medium font-mono" style={{ color: 'var(--theme-error-text)' }}>
                        0%
                      </span>
                    )}
                  </div>
                </div>
              </div>

              {/* Desktop Layout */}
              <div className="hidden sm:flex items-center gap-3">
                <ChevronRight
                  size={14}
                  className={`flex-shrink-0 text-[var(--theme-text-secondary)] transition-transform duration-200 ${
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
                  {group.downloads.some((d: Download) => d.gameName && d.gameName !== 'Unknown Steam Game' && !d.gameName.match(/^Steam App \d+$/)) && (
                    <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {group.name}
                    </span>
                  )}
                  {shouldShowDatasource && (
                    <Tooltip content={`Datasource: ${primaryDatasource}`}>
                      <span
                        className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0"
                        style={{
                          backgroundColor: 'var(--theme-bg-tertiary)',
                          color: 'var(--theme-text-secondary)',
                          border: '1px solid var(--theme-border-secondary)'
                        }}
                      >
                        {primaryDatasource}
                      </span>
                    </Tooltip>
                  )}
                  <span className="text-xs text-themed-muted flex-shrink-0">
                    {group.clientsSet.size} client{group.clientsSet.size !== 1 ? 's' : ''} · {group.count} request{group.count !== 1 ? 's' : ''}
                  </span>
                </div>
                <div className="flex items-center gap-3 flex-shrink-0">
                  <span className="text-sm font-semibold text-[var(--theme-text-primary)] font-mono text-right min-w-[70px]">
                    {formatBytes(group.totalBytes)}
                  </span>
                  {group.cacheHitBytes > 0 ? (
                    <span className="cache-hit font-medium text-xs font-mono text-right min-w-[45px]">
                      {formatPercent(hitPercent)}
                    </span>
                  ) : (
                    <span
                      className="font-medium text-xs font-mono text-right min-w-[45px]"
                      style={{ color: 'var(--theme-error-text)' }}
                    >
                      0%
                    </span>
                  )}
                </div>
              </div>
            </>
          );
        })()}
      </button>

      {isExpanded && (
        <div
          className="px-3 pb-4"
          onClick={(event) => event.stopPropagation()}
        >
          {/* Compact layout: stacked on mobile, side-by-side on desktop */}
          <div className="flex flex-col sm:flex-row gap-3">
            {/* Game image */}
            {showGameImage && primaryDownload?.gameAppId && (
              <div className="flex-shrink-0">
                {aestheticMode || imageErrors.has(String(primaryDownload.gameAppId)) ? (
                  <div
                    className="w-full sm:w-[120px] h-[60px] sm:h-[56px] rounded border flex items-center justify-center"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      borderColor: 'var(--theme-border-primary)'
                    }}
                  >
                    <SteamIcon size={28} style={{ color: 'var(--theme-steam)', opacity: 0.6 }} />
                  </div>
                ) : (
                  <img
                    src={`${API_BASE}/game-images/${primaryDownload.gameAppId}/header/`}
                    alt={primaryDownload.gameName || group.name}
                    className="w-full sm:w-[120px] h-[60px] sm:h-[56px] rounded object-cover"
                    loading="lazy"
                    onError={() => handleImageError(String(primaryDownload.gameAppId))}
                  />
                )}
              </div>
            )}

            {/* Stats and info */}
            <div className="flex-1 min-w-0">
              {/* Stats - grid on mobile, flex on desktop */}
              <div className="grid grid-cols-2 sm:flex sm:flex-wrap items-center gap-x-3 gap-y-2 text-xs text-themed-muted mb-2">
                <span>
                  <span className="text-themed-secondary">Hit:</span>{' '}
                  <span className="text-[var(--theme-text-primary)]">
                    {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '0 B'}
                  </span>
                </span>
                <span>
                  <span className="text-themed-secondary">Miss:</span>{' '}
                  <span className="text-[var(--theme-text-primary)]">
                    {formatBytes(group.cacheMissBytes || 0)}
                  </span>
                </span>
                <span>
                  <span className="text-themed-secondary">First:</span>{' '}
                  {formatRelativeTime(group.firstSeen)}
                </span>
                <span>
                  <span className="text-themed-secondary">Last:</span>{' '}
                  {formatRelativeTime(group.lastSeen)}
                </span>
                {storeLink && (
                  <a
                    href={storeLink}
                    target="_blank"
                    rel="noopener noreferrer"
                    onClick={(event) => event.stopPropagation()}
                    className="inline-flex items-center gap-1 text-[var(--theme-primary)] hover:text-[var(--theme-primary-hover)] transition-colors col-span-2 sm:col-span-1"
                    title="View in Steam Store"
                  >
                    <ExternalLink size={11} />
                    <span>Store</span>
                  </a>
                )}
              </div>

              {/* Sessions list */}
              {(() => {
                const currentPage = groupPages[group.id] || 1;
                const sortedDownloads = group.downloads.sort(
                  (a, b) =>
                    new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
                );
                const totalPages = Math.ceil(sortedDownloads.length / SESSIONS_PER_PAGE);
                const startIndex = (currentPage - 1) * SESSIONS_PER_PAGE;
                const endIndex = startIndex + SESSIONS_PER_PAGE;
                const paginatedDownloads = sortedDownloads.slice(startIndex, endIndex);

                const handlePageChange = (newPage: number) => {
                  setGroupPages((prev) => ({ ...prev, [group.id]: newPage }));
                };

                const handlePointerHoldStart = (
                  event: React.PointerEvent<HTMLButtonElement>,
                  direction: 'prev' | 'next'
                ) => {
                  const isPrevious = direction === 'prev';
                  if (
                    (isPrevious && currentPage === 1) ||
                    (!isPrevious && currentPage === totalPages)
                  ) {
                    return;
                  }

                  event.currentTarget.setPointerCapture?.(event.pointerId);
                  startHoldTimer(() => {
                    setGroupPages((prev) => {
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
                  <div>
                    <div className="flex items-center justify-between mb-1">
                      <span className="text-xs text-themed-muted">
                        Sessions ({group.downloads.length})
                      </span>
                      {/* Inline pagination */}
                      {totalPages > 1 && (
                        <div className="flex items-center gap-1">
                          <Tooltip content="Previous page (hold to skip)">
                            <button
                              onClick={() => handlePageChange(currentPage - 1)}
                              onPointerDown={(event) => handlePointerHoldStart(event, 'prev')}
                              onPointerUp={handlePointerHoldEnd}
                              onPointerCancel={handlePointerHoldEnd}
                              onLostPointerCapture={stopHoldTimer}
                              disabled={currentPage === 1}
                              className="p-0.5 rounded transition-colors disabled:opacity-30 hover:bg-[var(--theme-bg-tertiary)]"
                            >
                              <ChevronLeft size={14} />
                            </button>
                          </Tooltip>
                          <span className="text-xs text-themed-muted font-mono min-w-[40px] text-center">
                            {currentPage}/{totalPages}
                          </span>
                          <Tooltip content="Next page (hold to skip)">
                            <button
                              onClick={() => handlePageChange(currentPage + 1)}
                              onPointerDown={(event) => handlePointerHoldStart(event, 'next')}
                              onPointerUp={handlePointerHoldEnd}
                              onPointerCancel={handlePointerHoldEnd}
                              onLostPointerCapture={stopHoldTimer}
                              disabled={currentPage === totalPages}
                              className="p-0.5 rounded transition-colors disabled:opacity-30 hover:bg-[var(--theme-bg-tertiary)]"
                            >
                              <ChevronRight size={14} />
                            </button>
                          </Tooltip>
                        </div>
                      )}
                    </div>

                    {/* Sessions table */}
                    <div className="rounded border overflow-hidden" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                      {paginatedDownloads.map((download, idx) => {
                        const totalBytes = download.totalBytes || 0;
                        const cachePercent =
                          totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;
                        const associations = getAssociations(download.id);

                        return (
                          <div
                            key={download.id}
                            className={`text-xs px-2 py-1.5 ${
                              idx % 2 === 0 ? 'bg-[var(--theme-bg-tertiary)]/30' : ''
                            }`}
                          >
                            {/* Mobile: Stacked layout */}
                            <div className="sm:hidden">
                              <div className="flex items-center justify-between">
                                <span className="font-mono text-[var(--theme-text-primary)]">
                                  {download.clientIp}
                                </span>
                                <div className="flex items-center gap-2">
                                  <span className="font-medium text-[var(--theme-text-primary)] font-mono">
                                    {formatBytes(totalBytes)}
                                  </span>
                                  {download.cacheHitBytes > 0 ? (
                                    <span className="cache-hit font-medium font-mono">
                                      {formatPercent(cachePercent)}
                                    </span>
                                  ) : (
                                    <span className="font-medium font-mono" style={{ color: 'var(--theme-error-text)' }}>
                                      0%
                                    </span>
                                  )}
                                </div>
                              </div>
                              <div className="flex items-center justify-between mt-1">
                                <div className="text-themed-muted">
                                  {formatRelativeTime(download.startTimeUtc)}
                                </div>
                                {(associations.tags.length > 0 || associations.events.length > 0) && (
                                  <DownloadBadges
                                    tags={associations.tags}
                                    events={associations.events}
                                    maxVisible={2}
                                    size="sm"
                                  />
                                )}
                              </div>
                            </div>

                            {/* Desktop: Single row */}
                            <div className="hidden sm:flex items-center justify-between">
                              <div className="flex items-center gap-3">
                                <span className="font-mono text-[var(--theme-text-primary)]">
                                  {download.clientIp}
                                </span>
                                <span className="text-themed-muted">
                                  {formatRelativeTime(download.startTimeUtc)}
                                </span>
                                {(associations.tags.length > 0 || associations.events.length > 0) && (
                                  <DownloadBadges
                                    tags={associations.tags}
                                    events={associations.events}
                                    maxVisible={3}
                                    size="sm"
                                  />
                                )}
                              </div>
                              <div className="flex items-center gap-3">
                                <span className="font-medium text-[var(--theme-text-primary)] font-mono text-right min-w-[65px]">
                                  {formatBytes(totalBytes)}
                                </span>
                                {download.cacheHitBytes > 0 ? (
                                  <span className="cache-hit font-medium font-mono text-right min-w-[40px]">
                                    {formatPercent(cachePercent)}
                                  </span>
                                ) : (
                                  <span
                                    className="font-medium font-mono text-right min-w-[40px]"
                                    style={{ color: 'var(--theme-error-text)' }}
                                  >
                                    0%
                                  </span>
                                )}
                              </div>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  </div>
                );
              })()}
            </div>
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
  enableScrollIntoView = true,
  showDatasourceLabels = true,
  hasMultipleDatasources = false
}) => {
  const labels = { ...DEFAULT_SECTION_LABELS, ...sectionLabels };
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());
  const [groupPages, setGroupPages] = React.useState<Record<string, number>>({});
  const { startHoldTimer, stopHoldTimer } = useHoldTimer();

  const SESSIONS_PER_PAGE = 10;

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
  };

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
      showDatasourceLabels={showDatasourceLabels}
      hasMultipleDatasources={hasMultipleDatasources}
    />
  );

  const renderDownloadRow = (download: Download) => {
    const totalBytes = download.totalBytes || 0;

    const fakeGroup = {
      id: `individual-${download.id}`,
      name: download.gameName || download.service,
      type: 'game' as const,
      service: download.service,
      downloads: [download],
      totalBytes: totalBytes,
      totalDownloaded: totalBytes,
      cacheHitBytes: download.cacheHitBytes || 0,
      cacheMissBytes: download.cacheMissBytes || 0,
      clientsSet: new Set([download.clientIp]),
      firstSeen: download.startTimeUtc,
      lastSeen: download.startTimeUtc,
      count: 1
    };

    return renderGroupRow(fakeGroup);
  };

  let multipleDownloadsHeaderRendered = false;
  let singleDownloadsHeaderRendered = false;
  let individualHeaderRendered = false;

  return (
    <div className="space-y-1">
      <div className="px-3 py-2 text-sm font-semibold text-themed-primary">
        Downloads Overview
      </div>
      <div>
        {items.map((item) => {
          const isGroup = 'downloads' in item;
          const key = isGroup ? (item as DownloadGroup).id : `download-${(item as Download).id}`;
          let header: React.ReactNode = null;

          if (groupByFrequency) {
            if (isGroup) {
              const group = item as DownloadGroup;
              if (group.count > 1 && !multipleDownloadsHeaderRendered) {
                multipleDownloadsHeaderRendered = true;
                header = (
                  <div className="px-3 py-1.5 mt-2 mb-1">
                    <div className="text-xs font-semibold text-themed-muted uppercase tracking-wide">
                      {labels.multipleDownloads}
                    </div>
                  </div>
                );
              } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
                singleDownloadsHeaderRendered = true;
                header = (
                  <div className="px-3 py-1.5 mt-2 mb-1">
                    <div className="text-xs font-semibold text-themed-muted uppercase tracking-wide">
                      {labels.singleDownloads}
                    </div>
                  </div>
                );
              }
            } else if (!isGroup && !individualHeaderRendered) {
              individualHeaderRendered = true;
              header = (
                <div className="px-3 py-1.5 mt-2 mb-1">
                  <div className="text-xs font-semibold text-themed-muted uppercase tracking-wide">
                    {labels.individual}
                  </div>
                </div>
              );
            }
          }

          return (
            <React.Fragment key={key}>
              {header}
              {isGroup
                ? renderGroupRow(item as DownloadGroup)
                : renderDownloadRow(item as Download)}
            </React.Fragment>
          );
        })}
      </div>
    </div>
  );
};

export default CompactView;
