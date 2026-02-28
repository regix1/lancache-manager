import React from 'react';
import { useTranslation } from 'react-i18next';
import {
  ChevronRight,
  Clock,
  ExternalLink,
  CheckCircle,
  AlertCircle,
  ChevronLeft
} from 'lucide-react';
import { formatBytes, formatPercent, formatRelativeTime } from '@utils/formatters';
import { getServiceBadgeStyles } from '@utils/serviceColors';
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
import { useDownloadAssociations } from '@contexts/DownloadAssociationsContext';
import DownloadBadges from './DownloadBadges';
import type { Download, DownloadGroup } from '../../../types';

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
  SESSIONS_PER_PAGE,
  enableScrollIntoView,
  showDatasourceLabels,
  hasMultipleDatasources
}) => {
  const { t } = useTranslation();
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const isExpanded = expandedItem === group.id;
  const cardRef = React.useRef<HTMLDivElement>(null);
  const prevExpandedRef = React.useRef<boolean>(false);
  const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
  const primaryDownload = group.downloads[0];
  const serviceLower = group.service.toLowerCase();
  const isSteam = serviceLower === 'steam';
  const isWsus = serviceLower === 'wsus' || serviceLower === 'windows';
  const isRiot = serviceLower === 'riot' || serviceLower === 'riotgames';
  const isEpic = serviceLower === 'epic';
  const isEA = serviceLower === 'origin' || serviceLower === 'ea';
  const isBlizzard =
    serviceLower === 'blizzard' || serviceLower === 'battle.net' || serviceLower === 'battlenet';
  const isXbox = serviceLower === 'xbox' || serviceLower === 'xboxlive';
  const isOtherService =
    !isSteam && !isWsus && !isRiot && !isEpic && !isEA && !isBlizzard && !isXbox;
  const steamAppId = primaryDownload?.gameAppId ? String(primaryDownload.gameAppId) : null;
  const primaryName = primaryDownload?.gameName ?? '';
  const isGenericSteamTitle =
    primaryName === 'Unknown Steam Game' || /^Steam App \d+$/.test(primaryName);
  const showGameImage =
    group.type === 'game' &&
    isSteam &&
    Boolean(steamAppId) &&
    !!primaryName &&
    !isGenericSteamTitle;
  const storeLink = primaryDownload?.gameAppId
    ? `https://store.steampowered.com/app/${primaryDownload.gameAppId}`
    : null;
  const shouldRenderBanner =
    !aestheticMode &&
    (isSteam || isWsus || isRiot || isEpic || isEA || isBlizzard || isXbox || isOtherService);
  const hasSteamArtwork = showGameImage && steamAppId !== null && !imageErrors.has(steamAppId);
  // Mobile: use aspect-ratio for proper image display without cropping
  // Desktop: use fixed width with full height for side-by-side layout
  const placeholderIconSize = fullHeightBanners ? 80 : 72;
  const bannerWrapperClasses = fullHeightBanners
    ? 'download-banner-mobile sm:w-[280px] sm:aspect-auto sm:h-full'
    : 'download-banner-mobile sm:w-[280px] sm:aspect-auto sm:h-[130px] sm:self-start';

  React.useEffect(() => {
    if (!enableScrollIntoView) return;

    const wasExpanded = prevExpandedRef.current;
    prevExpandedRef.current = isExpanded;

    if (isExpanded && !wasExpanded && cardRef.current) {
      const timeoutId = setTimeout(() => {
        cardRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }, 450);
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
    if (hasSteamArtwork && steamAppId) {
      bannerContent = (
        <GameImage
          gameAppId={steamAppId}
          alt={primaryName || group.name}
          className="download-banner-image"
          sizes="(max-width: 639px) 100vw, 280px"
          onFinalError={handleImageError}
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
      className={`flex flex-col ${fullHeightBanners ? 'sm:flex-row sm:items-stretch' : 'sm:flex-row'}`}
    >
      {bannerContent && (
        <div
          className={`flex-shrink-0 overflow-hidden ${bannerWrapperClasses} sm:border-r sm:border-[var(--theme-border-secondary)]`}
        >
          {bannerContent}
        </div>
      )}
      <div
        className={`flex-1 ${
          fullHeightBanners ? 'px-3 py-3 sm:px-4 sm:py-3' : 'px-3 py-3 sm:px-5 sm:py-4'
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
                    d.gameName &&
                    d.gameName !== 'Unknown Steam Game' &&
                    !d.gameName.match(/^Steam App \d+$/)
                ) && (
                  <h3 className="text-sm font-bold text-[var(--theme-text-primary)] truncate flex-1 min-w-0">
                    {group.name}
                  </h3>
                )}
              </div>

              {/* Mobile Stats - Stacked layout for better spacing */}
              <div className="flex flex-col gap-1.5 text-xs">
                {/* Primary stats row */}
                <div className="flex items-center gap-3">
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
                {/* Secondary stats row */}
                <div className="flex items-center gap-1 text-[var(--theme-text-muted)]">
                  <Clock size={10} className="flex-shrink-0" />
                  <span>{formatRelativeTime(group.lastSeen)}</span>
                </div>
              </div>

              {/* Event badges - only show if present */}
              {groupEvents.length > 0 && (
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
                    d.gameName &&
                    d.gameName !== 'Unknown Steam Game' &&
                    !d.gameName.match(/^Steam App \d+$/)
                ) && (
                  <h3
                    className={`${fullHeightBanners ? 'text-lg' : 'text-xl'} font-bold text-[var(--theme-text-primary)] truncate`}
                  >
                    {group.name}
                  </h3>
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
                {groupEvents.length > 0 && (
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
      className={`rounded-lg border overflow-hidden shadow-sm ${
        isExpanded ? 'ring-2 border-[var(--theme-primary)]' : 'border-[var(--theme-border-primary)]'
      }`}
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

              <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
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

                {/* Data Volume */}
                <div className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
                  <h5 className="text-xs font-semibold text-[var(--theme-text-muted)] mb-3 uppercase tracking-wide">
                    Data Volume
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
                const currentPage = groupPages[group.id] || 1;
                const totalPages = Math.ceil(group.downloads.length / SESSIONS_PER_PAGE);
                const startIndex = (currentPage - 1) * SESSIONS_PER_PAGE;
                const endIndex = startIndex + SESSIONS_PER_PAGE;
                const paginatedDownloads = group.downloads.slice(startIndex, endIndex);
                const excludedSessions = Math.max(0, group.downloads.length - group.count);

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

                    {/* Group sessions by client IP */}
                    <div className="space-y-4">
                      {Object.entries(
                        paginatedDownloads.reduce(
                          (acc, d) => {
                            if (!acc[d.clientIp]) acc[d.clientIp] = [];
                            acc[d.clientIp].push(d);
                            return acc;
                          },
                          {} as Record<string, typeof group.downloads>
                        )
                      ).map(([clientIp, clientDownloads]) => {
                        const clientTotal = clientDownloads.reduce(
                          (sum, d) => sum + (d.totalBytes || 0),
                          0
                        );
                        const clientCacheHit = clientDownloads.reduce(
                          (sum, d) => sum + (d.cacheHitBytes || 0),
                          0
                        );

                        return (
                          <div
                            key={clientIp}
                            className="rounded-lg border border-[var(--theme-border-secondary)] overflow-hidden"
                          >
                            {/* Client Header */}
                            <div className="bg-[var(--theme-bg-tertiary)] px-4 py-2 flex items-center justify-between border-b border-[var(--theme-border-secondary)]">
                              <div className="flex items-center gap-2">
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
                            </div>

                            {/* Sessions Table-like list */}
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
                                    className="px-4 py-3 hover:bg-[var(--theme-bg-tertiary)] transition-colors"
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
                                        </div>
                                        {associations.events.length > 0 && (
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
                  <h2 className="text-lg font-bold text-themed-primary border-b border-[var(--theme-border-secondary)] pb-2">
                    {labels.multipleDownloads}
                  </h2>
                  <p className="text-xs text-themed-muted mt-1">
                    Games that have been downloaded multiple times
                  </p>
                </div>
              );
            } else if (group.count === 1 && !singleDownloadsHeaderRendered) {
              singleDownloadsHeaderRendered = true;
              header = (
                <div className="mb-4 mt-6 first:mt-0">
                  <h2 className="text-lg font-bold text-themed-primary border-b border-[var(--theme-border-secondary)] pb-2">
                    {labels.singleDownloads}
                  </h2>
                  <p className="text-xs text-themed-muted mt-1">
                    Games downloaded once in a single session
                  </p>
                </div>
              );
            }
          } else if (!isGroup && !individualHeaderRendered) {
            individualHeaderRendered = true;
            header = (
              <div className="mb-4 mt-6 first:mt-0">
                <h2 className="text-lg font-bold text-themed-primary border-b border-[var(--theme-border-secondary)] pb-2">
                  {labels.individual}
                </h2>
                <p className="text-xs text-themed-muted mt-1">
                  {t('downloads.tab.normal.sections.individualDescription')}
                </p>
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

export default NormalView;
