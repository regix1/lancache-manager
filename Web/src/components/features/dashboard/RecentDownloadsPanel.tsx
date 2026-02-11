import React, { useMemo, useState, useCallback, useEffect } from 'react';
import { Activity, Clock, Loader2, HardDrive, TrendingUp, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { useDownloads } from '@contexts/DashboardDataContext';
import { useDownloadAssociations } from '@contexts/DownloadAssociationsContext';
import { useClientGroups } from '@contexts/ClientGroupContext';
import { useSpeed } from '@contexts/SpeedContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import EventBadge from '../downloads/EventBadge';
import type { Download, EventSummary, GameSpeedInfo } from '@/types';

interface DownloadGroup {
  id: string;
  name: string;
  type: 'game' | 'metadata' | 'content';
  service: string;
  downloads: Download[];
  totalBytes: number;
  totalDownloaded: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
  clientsSet: Set<string>;
  firstSeen: string;
  lastSeen: string;
  count: number;
}

interface RecentDownloadsPanelProps {
  downloads?: Download[];
  timeRange?: string;
  glassmorphism?: boolean;
}

// Active download item component using real-time speed data
const ActiveDownloadItem: React.FC<{ game: GameSpeedInfo; index: number; t: any }> = ({ game, index, t }) => {
  return (
    <div
      className="download-item active-item"
      style={{ animationDelay: `${index * 50}ms` }}
    >
      <div className="item-left">
        <div className="item-indicator">
          <div className="pulse-ring" />
          <div className="pulse-dot" />
        </div>
        <div className="item-info">
          <div className="item-name">
            {game.gameName && game.gameName !== 'Unknown Steam Game' && !game.gameName.match(/^Steam App \d+$/)
              ? game.gameName
              : game.gameName || `Depot ${game.depotId}`}
          </div>
          <div className="item-meta">
            <span className="service-badge">{game.service}</span>
            <span className="meta-separator">•</span>
            <span className="meta-text">{formatBytes(game.totalBytes)}</span>
            <span className="meta-separator">•</span>
            <span className="meta-text">{game.requestCount} {t('dashboard.downloadsPanel.req')}</span>
          </div>
        </div>
      </div>
      <div className="item-right">
        <div className="speed-display">
          <span className="speed-value">{formatSpeed(game.bytesPerSecond)}</span>
          <Loader2 className="speed-spinner" size={12} />
        </div>
        <div className={`hit-badge ${game.cacheHitPercent >= 80 ? 'high' : game.cacheHitPercent >= 50 ? 'medium' : 'low'}`}>
          {game.cacheHitPercent.toFixed(0)}%
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
}

const RecentDownloadItem: React.FC<RecentDownloadItemProps> = ({ item, events = [], index }) => {
  const isGroup = 'downloads' in item;
  const display = isGroup
    ? {
        service: item.service,
        name: item.name,
        totalBytes: item.totalBytes,
        cacheHitPercent: item.totalDownloaded > 0 ? (item.cacheHitBytes / item.totalDownloaded) * 100 : 0,
        startTime: item.lastSeen,
        clientInfo: `${item.clientsSet.size} client${item.clientsSet.size !== 1 ? 's' : ''}`,
        clientIp: null as string | null, // Multiple clients, no single IP
        count: item.count,
        hasGameName: item.downloads.some((d: Download) => d.gameName && d.gameName !== 'Unknown Steam Game' && !d.gameName.match(/^Steam App \d+$/))
      }
    : {
        service: item.service,
        name: item.gameName && item.gameName !== 'Unknown Steam Game' && !item.gameName.match(/^Steam App \d+$/)
          ? item.gameName
          : item.gameName || (item.depotId ? `Depot ${item.depotId}` : item.service),
        totalBytes: item.totalBytes,
        cacheHitPercent: item.cacheHitPercent,
        startTime: item.startTimeUtc,
        clientInfo: item.clientIp, // Fallback for display
        clientIp: item.clientIp, // Single client IP for nickname lookup
        count: 1,
        hasGameName: item.gameName && item.gameName !== 'Unknown Steam Game' && !item.gameName.match(/^Steam App \d+$/)
      };

  const formattedTime = useFormattedDateTime(display.startTime);

  return (
    <div
      className="download-item recent-item"
      style={{ animationDelay: `${index * 30}ms` }}
    >
      <div className="item-left">
        <div className="item-icon">
          <HardDrive size={16} />
        </div>
        <div className="item-info">
          <div className="item-name">
            {display.name}
            {isGroup && display.count > 1 && (
              <span className="count-badge">{display.count}×</span>
            )}
          </div>
          <div className="item-meta">
            <span className="service-badge">{display.service}</span>
            <span className="meta-separator">•</span>
            <span className="meta-text">
              {display.clientIp ? (
                <ClientIpDisplay clientIp={display.clientIp} />
              ) : (
                display.clientInfo
              )}
            </span>
            {events.length > 0 && events.slice(0, 1).map(event => (
              <EventBadge key={event.id} event={event} size="sm" />
            ))}
          </div>
        </div>
      </div>
      <div className="item-right">
        <div className="size-time">
          <span className="size-value">{formatBytes(display.totalBytes)}</span>
          <span className="time-value">{formattedTime}</span>
        </div>
        <div className={`hit-badge ${display.cacheHitPercent >= 75 ? 'high' : display.cacheHitPercent >= 50 ? 'medium' : display.cacheHitPercent >= 25 ? 'low' : 'critical'}`}>
          {formatPercent(display.cacheHitPercent)}
        </div>
      </div>
    </div>
  );
};

const RecentDownloadsPanel: React.FC<RecentDownloadsPanelProps> = ({
  timeRange = 'live',
  glassmorphism = false
}) => {
  const { t } = useTranslation();
  const [selectedService, setSelectedService] = useState<string>('all');
  const [selectedClient, setSelectedClient] = useState<string>('all');
  const [viewMode, setViewMode] = useState<'recent' | 'active'>('recent');
  const { latestDownloads, loading } = useDownloads();
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
  const createGroups = useCallback((downloads: Download[]): { groups: DownloadGroup[]; individuals: Download[] } => {
    const groups: Record<string, DownloadGroup> = {};
    const individuals: Download[] = [];

    downloads.forEach((download) => {
      let groupKey: string;
      let groupName: string;
      let groupType: 'game' | 'metadata' | 'content';

      // Check if we have a valid game (either by appId or by name)
      const hasValidGameAppId = download.gameAppId && download.gameAppId > 0;
      const hasValidGameName = download.gameName &&
        download.gameName !== 'Unknown Steam Game' &&
        !download.gameName.match(/^Steam App \d+$/);

      if (hasValidGameName) {
        // Only show as a named game when we have an actual resolved name
        groupKey = hasValidGameAppId
          ? `game-appid-${download.gameAppId}`
          : `game-${download.gameName}`;
        groupName = download.gameName!;
        groupType = 'game';
      } else if (download.service.toLowerCase() !== 'steam') {
        groupKey = `service-${download.service.toLowerCase()}`;
        groupName = `${download.service} Downloads`;
        groupType = download.totalBytes === 0 ? 'metadata' : 'content';
      } else {
        individuals.push(download);
        return;
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
      groups[groupKey].totalBytes += download.totalBytes || 0;
      groups[groupKey].totalDownloaded += download.totalBytes || 0;
      groups[groupKey].cacheHitBytes += download.cacheHitBytes || 0;
      groups[groupKey].cacheMissBytes += download.cacheMissBytes || 0;
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
  }, []);

  const getTimeRangeLabel = useMemo(() => {
    const key = `dashboard.downloadsPanel.timeRanges.${timeRange}` as const;
    return t(key);
  }, [timeRange, t]);

  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map((d) => d.service));
    return ['all', ...Array.from(services).sort()];
  }, [latestDownloads]);

  const { clientGroups } = useClientGroups();

  const availableClients = useMemo(() => {
    const clients = new Set(latestDownloads.map((d) => d.clientIp));
    return Array.from(clients).sort();
  }, [latestDownloads]);

  const clientOptions = useMemo(() => {
    // Build a map of group IDs to the IPs in downloads that belong to that group
    const groupedIps = new Map<number, { group: typeof clientGroups[0]; ips: string[] }>();
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
  }, [availableClients, getGroupForIp, clientGroups, t]);

  const filteredDownloads = useMemo(() => {
    return latestDownloads.filter((download) => {
      if (selectedService !== 'all' && download.service !== selectedService) return false;
      if (selectedClient !== 'all') {
        // Check if it's a group selection (e.g., "group-123")
        if (selectedClient.startsWith('group-')) {
          const groupId = parseInt(selectedClient.replace('group-', ''), 10);
          const group = clientGroups.find(g => g.id === groupId);
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
      if (download.gameName && download.gameName !== 'Unknown Steam Game' && !download.gameName.match(/^Steam App \d+$/)) {
        return true;
      }
      if (download.service.toLowerCase() !== 'steam') return true;
      return false;
    });

    const allItems: (DownloadGroup | Download)[] = [...groups, ...filteredIndividuals];

    allItems.sort((a, b) => {
      const aTime = 'downloads' in a
        ? Math.max(...a.downloads.map((d: Download) => new Date(d.startTimeUtc).getTime()))
        : new Date(a.startTimeUtc).getTime();
      const bTime = 'downloads' in b
        ? Math.max(...b.downloads.map((d: Download) => new Date(d.startTimeUtc).getTime()))
        : new Date(b.startTimeUtc).getTime();
      return bTime - aTime;
    });

    return {
      displayedItems: allItems.slice(0, displayCount),
      totalGroups: allItems.length
    };
  }, [filteredDownloads, createGroups, getAssociations]); // getAssociations triggers re-render when associations load

  // Fetch associations for all downloads in displayed groups
  useEffect(() => {
    const downloadIds: number[] = [];
    groupedItems.displayedItems.forEach(item => {
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
    const totalBytes = filteredDownloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
    const totalCacheHits = filteredDownloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
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
          background: color-mix(in srgb, var(--theme-bg-secondary) 50%, transparent);
        }

        .tab-btn.active {
          color: var(--theme-button-text);
          background: var(--theme-primary);
          box-shadow: 0 2px 4px color-mix(in srgb, var(--theme-primary) 25%, transparent);
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
          0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--theme-success) 40%, transparent); }
          50% { box-shadow: 0 0 8px 2px color-mix(in srgb, var(--theme-success) 30%, transparent); }
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
          background: color-mix(in srgb, var(--theme-bg-secondary) 80%, var(--theme-bg-tertiary));
        }

        .download-item.active-item {
          background: linear-gradient(
            135deg,
            color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-secondary)) 0%,
            var(--theme-bg-secondary) 100%
          );
          border-color: color-mix(in srgb, var(--theme-success) 25%, var(--theme-border-secondary));
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
          background: color-mix(in srgb, var(--theme-primary) 15%, transparent);
          color: var(--theme-primary);
          font-weight: 600;
          font-size: 0.65rem;
          text-transform: uppercase;
          letter-spacing: 0.02em;
        }

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

        .hit-badge {
          padding: 0.25rem 0.5rem;
          border-radius: var(--theme-border-radius);
          font-size: 0.7rem;
          font-weight: 700;
          min-width: 42px;
          text-align: center;
        }

        .hit-badge.high {
          background: color-mix(in srgb, var(--theme-success) 15%, transparent);
          color: var(--theme-success);
        }

        .hit-badge.medium {
          background: color-mix(in srgb, var(--theme-warning) 15%, transparent);
          color: var(--theme-warning);
        }

        .hit-badge.low {
          background: color-mix(in srgb, var(--theme-warning) 10%, transparent);
          color: color-mix(in srgb, var(--theme-warning) 80%, var(--theme-error));
        }

        .hit-badge.critical {
          background: color-mix(in srgb, var(--theme-error) 15%, transparent);
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

        .loading-state svg {
          animation: spin 1s linear infinite;
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
              { value: 'recent', label: t('dashboard.downloadsPanel.recent'), icon: <Clock size={14} /> },
              {
                value: 'active',
                label: <>{t('dashboard.downloadsPanel.active')}{!isHistoricalView && activeCount > 0 && <span className="count-badge">{activeCount}</span>}</>,
                icon: <Activity size={14} />,
                disabled: isHistoricalView,
                tooltip: isHistoricalView ? t('dashboard.downloadsPanel.activeDownloadsOnly') : undefined
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
                <span className="stat-value">{activeCount}</span> {t('dashboard.downloadsPanel.game', { count: activeCount })}
              </div>
              <div className="stat-item">
                <span>{t('dashboard.downloadsPanel.live')}</span>
              </div>
            </>
          ) : (
            <>
              <div className="stat-item">
                <span className={`stat-value ${stats.overallHitRate >= 75 ? 'hit-high' : stats.overallHitRate >= 50 ? 'hit-medium' : 'hit-low'}`}>
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
                label: service === 'all' ? t('dashboard.downloadsPanel.allServices') : service.charAt(0).toUpperCase() + service.slice(1)
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
              <ActiveDownloadItem key={game.depotId} game={game} index={idx} t={t} />
            ))
          ) : (
            <div className="empty-state">
              <div className="empty-icon">
                <div className="empty-icon-bg" />
                <Activity size={24} />
              </div>
              <div className="empty-title">{t('dashboard.downloadsPanel.emptyStates.noActive')}</div>
              <div className="empty-desc">{t('dashboard.downloadsPanel.emptyStates.noActiveDesc')}</div>
            </div>
          )
        ) : loading ? (
          <div className="loading-state">
            <Loader2 size={18} />
            <span>{t('dashboard.downloadsPanel.emptyStates.loading')}</span>
          </div>
        ) : groupedItems.displayedItems.length > 0 ? (
          groupedItems.displayedItems.map((item, idx) => {
            const isGroup = 'downloads' in item;
            const events = isGroup
              ? Array.from(
                  item.downloads.reduce((acc, d) => {
                    getAssociations(d.id).events.forEach(e => acc.set(e.id, e));
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
              />
            );
          })
        ) : (
          <div className="empty-state">
            <div className="empty-icon">
              <div className="empty-icon-bg" />
              <Clock size={24} />
            </div>
            <div className="empty-title">{t('dashboard.downloadsPanel.emptyStates.noDownloads')}</div>
            <div className="empty-desc">{t('dashboard.downloadsPanel.emptyStates.noDownloadsInPeriod', { period: getTimeRangeLabel.toLowerCase() })}</div>
          </div>
        )}
      </div>

      {/* Footer */}
      {viewMode === 'active' && hasActiveDownloads && (
        <div className="panel-footer">
          <div className="footer-stat">
            <strong>{activeGames.length}</strong> {t('dashboard.downloadsPanel.game', { count: activeGames.length })} {t('dashboard.downloadsPanel.downloading')}
          </div>
          <button className="refresh-btn" onClick={refreshSpeed}>
            <RefreshCw />
            {t('dashboard.downloadsPanel.refresh')}
          </button>
        </div>
      )}

      {viewMode === 'recent' && groupedItems.totalGroups > displayCount && (
        <div className="panel-footer">
          <div className="footer-stat" dangerouslySetInnerHTML={{ 
            __html: t('dashboard.downloadsPanel.showing', { 
              displayed: Math.min(displayCount, groupedItems.displayedItems.length), 
              total: groupedItems.totalGroups 
            })
          }} />
          <div className="footer-stat">
            <strong>{stats.totalDownloads}</strong> {t('dashboard.downloadsPanel.totalDownloads')}
          </div>
        </div>
      )}
    </Card>
  );
};

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default RecentDownloadsPanel;
