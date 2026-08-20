import React, { useMemo, useState, useCallback, useEffect } from 'react';
import { Activity, Clock, Rows3 } from 'lucide-react';
import { type TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import BadgesRow from '../downloads/BadgesRow';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { EmptyState } from '@components/ui/ManagerCard';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Tooltip } from '@components/ui/Tooltip';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import { useClientGroups } from '@contexts/useClientGroups';
import { useClientHostnames } from '@contexts/useClientHostnames';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import EventBadge from '../downloads/EventBadge';
import LiveDownloadRows from '../downloads/LiveDownloadRows';
import { useLiveDownloadPreviews } from '../downloads/useLiveDownloadPreviews';
import {
  buildTrafficKey,
  filterLivePreviews,
  isResolvedGameName
} from '../downloads/liveDownloadPreviews';
import { storage } from '@utils/storage';
import { APP_EVENTS, STORAGE_KEYS } from '@utils/constants';
import {
  formatServiceLabel,
  getServiceDisplayName,
  getServiceFilterKey
} from '@utils/serviceDisplayName';
import { buildClientFilterOptions } from '@utils/clientFilterOptions';
import type {
  Download,
  DownloadGroup,
  EventSummary,
  GameSpeedInfo,
  GameDetectionSummary
} from '@/types';
import { resolveGameDetection } from '@utils/gameDetection';

interface RecentDownloadsPanelProps {
  downloads: Download[];
  /** The dashboard's range chip, shown beside the title. */
  badge?: React.ReactNode;
  loading?: boolean;
  timeRange?: string;
  glassmorphism?: boolean;
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
}

// Active download item component using real-time speed data
const ActiveDownloadItem: React.FC<{
  game: GameSpeedInfo;
  t: TFunction;
  fallbackActive: boolean;
}> = ({ game, t, fallbackActive }) => {
  // The pulse dot's live state flows through the unified activity registry, which is authoritative
  // once ready; the snapshot's active flag is the fallback only before the first activity snapshot
  // arrives.
  const activity = useActivityStatus();
  const downloading = activity.isActiveOrFallback(
    'download',
    buildTrafficKey(game),
    'downloading',
    fallbackActive
  );
  const displayName = isResolvedGameName(game.gameName, game.service)
    ? game.gameName!
    : game.gameName ||
      (game.depotId ? `Depot ${game.depotId}` : getServiceDisplayName(game.service));
  return (
    <div className="rdl-row rdl-row-active">
      <div className="rdl-row-main">
        {downloading && (
          <div className="rdl-active-indicator">
            <div className="rdl-pulse-ring" />
            <div className="rdl-pulse-dot" />
          </div>
        )}
        <div className="rdl-row-info">
          <div className="rdl-row-name">
            <Tooltip content={displayName} position="top" className="flex min-w-0">
              <span className="rdl-name-text">{displayName}</span>
            </Tooltip>
          </div>
          <div className="rdl-row-meta">
            <BadgesRow service={game.service} showDatasource={false} />
            <span className="rdl-meta-sep">•</span>
            <span>{formatBytes(game.totalBytes)}</span>
            <span className="rdl-meta-sep">•</span>
            <span>
              {game.requestCount} {t('dashboard.downloadsPanel.req')}
            </span>
          </div>
        </div>
      </div>
      <div className="rdl-row-stats">
        <div className="rdl-row-figures">
          <span className="rdl-row-speed tabular-nums">{formatSpeed(game.bytesPerSecond)}</span>
          <div
            className={`tabular-nums rdl-hit ${game.cacheHitPercent >= 80 ? 'high' : game.cacheHitPercent >= 50 ? 'medium' : 'low'}`}
          >
            {formatPercent(game.cacheHitPercent, 0)} {t('dashboard.downloadsPanel.hitLabel')}
          </div>
        </div>
      </div>
    </div>
  );
};

// Recent download item component
interface RecentDownloadItemProps {
  item: DownloadGroup | Download;
  events?: EventSummary[];
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
  detailed?: boolean;
}

const RecentDownloadItem: React.FC<RecentDownloadItemProps> = ({
  item,
  events = [],
  detectionLookup = null,
  detectionByName = null,
  detectionByService = null,
  detailed = false
}) => {
  const { t } = useTranslation();
  const isGroup = 'downloads' in item;
  const display = isGroup
    ? {
        service: item.service,
        name: item.name,
        totalBytes: item.totalBytes,
        cacheHitPercent:
          item.totalDownloaded > 0 ? (item.cacheHitBytes / item.totalDownloaded) * 100 : 0,
        cacheHitBytes: item.cacheHitBytes,
        startTime: item.lastSeen,
        clientInfo: `${item.clientsSet.size} client${item.clientsSet.size !== 1 ? 's' : ''}`,
        clientIp: null as string | null, // Multiple clients, no single IP
        count: item.count,
        hasGameName: item.downloads.some((d: Download) =>
          isResolvedGameName(d.gameName, d.service)
        ),
        isEvicted: item.downloads.every((d: Download) => d.isEvicted),
        isPartiallyEvicted:
          item.downloads.some((d: Download) => d.isEvicted) &&
          !item.downloads.every((d: Download) => d.isEvicted),
        gameAppId: item.downloads.find((d: Download) => d.gameAppId)?.gameAppId ?? null
      }
    : {
        service: item.service,
        name: isResolvedGameName(item.gameName, item.service)
          ? item.gameName!
          : item.gameName ||
            (item.depotId ? `Depot ${item.depotId}` : getServiceDisplayName(item.service)),
        totalBytes: item.totalBytes,
        cacheHitPercent: item.cacheHitPercent,
        cacheHitBytes: item.cacheHitBytes,
        startTime: item.startTimeUtc,
        clientInfo: item.clientIp, // Fallback for display
        clientIp: item.clientIp, // Single client IP for nickname lookup
        count: 1,
        hasGameName: isResolvedGameName(item.gameName, item.service),
        isEvicted: item.isEvicted,
        isPartiallyEvicted: false,
        gameAppId: item.gameAppId ?? null
      };

  const primaryDownload = isGroup ? (item as DownloadGroup).downloads[0] : (item as Download);
  const isServiceBucket = isGroup && item.type !== 'game';
  const detection = isServiceBucket
    ? resolveGameDetection(
        null,
        null,
        detectionLookup,
        detectionByName,
        display.service,
        detectionByService
      )
    : resolveGameDetection(
        primaryDownload?.gameAppId,
        primaryDownload?.gameName ?? display.name,
        detectionLookup,
        detectionByName,
        display.service,
        detectionByService
      );
  const diskSizeBytes = detection?.total_size_bytes;

  const hitTooltip =
    display.cacheHitBytes > 0
      ? diskSizeBytes
        ? t('dashboard.downloadsPanel.hitTooltipDetailed', {
            percent: formatPercent(display.cacheHitPercent),
            saved: formatBytes(display.cacheHitBytes),
            disk: formatBytes(diskSizeBytes)
          })
        : t('dashboard.downloadsPanel.hitTooltipSaved', {
            percent: formatPercent(display.cacheHitPercent),
            saved: formatBytes(display.cacheHitBytes)
          })
      : t('dashboard.downloadsPanel.hitTooltip', {
          percent: formatPercent(display.cacheHitPercent)
        });

  const formattedTime = useFormattedDateTime(display.startTime);

  // Shared hit-rate band so the simple and detailed views color the figure the
  // same way (green = mostly served from cache, red = mostly missed).
  const hitClass =
    display.cacheHitPercent >= 75
      ? 'high'
      : display.cacheHitPercent >= 50
        ? 'medium'
        : display.cacheHitPercent >= 25
          ? 'low'
          : 'critical';

  const handleClick = useCallback(() => {
    // Service buckets use a synthesized display name ("Wsus Downloads") that the
    // downloads search can't match — search by the raw service instead.
    storage.setItem('lancache_downloads_search', isServiceBucket ? display.service : display.name);
    window.dispatchEvent(
      new CustomEvent(APP_EVENTS.NAVIGATE_TO_TAB, { detail: { tab: 'downloads' } })
    );
  }, [isServiceBucket, display.service, display.name]);

  return (
    <div
      className={`rdl-row rdl-row-clickable${display.isEvicted ? ' evicted-row' : ''}`}
      onClick={handleClick}
      role="button"
      tabIndex={0}
      onKeyDown={(e: React.KeyboardEvent) => {
        if (e.key === 'Enter' || e.key === ' ') handleClick();
      }}
    >
      <div className="rdl-row-main">
        <div className="rdl-row-info">
          <div className="rdl-row-name">
            {!detailed && <BadgesRow service={display.service} showDatasource={false} />}
            <Tooltip content={display.name} position="top" className="flex min-w-0">
              <span className="rdl-name-text">{display.name}</span>
            </Tooltip>
            {isGroup && display.count > 1 && (
              <Badge variant="neutral" className="badge-count">
                {display.count}×
              </Badge>
            )}
          </div>
          {detailed && (
            <div className="rdl-row-meta">
              <BadgesRow
                service={display.service}
                showDatasource={false}
                isEvicted={display.isEvicted}
                isPartiallyEvicted={display.isPartiallyEvicted}
              />
              <span className="rdl-meta-sep">•</span>
              <span>
                {display.clientIp ? (
                  <ClientIpDisplay clientIp={display.clientIp} />
                ) : (
                  display.clientInfo
                )}
              </span>
              <span className="rdl-meta-sep">•</span>
              <span>{formattedTime}</span>

              {events.length > 0 &&
                events
                  .slice(0, 1)
                  .map((event) => <EventBadge key={event.id} event={event} size="sm" />)}
            </div>
          )}
        </div>
      </div>
      <div className="rdl-row-stats">
        <div className="rdl-row-figures">
          <span className="rdl-row-size">{formatBytes(display.totalBytes)}</span>
          {detailed ? (
            <div className="rdl-row-subline">
              {diskSizeBytes ? (
                <span className="rdl-row-sub">
                  {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })} ·
                </span>
              ) : null}
              <Tooltip content={hitTooltip} className={`tabular-nums rdl-hit ${hitClass}`}>
                {formatPercent(display.cacheHitPercent)} {t('dashboard.downloadsPanel.hitLabel')}
              </Tooltip>
            </div>
          ) : (
            display.totalBytes > 0 && (
              <div className="rdl-row-subline">
                <Tooltip content={hitTooltip} className={`tabular-nums rdl-hit ${hitClass}`}>
                  {formatPercent(display.cacheHitPercent)} {t('dashboard.downloadsPanel.hitLabel')}
                </Tooltip>
              </div>
            )
          )}
        </div>
      </div>
    </div>
  );
};

const RecentDownloadsPanel: React.FC<RecentDownloadsPanelProps> = ({
  downloads = [],
  badge,
  loading = false,
  timeRange = 'live',
  glassmorphism = false,
  detectionLookup = null,
  detectionByName = null,
  detectionByService = null
}) => {
  const { t } = useTranslation();
  const [selectedService, setSelectedService] = useState<string>('all');
  const [selectedClient, setSelectedClient] = useState<string>('all');
  const [viewMode, setViewMode] = useState<'recent' | 'active'>('recent');
  // Simple rows by default; the header toggle opts into the detailed stats view.
  const [showDetails, setShowDetails] = useState(
    () => storage.getItem(STORAGE_KEYS.RECENT_DOWNLOADS_DETAILED) === 'true'
  );
  const toggleDetails = () => {
    const next = !showDetails;
    setShowDetails(next);
    storage.setItem(STORAGE_KEYS.RECENT_DOWNLOADS_DETAILED, String(next));
  };

  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const { getGroupForIp } = useClientGroups();
  const { getHostnameForIp } = useClientHostnames();
  const { speedSnapshot, gameSpeeds, activeDownloadCount, isLoading: speedLoading } = useSpeed();
  const { timeRange: contextTimeRange, selectedEventIds } = useTimeFilter();

  // Match Dashboard/DownloadsTab: non-live time range or event filter disables live Active tab
  const isHistoricalView = contextTimeRange !== 'live' || selectedEventIds.length > 0;

  // In-progress previews for the Recent view (live range, no event filter only). Matching
  // runs against the FULL downloads list - reconciliation must see rows the panel
  // filters hide - while the panel's own service/client filters are applied separately
  // below. Previews never enter the recorded rows, the grouped items, the footer stats,
  // or the association fetches.
  const livePreviews = useLiveDownloadPreviews(
    downloads,
    viewMode === 'recent' && !isHistoricalView
  );

  // Auto-switch to Recent view when user switches to historical view while on Active tab
  useEffect(() => {
    if (isHistoricalView && viewMode === 'active') {
      setViewMode('recent');
    }
  }, [isHistoricalView, viewMode]);

  // Fetch associations for visible downloads - moved after groupedItems is computed

  // Grouping logic
  const createGroups = useCallback(
    (downloads: Download[]): DownloadGroup[] => {
      const groups: Record<string, DownloadGroup> = {};

      downloads.forEach((download) => {
        let groupKey: string;
        let groupName: string;
        let groupType: 'game' | 'metadata' | 'content';

        // Check if we have a valid game (either by appId or by name)
        const hasValidGameAppId = !!download.gameAppId;
        const hasValidGameName = isResolvedGameName(download.gameName, download.service);

        if (hasValidGameName) {
          // Only show as a named game when we have an actual resolved name
          groupKey = hasValidGameAppId
            ? `game-appid-${download.gameAppId}`
            : `game-${download.gameName}`;
          groupName = download.gameName!;
          groupType = 'game';
        } else {
          // Group by service for all platforms (including unmapped Steam)
          const svcLower = (download.service ?? '').toLowerCase();
          groupKey = `service-${svcLower}`;
          groupName =
            svcLower === 'epicgames'
              ? 'Epic Games'
              : t('dashboard.downloadsPanel.serviceGroup', {
                  service: formatServiceLabel(download.service ?? '')
                });
          groupType = download.totalBytes === 0 ? 'metadata' : 'content';
        }

        if (!groups[groupKey]) {
          groups[groupKey] = {
            id: groupKey,
            name: groupName,
            type: groupType,
            service: download.service,
            downloads: [],
            totalBytes: 0,
            totalDownloaded: 0,
            cacheHitBytes: 0,
            cacheMissBytes: 0,
            clientsSet: new Set<string>(),
            firstSeen: download.startTimeUtc,
            lastSeen: download.startTimeUtc,
            count: 0
          };
        }

        groups[groupKey].downloads.push(download);
        groups[groupKey].totalBytes += download.totalBytes;
        groups[groupKey].totalDownloaded += download.totalBytes;
        groups[groupKey].cacheHitBytes += download.cacheHitBytes;
        groups[groupKey].cacheMissBytes += download.cacheMissBytes;
        groups[groupKey].clientsSet.add(download.clientIp);
        groups[groupKey].count++;

        if (download.startTimeUtc < groups[groupKey].firstSeen) {
          groups[groupKey].firstSeen = download.startTimeUtc;
        }
        if (download.startTimeUtc > groups[groupKey].lastSeen) {
          groups[groupKey].lastSeen = download.startTimeUtc;
        }
      });

      return Object.values(groups);
    },
    [t]
  );

  const getTimeRangeLabel = useMemo(() => {
    const key = `dashboard.downloadsPanel.timeRanges.${timeRange}` as const;
    return t(key);
  }, [timeRange, t]);

  // Group raw service names by their folded display name (e.g. "xbox" and
  // "xboxlive" both fold to "Xbox") so the filter dropdown shows one entry
  // per displayed name instead of one per raw alias.
  const serviceFilterOptions = useMemo(() => {
    const representatives = new Map<string, string>();
    downloads.forEach((d: Download) => {
      const key = getServiceFilterKey(d.service);
      if (!representatives.has(key)) {
        representatives.set(key, d.service);
      }
    });
    return Array.from(representatives.entries())
      .map(([key, service]) => ({ key, service }))
      .sort((a, b) => a.key.localeCompare(b.key));
  }, [downloads]);

  const { clientGroups } = useClientGroups();

  const availableClients = useMemo(() => {
    const clients = new Set(downloads.map((d) => d.clientIp));
    return Array.from(clients).sort();
  }, [downloads]);

  const clientOptions = useMemo(
    () =>
      buildClientFilterOptions(
        availableClients,
        getGroupForIp,
        getHostnameForIp,
        t('dashboard.downloadsPanel.allClients')
      ),
    [availableClients, getGroupForIp, getHostnameForIp, t]
  );

  const filteredDownloads = useMemo(() => {
    return downloads.filter((download) => {
      if (selectedService !== 'all' && getServiceFilterKey(download.service) !== selectedService)
        return false;
      if (selectedClient !== 'all') {
        // Check if it's a group selection (e.g., "group-123")
        if (selectedClient.startsWith('group-')) {
          const groupId = parseInt(selectedClient.replace('group-', ''), 10);
          const group = clientGroups.find((g) => g.id === groupId);
          if (group) {
            // Filter by any IP in the group
            if (!group.memberIps.includes(download.clientIp)) return false;
          }
        } else {
          // Filter by exact IP
          if (download.clientIp !== selectedClient) return false;
        }
      }
      return true;
    });
  }, [downloads, selectedService, selectedClient, clientGroups]);

  // Panel filters applied to previews with the same predicates as the recorded rows.
  const visibleLivePreviews = useMemo(() => {
    if (livePreviews.length === 0) return livePreviews;
    const clientFilter =
      selectedClient === 'all'
        ? { type: 'all' as const }
        : selectedClient.startsWith('group-')
          ? {
              type: 'group' as const,
              memberIps:
                clientGroups.find(
                  (g) => g.id === parseInt(selectedClient.replace('group-', ''), 10)
                )?.memberIps ?? []
            }
          : { type: 'ip' as const, ip: selectedClient };
    return filterLivePreviews(livePreviews, {
      serviceFilterKey: selectedService,
      clientFilter
    });
  }, [livePreviews, selectedService, selectedClient, clientGroups]);

  const displayCount = 10;
  const groupedItems = useMemo(() => {
    const allItems = createGroups(filteredDownloads);

    allItems.sort((a, b) => {
      const aTime = Math.max(
        ...a.downloads.map((d: Download) => new Date(d.startTimeUtc).getTime())
      );
      const bTime = Math.max(
        ...b.downloads.map((d: Download) => new Date(d.startTimeUtc).getTime())
      );
      return bTime - aTime;
    });

    return {
      displayedItems: allItems.slice(0, displayCount),
      totalGroups: allItems.length
    };
  }, [filteredDownloads, createGroups]);

  // Live previews render above the recorded rows and share the 10-row cap. Recorded rows
  // yield space to previews; footer stats and the association fetch stay recorded-only.
  const displayedLivePreviews = visibleLivePreviews.slice(0, displayCount);
  const visibleDbItems = groupedItems.displayedItems.slice(
    0,
    Math.max(0, displayCount - displayedLivePreviews.length)
  );

  // Fetch associations for all downloads in displayed groups
  useEffect(() => {
    const downloadIds: number[] = [];
    groupedItems.displayedItems.forEach((item) => {
      item.downloads.forEach((d: Download) => downloadIds.push(d.id));
    });

    if (downloadIds.length > 0) {
      fetchAssociations(downloadIds);
    }
  }, [groupedItems.displayedItems, fetchAssociations, refreshVersion]);

  const stats = useMemo(() => {
    const totalBytes = filteredDownloads.reduce((sum, d) => sum + d.totalBytes, 0);
    const totalCacheHits = filteredDownloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
    const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;

    return { totalBytes, overallHitRate };
  }, [filteredDownloads]);

  // Active downloads data from speed context (same source as Active Downloads stat card)
  const activeGames = gameSpeeds;
  const activeCount = activeDownloadCount;
  const totalSpeed = speedSnapshot?.totalBytesPerSecond || 0;
  const hasActiveDownloads = speedSnapshot?.hasActiveDownloads || false;

  const hitRateClass =
    stats.overallHitRate >= 75
      ? 'is-success'
      : stats.overallHitRate >= 50
        ? 'is-warning'
        : 'is-error';

  // Footer readout only appears once there's real data to summarize, so an
  // empty panel shows no placeholder strip (matches Service Analytics / Peak Usage).
  const showFooterReadout =
    viewMode === 'active'
      ? hasActiveDownloads && activeGames.length > 0
      : !loading && groupedItems.displayedItems.length > 0;

  return (
    <Card glassmorphism={glassmorphism} className="recent-downloads-panel">
      {/* Header: the title owns the first row and every control shares the second one, so this panel
          keeps the same two-row rhythm as the chart panel beside it and the two sets of rows land on
          the same baselines. */}
      <div className="rdl-header">
        <h3 className="dash-panel-title">{t('dashboard.downloadsPanel.title')}</h3>

        <div className="rdl-view-switch">
          <SegmentedControl
            options={[
              {
                value: 'recent',
                label: t('dashboard.downloadsPanel.recent')
              },
              {
                value: 'active',
                label: (
                  <span className="segmented-control-label">
                    {t('dashboard.downloadsPanel.active')}
                    {!isHistoricalView && activeCount > 0 && (
                      <span className="rdl-tab-badge tabular-nums">{activeCount}</span>
                    )}
                  </span>
                ),
                disabled: isHistoricalView,
                tooltip: isHistoricalView
                  ? t('dashboard.downloadsPanel.activeDownloadsOnly')
                  : undefined
              }
            ]}
            value={viewMode}
            onChange={(value) => setViewMode(value as 'recent' | 'active')}
            size="md"
            showLabels={true}
            fullWidth
          />
        </div>

        {/* Filters (only for recent view). The detail toggle leads the row, immediately left of the
            service filter. The row itself renders for the whole recent view, not only when there are
            downloads to filter, so the toggle keeps the visibility it had in the header. */}
        {viewMode === 'recent' && (
          <div className="rdl-filters">
            <Tooltip
              content={t('dashboard.downloadsPanel.showDetails')}
              className="rdl-detail-toggle-slot"
            >
              <Button
                variant="filled"
                color={showDetails ? 'blue' : 'gray'}
                size="md"
                onClick={toggleDetails}
                aria-label={t('dashboard.downloadsPanel.showDetails')}
                aria-pressed={showDetails}
                leftSection={<Rows3 className="w-4 h-4" />}
                className="min-h-10 rounded-[var(--theme-border-radius)]"
              />
            </Tooltip>
            {downloads.length > 0 && (
              <>
                <EnhancedDropdown
                  options={[
                    { value: 'all', label: t('dashboard.downloadsPanel.allServices') },
                    ...serviceFilterOptions.map(({ key, service }) => ({
                      value: key,
                      label: formatServiceLabel(service)
                    }))
                  ]}
                  value={selectedService}
                  onChange={setSelectedService}
                  size="md"
                  variant="button"
                  prefix={t('dashboard.downloadsPanel.servicePrefix')}
                  className="rdl-filter-select"
                />
                <EnhancedDropdown
                  options={clientOptions}
                  value={selectedClient}
                  onChange={setSelectedClient}
                  size="md"
                  variant="button"
                  prefix={t('dashboard.downloadsPanel.clientPrefix')}
                  className="rdl-filter-select rdl-client-filter"
                />
              </>
            )}
          </div>
        )}
      </div>

      {/* Downloads list */}
      <div className="rdl-well well-surface">
        <CustomScrollbar maxHeight="380px" paddingMode="none" radius="none" className="rdl-scroll">
          <div className="rdl-list divided-list">
            {viewMode === 'active' ? (
              speedLoading ? (
                <div role="status" aria-live="polite" aria-busy="true">
                  <span className="sr-only">
                    {t('dashboard.downloadsPanel.emptyStates.loading')}
                  </span>
                  <div className="flex flex-col gap-3 py-2" aria-hidden="true">
                    {Array.from({ length: 3 }).map((_, i) => (
                      <div key={i} className="rdl-row">
                        <div className="rdl-row-main">
                          <div className="rdl-active-indicator skeleton-shimmer rounded-full" />
                          <div className="rdl-row-info">
                            <div className="rdl-row-name">
                              <div
                                className={`skeleton-shimmer rounded h-3.5 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`}
                              />
                            </div>
                            <div className="rdl-row-meta">
                              <div className="skeleton-shimmer rounded h-4 w-14" />
                              <div className="skeleton-shimmer rounded h-2.5 w-16" />
                            </div>
                          </div>
                        </div>
                        <div className="rdl-row-stats">
                          <div className="rdl-row-figures">
                            <div className="skeleton-shimmer rounded h-3.5 w-12" />
                            <div className="skeleton-shimmer rounded h-2.5 w-10" />
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              ) : hasActiveDownloads && activeGames.length > 0 ? (
                activeGames.map((game) => (
                  <ActiveDownloadItem
                    key={`${game.service}-${game.gameAppId || game.gameName || game.depotId}-${game.clientIp ?? 'unknown'}`}
                    game={game}
                    t={t}
                    fallbackActive={hasActiveDownloads}
                  />
                ))
              ) : (
                <EmptyState
                  variant="panel"
                  icon={Activity}
                  title={t('dashboard.downloadsPanel.emptyStates.noActive')}
                  subtitle={t('dashboard.downloadsPanel.emptyStates.noActiveDesc')}
                />
              )
            ) : (
              <>
                {/* In-progress previews stay visible even while the recorded list is
                    loading or empty - they come from the speed snapshot, not the DB. */}
                <LiveDownloadRows previews={displayedLivePreviews} variant="panel" />
                {loading ? (
                  <div className="recent-downloads-skeleton">
                    {Array.from({ length: 5 }).map((_, i) => (
                      <div key={i} className="recent-downloads-skeleton-row">
                        <div className="recent-downloads-skeleton-icon skeleton-shimmer" />
                        <div className="recent-downloads-skeleton-content">
                          <div className="recent-downloads-skeleton-title skeleton-shimmer" />
                          <div className="recent-downloads-skeleton-meta skeleton-shimmer" />
                        </div>
                        <div className="recent-downloads-skeleton-stats">
                          <div className="recent-downloads-skeleton-size skeleton-shimmer" />
                          <div className="recent-downloads-skeleton-date skeleton-shimmer" />
                          <div className="recent-downloads-skeleton-hit-rate skeleton-shimmer" />
                        </div>
                      </div>
                    ))}
                  </div>
                ) : visibleDbItems.length > 0 ? (
                  visibleDbItems.map((item) => {
                    const events = Array.from(
                      item.downloads.reduce((acc, d) => {
                        getAssociations(d.id).events.forEach((e) => acc.set(e.id, e));
                        return acc;
                      }, new Map<number, EventSummary>())
                    ).map(([, e]) => e);
                    return (
                      <RecentDownloadItem
                        key={item.id}
                        item={item}
                        events={events}
                        detectionLookup={detectionLookup}
                        detectionByName={detectionByName}
                        detectionByService={detectionByService}
                        detailed={showDetails}
                      />
                    );
                  })
                ) : displayedLivePreviews.length === 0 ? (
                  <EmptyState
                    variant="panel"
                    icon={Clock}
                    title={t('dashboard.downloadsPanel.emptyStates.noDownloads')}
                    subtitle={t('dashboard.downloadsPanel.emptyStates.noDownloadsInPeriod', {
                      period: getTimeRangeLabel.toLowerCase()
                    })}
                  />
                ) : null}
              </>
            )}
          </div>
        </CustomScrollbar>
      </div>

      {/* Labeled readout strip, pinned to the card bottom to mirror Service Analytics.
          Only shown once there's real data (no placeholder strip on an empty panel). */}
      {showFooterReadout && (
        <div className="dash-readout dash-readout--footer">
          {viewMode === 'active' ? (
            <>
              <div className="dash-readout-item">
                <div className={`dash-readout-value${hasActiveDownloads ? ' is-success' : ''}`}>
                  {hasActiveDownloads ? formatSpeed(totalSpeed) : '—'}
                </div>
                <div className="caps-label caps-label--wide dash-readout-label">
                  {t('dashboard.downloadsPanel.speed')}
                </div>
              </div>
              <div className="dash-readout-item">
                <div className="dash-readout-value">{activeCount}</div>
                <div className="caps-label caps-label--wide dash-readout-label">
                  {t('dashboard.downloadsPanel.game', { count: activeCount })}
                </div>
              </div>
              <div className="dash-readout-item">
                <div className="dash-readout-value">
                  <span className="rdl-live-dot" />
                  {t('dashboard.downloadsPanel.live')}
                </div>
                <div className="caps-label caps-label--wide dash-readout-label">
                  {t('dashboard.downloadsPanel.period')}
                </div>
              </div>
            </>
          ) : (
            <>
              <div className="dash-readout-item">
                <div
                  className={`dash-readout-value${stats.totalBytes > 0 ? ` ${hitRateClass}` : ''}`}
                >
                  {stats.totalBytes > 0 ? formatPercent(stats.overallHitRate) : '—'}
                </div>
                <div className="caps-label caps-label--wide dash-readout-label">
                  {t('dashboard.downloadsPanel.hitRate')}
                </div>
              </div>
              {groupedItems.totalGroups > 0 && (
                <div className="dash-readout-item">
                  <div className="dash-readout-value">
                    {visibleDbItems.length} / {groupedItems.totalGroups}
                  </div>
                  <div className="caps-label caps-label--wide dash-readout-label">
                    {t('dashboard.downloadsPanel.showingLabel')}
                  </div>
                </div>
              )}
            </>
          )}
          {badge ? <div className="dash-readout-item dash-readout-item--chip">{badge}</div> : null}
        </div>
      )}
      {/* Only when the strip above is absent, so the chip never sits on a band of its own under a
          footer that already exists. */}
      {!showFooterReadout && badge ? <div className="dash-range-footer">{badge}</div> : null}
    </Card>
  );
};

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default React.memo(RecentDownloadsPanel);
