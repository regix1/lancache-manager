import React, { memo, useMemo, useState, useCallback, useRef } from 'react';
import { Activity, Clock, Loader2 } from 'lucide-react';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { EnhancedDropdown } from '../ui/EnhancedDropdown';
import { useDownloads } from '../../contexts/DownloadsContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

interface DownloadGroup {
  id: string;
  name: string;
  type: 'game' | 'metadata' | 'content';
  service: string;
  downloads: any[];
  totalBytes: number; // Size of the game/content (largest single download)
  totalDownloaded: number; // Total bytes downloaded across all sessions
  cacheHitBytes: number;
  cacheMissBytes: number;
  clientsSet: Set<string>;
  firstSeen: string;
  lastSeen: string;
  count: number;
}

interface RecentDownloadsPanelProps {
  downloads?: any[]; // Keep for backward compatibility but won't be used
  timeRange?: string;
}

interface ActiveDownloadRowProps {
  download: any;
}

const ActiveDownloadRow: React.FC<ActiveDownloadRowProps> = ({ download }) => {
  const formattedStartTime = useFormattedDateTime(download.startTimeUtc);

  return (
    <div
      className="rounded-lg p-3 border transition-all duration-200 themed-card hover:shadow-lg"
      style={{
        backgroundColor: 'var(--theme-bg-primary)',
        borderColor: 'var(--theme-border-primary)'
      }}
      onMouseEnter={(e) =>
        (e.currentTarget.style.borderColor = 'var(--theme-border-secondary)')
      }
      onMouseLeave={(e) =>
        (e.currentTarget.style.borderColor = 'var(--theme-border-primary)')
      }
    >
      <div className="flex justify-between items-start mb-2">
        <div className="flex-1">
          <div className="flex items-center gap-2 mb-1">
            <span
              className="inline-block w-2 h-2 rounded-full animate-pulse"
              style={{ backgroundColor: 'var(--theme-success)' }}
            ></span>
            {download.gameName && (
              <div className="text-sm font-medium text-themed-primary truncate flex items-center gap-2">
                <span>{download.gameName}</span>
                <Loader2
                  className="w-4 h-4 animate-spin"
                  style={{ color: 'var(--theme-primary)' }}
                />
              </div>
            )}
          </div>
          <div className="flex items-center gap-2">
            <span className="text-xs px-2 py-0.5 rounded bg-themed-accent bg-opacity-10 text-themed-accent font-medium">
              {download.service}
            </span>
            <span className="text-xs text-themed-muted">{download.clientIp}</span>
          </div>
        </div>
        <span className="text-xs text-themed-muted whitespace-nowrap ml-2">
          {formattedStartTime}
        </span>
      </div>
      <div className="flex justify-between items-center mt-2">
        <div className="flex items-center gap-3">
          <span className="text-themed-primary text-sm">
            {formatBytes(download.totalBytes)}
          </span>
          <div className="flex gap-2 text-xs">
            <span className="cache-hit">↓ {formatBytes(download.cacheHitBytes)}</span>
            <span className="cache-miss">
              ↑ {formatBytes(download.cacheMissBytes)}
            </span>
          </div>
        </div>
        <span
          className={`text-xs px-2 py-1 rounded hit-rate-badge ${
            download.cacheHitPercent > 75
              ? 'high'
              : download.cacheHitPercent > 50
                ? 'medium'
                : download.cacheHitPercent > 25
                  ? 'low'
                  : 'warning'
          }`}
        >
          {formatPercent(download.cacheHitPercent)} Hit
        </span>
      </div>
    </div>
  );
};

interface RecentDownloadRowProps {
  item: DownloadGroup | any;
}

const RecentDownloadRow: React.FC<RecentDownloadRowProps> = ({ item }) => {
  const isGroup = 'downloads' in item;
  const display = isGroup
    ? {
        service: item.service,
        name: item.name,
        totalBytes: item.totalBytes,
        totalDownloaded: item.totalDownloaded,
        cacheHitBytes: item.cacheHitBytes,
        cacheMissBytes: item.cacheMissBytes,
        cacheHitPercent:
          item.totalDownloaded > 0
            ? (item.cacheHitBytes / item.totalDownloaded) * 100
            : 0,
        startTime: item.lastSeen,
        clientIp: `${item.clientsSet.size} client${item.clientsSet.size !== 1 ? 's' : ''}`,
        count: item.count,
        type: item.type,
        hasGameName: item.downloads.some((d: any) => d.gameName && d.gameName !== 'Unknown Steam Game' && !d.gameName.match(/^Steam App \d+$/))
      }
    : {
        service: item.service,
        name:
          item.gameName &&
          item.gameName !== 'Unknown Steam Game' &&
          !item.gameName.match(/^Steam App \d+$/)
            ? item.gameName
            : item.service,
        totalBytes: item.totalBytes,
        totalDownloaded: item.totalBytes,
        cacheHitBytes: item.cacheHitBytes,
        cacheMissBytes: item.cacheMissBytes,
        cacheHitPercent: item.cacheHitPercent,
        startTime: item.startTimeUtc,
        clientIp: item.clientIp,
        count: 1,
        type: 'individual',
        hasGameName: item.gameName &&
          item.gameName !== 'Unknown Steam Game' &&
          !item.gameName.match(/^Steam App \d+$/)
      };

  const formattedStartTime = useFormattedDateTime(display.startTime);

  return (
    <div
      className="rounded-lg p-3 border transition-all duration-200 themed-card hover:shadow-lg"
      style={{
        backgroundColor: 'var(--theme-bg-primary)',
        borderColor: 'var(--theme-border-primary)'
      }}
      onMouseEnter={(e) =>
        (e.currentTarget.style.borderColor = 'var(--theme-border-secondary)')
      }
      onMouseLeave={(e) =>
        (e.currentTarget.style.borderColor = 'var(--theme-border-primary)')
      }
    >
      <div className="flex justify-between items-start mb-2">
        <div className="flex-1">
          {('hasGameName' in display && display.hasGameName) && (
            <div className="flex items-center gap-2 mb-1">
              <div className="text-sm font-medium text-themed-primary truncate">
                {display.name}
              </div>
              {isGroup && (
                <span className="text-xs px-2 py-0.5 rounded bg-themed-tertiary text-themed-secondary">
                  {display.count}×
                </span>
              )}
            </div>
          )}
          <div className="flex items-center gap-2">
            <span className="text-xs px-2 py-0.5 rounded bg-themed-accent bg-opacity-10 text-themed-accent font-medium">
              {display.service}
            </span>
            {isGroup && !('hasGameName' in display && display.hasGameName) && (
              <span className="text-xs px-2 py-0.5 rounded bg-themed-tertiary text-themed-secondary">
                {display.count}×
              </span>
            )}
            <span className="text-xs text-themed-muted">{display.clientIp}</span>
          </div>
        </div>
        <span className="text-xs text-themed-muted whitespace-nowrap ml-2">
          {formattedStartTime}
        </span>
      </div>
      <div className="flex justify-between items-center mt-2">
        <div className="flex items-center gap-3">
          <span className="text-themed-primary text-sm">
            {formatBytes(display.totalBytes)}
          </span>
          <div className="flex gap-2 text-xs">
            <span className="cache-hit">↓ {formatBytes(display.cacheHitBytes)}</span>
            <span className="cache-miss">
              ↑ {formatBytes(display.cacheMissBytes)}
            </span>
          </div>
        </div>
        <span
          className={`text-xs px-2 py-1 rounded hit-rate-badge ${
            display.cacheHitPercent > 75
              ? 'high'
              : display.cacheHitPercent > 50
                ? 'medium'
                : display.cacheHitPercent > 25
                  ? 'low'
                  : 'warning'
          }`}
        >
          {formatPercent(display.cacheHitPercent)} Hit
        </span>
      </div>
    </div>
  );
};

const RecentDownloadsPanel: React.FC<RecentDownloadsPanelProps> = memo(
  ({ timeRange = 'live' }) => {
    const [selectedService, setSelectedService] = useState<string>('all');
    const [selectedClient, setSelectedClient] = useState<string>('all');
    const [viewMode, setViewMode] = useState<'recent' | 'active'>('recent');
    const { activeDownloads, latestDownloads, loading } = useDownloads();

    // Touch/swipe handling
    const touchStartX = useRef<number>(0);
    const touchEndX = useRef<number>(0);
    const containerRef = useRef<HTMLDivElement>(null);

    // Grouping logic adapted from DownloadsTab
    const createGroups = useCallback(
      (downloads: any[]): { groups: DownloadGroup[]; individuals: any[] } => {
        const groups: Record<string, DownloadGroup> = {};
        const individuals: any[] = [];

        downloads.forEach((download) => {
          let groupKey: string;
          let groupName: string;
          let groupType: 'game' | 'metadata' | 'content';

          if (
            download.gameName &&
            download.gameName !== 'Unknown Steam Game' &&
            !download.gameName.match(/^Steam App \d+$/)
          ) {
            groupKey = `game-${download.gameName}`;
            groupName = download.gameName;
            groupType = 'game';
          } else if (download.gameName && download.gameName.match(/^Steam App \d+$/)) {
            groupKey = 'unmapped-steam-apps';
            groupName = 'Unmapped Steam Apps';
            groupType = 'content';
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
          // Track total downloaded across all sessions
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
      },
      []
    );

    // Backend now groups chunks by game, so we just need to display them
    // No frontend grouping needed - backend handles grouping via GameAppId + ClientIp
    const groupedActiveDownloads = useMemo(() => {
      // Filter out unknown/unmapped games from active downloads
      const filtered = activeDownloads.filter((download) => {
        // For Steam downloads, check if game name is valid
        if (download.service.toLowerCase() === 'steam') {
          // Filter out downloads without a game name
          if (!download.gameName) {
            return false;
          }

          const trimmedName = download.gameName.trim();
          const gameNameLower = trimmedName.toLowerCase();

          // Filter out "Unknown Steam Game" or any variation with "unknown"
          if (gameNameLower.includes('unknown')) {
            return false;
          }

          // Filter out unmapped Steam apps (e.g., "Steam App 12345")
          if (/^steam app \d+$/i.test(trimmedName)) {
            return false;
          }
        }

        // Keep all non-Steam downloads and valid Steam downloads
        return true;
      });

      // Backend already grouped by game, just sort and limit
      const sorted = [...filtered].sort(
        (a, b) => new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
      );
      return sorted.slice(0, 10);
    }, [activeDownloads]);

    // Count total active downloads (backend already grouped by game)
    // Use the filtered count to show only valid downloads
    const activeDownloadCount = useMemo(() => {
      return groupedActiveDownloads.length;
    }, [groupedActiveDownloads]);

    const getTimeRangeLabel = useMemo(() => {
      const labels: Record<string, string> = {
        '15m': 'Last 15 Minutes',
        '30m': 'Last 30 Minutes',
        '1h': 'Last Hour',
        '6h': 'Last 6 Hours',
        '12h': 'Last 12 Hours',
        '24h': 'Last 24 Hours',
        '7d': 'Last 7 Days',
        '30d': 'Last 30 Days',
        '90d': 'Last 90 Days',
        live: 'Live Data'
      };
      return labels[timeRange] || 'Recent';
    }, [timeRange]);

    const availableServices = useMemo(() => {
      const services = new Set(latestDownloads.map((d) => d.service));
      return ['all', ...Array.from(services).sort()];
    }, [latestDownloads]);

    const availableClients = useMemo(() => {
      const clients = new Set(latestDownloads.map((d) => d.clientIp));
      return ['all', ...Array.from(clients).sort()];
    }, [latestDownloads]);

    const filteredDownloads = useMemo(() => {
      return latestDownloads.filter((download) => {
        if (selectedService !== 'all' && download.service !== selectedService) {
          return false;
        }
        if (selectedClient !== 'all' && download.clientIp !== selectedClient) {
          return false;
        }
        return true;
      });
    }, [latestDownloads, selectedService, selectedClient]);

    const displayCount = 10;
    const groupedItems = useMemo(() => {
      const { groups, individuals } = createGroups(filteredDownloads);

      // Filter out unmapped/unknown individual downloads - only show grouped items
      // Individual downloads without proper game names will be hidden until they're mapped
      const filteredIndividuals = individuals.filter((download) => {
        // Show if it has a valid game name (not Unknown or Steam App pattern)
        if (
          download.gameName &&
          download.gameName !== 'Unknown Steam Game' &&
          !download.gameName.match(/^Steam App \d+$/)
        ) {
          return true;
        }
        // Show non-Steam services
        if (download.service.toLowerCase() !== 'steam') {
          return true;
        }
        // Hide unmapped Steam downloads
        return false;
      });

      const allItems: (DownloadGroup | any)[] = [...groups, ...filteredIndividuals];

      allItems.sort((a, b) => {
        const aTime =
          'downloads' in a
            ? Math.max(...a.downloads.map((d: any) => new Date(d.startTimeUtc).getTime()))
            : new Date(a.startTimeUtc).getTime();
        const bTime =
          'downloads' in b
            ? Math.max(...b.downloads.map((d: any) => new Date(d.startTimeUtc).getTime()))
            : new Date(b.startTimeUtc).getTime();
        return bTime - aTime;
      });

      return {
        displayedItems: allItems.slice(0, displayCount),
        totalGroups: allItems.length
      };
    }, [filteredDownloads, createGroups]);

    const stats = useMemo(() => {
      // Count downloads in the displayed groups only
      const displayedDownloads = groupedItems.displayedItems.reduce((count, item) => {
        if ('downloads' in item) {
          // It's a group, count all downloads in the group
          return count + item.downloads.length;
        } else {
          // It's an individual download
          return count + 1;
        }
      }, 0);

      const totalDownloads = filteredDownloads.length;
      const totalBytes = filteredDownloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
      const totalCacheHits = filteredDownloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
      const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;

      return { displayedDownloads, totalDownloads, totalBytes, totalCacheHits, overallHitRate };
    }, [filteredDownloads, groupedItems]);

    // Swipe handlers for switching between Recent and Active views
    const handleTouchStart = useCallback((e: React.TouchEvent) => {
      touchEndX.current = 0; // Reset touchEnd to detect if movement occurred
      touchStartX.current = e.touches[0].clientX;
    }, []);

    const handleTouchMove = useCallback((e: React.TouchEvent) => {
      touchEndX.current = e.touches[0].clientX;
    }, []);

    const handleTouchEnd = useCallback(() => {
      // Only trigger swipe if there was actual movement (touchEnd was set by touchMove)
      if (!touchStartX.current || !touchEndX.current) return;

      const minSwipeDistance = 50;
      const swipeDistance = touchStartX.current - touchEndX.current;

      if (Math.abs(swipeDistance) > minSwipeDistance) {
        if (swipeDistance > 0) {
          // Swiped left - go to active view
          setViewMode('active');
        } else {
          // Swiped right - go to recent view
          setViewMode('recent');
        }
      }

      // Reset
      touchStartX.current = 0;
      touchEndX.current = 0;
    }, []);

    return (
      <Card>
        <div
          ref={containerRef}
          onTouchStart={handleTouchStart}
          onTouchMove={handleTouchMove}
          onTouchEnd={handleTouchEnd}
        >
          <div className="mb-4">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 mb-3">
              <div className="flex flex-col sm:flex-row sm:items-center gap-3">
                <h3 className="text-lg font-semibold text-themed-primary">Downloads</h3>

                {/* View Mode Toggle */}
                <div className="flex items-center gap-1 p-1 rounded-lg bg-themed-tertiary w-full sm:w-auto">
                  <button
                    onClick={() => setViewMode('recent')}
                    className={`flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium transition-colors flex-1 sm:flex-initial ${
                      viewMode === 'recent' ? 'bg-primary' : 'text-themed-secondary hover:text-themed-primary'
                    }`}
                    style={{
                      color: viewMode === 'recent' ? 'var(--theme-button-text)' : undefined
                    }}
                  >
                    <Clock className="w-4 h-4" />
                    Recent
                  </button>
                  <button
                    onClick={() => setViewMode('active')}
                    className={`flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium transition-colors flex-1 sm:flex-initial ${
                      viewMode === 'active' ? 'bg-primary' : 'text-themed-secondary hover:text-themed-primary'
                    }`}
                    style={{
                      color: viewMode === 'active' ? 'var(--theme-button-text)' : undefined
                    }}
                  >
                    <Activity className="w-4 h-4" />
                    Active
                    {activeDownloadCount > 0 && (
                      <span
                        className="ml-1 px-1.5 py-0.5 rounded-full text-xs font-bold"
                        style={{
                          backgroundColor: 'var(--theme-error)',
                          color: 'white'
                        }}
                      >
                        {activeDownloadCount}
                      </span>
                    )}
                  </button>
                </div>
              </div>
              <div className="flex items-center flex-wrap gap-2 sm:gap-3 w-full sm:w-auto justify-start sm:justify-end">
                {viewMode === 'active' ? (
                  <>
                    {activeDownloadCount > 0 && (
                      <span className="text-xs text-themed-muted whitespace-nowrap">
                        {activeDownloadCount} active
                      </span>
                    )}
                    <span className="text-xs text-themed-muted whitespace-nowrap">Live</span>
                  </>
                ) : (
                  <>
                    {!loading && latestDownloads.length > 0 && (
                      <span
                        className={`text-xs px-2 py-0.5 rounded whitespace-nowrap ${
                          stats.overallHitRate > 50 ? 'hit-rate-high' : 'hit-rate-warning'
                        }`}
                      >
                        {formatPercent(stats.overallHitRate)} hit
                      </span>
                    )}
                    <span className="text-xs text-themed-muted whitespace-nowrap">
                      {getTimeRangeLabel}
                    </span>
                  </>
                )}
              </div>
            </div>

            {!loading && latestDownloads.length > 0 && viewMode === 'recent' && (
              <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center justify-between w-full">
                <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center flex-1 w-full sm:w-auto">
                  <EnhancedDropdown
                    options={availableServices.map((service) => ({
                      value: service,
                      label:
                        service === 'all'
                          ? 'All Services'
                          : service.charAt(0).toUpperCase() + service.slice(1)
                    }))}
                    value={selectedService}
                    onChange={setSelectedService}
                    className="w-full sm:w-40"
                  />

                  <EnhancedDropdown
                    options={availableClients.map((client) => ({
                      value: client,
                      label: client === 'all' ? 'All Clients' : client
                    }))}
                    value={selectedClient}
                    onChange={setSelectedClient}
                    className="w-full sm:w-48"
                  />
                </div>

                {(selectedService !== 'all' || selectedClient !== 'all') && (
                  <button
                    onClick={() => {
                      setSelectedService('all');
                      setSelectedClient('all');
                    }}
                    className="text-xs px-3 py-2 rounded-lg bg-themed-accent text-white hover:opacity-80 transition-opacity w-full sm:w-auto"
                  >
                    Clear Filters
                  </button>
                )}
              </div>
            )}
          </div>

          <div className="space-y-3 max-h-[400px] overflow-y-auto">
            {viewMode === 'active' ? (
              // Active Downloads View - backend already grouped chunks by game
              groupedActiveDownloads.length > 0 ? (
                groupedActiveDownloads.map((download, idx) => (
                  <ActiveDownloadRow key={download.id || idx} download={download} />
                ))
              ) : (
                <div className="flex flex-col items-center justify-center h-32 text-themed-muted">
                  <Activity className="w-12 h-12 mb-2 opacity-30" />
                  <span>No active downloads</span>
                </div>
              )
            ) : loading ? (
              <div className="flex items-center justify-center h-32 text-themed-muted">
                <div className="flex items-center gap-2">
                  <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-themed-accent"></div>
                  <span>Loading all downloads...</span>
                </div>
              </div>
            ) : groupedItems.displayedItems.length > 0 ? (
              groupedItems.displayedItems.map((item, idx) => {
                const isGroup = 'downloads' in item;
                return (
                  <RecentDownloadRow key={isGroup ? item.id : item.id || idx} item={item} />
                );
              })
            ) : (
              <div className="flex items-center justify-center h-32 text-themed-muted">
                No downloads in the {getTimeRangeLabel.toLowerCase()}
              </div>
            )}
          </div>

          {(groupedItems.totalGroups > displayCount ||
            selectedService !== 'all' ||
            selectedClient !== 'all') && (
            <div
              className="mt-3 pt-3 border-t text-center"
              style={{ borderColor: 'var(--theme-border-primary)' }}
            >
              <span className="text-xs text-themed-muted">
                {groupedItems.totalGroups > displayCount && (
                  <>
                    Showing {Math.min(displayCount, groupedItems.displayedItems.length)} of{' '}
                    {groupedItems.totalGroups} groups •{' '}
                  </>
                )}
                {stats.displayedDownloads} of {stats.totalDownloads} downloads
              </span>
            </div>
          )}
        </div>
      </Card>
    );
  },
  (prevProps, nextProps) => {
    // Only re-render if timeRange changed
    return prevProps.timeRange === nextProps.timeRange;
  }
);

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default RecentDownloadsPanel;
