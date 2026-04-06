import React, { memo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import Drawer from '@components/ui/Drawer';
import {
  ChevronRight,
  ChevronDown,
  Clock,
  ExternalLink,
  CheckCircle,
  AlertCircle,
  ChevronLeft,
  HardDrive
} from 'lucide-react';
import { formatBytes, formatCount, formatPercent, formatRelativeTime } from '@utils/formatters';
import { getServiceBadgeStyles } from '@utils/serviceColors';
import EvictedBadge from '@components/common/EvictedBadge';
import Badge from '@components/ui/Badge';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EAIcon } from '@components/ui/EAIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
import { Tooltip } from '@components/ui/Tooltip';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { GameImage } from '@components/common/GameImage';
import { useHoldTimer } from '@hooks/useHoldTimer';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';
import { useGroupPagination } from '@hooks/useGroupPagination';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import DownloadBadges from './DownloadBadges';
import { useSessionFilters } from './useSessionFilters';
import SessionFilterBar from './SessionFilterBar';
import { resolveGameDetection } from '@utils/gameDetection';
import type { Download, DownloadGroup, GameCacheInfo } from '../../../types';

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
  detectionLookup?: Map<number, GameCacheInfo> | null;
  detectionByName?: Map<string, GameCacheInfo> | null;
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
  detectionLookup?: Map<number, GameCacheInfo> | null;
  detectionByName?: Map<string, GameCacheInfo> | null;
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
    itemsPerSession: filters.itemsPerSession,
    groupId: group.id,
    groupPages,
    setGroupPages,
    startHoldTimer,
    stopHoldTimer
  });
  const [expandedIps, setExpandedIps] = React.useState<Record<string, boolean>>({});
  const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
  const primaryDownload = group.downloads[0];
  const serviceLower = (group.service ?? '').toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEpic = serviceLower === 'epic' || serviceLower === 'epicgames';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = serviceLower === 'xbox' || serviceLower === 'xboxlive';
  const isOtherService =
    !isSteam && !isWsus && !isRiot && !isEpic && !isEA && !isBlizzard && !isXbox;
  const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
  const epicAppId = primaryDownload?.epicAppId ?? null;
  const primaryName = primaryDownload?.gameName ?? '';
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
  const showSteamImage = isSteam && availableImages.has(String(primaryDownload?.gameAppId ?? ''));
  const showEpicImage = isEpic && availableImages.has(primaryDownload?.epicAppId ?? '');
  const storeLink = primaryDownload?.gameAppId
    ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
    : null;
  const shouldRenderBanner =
    !aestheticMode &&
    (isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService);
  const artworkId = showSteamImage ? steamAppId : showEpicImage ? `epic-${epicAppId}` : null;
  const hasArtwork = artworkId !== null && !imageErrors.has(artworkId);
  // Full-height: banner at natural aspect ratio, each row height varies
  // Fit-to-row: fixed height, banner centered with overflow hidden, uniform rows
  const placeholderIconSize = fullHeightBanners ? 80 : 72;
  const bannerWrapperClasses = fullHeightBanners
    ? 'download-banner-mobile sm:w-[280px] sm:self-start'
    : 'download-banner-mobile sm:w-[340px] sm:self-stretch';

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
  React.useEffect(() => {
    const downloadIds = group.downloads.map((d) => d.id);
    fetchAssociations(downloadIds);
  }, [group.downloads, fetchAssociations, refreshVersion]);

  // Aggregate unique events from all downloads in the group
  const groupEvents = React.useMemo(() => {
    const eventsMap = new Map<
      number,
      { id: number; name: string; colorIndex: number; autoTagged: boolean }
    >();
    group.downloads.forEach((d) => {
      const associations = getAssociations(d.id);
      associations.events.forEach((event) => {
        if (!eventsMap.has(event.id)) {
          eventsMap.set(event.id, { ...event, autoTagged: event.autoTagged ?? false });
        }
      });
    });
    return Array.from(eventsMap.values());
  }, [group.downloads, getAssociations]);

  let bannerContent: React.ReactNode | null = null;

  if (shouldRenderBanner) {
    if (hasArtwork && artworkId) {
      bannerContent = (
        <GameImage
          gameAppId={showEpicImage ? epicAppId! : steamAppId!}
          epicAppId={showEpicImage ? epicAppId! : undefined}
          alt={primaryName || group.name}
          className={fullHeightBanners ? 'download-banner-image-natural' : 'download-banner-image'}
          sizes="(max-width: 639px) 100vw, 280px"
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

  const cardContent = (
    <div
      className={`flex flex-col sm:flex-row ${fullHeightBanners ? 'download-card-fullheight' : 'sm:h-[160px]'}`}
    >
      {bannerContent && (
        <>
          <div className={`flex-shrink-0 overflow-hidden ${bannerWrapperClasses}`}>
            {bannerContent}
          </div>
          <div className="hidden sm:block w-px flex-shrink-0 self-stretch bg-[var(--theme-border-secondary)]" />
        </>
      )}
      <div
        className={`flex-1 ${
          fullHeightBanners
            ? 'download-card-content px-3 py-3 sm:px-4 sm:py-3'
            : 'px-3 py-3 sm:px-5 sm:py-4 sm:overflow-hidden'
        }`}
      >
        {/* Mobile Layout - Clean and Spacious */}
        <div className="sm:hidden">
          <div className="flex items-start gap-2.5">
            <ChevronRight
              size={16}
              className={`mt-1 text-[var(--theme-primary)] transition-transform duration-200 flex-shrink-0 ${isExpanded ? 'rotate-90' : ''}`}
              style={{ opacity: isExpanded ? 1 : 0.6 }}
            />
            <div className="flex-1 min-w-0">
              {/* Title Row - Service badge and game name */}
              <div className="flex items-center gap-2 mb-2 flex-wrap">
                <span
                  className="px-2 py-0.5 text-[11px] font-extrabold rounded-md shadow-sm flex-shrink-0"
                  style={getServiceBadgeStyles(group.service)}
                >
                  {group.service.toUpperCase()}
                </span>
                {group.downloads.some(
                  (d: Download) =>
                    d.gameName && d.gameName !== d.service && !d.gameName.match(/^Steam App \d+$/)
                ) && (
                  <h3 className="text-sm font-bold text-[var(--theme-text-primary)] truncate flex-1 min-w-0">
                    {group.name}
                  </h3>
                )}
                {isEvicted && <EvictedBadge />}
                {isPartiallyEvicted && (
                  <Badge variant="warning">{t('common.partiallyEvicted')}</Badge>
                )}
                {diskSizeBytes ? (
                  <span className="text-themed-muted text-xs ml-2">
                    {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })}
                  </span>
                ) : null}
              </div>

              {/* Mobile Stats - Stacked layout for better spacing */}
              <div className="flex flex-col gap-1.5 text-xs">
                {/* Primary stats row */}
                <div className="flex items-center gap-3">
                  <span className="font-semibold text-[var(--theme-text-primary)]">
                    {formatBytes(group.totalBytes)}
                  </span>
                  {showCacheHitBar &&
                    (hitPercent > 0 ? (
                      <span className="cache-hit font-semibold">{formatPercent(hitPercent)}</span>
                    ) : (
                      <span className="text-[var(--theme-text-muted)]">0%</span>
                    ))}
                  {group.count > 1 && (
                    <span className="text-[var(--theme-text-muted)]">{group.count} req</span>
                  )}
                </div>
                {/* Disk usage row */}
                {diskSizeBytes ? (
                  <div className="flex items-center gap-1 text-[var(--theme-text-muted)]">
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
                {/* Secondary stats row */}
                <div className="flex items-center gap-1 text-[var(--theme-text-muted)]">
                  <Clock size={10} className="flex-shrink-0" />
                  <span>{formatRelativeTime(group.lastSeen)}</span>
                </div>
              </div>

              {/* Event badges - only show if present */}
              {showEventBadges && groupEvents.length > 0 && (
                <div className="mt-2">
                  <DownloadBadges events={groupEvents} maxVisible={2} size="sm" />
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Desktop Layout - Full stats */}
        <div className="hidden sm:block">
          <div className="flex items-start gap-4">
            <ChevronRight
              size={18}
              className={`mt-1 text-[var(--theme-primary)] transition-transform duration-200 flex-shrink-0 ${isExpanded ? 'rotate-90' : ''}`}
              style={{ opacity: isExpanded ? 1 : 0.6 }}
            />
            <div className="flex-1 min-w-0">
              {/* Title Row */}
              <div
                className={`flex flex-row items-center gap-3 ${fullHeightBanners ? 'mb-2' : 'mb-3'}`}
              >
                <span
                  className={`${fullHeightBanners ? 'px-1.5 py-0.5 text-xs' : 'px-2.5 py-1 text-xs'} font-extrabold rounded-md shadow-sm`}
                  style={getServiceBadgeStyles(group.service)}
                >
                  {group.service.toUpperCase()}
                </span>
                {group.downloads.some(
                  (d: Download) =>
                    d.gameName && d.gameName !== d.service && !d.gameName.match(/^Steam App \d+$/)
                ) && (
                  <h3
                    className={`${fullHeightBanners ? 'text-lg' : 'text-xl'} font-bold text-[var(--theme-text-primary)] truncate`}
                  >
                    {group.name}
                  </h3>
                )}
                {isEvicted && <EvictedBadge />}
                {isPartiallyEvicted && (
                  <Badge variant="warning">{t('common.partiallyEvicted')}</Badge>
                )}
                {hasMultipleDatasources &&
                  showDatasourceLabels &&
                  group.downloads[0]?.datasource && (
                    <Tooltip
                      content={t('downloads.tab.normal.datasourceTooltip', {
                        datasource: group.downloads[0].datasource
                      })}
                    >
                      <span
                        className={`${fullHeightBanners ? 'px-1.5 py-0.5 text-xs' : 'px-2.5 py-1 text-xs'} font-medium rounded-md flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]`}
                      >
                        {group.downloads[0].datasource}
                      </span>
                    </Tooltip>
                  )}
                {diskSizeBytes ? (
                  <span className="text-themed-muted text-xs ml-2">
                    {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })}
                  </span>
                ) : null}
                {showEventBadges && groupEvents.length > 0 && (
                  <DownloadBadges events={groupEvents} maxVisible={2} size="sm" />
                )}
                {group.count > 1 && (
                  <span
                    className={`${fullHeightBanners ? 'px-1.5 py-0.5 text-xs' : 'px-2.5 py-1 text-xs'} font-semibold rounded-full bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] flex-shrink-0`}
                  >
                    {group.clientsSet.size} client{group.clientsSet.size !== 1 ? 's' : ''} ·{' '}
                    {group.count} request{group.count !== 1 ? 's' : ''}
                  </span>
                )}
              </div>

              {/* Stats Grid */}
              <div
                className={`grid grid-cols-2 ${fullHeightBanners ? 'gap-x-4 gap-y-1' : 'gap-x-8 gap-y-2'}`}
              >
                <div className="flex items-baseline gap-2">
                  <span
                    className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[80px]'}`}
                  >
                    {t('downloads.tab.normal.stats.totalDownloaded')}
                  </span>
                  <span
                    className={`${fullHeightBanners ? 'text-sm' : 'text-base'} font-bold text-[var(--theme-text-primary)]`}
                  >
                    {formatBytes(group.totalBytes)}
                  </span>
                </div>
                <div className="flex items-baseline gap-2">
                  <span
                    className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[80px]'}`}
                  >
                    Clients
                  </span>
                  <span
                    className={`${fullHeightBanners ? 'text-sm' : 'text-base'} font-bold text-[var(--theme-text-primary)]`}
                  >
                    {group.clientsSet.size}
                  </span>
                </div>
                {showCacheHitBar && (
                  <div className="flex items-baseline gap-2">
                    <span
                      className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[80px]'}`}
                    >
                      {t('downloads.tab.normal.stats.cacheSaved')}
                    </span>
                    <span
                      className={`${fullHeightBanners ? 'text-sm' : 'text-base'} font-bold text-[var(--theme-success-text)]`}
                    >
                      {formatBytes(group.cacheHitBytes)}
                    </span>
                  </div>
                )}
                <div className="flex items-baseline gap-2">
                  <span
                    className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[80px]'}`}
                  >
                    Last Active
                  </span>
                  <span
                    className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} font-medium text-[var(--theme-text-secondary)] inline-flex items-center gap-1.5`}
                  >
                    <Clock size={14} />
                    {formatRelativeTime(group.lastSeen)}
                  </span>
                </div>
                {showCacheHitBar && (
                  <div className="flex items-baseline gap-2">
                    <span
                      className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} text-themed-muted font-medium ${fullHeightBanners ? 'min-w-[60px]' : 'min-w-[80px]'}`}
                    >
                      {t('downloads.tab.normal.stats.efficiency')}
                    </span>
                    <span
                      className={`${fullHeightBanners ? 'text-xs' : 'text-sm'} font-bold inline-flex items-center gap-1.5 ${
                        hitPercent > 0 ? 'cache-hit' : 'text-[var(--theme-text-secondary)]'
                      }`}
                    >
                      {hitPercent > 0
                        ? formatPercent(hitPercent)
                        : t('downloads.tab.normal.stats.notAvailable')}
                    </span>
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );

  return (
    <div
      ref={cardRef}
      className={`rounded-lg border overflow-hidden shadow-sm bg-[var(--theme-bg-secondary)] ${
        isExpanded ? 'ring-2 border-[var(--theme-primary)]' : 'border-[var(--theme-border-primary)]'
      } ${!fullHeightBanners && !isExpanded ? 'sm:max-h-[160px]' : ''}${isEvicted ? ' opacity-60' : ''}`}
    >
      {fullHeightBanners ? (
        <div
          onClick={() => onItemClick(group.id)}
          className="w-full text-left cursor-pointer bg-[var(--theme-bg-secondary)] hover:bg-[var(--theme-bg-tertiary)]"
        >
          {cardContent}
        </div>
      ) : (
        <button
          onClick={() => onItemClick(group.id)}
          className="w-full text-left hover:bg-[var(--theme-bg-tertiary)] bg-[var(--theme-bg-secondary)]"
        >
          {cardContent}
        </button>
      )}

      {isExpanded && (
        <div
          className="border-t border-[var(--theme-primary)] bg-[var(--theme-bg-secondary)] px-4 pb-4 pt-4 sm:px-6 sm:pb-6 sm:pt-5 animate-[expandDown_0.4s_cubic-bezier(0.4,0,0.2,1)]"
          onClick={(event) => event.stopPropagation()}
        >
          <div className="flex flex-col gap-6">
            {/* Stats Overview Section */}
            <div>
              <div className="flex items-center justify-between mb-3">
                <h4 className="text-sm font-bold text-[var(--theme-text-primary)] uppercase tracking-wider opacity-80">
                  {t('downloads.tab.normal.stats.title', 'Analytics Overview')}
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
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
                  <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
                    Efficiency
                  </h5>
                  <div className="flex flex-col gap-4">
                    <div className="flex items-baseline justify-between">
                      <span className="text-sm text-[var(--theme-text-secondary)]">
                        {t('downloads.tab.normal.stats.efficiencyRate')}
                      </span>
                      <span
                        className={`text-xl font-bold ${hitPercent > 0 ? 'cache-hit' : 'text-[var(--theme-text-secondary)]'}`}
                      >
                        {hitPercent > 0 ? formatPercent(hitPercent) : '—'}
                      </span>
                    </div>
                    <div className="w-full bg-[var(--theme-bg-primary)] rounded-full h-1.5 overflow-hidden">
                      <div
                        className="h-full bg-[var(--theme-success)] transition-all duration-500"
                        style={{ width: `${hitPercent}%` }}
                      />
                    </div>
                    <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                      <span className="text-xs text-[var(--theme-text-muted)]">
                        {t('downloads.tab.normal.stats.cacheSaved')}
                      </span>
                      <span className="text-sm font-bold text-[var(--theme-success-text)]">
                        {group.cacheHitBytes > 0 ? formatBytes(group.cacheHitBytes) : '—'}
                      </span>
                    </div>
                  </div>
                </div>

                {/* Disk Usage */}
                {diskSizeBytes ? (
                  <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
                    <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
                      {t('downloads.tab.normal.stats.diskUsage', 'Disk Usage')}
                    </h5>
                    <div className="flex flex-col gap-4">
                      <div className="flex items-baseline justify-between">
                        <span className="text-sm text-[var(--theme-text-secondary)]">
                          {t('downloads.tab.normal.stats.dataOnDisk', 'Data on Disk')}
                        </span>
                        <span className="text-xl font-bold text-[var(--theme-primary)]">
                          {formatBytes(diskSizeBytes)}
                        </span>
                      </div>
                      <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                        <span className="text-xs text-[var(--theme-text-muted)]">
                          {t('downloads.tab.normal.stats.cacheFiles', 'Cache Files')}
                        </span>
                        <span className="text-sm font-bold text-[var(--theme-text-secondary)]">
                          {detection?.cache_files_found != null
                            ? formatCount(detection.cache_files_found)
                            : '—'}
                        </span>
                      </div>
                    </div>
                  </div>
                ) : null}

                {/* Network Traffic */}
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
                  <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
                    {t('downloads.tab.normal.stats.networkTraffic', 'Network Traffic')}
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
                        {formatBytes(group.cacheMissBytes || 0)}
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
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
                  <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
                    Activity
                  </h5>
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
                        {formatRelativeTime(group.lastSeen)}
                      </span>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            {/* Download Sessions List */}
            {group.downloads.length > 0 &&
              (() => {
                const toggleIp = (ip: string) => {
                  setExpandedIps((prev) => ({ ...prev, [ip]: !prev[ip] }));
                };
                const isIpExpanded = (ip: string, count: number): boolean => {
                  if (ip in expandedIps) return expandedIps[ip];
                  return count <= 5;
                };

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

                    {/* Filter bar — only shown for groups with more than 10 downloads */}
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

                    {/* Group sessions by client IP — collapsible */}
                    <div className="space-y-4">
                      {Object.entries(ipGroups).map(([clientIp, clientDownloads]) => {
                        const clientTotal = clientDownloads.reduce(
                          (sum, d) => sum + (d.totalBytes || 0),
                          0
                        );
                        const clientCacheHit = clientDownloads.reduce(
                          (sum, d) => sum + (d.cacheHitBytes || 0),
                          0
                        );
                        const expanded = isIpExpanded(clientIp, clientDownloads.length);

                        return (
                          <div
                            key={clientIp}
                            className="rounded-lg border border-[var(--theme-border-secondary)] overflow-hidden"
                          >
                            {/* Client Header — clickable to collapse/expand */}
                            <button
                              type="button"
                              onClick={() => toggleIp(clientIp)}
                              className="w-full bg-[var(--theme-bg-tertiary)] px-4 py-2 flex flex-wrap items-center justify-between gap-1 border-b border-[var(--theme-border-secondary)] text-left"
                            >
                              <div className="flex items-center gap-2">
                                <ChevronDown
                                  size={14}
                                  className={`text-[var(--theme-text-muted)] transition-transform duration-200 flex-shrink-0 ${expanded ? '' : '-rotate-90'}`}
                                />
                                <ClientIpDisplay
                                  clientIp={clientIp}
                                  className="font-mono text-xs font-bold text-[var(--theme-text-primary)]"
                                />
                                <span className="text-[10px] uppercase tracking-wide text-[var(--theme-text-muted)] font-semibold px-1.5 py-0.5 rounded bg-[var(--theme-bg-primary)]">
                                  {clientDownloads.length} sessions
                                </span>
                              </div>
                              <div className="flex items-center gap-3 text-xs">
                                <span className="font-medium text-[var(--theme-text-secondary)]">
                                  Total:{' '}
                                  <span className="text-[var(--theme-text-primary)] font-bold">
                                    {formatBytes(clientTotal)}
                                  </span>
                                </span>
                                {clientCacheHit > 0 && (
                                  <span className="font-medium text-[var(--theme-success-text)]">
                                    Saved:{' '}
                                    <span className="font-bold">{formatBytes(clientCacheHit)}</span>
                                  </span>
                                )}
                              </div>
                            </button>

                            {/* Sessions Table-like list — shown only when expanded */}
                            {expanded && (
                              <div className="divide-y divide-[var(--theme-border-secondary)]">
                                {clientDownloads.map((download) => {
                                  const totalBytes = download.totalBytes || 0;
                                  const cachePercent =
                                    totalBytes > 0
                                      ? ((download.cacheHitBytes || 0) / totalBytes) * 100
                                      : 0;
                                  const associations = getAssociations(download.id);

                                  return (
                                    <div
                                      key={download.id}
                                      className={`drawer-session-row px-4 py-3 transition-colors${download.isEvicted ? ' opacity-60' : ''}`}
                                    >
                                      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
                                        {/* Time & Events */}
                                        <div className="flex-1 min-w-0">
                                          <div className="flex items-center gap-2 mb-1">
                                            {download.endTimeUtc ? (
                                              <CheckCircle
                                                size={14}
                                                className="text-[var(--theme-success-text)]"
                                              />
                                            ) : (
                                              <AlertCircle
                                                size={14}
                                                className="text-[var(--theme-info-text)]"
                                              />
                                            )}
                                            <span className="text-sm text-[var(--theme-text-primary)]">
                                              {formatRelativeTime(download.startTimeUtc)}
                                            </span>
                                            {download.depotId && (
                                              <span className="text-xs font-mono text-[var(--theme-text-muted)] bg-[var(--theme-bg-tertiary)] px-1.5 rounded">
                                                {download.depotId}
                                              </span>
                                            )}
                                            {download.isEvicted && <EvictedBadge />}
                                          </div>
                                          {showEventBadges && associations.events.length > 0 && (
                                            <div className="mt-1">
                                              <DownloadBadges
                                                events={associations.events}
                                                maxVisible={3}
                                                size="sm"
                                              />
                                            </div>
                                          )}
                                        </div>

                                        {/* Stats */}
                                        <div className="flex items-center gap-4 sm:gap-6 text-sm">
                                          <div className="flex flex-col items-end">
                                            <span className="text-[10px] uppercase text-[var(--theme-text-muted)] font-semibold">
                                              Size
                                            </span>
                                            <span className="font-medium text-[var(--theme-text-primary)]">
                                              {formatBytes(totalBytes)}
                                            </span>
                                          </div>
                                          <div className="flex flex-col items-end w-20">
                                            <span className="text-[10px] uppercase text-[var(--theme-text-muted)] font-semibold">
                                              Cache
                                            </span>
                                            {download.cacheHitBytes > 0 ? (
                                              <div className="flex items-center gap-1.5">
                                                <span className="font-bold text-[var(--theme-success-text)]">
                                                  {formatPercent(cachePercent)}
                                                </span>
                                              </div>
                                            ) : (
                                              <span className="text-[var(--theme-text-muted)]">
                                                —
                                              </span>
                                            )}
                                          </div>
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

                    {/* Pagination Controls */}
                    {totalPages > 1 && (
                      <div className="flex items-center justify-center gap-2 mt-4 pt-2">
                        <Tooltip content={t('downloads.tab.normal.pagination.previous')}>
                          <button
                            onClick={() => handlePageChange(currentPage - 1)}
                            onPointerDown={(event) => handlePointerHoldStart(event, 'prev')}
                            onPointerUp={handlePointerHoldEnd}
                            onPointerCancel={handlePointerHoldEnd}
                            onLostPointerCapture={stopHoldTimer}
                            disabled={currentPage === 1}
                            className="p-1.5 rounded-full transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]"
                          >
                            <ChevronLeft size={16} />
                          </button>
                        </Tooltip>

                        <span className="text-xs text-[var(--theme-text-secondary)] font-medium font-mono px-2">
                          {currentPage} of {totalPages}
                        </span>

                        <Tooltip content={t('downloads.tab.normal.pagination.next')}>
                          <button
                            onClick={() => handlePageChange(currentPage + 1)}
                            onPointerDown={(event) => handlePointerHoldStart(event, 'next')}
                            onPointerUp={handlePointerHoldEnd}
                            onPointerCancel={handlePointerHoldEnd}
                            onLostPointerCapture={stopHoldTimer}
                            disabled={currentPage === totalPages}
                            className="p-1.5 rounded-full transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]"
                          >
                            <ChevronRight size={16} />
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
  const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
  const primaryDownload = group.downloads[0];
  const serviceLower = (group.service ?? '').toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isEpic = serviceLower === 'epic' || serviceLower === 'epicgames';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = serviceLower === 'xbox' || serviceLower === 'xboxlive';
  const isOtherService =
    !isSteam && !isWsus && !isRiot && !isEpic && !isEA && !isBlizzard && !isXbox;
  const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
  const epicAppId = primaryDownload?.epicAppId ?? null;
  const primaryName = primaryDownload?.gameName ?? '';
  const showSteamImage = isSteam && availableImages.has(String(primaryDownload?.gameAppId ?? ''));
  const showEpicImage = isEpic && availableImages.has(primaryDownload?.epicAppId ?? '');
  const artworkId = showSteamImage ? steamAppId : showEpicImage ? `epic-${epicAppId}` : null;
  const hasArtwork = artworkId !== null && !imageErrors.has(artworkId);
  const isEvicted = group.downloads.every((d: Download) => d.isEvicted);
  const isPartiallyEvicted = !isEvicted && group.downloads.some((d: Download) => d.isEvicted);
  const placeholderIconSize = 48;

  React.useEffect(() => {
    const downloadIds = group.downloads.map((d) => d.id);
    fetchAssociations(downloadIds);
  }, [group.downloads, fetchAssociations, refreshVersion]);

  const groupEvents = React.useMemo(() => {
    const eventsMap = new Map<
      number,
      { id: number; name: string; colorIndex: number; autoTagged: boolean }
    >();
    group.downloads.forEach((d) => {
      const associations = getAssociations(d.id);
      associations.events.forEach((event) => {
        if (!eventsMap.has(event.id)) {
          eventsMap.set(event.id, { ...event, autoTagged: event.autoTagged ?? false });
        }
      });
    });
    return Array.from(eventsMap.values());
  }, [group.downloads, getAssociations]);

  // Build banner content for the card
  const shouldRenderBanner =
    isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService;

  let bannerContent: React.ReactNode | null = null;
  if (shouldRenderBanner) {
    if (hasArtwork && artworkId) {
      bannerContent = (
        <GameImage
          gameAppId={showEpicImage ? epicAppId! : steamAppId!}
          epicAppId={showEpicImage ? epicAppId! : undefined}
          alt={primaryName || group.name}
          className="download-banner-image"
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
      title={bannerOnly ? group.name : undefined}
    >
      {/* Banner */}
      <div className="card-grid-item-banner">{bannerContent}</div>

      {/* Info */}
      {!bannerOnly && (
        <div className="card-grid-item-info">
          <div className="flex items-center gap-2 mb-1">
            <span
              className="px-2 py-0.5 text-[11px] font-extrabold rounded-md shadow-sm flex-shrink-0"
              style={getServiceBadgeStyles(group.service)}
            >
              {group.service.toUpperCase()}
            </span>
            {hasMultipleDatasources && showDatasourceLabels && group.downloads[0]?.datasource && (
              <span className="px-1.5 py-0.5 text-[10px] font-medium rounded-md flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]">
                {group.downloads[0].datasource}
              </span>
            )}
          </div>
          <div className="card-grid-item-name" title={group.name}>
            {group.name}
            {isEvicted && <EvictedBadge className="ml-1" />}
            {isPartiallyEvicted && (
              <Badge variant="warning" className="ml-1">
                {t('common.partiallyEvicted')}
              </Badge>
            )}
          </div>
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
              <span className="text-[var(--theme-text-muted)]">{group.count} req</span>
            )}
          </div>

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
  detectionLookup?: Map<number, GameCacheInfo> | null;
  detectionByName?: Map<string, GameCacheInfo> | null;
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
  const [expandedIps, setExpandedIps] = React.useState<Record<string, boolean>>({});
  const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
  const primaryDownload = group.downloads[0];
  const serviceLower = (group.service ?? '').toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isEpic = serviceLower === 'epic' || serviceLower === 'epicgames';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = serviceLower === 'xbox' || serviceLower === 'xboxlive';
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
  const artworkId = showSteamImage ? steamAppId : showEpicImage ? `epic-${epicAppId}` : null;
  const hasArtwork = artworkId !== null && !imageErrors.has(artworkId);
  const storeLink = primaryDownload?.gameAppId
    ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
    : null;

  React.useEffect(() => {
    const downloadIds = group.downloads.map((d) => d.id);
    fetchAssociations(downloadIds);
  }, [group.downloads, fetchAssociations, refreshVersion]);

  const groupEvents = React.useMemo(() => {
    const eventsMap = new Map<
      number,
      { id: number; name: string; colorIndex: number; autoTagged: boolean }
    >();
    group.downloads.forEach((d) => {
      const associations = getAssociations(d.id);
      associations.events.forEach((event) => {
        if (!eventsMap.has(event.id)) {
          eventsMap.set(event.id, { ...event, autoTagged: event.autoTagged ?? false });
        }
      });
    });
    return Array.from(eventsMap.values());
  }, [group.downloads, getAssociations]);

  // Build banner for drawer header
  const shouldRenderBanner =
    isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService;

  let drawerBanner: React.ReactNode | null = null;
  if (shouldRenderBanner && hasArtwork && artworkId) {
    drawerBanner = (
      <GameImage
        gameAppId={showEpicImage ? epicAppId! : steamAppId!}
        epicAppId={showEpicImage ? epicAppId! : undefined}
        alt={primaryName || group.name}
        className="drawer-banner-image"
        sizes="(max-width: 639px) 100vw, 550px"
        onError={handleImageError}
      />
    );
  }

  // Session pagination
  const toggleIp = (ip: string) => {
    setExpandedIps((prev) => ({ ...prev, [ip]: !prev[ip] }));
  };
  const isIpExpanded = (ip: string, count: number): boolean => {
    if (ip in expandedIps) return expandedIps[ip];
    return count <= 5;
  };

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
    itemsPerSession: filters.itemsPerSession,
    groupId: group.id,
    groupPages,
    setGroupPages,
    startHoldTimer,
    stopHoldTimer
  });

  return (
    <div className="drawer-detail-content">
      {/* Banner */}
      {drawerBanner && <div className="drawer-banner-wrapper">{drawerBanner}</div>}

      {/* Title area with service badge */}
      <div className="flex items-center gap-2 mb-4 flex-wrap">
        <span
          className="px-2.5 py-1 text-xs font-extrabold rounded-md shadow-sm flex-shrink-0"
          style={getServiceBadgeStyles(group.service)}
        >
          {group.service.toUpperCase()}
        </span>
        {hasMultipleDatasources && showDatasourceLabels && group.downloads[0]?.datasource && (
          <span className="px-2 py-0.5 text-xs font-medium rounded-md flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]">
            {group.downloads[0].datasource}
          </span>
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
          {t('downloads.tab.normal.stats.title', 'Analytics Overview')}
        </h4>

        <div className="flex flex-col gap-3">
          {/* Efficiency */}
          <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
            <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
              Efficiency
            </h5>
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
                  className="h-full bg-[var(--theme-success)] transition-all duration-500"
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
            <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
              <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
                {t('downloads.tab.normal.stats.diskUsage', 'Disk Usage')}
              </h5>
              <div className="flex flex-col gap-4">
                <div className="flex items-baseline justify-between">
                  <span className="text-sm text-[var(--theme-text-secondary)]">
                    {t('downloads.tab.normal.stats.dataOnDisk', 'Data on Disk')}
                  </span>
                  <span className="text-xl font-bold text-[var(--theme-primary)]">
                    {formatBytes(diskSizeBytes)}
                  </span>
                </div>
                <div className="flex items-baseline justify-between pt-2 border-t border-[var(--theme-border-secondary)]">
                  <span className="text-xs text-[var(--theme-text-muted)]">
                    {t('downloads.tab.normal.stats.cacheFiles', 'Cache Files')}
                  </span>
                  <span className="text-sm font-bold text-[var(--theme-text-secondary)]">
                    {detection?.cache_files_found != null
                      ? formatCount(detection.cache_files_found)
                      : '—'}
                  </span>
                </div>
              </div>
            </div>
          ) : null}

          {/* Network Traffic */}
          <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
            <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
              {t('downloads.tab.normal.stats.networkTraffic', 'Network Traffic')}
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
                  {formatBytes(group.cacheMissBytes || 0)}
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
          <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
            <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
              Activity
            </h5>
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
                  {formatRelativeTime(group.lastSeen)}
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

          <div className="space-y-4">
            {Object.entries(ipGroups).map(([clientIp, clientDownloads]) => {
              const clientTotal = clientDownloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
              const clientCacheHit = clientDownloads.reduce(
                (sum, d) => sum + (d.cacheHitBytes || 0),
                0
              );
              const expanded = isIpExpanded(clientIp, clientDownloads.length);

              return (
                <div
                  key={clientIp}
                  className="rounded-lg border border-[var(--theme-border-secondary)] overflow-hidden"
                >
                  <button
                    type="button"
                    onClick={() => toggleIp(clientIp)}
                    className="w-full bg-[var(--theme-bg-tertiary)] px-4 py-2 flex flex-wrap items-center justify-between gap-1 border-b border-[var(--theme-border-secondary)] text-left"
                  >
                    <div className="flex items-center gap-2">
                      <ChevronDown
                        size={14}
                        className={`text-[var(--theme-text-muted)] transition-transform duration-200 flex-shrink-0 ${expanded ? '' : '-rotate-90'}`}
                      />
                      <ClientIpDisplay
                        clientIp={clientIp}
                        className="font-mono text-xs font-bold text-[var(--theme-text-primary)]"
                      />
                      <span className="text-[10px] uppercase tracking-wide text-[var(--theme-text-muted)] font-semibold px-1.5 py-0.5 rounded bg-[var(--theme-bg-primary)]">
                        {clientDownloads.length} sessions
                      </span>
                    </div>
                    <div className="flex items-center gap-3 text-xs">
                      <span className="font-medium text-[var(--theme-text-secondary)]">
                        Total:{' '}
                        <span className="text-[var(--theme-text-primary)] font-bold">
                          {formatBytes(clientTotal)}
                        </span>
                      </span>
                      {clientCacheHit > 0 && (
                        <span className="font-medium text-[var(--theme-success-text)]">
                          Saved: <span className="font-bold">{formatBytes(clientCacheHit)}</span>
                        </span>
                      )}
                    </div>
                  </button>

                  {expanded && (
                    <div className="divide-y divide-[var(--theme-border-secondary)]">
                      {clientDownloads.map((download) => {
                        const totalBytes = download.totalBytes || 0;
                        const cachePercent =
                          totalBytes > 0 ? ((download.cacheHitBytes || 0) / totalBytes) * 100 : 0;
                        const associations = getAssociations(download.id);

                        return (
                          <div
                            key={download.id}
                            className="drawer-session-row px-4 py-3 transition-colors"
                          >
                            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
                              <div className="flex-1 min-w-0">
                                <div className="flex items-center gap-2 mb-1">
                                  {download.endTimeUtc ? (
                                    <CheckCircle
                                      size={14}
                                      className="text-[var(--theme-success-text)]"
                                    />
                                  ) : (
                                    <AlertCircle
                                      size={14}
                                      className="text-[var(--theme-info-text)]"
                                    />
                                  )}
                                  <span className="text-sm text-[var(--theme-text-primary)]">
                                    {formatRelativeTime(download.startTimeUtc)}
                                  </span>
                                  {download.depotId && (
                                    <span className="text-xs font-mono text-[var(--theme-text-muted)] bg-[var(--theme-bg-tertiary)] px-1.5 rounded">
                                      {download.depotId}
                                    </span>
                                  )}
                                </div>
                                {showEventBadges && associations.events.length > 0 && (
                                  <div className="mt-1">
                                    <DownloadBadges
                                      events={associations.events}
                                      maxVisible={3}
                                      size="sm"
                                    />
                                  </div>
                                )}
                              </div>
                              <div className="flex items-center gap-4 sm:gap-6 text-sm">
                                <div className="flex flex-col items-end">
                                  <span className="text-[10px] uppercase text-[var(--theme-text-muted)] font-semibold">
                                    Size
                                  </span>
                                  <span className="font-medium text-[var(--theme-text-primary)]">
                                    {formatBytes(totalBytes)}
                                  </span>
                                </div>
                                <div className="flex flex-col items-end w-20">
                                  <span className="text-[10px] uppercase text-[var(--theme-text-muted)] font-semibold">
                                    Cache
                                  </span>
                                  {download.cacheHitBytes > 0 ? (
                                    <div className="flex items-center gap-1.5">
                                      <span className="font-bold text-[var(--theme-success-text)]">
                                        {formatPercent(cachePercent)}
                                      </span>
                                    </div>
                                  ) : (
                                    <span className="text-[var(--theme-text-muted)]">
                                      {'\u2014'}
                                    </span>
                                  )}
                                </div>
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

          {totalPages > 1 && (
            <div className="flex items-center justify-center gap-2 mt-4 pt-2">
              <Tooltip content={t('downloads.tab.normal.pagination.previous')}>
                <button
                  onClick={() => handlePageChange(currentPage - 1)}
                  onPointerDown={(event) => handlePointerHoldStart(event, 'prev')}
                  onPointerUp={handlePointerHoldEnd}
                  onPointerCancel={handlePointerHoldEnd}
                  onLostPointerCapture={stopHoldTimer}
                  disabled={currentPage === 1}
                  className="p-1.5 rounded-full transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]"
                >
                  <ChevronLeft size={16} />
                </button>
              </Tooltip>
              <span className="text-xs text-[var(--theme-text-secondary)] font-medium font-mono px-2">
                {currentPage} of {totalPages}
              </span>
              <Tooltip content={t('downloads.tab.normal.pagination.next')}>
                <button
                  onClick={() => handlePageChange(currentPage + 1)}
                  onPointerDown={(event) => handlePointerHoldStart(event, 'next')}
                  onPointerUp={handlePointerHoldEnd}
                  onPointerCancel={handlePointerHoldEnd}
                  onLostPointerCapture={stopHoldTimer}
                  disabled={currentPage === totalPages}
                  className="p-1.5 rounded-full transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]"
                >
                  <ChevronRight size={16} />
                </button>
              </Tooltip>
            </div>
          )}
        </div>
      )}
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
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());
  const [groupPages, setGroupPages] = React.useState<Record<string, number>>({});
  const [drawerItem, setDrawerItem] = useState<DownloadGroup | null>(null);
  const { startHoldTimer, stopHoldTimer } = useHoldTimer();
  const availableImages = useAvailableGameImages();

  const SESSIONS_PER_PAGE = 10;

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
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

  const renderDownloadCard = (download: Download) => {
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

    return renderGroupCard(fakeGroup);
  };

  const toGroup = (item: Download | DownloadGroup): DownloadGroup => {
    if ('downloads' in item) return item as DownloadGroup;
    const download = item as Download;
    const totalBytes = download.totalBytes || 0;
    return {
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
  };

  // Card Grid Layout mode
  if (cardGridLayout) {
    const gridSizeClass =
      cardSize === 'small'
        ? 'card-grid-container card-size-small'
        : cardSize === 'large'
          ? 'card-grid-container card-size-large'
          : 'card-grid-container';

    const handleGridCardClick = (groupId: string) => {
      const group = items
        .map((item: Download | DownloadGroup) => toGroup(item))
        .find((g: DownloadGroup) => g.id === groupId);
      if (group) {
        setDrawerItem(group);
      }
    };

    return (
      <>
        <div className={gridSizeClass}>
          {items.map((item) => {
            const group = toGroup(item);
            const key =
              'downloads' in item
                ? (item as DownloadGroup).id
                : `download-${(item as Download).id}`;

            return (
              <GridCard
                key={key}
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
          })}
        </div>

        <Drawer
          opened={drawerItem !== null}
          onClose={() => setDrawerItem(null)}
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
      </>
    );
  }

  // Standard list layout
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
                <div className="section-divider mb-5 mt-8 first:mt-0">
                  <div className="section-divider-inner">
                    <div className="section-divider-accent" />
                    <div className="section-divider-content">
                      <h2 className="section-divider-title">{labels.multipleDownloads}</h2>
                      <p className="section-divider-description">
                        Games that have been downloaded multiple times
                      </p>
                    </div>
                  </div>
                </div>
              );
            } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
              singleDownloadsHeaderRendered = true;
              header = (
                <div className="section-divider mb-5 mt-8 first:mt-0">
                  <div className="section-divider-inner">
                    <div className="section-divider-accent" />
                    <div className="section-divider-content">
                      <h2 className="section-divider-title">{labels.singleDownloads}</h2>
                      <p className="section-divider-description">
                        Games downloaded once in a single session
                      </p>
                    </div>
                  </div>
                </div>
              );
            }
          } else if (!isGroup && !individualHeaderRendered) {
            individualHeaderRendered = true;
            header = (
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
          }
        }

        return (
          <React.Fragment key={key}>
            {header}
            {isGroup
              ? renderGroupCard(item as DownloadGroup)
              : renderDownloadCard(item as Download)}
          </React.Fragment>
        );
      })}
    </div>
  );
};

const MemoizedNormalView = memo(NormalView);
export default MemoizedNormalView;
