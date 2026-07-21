import React, { useMemo, useState, useCallback, useEffect } from 'react';
import { Activity, Clock, RefreshCw, Rows3 } from 'lucide-react';
import { type TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import BadgesRow from '../downloads/BadgesRow';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { EmptyState } from '@components/ui/ManagerCard';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import { useClientGroups } from '@contexts/useClientGroups';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import EventBadge from '../downloads/EventBadge';
import { storage } from '@utils/storage';
import { APP_EVENTS, STORAGE_KEYS } from '@utils/constants';
import { getServiceDisplayName, getServiceFilterKey } from '@utils/serviceDisplayName';
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
const ActiveDownloadItem: React.FC<{ game: GameSpeedInfo; t: TFunction }> = ({ game, t }) => {
  return (
    <div className="rdl-row rdl-row-active">
      <div className="rdl-row-main">
        <div className="rdl-active-indicator">
          <div className="rdl-pulse-ring" />
          <div className="rdl-pulse-dot" />
        </div>
        <div className="rdl-row-info">
          <div className="rdl-row-name">
            <span className="rdl-name-text">
              {game.gameName &&
              game.gameName !== game.service &&
              !game.gameName.match(/^Steam App \d+$/)
                ? game.gameName
                : game.gameName || (game.depotId ? `Depot ${game.depotId}` : game.service)}
            </span>
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
        hasGameName: item.downloads.some(
          (d: Download) =>
            d.gameName && d.gameName !== d.service && !d.gameName.match(/^Steam App \d+$/)
        ),
        isEvicted: item.downloads.every((d: Download) => d.isEvicted),
        isPartiallyEvicted:
          item.downloads.some((d: Download) => d.isEvicted) &&
          !item.downloads.every((d: Download) => d.isEvicted),
        gameAppId: item.downloads.find((d: Download) => d.gameAppId)?.gameAppId ?? null
      }
    : {
        service: item.service,
        name:
          item.gameName && item.gameName !== item.service && !item.gameName.match(/^Steam App \d+$/)
            ? item.gameName
            : item.gameName || (item.depotId ? `Depot ${item.depotId}` : item.service),
        totalBytes: item.totalBytes,
        cacheHitPercent: item.cacheHitPercent,
        cacheHitBytes: item.cacheHitBytes,
        startTime: item.startTimeUtc,
        clientInfo: item.clientIp, // Fallback for display
        clientIp: item.clientIp, // Single client IP for nickname lookup
        count: 1,
        hasGameName:
          item.gameName &&
          item.gameName !== item.service &&
          !item.gameName.match(/^Steam App \d+$/),
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
            <span className="rdl-name-text">{display.name}</span>
            {isGroup && display.count > 1 && (
              <span className="themed-badge status-badge-neutral badge-count">
                {display.count}×
              </span>
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

  const latestDownloads = useMemo(() => downloads, [downloads]);
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const { getGroupForIp } = useClientGroups();
  const {
    speedSnapshot,
    gameSpeeds,
    activeDownloadCount,
    isLoading: speedLoading,
    refreshSpeed
  } = useSpeed();
  const { timeRange: contextTimeRange, selectedEventIds } = useTimeFilter();

  // Match Dashboard/DownloadsTab: non-live time range or event filter disables live Active tab
  const isHistoricalView = contextTimeRange !== 'live' || selectedEventIds.length > 0;

  // Auto-switch to Recent view when user switches to historical view while on Active tab
  useEffect(() => {
    if (isHistoricalView && viewMode === 'active') {
      setViewMode('recent');
    }
  }, [isHistoricalView, viewMode]);

  // Fetch associations for visible downloads - moved after groupedItems is computed

  // Grouping logic
  const createGroups = useCallback(
    (downloads: Download[]): { groups: DownloadGroup[]; individuals: Download[] } => {
      const groups: Record<string, DownloadGroup> = {};
      const individuals: Download[] = [];

      downloads.forEach((download) => {
        let groupKey: string;
        let groupName: string;
        let groupType: 'game' | 'metadata' | 'content';

        // Check if we have a valid game (either by appId or by name)
        const hasValidGameAppId = !!download.gameAppId;
        const hasValidGameName =
          download.gameName &&
          download.gameName !== download.service &&
          !download.gameName.match(/^Steam App \d+$/);

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
          const displayService = getServiceDisplayName(download.service ?? '');
          groupName =
            svcLower === 'epicgames'
              ? 'Epic Games'
              : svcLower === 'steam'
                ? 'Steam Downloads'
                : `${displayService.charAt(0).toUpperCase() + displayService.slice(1)} Downloads`;
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

      return { groups: Object.values(groups), individuals };
    },
    []
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
    latestDownloads.forEach((d: Download) => {
      const key = getServiceFilterKey(d.service);
      if (!representatives.has(key)) {
        representatives.set(key, d.service);
      }
    });
    return Array.from(representatives.entries())
      .map(([key, service]) => ({ key, service }))
      .sort((a, b) => a.key.localeCompare(b.key));
  }, [latestDownloads]);

  const { clientGroups } = useClientGroups();

  const availableClients = useMemo(() => {
    const clients = new Set(latestDownloads.map((d) => d.clientIp));
    return Array.from(clients).sort();
  }, [latestDownloads]);

  const clientOptions = useMemo(() => {
    // Build a map of group IDs to the IPs in downloads that belong to that group
    const groupedIps = new Map<number, { group: (typeof clientGroups)[0]; ips: string[] }>();
    const ungroupedIps: string[] = [];

    availableClients.forEach((clientIp) => {
      const group = getGroupForIp(clientIp);
      if (group && group.nickname) {
        const existing = groupedIps.get(group.id);
        if (existing) {
          existing.ips.push(clientIp);
        } else {
          groupedIps.set(group.id, { group, ips: [clientIp] });
        }
      } else {
        ungroupedIps.push(clientIp);
      }
    });

    const options: { value: string; label: string; description?: string }[] = [
      { value: 'all', label: t('dashboard.downloadsPanel.allClients') }
    ];

    // Add grouped clients - show once per group with IPs in description
    Array.from(groupedIps.values())
      .sort((a, b) => a.group.nickname.localeCompare(b.group.nickname))
      .forEach(({ group, ips }) => {
        options.push({
          value: `group-${group.id}`,
          label: group.nickname,
          description: ips.join(', ')
        });
      });

    // Add ungrouped IPs individually
    ungroupedIps.sort().forEach((ip) => {
      options.push({
        value: ip,
        label: ip
      });
    });

    return options;
  }, [availableClients, getGroupForIp, t]);

  const filteredDownloads = useMemo(() => {
    return latestDownloads.filter((download) => {
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
  }, [latestDownloads, selectedService, selectedClient, clientGroups]);

  const displayCount = 10;
  const groupedItems = useMemo(() => {
    const { groups, individuals } = createGroups(filteredDownloads);

    const filteredIndividuals = individuals.filter((download) => {
      if (
        download.gameName &&
        download.gameName !== download.service &&
        !download.gameName.match(/^Steam App \d+$/)
      ) {
        return true;
      }
      if ((download.service ?? '').toLowerCase() !== 'steam') return true;
      return false;
    });

    const allItems: (DownloadGroup | Download)[] = [...groups, ...filteredIndividuals];

    allItems.sort((a, b) => {
      const aTime =
        'downloads' in a
          ? Math.max(...a.downloads.map((d: Download) => new Date(d.startTimeUtc).getTime()))
          : new Date(a.startTimeUtc).getTime();
      const bTime =
        'downloads' in b
          ? Math.max(...b.downloads.map((d: Download) => new Date(d.startTimeUtc).getTime()))
          : new Date(b.startTimeUtc).getTime();
      return bTime - aTime;
    });

    return {
      displayedItems: allItems.slice(0, displayCount),
      totalGroups: allItems.length
    };
  }, [filteredDownloads, createGroups]);

  // Fetch associations for all downloads in displayed groups
  useEffect(() => {
    const downloadIds: number[] = [];
    groupedItems.displayedItems.forEach((item) => {
      if ('downloads' in item) {
        // It's a group - get all download IDs in the group
        item.downloads.forEach((d: Download) => downloadIds.push(d.id));
      } else {
        // It's an individual download
        downloadIds.push(item.id);
      }
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
      {/* Header */}
      <div className="rdl-header">
        <h3>{t('dashboard.downloadsPanel.title')}</h3>

        <div className="flex items-center gap-2">
          {viewMode === 'recent' && (
            <Tooltip content={t('dashboard.downloadsPanel.showDetails')}>
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
          )}
          <SegmentedControl
            options={[
              {
                value: 'recent',
                label: t('dashboard.downloadsPanel.recent'),
                icon: <Clock size={14} />
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
                icon: <Activity size={14} />,
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
          />
        </div>
      </div>

      {/* Filters (only for recent view) */}
      {viewMode === 'recent' && latestDownloads.length > 0 && (
        <div className="rdl-filters">
          <EnhancedDropdown
            options={[
              { value: 'all', label: t('dashboard.downloadsPanel.allServices') },
              ...serviceFilterOptions.map(({ key, service }) => {
                const displayService = getServiceDisplayName(service);
                return {
                  value: key,
                  label: displayService.charAt(0).toUpperCase() + displayService.slice(1)
                };
              })
            ]}
            value={selectedService}
            onChange={setSelectedService}
          />
          <EnhancedDropdown
            options={clientOptions}
            value={selectedClient}
            onChange={setSelectedClient}
          />
          {(selectedService !== 'all' || selectedClient !== 'all') && (
            <button
              className="rdl-clear-btn"
              onClick={() => {
                setSelectedService('all');
                setSelectedClient('all');
              }}
            >
              {t('dashboard.downloadsPanel.clear')}
            </button>
          )}
        </div>
      )}

      {/* Downloads list */}
      <div className="rdl-well well-surface">
        <CustomScrollbar maxHeight="380px" paddingMode="none" radius="none" className="rdl-scroll">
          <div className="rdl-list divided-list">
            {viewMode === 'active' ? (
              speedLoading ? (
                <div className="rdl-loading">
                  <LoadingSpinner size="md" />
                  <span>{t('dashboard.downloadsPanel.emptyStates.loading')}</span>
                </div>
              ) : hasActiveDownloads && activeGames.length > 0 ? (
                activeGames.map((game) => (
                  <ActiveDownloadItem
                    key={`${game.service}-${game.gameAppId || game.gameName || game.depotId}-${game.clientIp ?? 'unknown'}`}
                    game={game}
                    t={t}
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
            ) : loading ? (
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
            ) : groupedItems.displayedItems.length > 0 ? (
              groupedItems.displayedItems.map((item, idx) => {
                const isGroup = 'downloads' in item;
                const events = isGroup
                  ? Array.from(
                      item.downloads.reduce((acc, d) => {
                        getAssociations(d.id).events.forEach((e) => acc.set(e.id, e));
                        return acc;
                      }, new Map<number, EventSummary>())
                    ).map(([, e]) => e)
                  : getAssociations(item.id).events;
                return (
                  <RecentDownloadItem
                    key={isGroup ? item.id : item.id || idx}
                    item={item}
                    events={events}
                    detectionLookup={detectionLookup}
                    detectionByName={detectionByName}
                    detectionByService={detectionByService}
                    detailed={showDetails}
                  />
                );
              })
            ) : (
              <EmptyState
                variant="panel"
                icon={Clock}
                title={t('dashboard.downloadsPanel.emptyStates.noDownloads')}
                subtitle={t('dashboard.downloadsPanel.emptyStates.noDownloadsInPeriod', {
                  period: getTimeRangeLabel.toLowerCase()
                })}
              />
            )}
          </div>
        </CustomScrollbar>
      </div>

      {/* Footer */}
      {viewMode === 'active' && hasActiveDownloads && (
        <div className="rdl-footer">
          <div className="rdl-footer-stat">
            <strong>{activeGames.length}</strong>{' '}
            {t('dashboard.downloadsPanel.game', { count: activeGames.length })}{' '}
            {t('dashboard.downloadsPanel.downloading')}
          </div>
          <button className="rdl-refresh-btn" onClick={refreshSpeed}>
            <RefreshCw />
            {t('dashboard.downloadsPanel.refresh')}
          </button>
        </div>
      )}

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
              <div className="dash-readout-item">
                <div className="dash-readout-value">{getTimeRangeLabel}</div>
                <div className="caps-label caps-label--wide dash-readout-label">
                  {t('dashboard.downloadsPanel.period')}
                </div>
              </div>
              {groupedItems.totalGroups > 0 && (
                <div className="dash-readout-item">
                  <div className="dash-readout-value">
                    {Math.min(displayCount, groupedItems.displayedItems.length)} /{' '}
                    {groupedItems.totalGroups}
                  </div>
                  <div className="caps-label caps-label--wide dash-readout-label">
                    {t('dashboard.downloadsPanel.showingLabel')}
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </Card>
  );
};

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default React.memo(RecentDownloadsPanel);
