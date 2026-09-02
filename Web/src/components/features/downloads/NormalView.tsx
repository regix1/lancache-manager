import React, { memo, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useTranslation } from 'react-i18next';
import './VirtualizedList.css';
import Drawer from '@components/ui/Drawer';
import Badge from '@components/ui/Badge';
import { ChevronDown, ExternalLink } from 'lucide-react';
import { formatBytes, formatCount, formatPercent, formatRelativeTime } from '@utils/formatters';
import { getServiceBadgeStyles } from '@utils/serviceColors';
import { getServiceDisplayName, getServiceFilterKey } from '@utils/serviceDisplayName';
import BadgesRow from './BadgesRow';
import { DownloadTimestamp } from './DownloadTimestamp';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EAIcon } from '@components/ui/EAIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { Tooltip } from '@components/ui/Tooltip';
import { GameImage } from '@components/common/GameImage';
import { useHoldTimer } from '@hooks/useHoldTimer';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';
import { useImageErrors } from '@hooks/useImageErrors';
import { nameKeyedImageKey } from '@utils/gameBannerSlug';
import { useGroupPagination } from '@hooks/useGroupPagination';
import { useGroupDownloadAssociations } from '@hooks/useGroupDownloadAssociations';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import DownloadBadges from './DownloadBadges';
import { Pagination } from '@components/ui/Pagination';
import { BackToTopButton } from '@components/ui/BackToTopButton';
import IpDownloadGroup from './IpDownloadGroup';
import { useIpExpansion } from './useIpExpansion';
import { useSessionFilters } from './useSessionFilters';
import SessionFilterBar from './SessionFilterBar';
import { resolveGameDetection } from '@utils/gameDetection';
import type { DownloadAssociations } from '@contexts/DownloadAssociationsContext.types';
import type { Download, DownloadGroup, EventSummary, GameDetectionSummary } from '../../../types';
import { useFlatRows } from '@hooks/useFlatRows';
import type { HeaderRowKind } from './types';
import { cacheHitPercent, toGroup } from './downloadGrouping';

interface NormalViewSectionLabels {
  multipleDownloads: string;
  singleDownloads: string;
  individual: string;
}

const getDefaultSectionLabels = (
  t: (key: string, options?: Record<string, unknown>) => string
): NormalViewSectionLabels => ({
  multipleDownloads: t('downloads.tab.normal.sections.multipleDownloads'),
  singleDownloads: t('downloads.tab.normal.sections.singleDownloads'),
  individual: t('downloads.tab.normal.sections.individual')
});

/**
 * Unique events across every session in a group, in first-seen order. The card,
 * grid and drawer layouts all badge a group with the same aggregate, so they share
 * this one pass instead of each keeping its own copy.
 *
 * Takes the ids rather than the group's `downloads`: a collapsed group carries only its newest
 * session, so badging from those rows would drop every event tagged on the rest of the group.
 */
const collectGroupEvents = (
  downloadIds: number[],
  getAssociations: (downloadId: number) => DownloadAssociations
): EventSummary[] => {
  const eventsMap = new Map<number, EventSummary>();
  downloadIds.forEach((downloadId) => {
    getAssociations(downloadId).events.forEach((event) => {
      if (!eventsMap.has(event.id)) {
        eventsMap.set(event.id, { ...event });
      }
    });
  });
  return Array.from(eventsMap.values());
};

interface NormalViewProps {
  items: (Download | DownloadGroup)[];
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  sectionLabels?: NormalViewSectionLabels;
  aestheticMode?: boolean;
  fullHeightBanners?: boolean;
  groupByFrequency?: boolean;
  enableScrollIntoView?: boolean;
  showDatasourceLabels?: boolean;
  hasMultipleDatasources?: boolean;
  cardGridLayout?: boolean;
  cardSize?: 'small' | 'medium' | 'large';
  showCacheHitBar?: boolean;
  showEventBadges?: boolean;
  bannerOnly?: boolean;
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
}

interface GroupCardProps {
  group: DownloadGroup;
  expandedItem: string | null;
  onItemClick: (id: string) => void;
  aestheticMode: boolean;
  fullHeightBanners: boolean;
  imageErrors: Set<string>;
  handleImageError: (gameAppId: string) => void;
  groupPages: Record<string, number>;
  setGroupPages: React.Dispatch<React.SetStateAction<Record<string, number>>>;
  startHoldTimer: (callback: () => void) => void;
  stopHoldTimer: () => void;
  SESSIONS_PER_PAGE: number;
  enableScrollIntoView: boolean;
  showDatasourceLabels: boolean;
  hasMultipleDatasources: boolean;
  showCacheHitBar: boolean;
  showEventBadges: boolean;
  availableImages: Set<string>;
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
}

const GroupCard: React.FC<GroupCardProps> = ({
  group,
  expandedItem,
  onItemClick,
  aestheticMode,
  fullHeightBanners,
  imageErrors,
  handleImageError,
  groupPages,
  setGroupPages,
  startHoldTimer,
  stopHoldTimer,
  SESSIONS_PER_PAGE: _SESSIONS_PER_PAGE,
  enableScrollIntoView,
  showDatasourceLabels,
  hasMultipleDatasources,
  showCacheHitBar,
  showEventBadges,
  availableImages,
  detectionLookup,
  detectionByName,
  detectionByService
}) => {
  const { t } = useTranslation();
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  // Held here rather than in IpDownloadGroup: the collapsed card unmounts the region and
  // would throw away which client rows the user had opened.
  const { toggleIp, isIpExpanded } = useIpExpansion();
  const isExpanded = expandedItem === group.id;
  const cardRef = React.useRef<HTMLDivElement>(null);
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
    currentPage,
    totalPages,
    ipGroups,
    handlePageChange,
    handlePointerHoldStart,
    handlePointerHoldEnd
  } = useGroupPagination({
    filteredDownloads,
    sessionsPerPage: filters.sessionsPerPage,
    groupId: group.id,
    groupPages,
    setGroupPages,
    startHoldTimer,
    stopHoldTimer
  });
  const hitPercent = cacheHitPercent(group.cacheHitBytes, group.totalBytes);
  const primaryDownload = group.downloads[0];
  const serviceLower = group.service.toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEpic = serviceLower === 'epic' || serviceLower === 'epicgames';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = getServiceFilterKey(serviceLower) === 'xbox';
  const isOtherService =
    !isSteam && !isWsus && !isRiot && !isEpic && !isEA && !isBlizzard && !isXbox;
  const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
  const epicAppId = primaryDownload?.epicAppId ?? null;
  const primaryName = primaryDownload?.gameName ?? '';
  const { isEvicted, isPartiallyEvicted } = group;
  const detection = resolveGameDetection(
    primaryDownload?.gameAppId,
    primaryDownload?.gameName,
    detectionLookup,
    detectionByName,
    group.service,
    detectionByService
  );
  const diskSizeBytes = detection?.total_size_bytes;
  const showSteamImage = isSteam && availableImages.has(String(primaryDownload?.gameAppId ?? ''));
  const showEpicImage = isEpic && availableImages.has(primaryDownload?.epicAppId ?? '');
  const nameKeyed = nameKeyedImageKey(group.service, primaryName);
  const showNameKeyedImage = nameKeyed !== null && availableImages.has(nameKeyed.slug);
  const storeLink = primaryDownload?.gameAppId
    ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
    : null;
  const shouldRenderBanner =
    !aestheticMode &&
    (isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService);
  const artworkId = showSteamImage
    ? steamAppId
    : showEpicImage
      ? `epic-${epicAppId}`
      : showNameKeyedImage
        ? `${nameKeyed.service}-${nameKeyed.slug}`
        : null;
  const hasArtwork = artworkId !== null && !imageErrors.has(artworkId);
  const placeholderIconSize = 64;
  // Render the name for resolved games and for the Unknown/Other bucket, whose
  // members have no real game name so the sentinel service drives it.
  const hasRealGameName = serviceLower === 'unknown' || group.hasRealGameName;

  React.useEffect(() => {
    if (!enableScrollIntoView) return;

    const wasExpanded = prevExpandedRef.current;
    prevExpandedRef.current = isExpanded;

    if (isExpanded && !wasExpanded && cardRef.current) {
      const timeoutId = setTimeout(() => {
        if (!cardRef.current) return;
        const rect = cardRef.current.getBoundingClientRect();
        // Only scroll if the card is not fully visible in the viewport
        if (rect.top < 0 || rect.bottom > window.innerHeight) {
          const targetY = rect.top + window.scrollY - 16; // 16px buffer from top
          window.scrollTo({ top: targetY, behavior: 'smooth' });
        }
      }, 300);
      return () => clearTimeout(timeoutId);
    }
  }, [isExpanded, enableScrollIntoView]);

  // Fetch associations when group is rendered (not just when expanded)
  // This allows us to show event badges at the group level
  // refreshVersion triggers re-fetch when cache is invalidated (e.g., DownloadTagged event)
  useGroupDownloadAssociations(group.downloadIds, fetchAssociations, refreshVersion);

  const groupEvents = React.useMemo(
    () => collectGroupEvents(group.downloadIds, getAssociations),
    [group.downloadIds, getAssociations]
  );

  let bannerContent: React.ReactNode | null = null;

  if (shouldRenderBanner) {
    if (hasArtwork && artworkId) {
      bannerContent = (
        <GameImage
          gameAppId={showNameKeyedImage ? undefined : showEpicImage ? epicAppId! : steamAppId!}
          epicAppId={showEpicImage ? epicAppId! : undefined}
          nameKeyedService={showNameKeyedImage ? nameKeyed.service : undefined}
          nameKeyedSlug={showNameKeyedImage ? nameKeyed.slug : undefined}
          alt={primaryName || group.name}
          className="dl-card-banner-img"
          sizes={
            fullHeightBanners
              ? '(max-width: 639px) 100vw, 340px'
              : '(max-width: 639px) 100vw, 280px'
          }
          onError={handleImageError}
        />
      );
    } else {
      bannerContent = (
        <div className="download-banner-placeholder">
          {isSteam ? (
            <SteamIcon
              size={placeholderIconSize}
              className="opacity-75 text-[var(--theme-steam)]"
            />
          ) : isWsus ? (
            <WsusIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-wsus)]" />
          ) : isRiot ? (
            <RiotIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-riot)]" />
          ) : isEpic ? (
            <EpicIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-epic)]" />
          ) : isEA ? (
            <EAIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-origin)]" />
          ) : isBlizzard ? (
            <BlizzardIcon
              size={placeholderIconSize}
              className="opacity-75 text-[var(--theme-blizzard)]"
            />
          ) : isXbox ? (
            <XboxIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-xbox)]" />
          ) : (
            <UnknownServiceIcon
              size={placeholderIconSize + 12}
              className="opacity-75 text-[var(--theme-text-secondary)]"
            />
          )}
        </div>
      );
    }
  }

  const metaSummary =
    group.count > 1
      ? `${t('downloads.tab.normal.counts.clients', { count: group.clientsSet.size })} · ${t(
          'downloads.tab.normal.counts.requests',
          { count: group.count }
        )}`
      : null;

  const cardContent = (
    <>
      {bannerContent && <div className="dl-card-banner">{bannerContent}</div>}
      <div className="dl-card-body">
        {/* Title row: badges, game name, meta summary, expand chevron */}
        <div className="dl-card-title-row">
          <BadgesRow
            service={group.service}
            datasource={group.downloads[0]?.datasource}
            showDatasource={hasMultipleDatasources && showDatasourceLabels}
            isEvicted={isEvicted}
            isPartiallyEvicted={isPartiallyEvicted}
          />
          {hasRealGameName && <h3 className="dl-card-name">{group.name}</h3>}
          {showEventBadges && groupEvents.length > 0 && (
            <DownloadBadges events={groupEvents} maxVisible={2} size="sm" />
          )}
          <span className="dl-card-title-end">
            {metaSummary && <span className="dl-card-meta">{metaSummary}</span>}
            <ChevronDown size={18} className="dl-card-chevron" />
          </span>
        </div>

        {/* Labeled readout: the at-a-glance numbers for this game */}
        <div className="dl-card-stats">
          <div className="dl-stat">
            <span className="dl-stat-value">{formatBytes(group.totalBytes)}</span>
            <span className="dl-stat-label caps-label">
              {t('downloads.tab.normal.stats.downloaded')}
            </span>
          </div>
          {showCacheHitBar && (
            <div className="dl-stat">
              <span
                className={`dl-stat-value${hitPercent > 0 ? ' cache-hit' : ' dl-stat-value-muted'}`}
              >
                {hitPercent > 0 ? formatPercent(hitPercent) : '—'}
              </span>
              <span className="dl-stat-label caps-label">
                {t('downloads.tab.normal.stats.cacheHit')}
              </span>
            </div>
          )}
          {diskSizeBytes ? (
            <div className="dl-stat">
              <span className="dl-stat-value">{formatBytes(diskSizeBytes)}</span>
              <span className="dl-stat-label caps-label">
                {t('downloads.tab.normal.stats.onDisk')}
              </span>
            </div>
          ) : null}
          <div className="dl-stat">
            <span className="dl-stat-value dl-stat-value-muted">
              {formatRelativeTime(group.lastSeen)}
            </span>
            <span className="dl-stat-label caps-label">
              {t('downloads.tab.normal.stats.lastActivity')}
            </span>
          </div>
        </div>

        {showCacheHitBar && (
          <div className="dl-card-cache-bar">
            <div className="dl-card-cache-bar-fill" style={{ width: `${hitPercent}%` }} />
          </div>
        )}
      </div>
    </>
  );

  return (
    <div
      ref={cardRef}
      className={`dl-card${isExpanded ? ' dl-card-expanded' : ''}${
        fullHeightBanners ? ' dl-card-banner-lg' : ''
      }${isEvicted ? ' dl-card-evicted' : ''}`}
    >
      <button
        type="button"
        aria-expanded={isExpanded}
        onClick={() => onItemClick(group.id)}
        className="dl-card-header"
      >
        {cardContent}
      </button>

      <CollapsibleRegion
        open={isExpanded}
        contentClassName="border-t border-[var(--theme-primary)] bg-[var(--theme-card-bg)] px-4 pb-4 pt-4 sm:px-6 sm:pb-6 sm:pt-5"
      >
        <div onClick={(event: React.MouseEvent<HTMLDivElement>) => event.stopPropagation()}>
          <div className="flex flex-col gap-6">
            {/* Stats Overview Section */}
            <div>
              <div className="flex items-center justify-between mb-3">
                <h4 className="text-sm font-bold text-[var(--theme-text-primary)] uppercase tracking-wider opacity-80">
                  {t('downloads.tab.normal.stats.title')}
                </h4>
                {storeLink && (
                  <a
                    href={storeLink}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1.5 text-xs font-semibold text-[var(--theme-primary)] hover:text-[var(--theme-primary-hover)] transition-colors"
                  >
                    <span>{t('downloads.tab.normal.store.label')}</span>
                    <ExternalLink size={12} />
                  </a>
                )}
              </div>

              <div
                className={`grid grid-cols-1 sm:grid-cols-2 ${diskSizeBytes ? 'md:grid-cols-4' : 'md:grid-cols-3'} gap-4`}
              >
                {/* Efficiency & Savings */}
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
                  <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.efficiency')}</h5>
                  <div className="flex flex-col gap-4">
                    <div className="flex items-baseline justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.efficiencyRate')}
                      </span>
                      <span
                        className={`text-xl font-bold ${hitPercent > 0 ? 'cache-hit' : 'text-[var(--theme-text-secondary)]'}`}
                      >
                        {hitPercent > 0 ? formatPercent(hitPercent) : '-'}
                      </span>
                    </div>
                    <div className="w-full bg-[var(--theme-bg-primary)] rounded-full h-1.5 overflow-hidden">
                      <div
                        className="h-full bg-[var(--theme-success)] transition-[width] duration-500"
                        style={{ width: `${hitPercent}%` }}
                      />
                    </div>
                    <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                      <span className="text-xs text-[var(--theme-text-muted)]">
                        {t('downloads.tab.normal.stats.cacheSaved')}
                      </span>
                      <span className="text-sm font-bold text-[var(--theme-success-text)]">
                        {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '-'}
                      </span>
                    </div>
                  </div>
                </div>

                {/* Disk Usage */}
                {diskSizeBytes ? (
                  <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
                    <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.diskUsage')}</h5>
                    <div className="flex flex-col gap-4">
                      <div className="flex items-baseline justify-between">
                        <span className="text-sm text-[var(--theme-text-secondary)]">
                          {t('downloads.tab.normal.stats.dataOnDisk')}
                        </span>
                        <span className="text-xl font-bold text-[var(--theme-primary)]">
                          {formatBytes(diskSizeBytes)}
                        </span>
                      </div>
                      <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                        <span className="text-xs text-[var(--theme-text-muted)]">
                          {t('downloads.tab.normal.stats.cacheFiles')}
                        </span>
                        <span className="text-sm font-bold text-[var(--theme-text-secondary)]">
                          {formatCount(detection.cache_files_found)}
                        </span>
                      </div>
                    </div>
                  </div>
                ) : null}

                {/* Network Traffic */}
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
                  <h5 className="caps-label mb-3">
                    {t('downloads.tab.normal.stats.networkTraffic')}
                  </h5>
                  <div className="space-y-3">
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.totalDownloaded')}
                      </span>
                      <span className="text-base font-bold text-[var(--theme-text-primary)]">
                        {formatBytes(group.totalBytes)}
                      </span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.cacheMiss')}
                      </span>
                      <span className="text-sm font-medium text-[var(--theme-text-muted)]">
                        {formatBytes(group.cacheMissBytes)}
                      </span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.cacheHit')}
                      </span>
                      <span className="text-sm font-medium text-[var(--theme-success-text)]">
                        {formatBytes(group.cacheHitBytes)}
                      </span>
                    </div>
                  </div>
                </div>

                {/* Activity Summary */}
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
                  <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.activity')}</h5>
                  <div className="space-y-3">
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.downloadSessions')}
                      </span>
                      <span className="text-base font-bold text-[var(--theme-text-primary)]">
                        {group.count}
                      </span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.uniqueClients')}
                      </span>
                      <span className="text-sm font-medium text-[var(--theme-text-primary)]">
                        {group.clientsSet.size}
                      </span>
                    </div>
                    <div className="flex items-center justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                      <span className="text-xs text-[var(--theme-text-muted)]">
                        {t('downloads.tab.normal.stats.lastActivity')}
                      </span>
                      <span className="text-xs font-medium text-[var(--theme-text-secondary)]">
                        <DownloadTimestamp dateString={group.lastSeen} showAbsoluteInline />
                      </span>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            {/* Download Sessions List */}
            {group.downloads.length > 0 &&
              (() => {
                const excludedSessions = Math.max(0, group.downloads.length - group.count);

                return (
                  <div className="mt-2">
                    <div className="flex items-center justify-between mb-3">
                      <h4 className="text-sm font-bold text-[var(--theme-text-primary)] uppercase tracking-wider opacity-80">
                        {t('downloads.tab.normal.sessions.title')}
                      </h4>
                      <div className="flex items-center gap-3">
                        {excludedSessions > 0 && (
                          <span className="text-xs text-[var(--theme-text-muted)] italic">
                            {t('downloads.tab.normal.sessions.excluded', {
                              count: excludedSessions
                            })}
                          </span>
                        )}
                        {totalPages > 1 && (
                          <span className="text-xs font-mono text-[var(--theme-text-muted)]">
                            {currentPage} / {totalPages}
                          </span>
                        )}
                      </div>
                    </div>

                    {/* Filter bar - only shown for groups with more than 10 downloads */}
                    {group.downloads.length > 10 && (
                      <div className="mb-4">
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

                    <IpDownloadGroup
                      ipGroups={ipGroups}
                      itemsPerPage={filters.itemsPerSession}
                      getAssociations={getAssociations}
                      showEventBadges={showEventBadges}
                      toggleIp={toggleIp}
                      isIpExpanded={isIpExpanded}
                    />

                    {/* Pagination Controls */}
                    <Pagination
                      variant="group"
                      showCard={false}
                      currentPage={currentPage}
                      totalPages={totalPages}
                      onPageChange={handlePageChange}
                      holdToRepeat
                      onPointerHoldStart={handlePointerHoldStart}
                      onPointerHoldEnd={handlePointerHoldEnd}
                      onLostPointerCapture={stopHoldTimer}
                      previousLabel={t('downloads.tab.normal.pagination.previous')}
                      nextLabel={t('downloads.tab.normal.pagination.next')}
                    />
                  </div>
                );
              })()}
          </div>
        </div>
      </CollapsibleRegion>
    </div>
  );
};

interface GridCardProps {
  group: DownloadGroup;
  isExpanded: boolean;
  onItemClick: (id: string) => void;
  imageErrors: Set<string>;
  handleImageError: (gameAppId: string) => void;
  showCacheHitBar: boolean;
  showEventBadges: boolean;
  bannerOnly: boolean;
  groupPages: Record<string, number>;
  setGroupPages: React.Dispatch<React.SetStateAction<Record<string, number>>>;
  startHoldTimer: (callback: () => void) => void;
  stopHoldTimer: () => void;
  enableScrollIntoView: boolean;
  showDatasourceLabels: boolean;
  hasMultipleDatasources: boolean;
  availableImages: Set<string>;
}

const GridCard: React.FC<GridCardProps> = ({
  group,
  isExpanded: _isExpanded,
  onItemClick,
  imageErrors,
  handleImageError,
  showCacheHitBar,
  showEventBadges,
  bannerOnly,
  groupPages: _groupPages,
  setGroupPages: _setGroupPages,
  startHoldTimer: _startHoldTimer,
  stopHoldTimer: _stopHoldTimer,
  enableScrollIntoView: _enableScrollIntoView,
  showDatasourceLabels,
  hasMultipleDatasources,
  availableImages
}) => {
  const { t } = useTranslation();
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const cardRef = React.useRef<HTMLDivElement>(null);
  const hitPercent = cacheHitPercent(group.cacheHitBytes, group.totalBytes);
  const primaryDownload = group.downloads[0];
  const serviceLower = group.service.toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isEpic = serviceLower === 'epic' || serviceLower === 'epicgames';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = getServiceFilterKey(serviceLower) === 'xbox';
  const isOtherService =
    !isSteam && !isWsus && !isRiot && !isEpic && !isEA && !isBlizzard && !isXbox;
  const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
  const epicAppId = primaryDownload?.epicAppId ?? null;
  const primaryName = primaryDownload?.gameName ?? '';
  const showSteamImage = isSteam && availableImages.has(String(primaryDownload?.gameAppId ?? ''));
  const showEpicImage = isEpic && availableImages.has(primaryDownload?.epicAppId ?? '');
  const nameKeyed = nameKeyedImageKey(group.service, primaryName);
  const showNameKeyedImage = nameKeyed !== null && availableImages.has(nameKeyed.slug);
  const artworkId = showSteamImage
    ? steamAppId
    : showEpicImage
      ? `epic-${epicAppId}`
      : showNameKeyedImage
        ? `${nameKeyed.service}-${nameKeyed.slug}`
        : null;
  const hasArtwork = artworkId !== null && !imageErrors.has(artworkId);
  const { isEvicted, isPartiallyEvicted } = group;
  const placeholderIconSize = 48;

  useGroupDownloadAssociations(group.downloadIds, fetchAssociations, refreshVersion);

  const groupEvents = React.useMemo(
    () => collectGroupEvents(group.downloadIds, getAssociations),
    [group.downloadIds, getAssociations]
  );

  // Build banner content for the card
  const shouldRenderBanner =
    isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService;

  let bannerContent: React.ReactNode | null = null;
  if (shouldRenderBanner) {
    if (hasArtwork && artworkId) {
      bannerContent = (
        <GameImage
          gameAppId={showNameKeyedImage ? undefined : showEpicImage ? epicAppId! : steamAppId!}
          epicAppId={showEpicImage ? epicAppId! : undefined}
          nameKeyedService={showNameKeyedImage ? nameKeyed.service : undefined}
          nameKeyedSlug={showNameKeyedImage ? nameKeyed.slug : undefined}
          alt={primaryName || group.name}
          className="card-grid-banner-image"
          sizes="(max-width: 639px) 100vw, 360px"
          onError={handleImageError}
          loading="lazy"
        />
      );
    } else {
      bannerContent = (
        <div className="download-banner-placeholder">
          {isSteam ? (
            <SteamIcon
              size={placeholderIconSize}
              className="opacity-75 text-[var(--theme-steam)]"
            />
          ) : isWsus ? (
            <WsusIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-wsus)]" />
          ) : isRiot ? (
            <RiotIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-riot)]" />
          ) : isEpic ? (
            <EpicIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-epic)]" />
          ) : isEA ? (
            <EAIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-origin)]" />
          ) : isBlizzard ? (
            <BlizzardIcon
              size={placeholderIconSize}
              className="opacity-75 text-[var(--theme-blizzard)]"
            />
          ) : isXbox ? (
            <XboxIcon size={placeholderIconSize} className="opacity-75 text-[var(--theme-xbox)]" />
          ) : (
            <UnknownServiceIcon
              size={placeholderIconSize + 12}
              className="opacity-75 text-[var(--theme-text-secondary)]"
            />
          )}
        </div>
      );
    }
  }

  return (
    <div
      ref={cardRef}
      className={`card-grid-item ${bannerOnly ? 'banner-only' : ''}${isEvicted ? ' opacity-60' : ''}`}
      onClick={() => onItemClick(group.id)}
    >
      {/* Banner */}
      {bannerOnly ? (
        <Tooltip content={group.name} className="card-grid-item-banner">
          {bannerContent}
        </Tooltip>
      ) : (
        <div className="card-grid-item-banner">{bannerContent}</div>
      )}

      {/* Info */}
      {!bannerOnly && (
        <div className="card-grid-item-info">
          <div className="flex items-center gap-2 mb-1">
            <BadgesRow
              service={group.service}
              datasource={group.downloads[0]?.datasource}
              showDatasource={hasMultipleDatasources && showDatasourceLabels}
              isEvicted={isEvicted}
              isPartiallyEvicted={isPartiallyEvicted}
            />
          </div>
          <Tooltip content={group.name} className="card-grid-item-name">
            {group.name}
          </Tooltip>
          <div className="card-grid-item-stats">
            <span className="font-semibold text-[var(--theme-text-primary)]">
              {formatBytes(group.totalBytes)}
            </span>
            {hitPercent > 0 ? (
              <span className="cache-hit font-semibold">{formatPercent(hitPercent)}</span>
            ) : (
              <span className="text-[var(--theme-text-muted)]">0%</span>
            )}
            {group.count > 1 && (
              <span className="text-[var(--theme-text-muted)]">
                {group.count} {t('dashboard.downloadsPanel.req')}
              </span>
            )}
          </div>
          <DownloadTimestamp
            dateString={group.lastSeen}
            showAbsoluteInline
            className="text-xs text-[var(--theme-text-muted)] mt-1"
          />

          {/* Cache hit bar */}
          {showCacheHitBar && (
            <div className="card-grid-item-cache-bar">
              <div className="card-grid-item-cache-bar-fill" style={{ width: `${hitPercent}%` }} />
            </div>
          )}

          {/* Event badges */}
          {showEventBadges && groupEvents.length > 0 && (
            <div className="mt-2">
              <DownloadBadges events={groupEvents} maxVisible={2} size="sm" />
            </div>
          )}
        </div>
      )}
    </div>
  );
};

interface GridCardDrawerContentProps {
  group: DownloadGroup;
  imageErrors: Set<string>;
  handleImageError: (gameAppId: string) => void;
  showEventBadges: boolean;
  showDatasourceLabels: boolean;
  hasMultipleDatasources: boolean;
  groupPages: Record<string, number>;
  setGroupPages: React.Dispatch<React.SetStateAction<Record<string, number>>>;
  startHoldTimer: (callback: () => void) => void;
  stopHoldTimer: () => void;
  availableImages: Set<string>;
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
}

const GridCardDrawerContent: React.FC<GridCardDrawerContentProps> = ({
  group,
  imageErrors,
  handleImageError,
  showEventBadges,
  showDatasourceLabels,
  hasMultipleDatasources,
  groupPages,
  setGroupPages,
  startHoldTimer,
  stopHoldTimer,
  availableImages,
  detectionLookup,
  detectionByName,
  detectionByService
}) => {
  const { t } = useTranslation();
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
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
  const { toggleIp, isIpExpanded } = useIpExpansion();
  const drawerContentRef = React.useRef<HTMLDivElement>(null);
  const drawerScrollRef = React.useRef<HTMLElement | null>(null);

  React.useEffect(() => {
    const contentEl = drawerContentRef.current;
    if (!contentEl) {
      drawerScrollRef.current = null;
      return;
    }
    drawerScrollRef.current = contentEl.closest<HTMLElement>('.drawer-body');
  }, [group.id]);
  const hitPercent = cacheHitPercent(group.cacheHitBytes, group.totalBytes);
  const primaryDownload = group.downloads[0];
  const serviceLower = group.service.toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isEpic = serviceLower === 'epic' || serviceLower === 'epicgames';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = getServiceFilterKey(serviceLower) === 'xbox';
  const isOtherService =
    !isSteam && !isWsus && !isRiot && !isEpic && !isEA && !isBlizzard && !isXbox;
  const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
  const epicAppId = primaryDownload?.epicAppId ?? null;
  const primaryName = primaryDownload?.gameName ?? '';
  const showSteamImage = isSteam && availableImages.has(String(primaryDownload?.gameAppId ?? ''));
  const showEpicImage = isEpic && availableImages.has(primaryDownload?.epicAppId ?? '');
  const detection = resolveGameDetection(
    primaryDownload?.gameAppId,
    primaryDownload?.gameName,
    detectionLookup,
    detectionByName,
    group.service,
    detectionByService
  );
  const diskSizeBytes = detection?.total_size_bytes;
  const nameKeyed = nameKeyedImageKey(group.service, primaryName);
  const showNameKeyedImage = nameKeyed !== null && availableImages.has(nameKeyed.slug);
  const artworkId = showSteamImage
    ? steamAppId
    : showEpicImage
      ? `epic-${epicAppId}`
      : showNameKeyedImage
        ? `${nameKeyed.service}-${nameKeyed.slug}`
        : null;
  const hasArtwork = artworkId !== null && !imageErrors.has(artworkId);
  const storeLink = primaryDownload?.gameAppId
    ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
    : null;

  useGroupDownloadAssociations(group.downloadIds, fetchAssociations, refreshVersion);

  const groupEvents = React.useMemo(
    () => collectGroupEvents(group.downloadIds, getAssociations),
    [group.downloadIds, getAssociations]
  );

  // Build banner for drawer header
  const shouldRenderBanner =
    isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService;

  let drawerBanner: React.ReactNode | null = null;
  if (shouldRenderBanner && hasArtwork && artworkId) {
    drawerBanner = (
      <GameImage
        gameAppId={showNameKeyedImage ? undefined : showEpicImage ? epicAppId! : steamAppId!}
        epicAppId={showEpicImage ? epicAppId! : undefined}
        nameKeyedService={showNameKeyedImage ? nameKeyed.service : undefined}
        nameKeyedSlug={showNameKeyedImage ? nameKeyed.slug : undefined}
        alt={primaryName || group.name}
        className="drawer-banner-image"
        sizes="(max-width: 639px) 100vw, 550px"
        onError={handleImageError}
      />
    );
  }

  // Session pagination
  const excludedSessions = Math.max(0, group.downloads.length - group.count);

  const {
    currentPage,
    totalPages,
    ipGroups,
    handlePageChange,
    handlePointerHoldStart,
    handlePointerHoldEnd
  } = useGroupPagination({
    filteredDownloads,
    sessionsPerPage: filters.sessionsPerPage,
    groupId: group.id,
    groupPages,
    setGroupPages,
    startHoldTimer,
    stopHoldTimer
  });

  return (
    <div className="drawer-detail-content" ref={drawerContentRef}>
      {/* Banner */}
      {drawerBanner && <div className="drawer-banner-wrapper">{drawerBanner}</div>}

      {/* Title area with service badge */}
      <div className="flex items-center gap-2 mb-4 flex-wrap">
        <span className="themed-badge" style={getServiceBadgeStyles(group.service)}>
          {getServiceDisplayName(group.service)}
        </span>
        {hasMultipleDatasources && showDatasourceLabels && group.downloads[0]?.datasource && (
          <Badge variant="neutral">{group.downloads[0].datasource}</Badge>
        )}
        {storeLink && (
          <a
            href={storeLink}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1.5 text-xs font-semibold text-[var(--theme-primary)] hover:text-[var(--theme-primary-hover)] transition-colors ml-auto"
          >
            <span>{t('downloads.tab.normal.store.label')}</span>
            <ExternalLink size={12} />
          </a>
        )}
      </div>

      {/* Event badges */}
      {showEventBadges && groupEvents.length > 0 && (
        <div className="mb-4">
          <DownloadBadges events={groupEvents} maxVisible={5} size="sm" />
        </div>
      )}

      {/* Analytics Overview */}
      <div className="mb-4">
        <h4 className="text-sm font-bold text-[var(--theme-text-primary)] uppercase tracking-wider opacity-80 mb-3">
          {t('downloads.tab.normal.stats.title')}
        </h4>

        <div className="flex flex-col gap-3">
          {/* Efficiency */}
          <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
            <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.efficiency')}</h5>
            <div className="flex flex-col gap-4">
              <div className="flex items-baseline justify-between">
                <span className="text-sm text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.stats.efficiencyRate')}
                </span>
                <span
                  className={`text-xl font-bold ${hitPercent > 0 ? 'cache-hit' : 'text-[var(--theme-text-secondary)]'}`}
                >
                  {hitPercent > 0 ? formatPercent(hitPercent) : '\u2014'}
                </span>
              </div>
              <div className="w-full bg-[var(--theme-bg-primary)] rounded-full h-1.5 overflow-hidden">
                <div
                  className="h-full bg-[var(--theme-success)] transition-[width] duration-500"
                  style={{ width: `${hitPercent}%` }}
                />
              </div>
              <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                <span className="text-xs text-[var(--theme-text-muted)]">
                  {t('downloads.tab.normal.stats.cacheSaved')}
                </span>
                <span className="text-sm font-bold text-[var(--theme-success-text)]">
                  {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '\u2014'}
                </span>
              </div>
            </div>
          </div>

          {/* Disk Usage */}
          {diskSizeBytes ? (
            <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
              <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.diskUsage')}</h5>
              <div className="flex flex-col gap-4">
                <div className="flex items-baseline justify-between">
                  <span className="text-sm text-[var(--theme-text-secondary)]">
                    {t('downloads.tab.normal.stats.dataOnDisk')}
                  </span>
                  <span className="text-xl font-bold text-[var(--theme-primary)]">
                    {formatBytes(diskSizeBytes)}
                  </span>
                </div>
                <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                  <span className="text-xs text-[var(--theme-text-muted)]">
                    {t('downloads.tab.normal.stats.cacheFiles')}
                  </span>
                  <span className="text-sm font-bold text-[var(--theme-text-secondary)]">
                    {formatCount(detection.cache_files_found)}
                  </span>
                </div>
              </div>
            </div>
          ) : null}

          {/* Network Traffic */}
          <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
            <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.networkTraffic')}</h5>
            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <span className="text-sm text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.stats.totalDownloaded')}
                </span>
                <span className="text-base font-bold text-[var(--theme-text-primary)]">
                  {formatBytes(group.totalBytes)}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-sm text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.stats.cacheMiss')}
                </span>
                <span className="text-sm font-medium text-[var(--theme-text-muted)]">
                  {formatBytes(group.cacheMissBytes)}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-sm text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.stats.cacheHit')}
                </span>
                <span className="text-sm font-medium text-[var(--theme-success-text)]">
                  {formatBytes(group.cacheHitBytes)}
                </span>
              </div>
            </div>
          </div>

          {/* Activity */}
          <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-well)] [background-clip:padding-box]">
            <h5 className="caps-label mb-3">{t('downloads.tab.normal.stats.activity')}</h5>
            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <span className="text-sm text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.stats.downloadSessions')}
                </span>
                <span className="text-base font-bold text-[var(--theme-text-primary)]">
                  {group.count}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-sm text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.stats.uniqueClients')}
                </span>
                <span className="text-sm font-medium text-[var(--theme-text-primary)]">
                  {group.clientsSet.size}
                </span>
              </div>
              <div className="flex items-center justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                <span className="text-xs text-[var(--theme-text-muted)]">
                  {t('downloads.tab.normal.stats.lastActivity')}
                </span>
                <span className="text-xs font-medium text-[var(--theme-text-secondary)]">
                  <DownloadTimestamp dateString={group.lastSeen} showAbsoluteInline />
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Download Sessions */}
      {group.downloads.length > 0 && (
        <div>
          <div className="flex items-center justify-between mb-3">
            <h4 className="text-sm font-bold text-[var(--theme-text-primary)] uppercase tracking-wider opacity-80">
              {t('downloads.tab.normal.sessions.title')}
            </h4>
            <div className="flex items-center gap-3">
              {excludedSessions > 0 && (
                <span className="text-xs text-[var(--theme-text-muted)] italic">
                  {t('downloads.tab.normal.sessions.excluded', { count: excludedSessions })}
                </span>
              )}
              {totalPages > 1 && (
                <span className="text-xs font-mono text-[var(--theme-text-muted)]">
                  {currentPage} / {totalPages}
                </span>
              )}
            </div>
          </div>

          {group.downloads.length > 10 && (
            <div className="mb-4">
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

          <IpDownloadGroup
            ipGroups={ipGroups}
            itemsPerPage={filters.itemsPerSession}
            getAssociations={getAssociations}
            showEventBadges={showEventBadges}
            toggleIp={toggleIp}
            isIpExpanded={isIpExpanded}
          />

          <Pagination
            variant="group"
            showCard={false}
            currentPage={currentPage}
            totalPages={totalPages}
            onPageChange={handlePageChange}
            holdToRepeat
            onPointerHoldStart={handlePointerHoldStart}
            onPointerHoldEnd={handlePointerHoldEnd}
            onLostPointerCapture={stopHoldTimer}
            previousLabel={t('downloads.tab.normal.pagination.previous')}
            nextLabel={t('downloads.tab.normal.pagination.next')}
          />
        </div>
      )}

      <BackToTopButton scrollContainerRef={drawerScrollRef} />
    </div>
  );
};

const NormalView: React.FC<NormalViewProps> = ({
  items,
  expandedItem,
  onItemClick,
  sectionLabels,
  aestheticMode = false,
  fullHeightBanners = false,
  groupByFrequency = true,
  enableScrollIntoView = true,
  showDatasourceLabels = true,
  hasMultipleDatasources = false,
  cardGridLayout = false,
  cardSize = 'medium',
  showCacheHitBar = true,
  showEventBadges = true,
  bannerOnly = false,
  detectionLookup = null,
  detectionByName = null,
  detectionByService = null
}) => {
  const { t } = useTranslation();
  const labels = { ...getDefaultSectionLabels(t), ...sectionLabels };
  const { imageErrors, handleImageError } = useImageErrors();
  const [groupPages, setGroupPages] = React.useState<Record<string, number>>({});
  const [drawerGroupId, setDrawerGroupId] = useState<string | null>(null);
  const { startHoldTimer, stopHoldTimer } = useHoldTimer();
  const availableImages = useAvailableGameImages();

  const SESSIONS_PER_PAGE = 10;

  // Virtualization hooks must be declared unconditionally (before any early
  // return) to satisfy the Rules of Hooks.
  const VIRTUALIZATION_THRESHOLD = 200;

  // Flatten `items` (and optional section headers from `groupByFrequency`) into
  // a single typed row array. A discriminated union keeps the virtualizer's
  // index-based lookup strongly typed: each row is either a synthetic section
  // header or a real download/group card.
  const flatRows = useFlatRows({ items, groupByFrequency });

  // List-mode virtualization (with or without groupByFrequency).
  const shouldVirtualizeList = !cardGridLayout && flatRows.length > VIRTUALIZATION_THRESHOLD;
  const virtualParentRef = useRef<HTMLDivElement | null>(null);
  const rowVirtualizer = useVirtualizer({
    count: shouldVirtualizeList ? flatRows.length : 0,
    getScrollElement: () => virtualParentRef.current,
    estimateSize: (index) => (flatRows[index]?.kind === 'header' ? 88 : 240),
    overscan: 5,
    measureElement: (el) => el?.getBoundingClientRect().height ?? 240
  });

  // Which rows are on screen, as a value that only changes when the rows do. Opening a group
  // refetches its sessions and rebuilds `items`, so `flatRows` is a new array holding the same
  // rows; resetting on the array itself sent the reader back to the top on every click. The card
  // grid chunks the same items, so this identifies its row set too.
  const rowSetKey = useMemo(() => flatRows.map((row) => row.id).join('\n'), [flatRows]);

  // Reset virtualized scroll to top when filters/sort change the row set, preventing a stale offset.
  useEffect(() => {
    if (virtualParentRef.current) {
      virtualParentRef.current.scrollTop = 0;
    }
  }, [rowSetKey]);

  // The card grid's drawer reads its group back out of the page rows every time they change rather
  // than snapshotting it at click time, so the sessions fetched after the click reach the drawer
  // while it is open. Recomputed only when the rows or the open group change: with a drawer open
  // over an unpaged list this otherwise ran on every scroll frame.
  const drawerItem = useMemo<DownloadGroup | null>(() => {
    if (drawerGroupId === null) {
      return null;
    }
    const row = items.find((item) => toGroup(item).id === drawerGroupId);
    return row ? toGroup(row) : null;
  }, [items, drawerGroupId]);

  // A live refetch re-orders the page and can push the open group off it. The drawer is opened by
  // the lookup above, so it would vanish without Mantine ever running `onClose`, leaving the id set
  // and letting the next refetch that returns the group re-open the drawer on its own. Closing it
  // here gives it one close path.
  useEffect(() => {
    if (drawerGroupId !== null && drawerItem === null) {
      setDrawerGroupId(null);
    }
  }, [drawerGroupId, drawerItem]);

  // Card-grid virtualization: chunk flat `items` into rows of `gridCols` cards
  // and virtualize the resulting row list. Column count is measured from the
  // parent width via a ResizeObserver so it mirrors the CSS grid's auto-fill
  // behaviour (which otherwise cannot be observed from JS).
  const GRID_MIN_WIDTH: Record<'small' | 'medium' | 'large', number> = {
    small: 200,
    medium: 280,
    large: 360
  };
  const GRID_GAP_PX = 16; // matches `gap: 1rem` in .card-grid-container
  const shouldVirtualizeGrid = cardGridLayout && items.length > VIRTUALIZATION_THRESHOLD;
  const gridParentRef = useRef<HTMLDivElement | null>(null);
  const [gridCols, setGridCols] = useState<number>(1);

  useEffect(() => {
    if (!shouldVirtualizeGrid) return;
    const el = gridParentRef.current;
    if (!el) return;
    const minWidth = GRID_MIN_WIDTH[cardSize];
    const computeCols = (width: number): number => {
      if (width <= 0) return 1;
      const usable = width + GRID_GAP_PX;
      const per = minWidth + GRID_GAP_PX;
      return Math.max(1, Math.floor(usable / per));
    };
    setGridCols(computeCols(el.clientWidth));
    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const next = computeCols(entry.contentRect.width);
        setGridCols((prev) => (prev === next ? prev : next));
      }
    });
    observer.observe(el);
    return () => observer.disconnect();
    // GRID_MIN_WIDTH is a stable literal; only cardSize/shouldVirtualizeGrid matter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cardSize, shouldVirtualizeGrid]);

  const gridRowGroups = useMemo<(Download | DownloadGroup)[][]>(() => {
    if (!shouldVirtualizeGrid) return [];
    const cols = Math.max(1, gridCols);
    const rows: (Download | DownloadGroup)[][] = [];
    for (let i = 0; i < items.length; i += cols) {
      rows.push(items.slice(i, i + cols));
    }
    return rows;
  }, [items, gridCols, shouldVirtualizeGrid]);

  const gridVirtualizer = useVirtualizer({
    count: shouldVirtualizeGrid ? gridRowGroups.length : 0,
    getScrollElement: () => gridParentRef.current,
    estimateSize: () => 320,
    overscan: 4,
    measureElement: (el) => el?.getBoundingClientRect().height ?? 320
  });

  // Column count changes → re-measure every row so previously cached sizes are invalidated.
  useEffect(() => {
    if (shouldVirtualizeGrid) {
      gridVirtualizer.measure();
    }
  }, [gridCols, shouldVirtualizeGrid, gridVirtualizer]);

  // Reset grid scroll when the item set changes.
  useEffect(() => {
    if (gridParentRef.current) {
      gridParentRef.current.scrollTop = 0;
    }
  }, [rowSetKey]);

  const renderSectionHeader = (variant: HeaderRowKind): React.ReactNode => {
    if (variant === 'multiple') {
      return (
        <div className="section-divider mb-5 mt-8 first:mt-0">
          <div className="section-divider-inner">
            <div className="section-divider-accent" />
            <div className="section-divider-content">
              <h2 className="section-divider-title">{labels.multipleDownloads}</h2>
              <p className="section-divider-description">
                {t('downloads.tab.normal.sections.multipleDownloadsDescription')}
              </p>
            </div>
          </div>
        </div>
      );
    }
    if (variant === 'single') {
      return (
        <div className="section-divider mb-5 mt-8 first:mt-0">
          <div className="section-divider-inner">
            <div className="section-divider-accent" />
            <div className="section-divider-content">
              <h2 className="section-divider-title">{labels.singleDownloads}</h2>
              <p className="section-divider-description">
                {t('downloads.tab.normal.sections.singleDownloadsDescription')}
              </p>
            </div>
          </div>
        </div>
      );
    }
    return (
      <div className="section-divider mb-5 mt-8 first:mt-0">
        <div className="section-divider-inner">
          <div className="section-divider-accent" />
          <div className="section-divider-content">
            <h2 className="section-divider-title">{labels.individual}</h2>
            <p className="section-divider-description">
              {t('downloads.tab.normal.sections.individualDescription')}
            </p>
          </div>
        </div>
      </div>
    );
  };

  const renderGroupCard = (group: DownloadGroup) => (
    <GroupCard
      group={group}
      expandedItem={expandedItem}
      onItemClick={onItemClick}
      aestheticMode={aestheticMode}
      fullHeightBanners={fullHeightBanners}
      imageErrors={imageErrors}
      handleImageError={handleImageError}
      groupPages={groupPages}
      setGroupPages={setGroupPages}
      startHoldTimer={startHoldTimer}
      stopHoldTimer={stopHoldTimer}
      SESSIONS_PER_PAGE={SESSIONS_PER_PAGE}
      enableScrollIntoView={enableScrollIntoView}
      showDatasourceLabels={showDatasourceLabels}
      hasMultipleDatasources={hasMultipleDatasources}
      showCacheHitBar={showCacheHitBar}
      showEventBadges={showEventBadges}
      availableImages={availableImages}
      detectionLookup={detectionLookup}
      detectionByName={detectionByName}
      detectionByService={detectionByService}
    />
  );

  // Card Grid Layout mode
  if (cardGridLayout) {
    const gridSizeClass =
      cardSize === 'small'
        ? 'card-grid-container card-size-small'
        : cardSize === 'large'
          ? 'card-grid-container card-size-large'
          : 'card-grid-container';

    // A row carries only its newest session until DownloadsTab is told which group is open, so the
    // click has to both open the drawer and ask for the rest.
    const handleGridCardClick = (groupId: string) => {
      setDrawerGroupId(groupId);
      if (expandedItem !== groupId) {
        onItemClick(groupId);
      }
    };

    const itemKey = (item: Download | DownloadGroup): string =>
      'downloads' in item ? item.id : `download-${item.id}`;

    const renderGridCard = (item: Download | DownloadGroup): React.ReactNode => {
      const group = toGroup(item);
      return (
        <GridCard
          key={itemKey(item)}
          group={group}
          isExpanded={false}
          onItemClick={handleGridCardClick}
          imageErrors={imageErrors}
          handleImageError={handleImageError}
          showCacheHitBar={showCacheHitBar}
          showEventBadges={showEventBadges}
          bannerOnly={bannerOnly}
          groupPages={groupPages}
          setGroupPages={setGroupPages}
          startHoldTimer={startHoldTimer}
          stopHoldTimer={stopHoldTimer}
          enableScrollIntoView={false}
          showDatasourceLabels={showDatasourceLabels}
          hasMultipleDatasources={hasMultipleDatasources}
          availableImages={availableImages}
        />
      );
    };

    const drawerNode = (
      <Drawer
        opened={drawerItem !== null}
        onClose={() => {
          if (expandedItem !== null && expandedItem === drawerGroupId) {
            onItemClick(expandedItem);
          }
          setDrawerGroupId(null);
        }}
        position="right"
        title={drawerItem?.name ?? ''}
        classNames={{
          header: 'drawer-header',
          body: 'drawer-body',
          content: 'drawer-content',
          title: 'drawer-title'
        }}
      >
        {drawerItem && (
          <GridCardDrawerContent
            group={drawerItem}
            imageErrors={imageErrors}
            handleImageError={handleImageError}
            showEventBadges={showEventBadges}
            showDatasourceLabels={showDatasourceLabels}
            hasMultipleDatasources={hasMultipleDatasources}
            groupPages={groupPages}
            setGroupPages={setGroupPages}
            startHoldTimer={startHoldTimer}
            stopHoldTimer={stopHoldTimer}
            availableImages={availableImages}
            detectionLookup={detectionLookup}
            detectionByName={detectionByName}
            detectionByService={detectionByService}
          />
        )}
      </Drawer>
    );

    if (shouldVirtualizeGrid) {
      const virtualItems = gridVirtualizer.getVirtualItems();
      // Dynamic `--grid-cols` must be per-element (viewport derived). All other
      // styling (gap, padding, track sizing) lives in VirtualizedList.css.
      const gridTemplateStyle: React.CSSProperties = {
        gridTemplateColumns: `repeat(${Math.max(1, gridCols)}, minmax(0, 1fr))`
      };
      return (
        <>
          <div ref={gridParentRef} className="virtual-list-parent virtual-list-parent-cardgrid">
            <div
              className="virtual-list-inner"
              style={{ height: `${gridVirtualizer.getTotalSize()}px` }}
            >
              {virtualItems.map((virtualRow) => {
                const rowItems = gridRowGroups[virtualRow.index];
                if (!rowItems) return null;
                return (
                  <div
                    key={virtualRow.key}
                    data-index={virtualRow.index}
                    ref={gridVirtualizer.measureElement}
                    className={`virtual-row virtual-grid-row ${gridSizeClass}`}
                    style={{
                      transform: `translateY(${virtualRow.start}px)`,
                      ...gridTemplateStyle
                    }}
                  >
                    {rowItems.map((item) => renderGridCard(item))}
                  </div>
                );
              })}
            </div>
          </div>
          {drawerNode}
        </>
      );
    }

    return (
      <>
        <div ref={gridParentRef} className={gridSizeClass}>
          {items.map((item) => renderGridCard(item))}
        </div>
        {drawerNode}
      </>
    );
  }

  // Standard list layout
  // Virtualization now covers BOTH plain list and `groupByFrequency` modes by
  // flattening section headers into the same typed row array. cardGridLayout
  // still bypasses (handled in its own branch above).
  if (shouldVirtualizeList) {
    const virtualItems = rowVirtualizer.getVirtualItems();
    return (
      <div ref={virtualParentRef} className="virtual-list-parent virtual-list-parent-normal">
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
                className="virtual-row virtual-row-normal"
                style={{ transform: `translateY(${virtualRow.start}px)` }}
              >
                {row.kind === 'header'
                  ? renderSectionHeader(row.variant)
                  : renderGroupCard(toGroup(row.item))}
              </div>
            );
          })}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {flatRows.map((row) => (
        <React.Fragment key={row.id}>
          {row.kind === 'header'
            ? renderSectionHeader(row.variant)
            : renderGroupCard(toGroup(row.item))}
        </React.Fragment>
      ))}
    </div>
  );
};

const MemoizedNormalView = memo(NormalView);
export default MemoizedNormalView;
