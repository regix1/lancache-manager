import React, { useMemo, useState, useCallback, useEffect } from 'react';
import { Activity, Clock, HardDrive, TrendingUp, RefreshCw } from 'lucide-react';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { type TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import BadgesRow from '../downloads/BadgesRow';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import { useClientGroups } from '@contexts/useClientGroups';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import EventBadge from '../downloads/EventBadge';
import { storage } from '@utils/storage';
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
const ActiveDownloadItem: React.FC<{ game: GameSpeedInfo; index: number; t: TFunction }> = ({
  game,
  index,
  t
}) => {
  return (
    <div className="download-item active-item" style={{ animationDelay: `${index * 50}ms` }}>
      <div className="item-left">
        <div className="item-indicator">
          <div className="pulse-ring" />
          <div className="pulse-dot" />
        </div>
        <div className="item-info">
          <div className="item-name">
            {game.gameName &&
            game.gameName !== game.service &&
            !game.gameName.match(/^Steam App \d+$/)
              ? game.gameName
              : game.gameName || (game.depotId ? `Depot ${game.depotId}` : game.service)}
          </div>
          <div className="item-meta">
            <BadgesRow service={game.service} showDatasource={false} />
            <span className="meta-separator">•</span>
            <span className="meta-text">{formatBytes(game.totalBytes)}</span>
            <span className="meta-separator">•</span>
            <span className="meta-text">
              {game.requestCount} {t('dashboard.downloadsPanel.req')}
            </span>
          </div>
        </div>
      </div>
      <div className="item-right">
        <div className="speed-display">
          <span className="speed-value">{formatSpeed(game.bytesPerSecond)}</span>
          <LoadingSpinner inline size="sm" className="speed-spinner" />
        </div>
        <div
          className={`hit-badge ${game.cacheHitPercent >= 80 ? 'high' : game.cacheHitPercent >= 50 ? 'medium' : 'low'}`}
        >
          {formatPercent(game.cacheHitPercent, 0)}
        </div>
      </div>
    </div>
  );
};

// Recent download item component
interface RecentDownloadItemProps {
  item: DownloadGroup | Download;
  events?: EventSummary[];
  index: number;
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
}

const RecentDownloadItem: React.FC<RecentDownloadItemProps> = ({
  item,
  events = [],
  index,
  detectionLookup = null,
  detectionByName = null,
  detectionByService = null
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

  const handleClick = useCallback(() => {
    storage.setItem('lancache_downloads_search', display.name);
    window.dispatchEvent(new CustomEvent('navigate-to-tab', { detail: { tab: 'downloads' } }));
  }, [display.name]);

  return (
    <div
      className={`download-item recent-item clickable-row${display.isEvicted ? ' evicted-row' : ''}`}
      style={{ animationDelay: `${index * 30}ms` }}
      onClick={handleClick}
      role="button"
      tabIndex={0}
      onKeyDown={(e: React.KeyboardEvent) => {
        if (e.key === 'Enter' || e.key === ' ') handleClick();
      }}
    >
      <div className="item-left">
        <div className="item-icon">
          <HardDrive size={16} />
        </div>
        <div className="item-info">
          <div className="item-name">
            {display.name}
            {isGroup && display.count > 1 && <span className="count-badge">{display.count}×</span>}
          </div>
          <div className="item-meta">
            <BadgesRow
              service={display.service}
              showDatasource={false}
              isEvicted={display.isEvicted}
              isPartiallyEvicted={display.isPartiallyEvicted}
            />
            <span className="meta-separator">•</span>
            <span className="meta-text">
              {display.clientIp ? (
                <ClientIpDisplay clientIp={display.clientIp} />
              ) : (
                display.clientInfo
              )}
            </span>

            {events.length > 0 &&
              events
                .slice(0, 1)
                .map((event) => <EventBadge key={event.id} event={event} size="sm" />)}
          </div>
        </div>
      </div>
      <div className="item-right">
        <div className="size-time">
          <span className="size-value">
            {formatBytes(display.totalBytes)} {t('dashboard.downloadsPanel.transferred')}
          </span>
          {diskSizeBytes ? (
            <span className="disk-value">
              {t('dashboard.downloadsPanel.onDisk', { size: formatBytes(diskSizeBytes) })}
            </span>
          ) : null}
          <span className="time-value">{formattedTime}</span>
        </div>
        <div
          className={`hit-badge ${
            display.cacheHitPercent >= 75
              ? 'high'
              : display.cacheHitPercent >= 50
                ? 'medium'
                : display.cacheHitPercent >= 25
                  ? 'low'
                  : 'critical'
          }`}
          title={hitTooltip}
        >
          {formatPercent(display.cacheHitPercent)} {t('dashboard.downloadsPanel.hitLabel')}
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

  const latestDownloads = useMemo(() => downloads, [downloads]);
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();
  const { getGroupForIp } = useClientGroups();
  const { speedSnapshot, gameSpeeds, refreshSpeed } = useSpeed();
  const { timeRange: contextTimeRange } = useTimeFilter();

  // Determine if we're viewing historical data (not live)
  // Only time ranges other than 'live' are considered historical
  // Event selection does NOT make it historical - user can filter live downloads by event
  const isHistoricalView = contextTimeRange !== 'live';

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
          groupName =
            svcLower === 'epicgames'
              ? 'Epic Games'
              : svcLower === 'steam'
                ? 'Steam Downloads'
                : `${(download.service ?? '').charAt(0).toUpperCase() + (download.service ?? '').slice(1)} Downloads`;
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

  const availableServices = useMemo(() => {
    const services = new Set<string>(latestDownloads.map((d: Download) => d.service));
    return ['all', ...Array.from(services).sort()];
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
      if (selectedService !== 'all' && download.service !== selectedService) return false;
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
    const totalDownloads = filteredDownloads.length;
    const totalBytes = filteredDownloads.reduce((sum, d) => sum + d.totalBytes, 0);
    const totalCacheHits = filteredDownloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
    const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;

    return { totalDownloads, totalBytes, overallHitRate };
  }, [filteredDownloads]);

  // Active downloads data from speed context
  const activeGames = gameSpeeds;
  const activeCount = activeGames.length;
  const totalSpeed = speedSnapshot?.totalBytesPerSecond || 0;
  const hasActiveDownloads = speedSnapshot?.hasActiveDownloads || false;

  return (
    <Card glassmorphism={glassmorphism} className="downloads-panel-redesign">
      <style>{`
        .downloads-panel-redesign {
          container-type: inline-size;
        }

        .panel-header {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
          margin-bottom: 1rem;
        }

        .header-top {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 1rem;
        }

        .header-title {
          display: flex;
          align-items: center;
          gap: 0.5rem;
        }

        .header-title h3 {
          font-size: 1rem;
          font-weight: 600;
          color: var(--theme-text-primary);
          margin: 0;
        }

        .tab-toggle {
          display: flex;
          padding: 3px;
          border-radius: var(--theme-border-radius-lg);
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
        }

        .tab-btn {
          display: flex;
          align-items: center;
          gap: 0.4rem;
          padding: 0.4rem 0.75rem;
          font-size: 0.75rem;
          font-weight: 600;
          color: var(--theme-text-muted);
          background: transparent;
          border: none;
          border-radius: var(--theme-border-radius);
          cursor: pointer;
          transition: all 0.2s ease;
        }

        .tab-btn:hover:not(.active) {
          color: var(--theme-text-secondary);
          background: var(--theme-bg-secondary-strong);
        }

        .tab-btn.active {
          color: var(--theme-button-text);
          background: var(--theme-primary);
          box-shadow: 0 2px 4px var(--theme-primary-muted);
        }

        .tab-btn svg {
          width: 14px;
          height: 14px;
        }

        .tab-badge {
          display: inline-flex;
          align-items: center;
          justify-content: center;
          min-width: 18px;
          height: 18px;
          padding: 0 5px;
          font-size: 0.65rem;
          font-weight: 700;
          border-radius: 9px;
          background: var(--theme-success);
          color: white;
          animation: badge-glow 2s ease-in-out infinite;
        }

        @keyframes badge-glow {
          0%, 100% { box-shadow: 0 0 0 0 var(--theme-success-strong); }
          50% { box-shadow: 0 0 8px 2px var(--theme-success-strong); }
        }

        .header-stats {
          display: flex;
          align-items: center;
          gap: 0.75rem;
          font-size: 0.7rem;
        }

        .stat-item {
          display: flex;
          align-items: center;
          gap: 0.35rem;
          color: var(--theme-text-muted);
        }

        .stat-item svg {
          width: 12px;
          height: 12px;
        }

        .stat-value {
          font-weight: 600;
          color: var(--theme-text-secondary);
        }

        .stat-value.speed {
          color: var(--theme-success);
        }

        .stat-value.hit-high {
          color: var(--theme-success);
        }

        .stat-value.hit-medium {
          color: var(--theme-warning);
        }

        .stat-value.hit-low {
          color: var(--theme-error);
        }

        .filters-row {
          display: flex;
          gap: 0.5rem;
          flex-wrap: wrap;
        }

        .filters-row > * {
          flex: 1;
          min-width: 120px;
        }

        @container (min-width: 400px) {
          .filters-row > * {
            flex: 0 1 auto;
            min-width: 140px;
          }
        }

        .clear-filters-btn {
          padding: 0.5rem 0.75rem;
          font-size: 0.7rem;
          font-weight: 500;
          color: var(--theme-button-text);
          background: var(--theme-primary);
          border: none;
          border-radius: var(--theme-border-radius);
          cursor: pointer;
          transition: opacity 0.2s ease;
          white-space: nowrap;
        }

        .clear-filters-btn:hover {
          opacity: 0.9;
        }

        .downloads-list {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
          max-height: 380px;
          overflow-y: auto;
          padding-right: 4px;
        }

        .downloads-list::-webkit-scrollbar {
          width: 4px;
        }

        .downloads-list::-webkit-scrollbar-track {
          background: transparent;
        }

        .downloads-list::-webkit-scrollbar-thumb {
          background: var(--theme-border-secondary);
          border-radius: 2px;
        }

        .download-item {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 0.75rem;
          padding: 0.75rem;
          border-radius: var(--theme-border-radius-lg);
          background: var(--theme-bg-secondary);
          border: 1px solid var(--theme-border-secondary);
          transition: all 0.2s ease;
          animation: item-slide-in 0.3s ease forwards;
          opacity: 0;
          transform: translateY(8px);
        }

        @keyframes item-slide-in {
          to {
            opacity: 1;
            transform: translateY(0);
          }
        }

        .download-item:hover {
          border-color: var(--theme-border-primary);
          background: var(--theme-bg-secondary-on-tertiary);
        }

        .download-item.clickable-row {
          cursor: pointer;
        }

        .download-item.active-item {
          background: linear-gradient(
            135deg,
            var(--theme-success-on-bg) 0%,
            var(--theme-bg-secondary) 100%
          );
          border-color: var(--theme-success-on-border);
        }

        .item-left {
          display: flex;
          align-items: center;
          gap: 0.65rem;
          flex: 1;
          min-width: 0;
        }

        .item-indicator {
          position: relative;
          width: 32px;
          height: 32px;
          display: flex;
          align-items: center;
          justify-content: center;
        }

        .pulse-ring {
          position: absolute;
          inset: 0;
          border-radius: 50%;
          border: 2px solid var(--theme-success);
          animation: pulse-expand 1.5s ease-out infinite;
        }

        @keyframes pulse-expand {
          0% { transform: scale(0.8); opacity: 1; }
          100% { transform: scale(1.4); opacity: 0; }
        }

        .pulse-dot {
          width: 10px;
          height: 10px;
          border-radius: 50%;
          background: var(--theme-success);
          box-shadow: 0 0 8px var(--theme-success);
        }

        .item-icon {
          width: 32px;
          height: 32px;
          display: flex;
          align-items: center;
          justify-content: center;
          border-radius: var(--theme-border-radius);
          background: var(--theme-bg-tertiary);
          color: var(--theme-text-muted);
        }

        .item-info {
          flex: 1;
          min-width: 0;
        }

        .item-name {
          font-size: 0.8rem;
          font-weight: 600;
          color: var(--theme-text-primary);
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
          display: flex;
          align-items: center;
          gap: 0.4rem;
        }

        .count-badge {
          font-size: 0.65rem;
          font-weight: 600;
          padding: 0.1rem 0.35rem;
          border-radius: 4px;
          background: var(--theme-bg-tertiary);
          color: var(--theme-text-muted);
        }

        .item-meta {
          display: flex;
          align-items: center;
          gap: 0.4rem;
          margin-top: 0.2rem;
          font-size: 0.7rem;
          color: var(--theme-text-muted);
          flex-wrap: wrap;
        }

        .service-badge {
          padding: 0.15rem 0.4rem;
          border-radius: 4px;
          background: var(--theme-bg-tertiary);
          color: var(--theme-text-secondary);
          font-weight: 600;
          font-size: 0.65rem;
          text-transform: uppercase;
          letter-spacing: 0.02em;
        }
        .service-badge--service-steam { color: var(--theme-steam); }
        .service-badge--service-epic { color: var(--theme-epic); }
        .service-badge--service-origin { color: var(--theme-origin); }
        .service-badge--service-blizzard { color: var(--theme-blizzard); }
        .service-badge--service-wsus { color: var(--theme-wsus); }
        .service-badge--service-riot { color: var(--theme-riot); }
        .service-badge--service-xbox { color: var(--theme-xbox); }

        .meta-separator {
          color: var(--theme-border-secondary);
        }

        .meta-text {
          color: var(--theme-text-muted);
        }

        .item-right {
          display: flex;
          align-items: center;
          gap: 0.65rem;
          flex-shrink: 0;
        }

        .speed-display {
          display: flex;
          align-items: center;
          gap: 0.35rem;
        }

        .speed-value {
          font-size: 0.8rem;
          font-weight: 700;
          color: var(--theme-success);
          font-variant-numeric: tabular-nums;
        }

        .speed-spinner {
          width: 0.75rem;
          height: 0.75rem;
          color: var(--theme-primary);
          animation: spin 1s linear infinite;
        }

        @keyframes spin {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }

        .size-time {
          display: flex;
          flex-direction: column;
          align-items: flex-end;
          gap: 0.1rem;
        }

        .size-value {
          font-size: 0.75rem;
          font-weight: 600;
          color: var(--theme-text-primary);
        }

        .time-value {
          font-size: 0.65rem;
          color: var(--theme-text-muted);
        }

        .disk-value {
          font-size: 0.65rem;
          color: var(--theme-text-muted);
          font-variant-numeric: tabular-nums;
        }

        .hit-badge {
          padding: 0.25rem 0.5rem;
          border-radius: var(--theme-border-radius);
          font-size: 0.7rem;
          font-weight: 700;
          min-width: 42px;
          text-align: center;
        }

        .hit-badge.high {
          background: var(--theme-success-subtle);
          color: var(--theme-success);
        }

        .hit-badge.medium {
          background: var(--theme-warning-subtle);
          color: var(--theme-warning);
        }

        .hit-badge.low {
          background: var(--theme-warning-faint);
          color: var(--theme-warning-on-error);
        }

        .hit-badge.critical {
          background: var(--theme-error-subtle);
          color: var(--theme-error);
        }

        .empty-state {
          display: flex;
          flex-direction: column;
          align-items: center;
          justify-content: center;
          padding: 2.5rem 1rem;
          text-align: center;
        }

        .empty-icon {
          position: relative;
          width: 56px;
          height: 56px;
          margin-bottom: 1rem;
          display: flex;
          align-items: center;
          justify-content: center;
        }

        .empty-icon-bg {
          position: absolute;
          inset: 0;
          border-radius: 50%;
          border: 2px dashed var(--theme-border-secondary);
          animation: rotate-slow 15s linear infinite;
        }

        @keyframes rotate-slow {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }

        .empty-icon svg {
          position: relative;
          z-index: 1;
          color: var(--theme-text-muted);
          opacity: 0.5;
        }

        .empty-title {
          font-size: 0.9rem;
          font-weight: 600;
          color: var(--theme-text-primary);
          margin-bottom: 0.25rem;
        }

        .empty-desc {
          font-size: 0.75rem;
          color: var(--theme-text-muted);
        }

        .panel-footer {
          display: flex;
          align-items: center;
          justify-content: space-between;
          margin-top: 0.75rem;
          padding-top: 0.75rem;
          border-top: 1px solid var(--theme-border-secondary);
          font-size: 0.7rem;
          color: var(--theme-text-muted);
        }

        .footer-stat {
          display: flex;
          align-items: center;
          gap: 0.25rem;
        }

        .footer-stat strong {
          color: var(--theme-text-secondary);
        }

        .refresh-btn {
          display: flex;
          align-items: center;
          gap: 0.35rem;
          padding: 0.35rem 0.5rem;
          font-size: 0.65rem;
          font-weight: 500;
          color: var(--theme-text-muted);
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
          border-radius: var(--theme-border-radius);
          cursor: pointer;
          transition: all 0.2s ease;
        }

        .refresh-btn:hover {
          color: var(--theme-text-primary);
          border-color: var(--theme-border-primary);
        }

        .refresh-btn svg {
          width: 12px;
          height: 12px;
        }

        .loading-state {
          display: flex;
          align-items: center;
          justify-content: center;
          padding: 3rem 1rem;
          color: var(--theme-text-muted);
          gap: 0.5rem;
          font-size: 0.8rem;
        }

        .loading-spinner {
          width: 1.125rem;
          height: 1.125rem;
          animation: spin 1s linear infinite;
        }

        @media (max-width: 767px) {
          .loading-spinner {
            width: 0.875rem;
            height: 0.875rem;
          }

          .loading-state {
            padding: 2rem 0.75rem;
          }
        }
      `}</style>

      {/* Header */}
      <div className="panel-header">
        <div className="header-top">
          <div className="header-title">
            <h3>{t('dashboard.downloadsPanel.title')}</h3>
          </div>

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
                  <>
                    {t('dashboard.downloadsPanel.active')}
                    {!isHistoricalView && activeCount > 0 && (
                      <span className="count-badge">{activeCount}</span>
                    )}
                  </>
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

        {/* Stats row */}
        <div className="header-stats">
          {viewMode === 'active' ? (
            <>
              {hasActiveDownloads && (
                <div className="stat-item">
                  <TrendingUp />
                  <span className="stat-value speed">{formatSpeed(totalSpeed)}</span>
                </div>
              )}
              <div className="stat-item">
                <HardDrive />
                <span className="stat-value">{activeCount}</span>{' '}
                {t('dashboard.downloadsPanel.game', { count: activeCount })}
              </div>
              <div className="stat-item">
                <span>{t('dashboard.downloadsPanel.live')}</span>
              </div>
            </>
          ) : (
            <>
              <div className="stat-item">
                <span
                  className={`stat-value ${stats.overallHitRate >= 75 ? 'hit-high' : stats.overallHitRate >= 50 ? 'hit-medium' : 'hit-low'}`}
                >
                  {formatPercent(stats.overallHitRate)}
                </span>
                <span>{t('dashboard.downloadsPanel.hitRate')}</span>
              </div>
              <div className="stat-item">
                <span>{getTimeRangeLabel}</span>
              </div>
            </>
          )}
        </div>

        {/* Filters (only for recent view) */}
        {viewMode === 'recent' && latestDownloads.length > 0 && (
          <div className="filters-row">
            <EnhancedDropdown
              options={availableServices.map((service) => ({
                value: service,
                label:
                  service === 'all'
                    ? t('dashboard.downloadsPanel.allServices')
                    : service.charAt(0).toUpperCase() + service.slice(1)
              }))}
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
                className="clear-filters-btn"
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
      </div>

      {/* Downloads List */}
      <div className="downloads-list">
        {viewMode === 'active' ? (
          hasActiveDownloads && activeGames.length > 0 ? (
            activeGames.map((game, idx) => (
              <ActiveDownloadItem
                key={`${game.service}-${game.depotId}-${game.clientIp ?? 'unknown'}`}
                game={game}
                index={idx}
                t={t}
              />
            ))
          ) : (
            <div className="empty-state">
              <div className="empty-icon">
                <div className="empty-icon-bg" />
                <Activity size={24} />
              </div>
              <div className="empty-title">
                {t('dashboard.downloadsPanel.emptyStates.noActive')}
              </div>
              <div className="empty-desc">
                {t('dashboard.downloadsPanel.emptyStates.noActiveDesc')}
              </div>
            </div>
          )
        ) : loading ? (
          <div className="recent-downloads-skeleton">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="recent-downloads-skeleton-row">
                <div className="recent-downloads-skeleton-icon" />
                <div className="recent-downloads-skeleton-content">
                  <div className="recent-downloads-skeleton-title" />
                  <div className="recent-downloads-skeleton-meta" />
                </div>
                <div className="recent-downloads-skeleton-stats">
                  <div className="recent-downloads-skeleton-size" />
                  <div className="recent-downloads-skeleton-date" />
                  <div className="recent-downloads-skeleton-hit-rate" />
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
                index={idx}
                detectionLookup={detectionLookup}
                detectionByName={detectionByName}
                detectionByService={detectionByService}
              />
            );
          })
        ) : (
          <div className="empty-state">
            <div className="empty-icon">
              <div className="empty-icon-bg" />
              <Clock size={24} />
            </div>
            <div className="empty-title">
              {t('dashboard.downloadsPanel.emptyStates.noDownloads')}
            </div>
            <div className="empty-desc">
              {t('dashboard.downloadsPanel.emptyStates.noDownloadsInPeriod', {
                period: getTimeRangeLabel.toLowerCase()
              })}
            </div>
          </div>
        )}
      </div>

      {/* Footer */}
      {viewMode === 'active' && hasActiveDownloads && (
        <div className="panel-footer">
          <div className="footer-stat">
            <strong>{activeGames.length}</strong>{' '}
            {t('dashboard.downloadsPanel.game', { count: activeGames.length })}{' '}
            {t('dashboard.downloadsPanel.downloading')}
          </div>
          <button className="refresh-btn" onClick={refreshSpeed}>
            <RefreshCw />
            {t('dashboard.downloadsPanel.refresh')}
          </button>
        </div>
      )}

      {viewMode === 'recent' && groupedItems.totalGroups > displayCount && (
        <div className="panel-footer">
          <div
            className="footer-stat"
            dangerouslySetInnerHTML={{
              __html: t('dashboard.downloadsPanel.showing', {
                displayed: Math.min(displayCount, groupedItems.displayedItems.length),
                total: groupedItems.totalGroups
              })
            }}
          />
          <div className="footer-stat">
            <strong>{stats.totalDownloads}</strong> {t('dashboard.downloadsPanel.totalDownloads')}
          </div>
        </div>
      )}
    </Card>
  );
};

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default React.memo(RecentDownloadsPanel);
