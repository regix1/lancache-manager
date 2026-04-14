import React, { useEffect, useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useTranslation } from 'react-i18next';
import './VirtualizedList.css';
import { ChevronRight, ExternalLink, HardDrive } from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime, formatCount } from '@utils/formatters';
import BadgesRow from './BadgesRow';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { SteamIcon } from '@components/ui/SteamIcon';
import { GameImage } from '@components/common/GameImage';
import EvictedBadge from '@components/common/EvictedBadge';
import { useHoldTimer } from '@hooks/useHoldTimer';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';
import { useGroupPagination } from '@hooks/useGroupPagination';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import DownloadBadges from './DownloadBadges';
import { Pagination } from '@components/ui/Pagination';
import { useSessionFilters } from './useSessionFilters';
import SessionFilterBar from './SessionFilterBar';
import { resolveGameDetection } from '@utils/gameDetection';
import type { Download, DownloadGroup, GameCacheInfo } from '../../../types';
import { useFlatRows } from '@hooks/useFlatRows';
import type { HeaderRowKind } from './types';

interface CompactViewSectionLabels {
  multipleDownloads: string;
  singleDownloads: string;
  individual: string;
  banner: string;
  downloadList: string;
}

const getDefaultSectionLabels = (
  t: (key: string, options?: Record<string, unknown>) => string
): CompactViewSectionLabels => ({
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
  detectionLookup?: Map<number, GameCacheInfo> | null;
  detectionByName?: Map<string, GameCacheInfo> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
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
  labels: CompactViewSectionLabels;
  enableScrollIntoView: boolean;
  showDatasourceLabels: boolean;
  hasMultipleDatasources: boolean;
  detectionLookup?: Map<number, GameCacheInfo> | null;
  detectionByName?: Map<string, GameCacheInfo> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
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
  enableScrollIntoView,
  showDatasourceLabels,
  hasMultipleDatasources,
  detectionLookup,
  detectionByName,
  detectionByService
}) => {
  const { t } = useTranslation();
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const isExpanded = expandedItem === group.id;
  const rowRef = React.useRef<HTMLDivElement>(null);
  const prevExpandedRef = React.useRef<boolean>(false);

  const {
    filters,
    updateFilter,
    resetFilters,
    filteredDownloads,
    uniqueIps,
    totalCount,
    filteredCount,
    hasActiveFilters
  } = useSessionFilters(group.downloads);
  const {
    currentPage: safePage,
    totalPages: totalFilteredPages,
    ipGroups,
    handlePageChange,
    handlePointerHoldStart,
    handlePointerHoldEnd
  } = useGroupPagination({
    filteredDownloads,
    sessionsPerPage: filters.sessionsPerPage,
    itemsPerSession: filters.itemsPerSession,
    groupId: group.id,
    groupPages,
    setGroupPages,
    startHoldTimer,
    stopHoldTimer
  });

  const [expandedIps, setExpandedIps] = React.useState<Record<string, boolean>>({});

  const toggleIp = (ip: string): void => {
    setExpandedIps((prev) => ({ ...prev, [ip]: !prev[ip] }));
  };

  const isIpExpanded = (ip: string, sessionCount: number): boolean => {
    if (ip in expandedIps) return expandedIps[ip];
    return sessionCount <= 5;
  };

  // Fetch associations when group is expanded
  // refreshVersion triggers re-fetch when cache is invalidated (e.g., DownloadTagged event)
  React.useEffect(() => {
    if (isExpanded) {
      const downloadIds = group.downloads.map((d) => d.id);
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

  const availableImages = useAvailableGameImages();
  const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
  const primaryDownload = group.downloads[0];
  const serviceLower = (group.service ?? '').toLowerCase();
  const isEpicService = serviceLower === 'epic' || serviceLower === 'epicgames';
  const showSteamImage =
    serviceLower === 'steam' && availableImages.has(String(primaryDownload?.gameAppId ?? ''));
  const showEpicImage = isEpicService && availableImages.has(primaryDownload?.epicAppId ?? '');
  const showGameImage = showSteamImage || showEpicImage;
  const gameImageAppId = showEpicImage ? primaryDownload?.epicAppId : primaryDownload?.gameAppId;
  const gameImageErrorKey = showEpicImage
    ? `epic-${primaryDownload?.epicAppId}`
    : String(primaryDownload?.gameAppId);
  const storeLink =
    showSteamImage && primaryDownload?.gameAppId
      ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
      : null;
  const isEvicted = group.downloads.every((d: Download) => d.isEvicted);
  const isPartiallyEvicted = !isEvicted && group.downloads.some((d: Download) => d.isEvicted);
  const detection = resolveGameDetection(
    primaryDownload?.gameAppId,
    primaryDownload?.gameName,
    detectionLookup,
    detectionByName,
    group.service,
    detectionByService
  );
  const diskSizeBytes = detection?.total_size_bytes;

  return (
    <div
      ref={rowRef}
      className={`rounded-lg border ${
        isExpanded
          ? 'bg-[var(--theme-bg-secondary)] border-[var(--theme-primary)]'
          : 'hover:bg-[var(--theme-bg-tertiary)] border-transparent'
      }${isEvicted ? ' opacity-60' : ''}`}
    >
      <button
        type="button"
        onClick={() => onItemClick(group.id)}
        className="w-full text-left px-3 py-3 focus:outline-none [-webkit-tap-highlight-color:transparent]"
      >
        {/* Get datasource from primary download */}
        {(() => {
          const primaryDatasource = group.downloads[0]?.datasource;
          const shouldShowDatasource =
            hasMultipleDatasources && showDatasourceLabels && primaryDatasource;

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
                  <BadgesRow
                    service={group.service}
                    datasource={primaryDatasource}
                    showDatasource={!!shouldShowDatasource}
                    isEvicted={isEvicted}
                    isPartiallyEvicted={isPartiallyEvicted}
                  />
                  {group.downloads.some(
                    (d: Download) =>
                      d.gameName && d.gameName !== d.service && !d.gameName.match(/^Steam App \d+$/)
                  ) && (
                    <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate flex-1">
                      {group.name}
                    </span>
                  )}
                  {diskSizeBytes ? (
                    <span className="text-themed-muted text-xs ml-2">
                      {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })}
                    </span>
                  ) : null}
                </div>
                <div className="flex flex-col gap-1 pl-6 text-xs">
                  {diskSizeBytes ? (
                    <div className="flex items-center gap-1 text-themed-muted">
                      <HardDrive size={10} className="flex-shrink-0" />
                      <span>
                        {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })}
                      </span>
                      {detection?.cache_files_found ? (
                        <span className="ml-1">
                          · {formatCount(detection.cache_files_found)} files
                        </span>
                      ) : null}
                    </div>
                  ) : null}
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <span className="text-themed-muted">
                        {t('downloads.tab.compact.counts.clients', {
                          count: group.clientsSet.size
                        })}{' '}
                        · {t('downloads.tab.compact.counts.requests', { count: group.count })}
                      </span>
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
                  <BadgesRow
                    service={group.service}
                    datasource={primaryDatasource}
                    showDatasource={!!shouldShowDatasource}
                    isEvicted={isEvicted}
                    isPartiallyEvicted={isPartiallyEvicted}
                  />
                  {group.downloads.some(
                    (d: Download) =>
                      d.gameName && d.gameName !== d.service && !d.gameName.match(/^Steam App \d+$/)
                  ) && (
                    <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {group.name}
                    </span>
                  )}
                  {diskSizeBytes ? (
                    <span className="text-themed-muted text-xs ml-2">
                      {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })}
                    </span>
                  ) : null}
                  <span className="text-xs text-themed-muted flex-shrink-0">
                    {t('downloads.tab.compact.counts.clients', { count: group.clientsSet.size })} ·{' '}
                    {t('downloads.tab.compact.counts.requests', { count: group.count })}
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
                    <span className="font-medium text-xs font-mono text-right min-w-[45px] text-[var(--theme-error-text)]">
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
            {showGameImage && gameImageAppId && (
              <div className="flex-shrink-0">
                {aestheticMode || imageErrors.has(gameImageErrorKey) ? (
                  <div className="compact-expanded-banner sm:w-[100px] sm:h-[46px] rounded border flex items-center justify-center bg-[var(--theme-bg-tertiary)] border-[var(--theme-border-secondary)]">
                    <SteamIcon size={24} className="text-[var(--theme-steam)] opacity-60" />
                  </div>
                ) : (
                  <GameImage
                    gameAppId={gameImageAppId}
                    epicAppId={showEpicImage ? primaryDownload.epicAppId! : undefined}
                    alt={primaryDownload.gameName || group.name}
                    className="compact-expanded-banner sm:w-[100px] sm:h-[46px] rounded object-cover border border-[var(--theme-border-secondary)]"
                    sizes="(max-width: 639px) 100%, 100px"
                    onError={handleImageError}
                  />
                )}
              </div>
            )}

            {/* Stats and info */}
            <div className="flex-1 min-w-0">
              {/* Compact stats row */}
              <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs mb-2">
                <div className="flex items-center gap-1.5">
                  <span className="text-[var(--theme-text-muted)]">
                    {t('downloads.tab.compact.labels.hit')}
                  </span>
                  <span className="font-semibold text-[var(--theme-success-text)]">
                    {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '—'}
                  </span>
                </div>
                <div className="flex items-center gap-1.5">
                  <span className="text-[var(--theme-text-muted)]">
                    {t('downloads.tab.compact.labels.miss')}
                  </span>
                  <span className="font-medium text-[var(--theme-text-secondary)]">
                    {formatBytes(group.cacheMissBytes || 0)}
                  </span>
                </div>
                <div className="flex items-center gap-1.5">
                  <span className="text-[var(--theme-text-muted)]">
                    {t('downloads.tab.compact.labels.last')}
                  </span>
                  <span className="text-[var(--theme-text-secondary)]">
                    {formatRelativeTime(group.lastSeen)}
                  </span>
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
                const excludedSessions = Math.max(0, group.downloads.length - group.count);

                return (
                  <div>
                    {/* Filter bar — only for groups with many sessions */}
                    {group.downloads.length > 10 && (
                      <div className="mb-2">
                        <SessionFilterBar
                          filters={filters}
                          updateFilter={updateFilter}
                          resetFilters={resetFilters}
                          uniqueIps={uniqueIps}
                          totalCount={totalCount}
                          filteredCount={filteredCount}
                          hasActiveFilters={hasActiveFilters}
                        />
                      </div>
                    )}

                    {/* Sessions header with count and pagination */}
                    <div className="flex flex-wrap items-center justify-between mb-1.5">
                      <span className="text-[10px] uppercase tracking-wide font-semibold text-[var(--theme-text-muted)]">
                        {t('downloads.tab.compact.labels.sessions', { count: group.count })}
                        {excludedSessions > 0 && (
                          <span className="ml-1 opacity-60">
                            (
                            {t('downloads.tab.compact.labels.excluded', {
                              count: excludedSessions
                            })}
                            )
                          </span>
                        )}
                      </span>
                      {/* Inline pagination */}
                      <Pagination
                        variant="inline"
                        showCard={false}
                        currentPage={safePage}
                        totalPages={totalFilteredPages}
                        onPageChange={handlePageChange}
                        holdToRepeat
                        onPointerHoldStart={handlePointerHoldStart}
                        onPointerHoldEnd={handlePointerHoldEnd}
                        onLostPointerCapture={stopHoldTimer}
                        previousLabel={t('downloads.tab.compact.pagination.previous')}
                        nextLabel={t('downloads.tab.compact.pagination.next')}
                      />
                    </div>

                    {/* Collapsible IP groups */}
                    <div className="rounded-md border border-[var(--theme-border-secondary)] overflow-hidden divide-y divide-[var(--theme-border-secondary)]">
                      {Object.entries(ipGroups).map(([ip, ipDownloads]) => {
                        const ipTotal = ipDownloads.reduce((s, d) => s + (d.totalBytes || 0), 0);
                        const ipCacheHit = ipDownloads.reduce(
                          (s, d) => s + (d.cacheHitBytes || 0),
                          0
                        );
                        const expanded = isIpExpanded(ip, ipDownloads.length);

                        return (
                          <div key={ip}>
                            {/* IP header — clickable to toggle */}
                            <button
                              type="button"
                              onClick={() => toggleIp(ip)}
                              className="w-full text-left text-xs px-2.5 py-1.5 flex items-center justify-between bg-[var(--theme-bg-tertiary)]"
                            >
                              <div className="flex items-center gap-2">
                                <ChevronRight
                                  size={10}
                                  className={`flex-shrink-0 text-[var(--theme-text-muted)] transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`}
                                />
                                <ClientIpDisplay
                                  clientIp={ip}
                                  className="font-mono text-[var(--theme-text-primary)] text-[11px]"
                                />
                                <span className="text-[10px] text-[var(--theme-text-muted)] bg-[var(--theme-bg-primary)] px-1 rounded">
                                  {ipDownloads.length}
                                </span>
                              </div>
                              <div className="flex items-center gap-3 text-[11px]">
                                <span className="font-medium text-[var(--theme-text-primary)] font-mono">
                                  {formatBytes(ipTotal)}
                                </span>
                                {ipCacheHit > 0 && (
                                  <span className="font-medium text-[var(--theme-success-text)] font-mono">
                                    {formatPercent((ipCacheHit / ipTotal) * 100)}
                                  </span>
                                )}
                              </div>
                            </button>

                            {/* Session rows — only when expanded */}
                            {expanded && (
                              <div className="divide-y divide-[var(--theme-border-secondary)]">
                                {ipDownloads.map((download) => {
                                  const totalBytes = download.totalBytes || 0;
                                  const cachePercent =
                                    totalBytes > 0
                                      ? ((download.cacheHitBytes || 0) / totalBytes) * 100
                                      : 0;
                                  const associations = getAssociations(download.id);

                                  return (
                                    <div
                                      key={download.id}
                                      className={`text-xs px-2.5 py-2 hover:bg-[var(--theme-bg-tertiary)] transition-colors${download.isEvicted ? ' opacity-60' : ''}`}
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
                                          <div className="flex items-center gap-1">
                                            {download.isEvicted && <EvictedBadge />}
                                            {associations.events.length > 0 && (
                                              <DownloadBadges
                                                events={associations.events}
                                                maxVisible={2}
                                                size="sm"
                                              />
                                            )}
                                          </div>
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
                                          {download.isEvicted && <EvictedBadge />}
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
                            )}
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

const CompactView = React.memo(function CompactView({
  items,
  expandedItem,
  onItemClick,
  sectionLabels,
  aestheticMode = false,
  groupByFrequency = true,
  enableScrollIntoView = true,
  showDatasourceLabels = true,
  hasMultipleDatasources = false,
  detectionLookup = null,
  detectionByName = null,
  detectionByService = null
}: CompactViewProps) {
  const { t } = useTranslation();
  const labels = { ...getDefaultSectionLabels(t), ...sectionLabels };
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());
  const [groupPages, setGroupPages] = React.useState<Record<string, number>>({});
  const { startHoldTimer, stopHoldTimer } = useHoldTimer();

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
      labels={labels}
      enableScrollIntoView={enableScrollIntoView}
      showDatasourceLabels={showDatasourceLabels}
      hasMultipleDatasources={hasMultipleDatasources}
      detectionLookup={detectionLookup}
      detectionByName={detectionByName}
      detectionByService={detectionByService}
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

  // Virtualization threshold: >200 rows. Section headers from `groupByFrequency`
  // are flattened into the same typed row array so the virtualizer can index
  // over them uniformly. Compact rows have roughly constant height (~48px);
  // variance from row expansion / header size is absorbed by `measureElement`.
  const VIRTUALIZATION_THRESHOLD = 200;

  const flatRows = useFlatRows({ items, groupByFrequency });

  const shouldVirtualize = flatRows.length > VIRTUALIZATION_THRESHOLD;
  const virtualParentRef = useRef<HTMLDivElement | null>(null);
  const rowVirtualizer = useVirtualizer({
    count: shouldVirtualize ? flatRows.length : 0,
    getScrollElement: () => virtualParentRef.current,
    estimateSize: (index) => (flatRows[index]?.kind === 'header' ? 36 : 48),
    overscan: 5,
    measureElement: (el) => el?.getBoundingClientRect().height ?? 48
  });

  // Reset virtualized scroll to top when filters/sort change the row set, preventing a stale offset.
  useEffect(() => {
    if (virtualParentRef.current) {
      virtualParentRef.current.scrollTop = 0;
    }
  }, [flatRows]);

  const renderSectionHeader = (variant: HeaderRowKind): React.ReactNode => {
    const text =
      variant === 'multiple'
        ? labels.multipleDownloads
        : variant === 'single'
          ? labels.singleDownloads
          : labels.individual;
    return (
      <div className="px-3 py-1.5 mt-2 mb-1">
        <div className="text-xs font-semibold text-themed-muted uppercase tracking-wide">
          {text}
        </div>
      </div>
    );
  };

  if (shouldVirtualize) {
    const virtualItems = rowVirtualizer.getVirtualItems();
    return (
      <div className="space-y-1">
        <div className="px-3 py-2 text-sm font-semibold text-themed-primary">
          Downloads Overview
        </div>
        <div ref={virtualParentRef} className="virtual-list-parent virtual-list-parent-compact">
          <div
            className="virtual-list-inner"
            style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
          >
            {virtualItems.map((virtualRow) => {
              const row = flatRows[virtualRow.index];
              if (!row) return null;
              return (
                <div
                  key={row.id}
                  data-index={virtualRow.index}
                  ref={rowVirtualizer.measureElement}
                  className="virtual-row"
                  style={{ transform: `translateY(${virtualRow.start}px)` }}
                >
                  {row.kind === 'header'
                    ? renderSectionHeader(row.variant)
                    : 'downloads' in row.item
                      ? renderGroupRow(row.item as DownloadGroup)
                      : renderDownloadRow(row.item as Download)}
                </div>
              );
            })}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <div className="px-3 py-2 text-sm font-semibold text-themed-primary">Downloads Overview</div>
      <div>
        {flatRows.map((row) => (
          <React.Fragment key={row.id}>
            {row.kind === 'header'
              ? renderSectionHeader(row.variant)
              : 'downloads' in row.item
                ? renderGroupRow(row.item as DownloadGroup)
                : renderDownloadRow(row.item as Download)}
          </React.Fragment>
        ))}
      </div>
    </div>
  );
});

export default CompactView;
