import React from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronRight, ExternalLink, ChevronLeft } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import { getServiceBadgeStyles } from '@utils/serviceColors';
import { Tooltip } from '@components/ui/Tooltip';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { SteamIcon } from '@components/ui/SteamIcon';
import { GameImage } from '@components/common/GameImage';
import { useHoldTimer } from '@hooks/useHoldTimer';
import { useDownloadAssociations } from '@contexts/DownloadAssociationsContext';
import DownloadBadges from './DownloadBadges';
import type { Download, DownloadGroup } from '../../../types';

interface CompactViewSectionLabels {
  multipleDownloads: string;
  singleDownloads: string;
  individual: string;
  banner: string;
  downloadList: string;
}

const getDefaultSectionLabels = (t: (key: string, options?: Record<string, unknown>) => string): CompactViewSectionLabels => ({
  multipleDownloads: t('downloads.tab.compact.sections.multipleDownloads'),
  singleDownloads: t('downloads.tab.compact.sections.singleDownloads'),
  individual: t('downloads.tab.compact.sections.individual'),
  banner: t('downloads.tab.compact.sections.banner'),
  downloadList: t('downloads.tab.compact.sections.downloadList')
});

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
  const { t } = useTranslation();
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const isExpanded = expandedItem === group.id;
  const rowRef = React.useRef<HTMLDivElement>(null);
  const prevExpandedRef = React.useRef<boolean>(false);

  // Fetch associations when group is expanded
  // refreshVersion triggers re-fetch when cache is invalidated (e.g., DownloadTagged event)
  React.useEffect(() => {
    if (isExpanded) {
      const downloadIds = group.downloads.map(d => d.id);
      fetchAssociations(downloadIds);
    }
  }, [isExpanded, group.downloads, fetchAssociations, refreshVersion]);

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
      className={`rounded-lg border ${
        isExpanded
          ? 'bg-[var(--theme-bg-secondary)] border-[var(--theme-primary)]'
          : 'hover:bg-[var(--theme-bg-tertiary)] border-transparent'
      }`}
    >
      <button
        type="button"
        onClick={() => onItemClick(group.id)}
        className="w-full text-left px-3 py-3 focus:outline-none [-webkit-tap-highlight-color:transparent]"
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
                      {t('downloads.tab.compact.counts.clients', { count: group.clientsSet.size })}{' '}
                      · {t('downloads.tab.compact.counts.requests', { count: group.count })}
                    </span>
                    {shouldShowDatasource && (
                      <Tooltip content={t('downloads.tab.compact.datasourceTooltip', { datasource: primaryDatasource })}>
                        <span
                          className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]"
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
                      <span className="font-medium font-mono text-[var(--theme-error-text)]">
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
                    <Tooltip content={t('downloads.tab.compact.datasourceTooltip', { datasource: primaryDatasource })}>
                      <span
                        className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]"
                      >
                        {primaryDatasource}
                      </span>
                    </Tooltip>
                  )}
                  <span className="text-xs text-themed-muted flex-shrink-0">
                    {t('downloads.tab.compact.counts.clients', { count: group.clientsSet.size })}{' '}
                    · {t('downloads.tab.compact.counts.requests', { count: group.count })}
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
                      className="font-medium text-xs font-mono text-right min-w-[45px] text-[var(--theme-error-text)]"
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
          className="px-3 pb-3 pt-2 border-t border-[var(--theme-border-secondary)]"
          onClick={(event) => event.stopPropagation()}
        >
          {/* Compact layout: stacked on mobile, side-by-side on desktop */}
          <div className="flex flex-col sm:flex-row gap-3">
            {/* Game image */}
            {showGameImage && primaryDownload?.gameAppId && (
              <div className="flex-shrink-0">
                {aestheticMode || imageErrors.has(String(primaryDownload.gameAppId)) ? (
                  <div
                    className="compact-expanded-banner sm:w-[100px] sm:h-[46px] rounded border flex items-center justify-center bg-[var(--theme-bg-tertiary)] border-[var(--theme-border-secondary)]"
                  >
                    <SteamIcon size={24} className="text-[var(--theme-steam)] opacity-60" />
                  </div>
                ) : (
                  <GameImage
                    gameAppId={primaryDownload.gameAppId}
                    alt={primaryDownload.gameName || group.name}
                    className="compact-expanded-banner sm:w-[100px] sm:h-[46px] rounded object-cover border border-[var(--theme-border-secondary)]"
                    sizes="(max-width: 639px) 100%, 100px"
                    onFinalError={handleImageError}
                  />
                )}
              </div>
            )}

            {/* Stats and info */}
            <div className="flex-1 min-w-0">
              {/* Compact stats row */}
              <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs mb-2">
                <div className="flex items-center gap-1.5">
                  <span className="text-[var(--theme-text-muted)]">{t('downloads.tab.compact.labels.hit')}</span>
                  <span className="font-semibold text-[var(--theme-success-text)]">
                    {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '—'}
                  </span>
                </div>
                <div className="flex items-center gap-1.5">
                  <span className="text-[var(--theme-text-muted)]">{t('downloads.tab.compact.labels.miss')}</span>
                  <span className="font-medium text-[var(--theme-text-secondary)]">
                    {formatBytes(group.cacheMissBytes || 0)}
                  </span>
                </div>
                <div className="flex items-center gap-1.5">
                  <span className="text-[var(--theme-text-muted)]">{t('downloads.tab.compact.labels.last')}</span>
                  <span className="text-[var(--theme-text-secondary)]">{formatRelativeTime(group.lastSeen)}</span>
                </div>
                {storeLink && (
                  <a
                    href={storeLink}
                    target="_blank"
                    rel="noopener noreferrer"
                    onClick={(event) => event.stopPropagation()}
                    className="inline-flex items-center gap-1 text-[var(--theme-primary)] hover:underline transition-colors"
                  >
                    <ExternalLink size={10} />
                    <span>{t('downloads.tab.compact.labels.store')}</span>
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
                const excludedSessions = Math.max(0, sortedDownloads.length - group.count);

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
                    <div className="flex items-center justify-between mb-1.5">
                      <span className="text-[10px] uppercase tracking-wide font-semibold text-[var(--theme-text-muted)]">
                        {t('downloads.tab.compact.labels.sessions', { count: group.count })}
                        {excludedSessions > 0 && (
                          <span className="ml-1 opacity-60">
                            ({t('downloads.tab.compact.labels.excluded', { count: excludedSessions })})
                          </span>
                        )}
                      </span>
                      {/* Inline pagination */}
                      {totalPages > 1 && (
                        <div className="flex items-center gap-0.5">
                          <Tooltip content={t('downloads.tab.compact.pagination.previous')}>
                            <button
                              onClick={() => handlePageChange(currentPage - 1)}
                              onPointerDown={(event) => handlePointerHoldStart(event, 'prev')}
                              onPointerUp={handlePointerHoldEnd}
                              onPointerCancel={handlePointerHoldEnd}
                              onLostPointerCapture={stopHoldTimer}
                              disabled={currentPage === 1}
                              className="p-0.5 rounded transition-colors disabled:opacity-30 hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]"
                            >
                              <ChevronLeft size={12} />
                            </button>
                          </Tooltip>
                          <span className="text-[10px] text-[var(--theme-text-muted)] font-mono min-w-[28px] text-center">
                            {currentPage}/{totalPages}
                          </span>
                          <Tooltip content={t('downloads.tab.compact.pagination.next')}>
                            <button
                              onClick={() => handlePageChange(currentPage + 1)}
                              onPointerDown={(event) => handlePointerHoldStart(event, 'next')}
                              onPointerUp={handlePointerHoldEnd}
                              onPointerCancel={handlePointerHoldEnd}
                              onLostPointerCapture={stopHoldTimer}
                              disabled={currentPage === totalPages}
                              className="p-0.5 rounded transition-colors disabled:opacity-30 hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]"
                            >
                              <ChevronRight size={12} />
                            </button>
                          </Tooltip>
                        </div>
                      )}
                    </div>

                    {/* Sessions table */}
                    <div className="rounded-md border border-[var(--theme-border-secondary)] overflow-hidden divide-y divide-[var(--theme-border-secondary)]">
                      {paginatedDownloads.map((download) => {
                        const totalBytes = download.totalBytes || 0;
                        const cachePercent =
                          totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;
                        const associations = getAssociations(download.id);

                        return (
                          <div
                            key={download.id}
                            className="text-xs px-2.5 py-2 hover:bg-[var(--theme-bg-tertiary)] transition-colors"
                          >
                            {/* Mobile: Stacked layout */}
                            <div className="sm:hidden">
                              <div className="flex items-center justify-between">
                                <ClientIpDisplay
                                  clientIp={download.clientIp}
                                  className="font-mono text-[var(--theme-text-primary)] text-[11px]"
                                />
                                <div className="flex items-center gap-2">
                                  <span className="font-semibold text-[var(--theme-text-primary)] font-mono">
                                    {formatBytes(totalBytes)}
                                  </span>
                                  {download.cacheHitBytes > 0 ? (
                                    <span className="font-semibold font-mono text-[var(--theme-success-text)]">
                                      {formatPercent(cachePercent)}
                                    </span>
                                  ) : (
                                    <span className="font-medium font-mono text-[var(--theme-text-muted)]">
                                      —
                                    </span>
                                  )}
                                </div>
                              </div>
                              <div className="flex items-center justify-between mt-1">
                                <span className="text-[var(--theme-text-muted)]">
                                  {formatRelativeTime(download.startTimeUtc)}
                                </span>
                                {associations.events.length > 0 && (
                                  <DownloadBadges
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
                                <ClientIpDisplay
                                  clientIp={download.clientIp}
                                  className="font-mono text-[var(--theme-text-primary)] text-[11px]"
                                />
                                <span className="text-[var(--theme-text-muted)]">
                                  {formatRelativeTime(download.startTimeUtc)}
                                </span>
                                {associations.events.length > 0 && (
                                  <DownloadBadges
                                    events={associations.events}
                                    maxVisible={3}
                                    size="sm"
                                  />
                                )}
                              </div>
                              <div className="flex items-center gap-4">
                                <span className="font-semibold text-[var(--theme-text-primary)] font-mono text-right min-w-[60px]">
                                  {formatBytes(totalBytes)}
                                </span>
                                {download.cacheHitBytes > 0 ? (
                                  <span className="font-semibold font-mono text-right min-w-[36px] text-[var(--theme-success-text)]">
                                    {formatPercent(cachePercent)}
                                  </span>
                                ) : (
                                  <span className="font-medium font-mono text-right min-w-[36px] text-[var(--theme-text-muted)]">
                                    —
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
  const { t } = useTranslation();
  const labels = { ...getDefaultSectionLabels(t), ...sectionLabels };
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
