import React, { memo, useMemo, useState, useEffect } from 'react';
import { ArrowRight } from 'lucide-react';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { EnhancedDropdown } from '../ui/EnhancedDropdown';
import ApiService from '../../services/api.service';
import { useTimeFilter } from '../../contexts/TimeFilterContext';

interface DownloadGroup {
  id: string;
  name: string;
  type: 'game' | 'metadata' | 'content';
  service: string;
  downloads: any[];
  totalBytes: number;
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
  onNavigateToDownloads?: () => void;
}

const RecentDownloadsPanel: React.FC<RecentDownloadsPanelProps> = memo(
  ({ timeRange = 'live', onNavigateToDownloads }) => {
    const [allDownloads, setAllDownloads] = useState<any[]>([]);
    const [loading, setLoading] = useState(true);
    const [selectedService, setSelectedService] = useState<string>('all');
    const [selectedClient, setSelectedClient] = useState<string>('all');
    const { getTimeRangeParams } = useTimeFilter();

    // Fetch ALL downloads for the recent downloads panel
    useEffect(() => {
      const fetchAllDownloads = async () => {
        try {
          setLoading(true);
          const { startTime, endTime } = getTimeRangeParams();
          const downloads = await ApiService.getLatestDownloads(
            undefined, // signal
            'unlimited', // Get ALL downloads
            startTime,
            endTime
          );
          setAllDownloads(downloads);
        } catch (error) {
          console.error('Failed to fetch all downloads for Recent Downloads Panel:', error);
          setAllDownloads([]);
        } finally {
          setLoading(false);
        }
      };

      fetchAllDownloads();
    }, [timeRange, getTimeRangeParams]);

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

    // Grouping logic adapted from DownloadsTab
    const createGroups = (downloads: any[]): { groups: DownloadGroup[], individuals: any[] } => {
      const groups: Record<string, DownloadGroup> = {};
      const individuals: any[] = [];

      downloads.forEach(download => {
        let groupKey: string;
        let groupName: string;
        let groupType: 'game' | 'metadata' | 'content';

        if (download.gameName &&
            download.gameName !== 'Unknown Steam Game' &&
            !download.gameName.match(/^Steam App \d+$/)) {
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
            cacheHitBytes: 0,
            cacheMissBytes: 0,
            clientsSet: new Set<string>(),
            firstSeen: download.startTime,
            lastSeen: download.startTime,
            count: 0
          };
        }

        groups[groupKey].downloads.push(download);
        groups[groupKey].totalBytes += download.totalBytes || 0;
        groups[groupKey].cacheHitBytes += download.cacheHitBytes || 0;
        groups[groupKey].cacheMissBytes += download.cacheMissBytes || 0;
        groups[groupKey].clientsSet.add(download.clientIp);
        groups[groupKey].count++;

        if (download.startTime < groups[groupKey].firstSeen) {
          groups[groupKey].firstSeen = download.startTime;
        }
        if (download.startTime > groups[groupKey].lastSeen) {
          groups[groupKey].lastSeen = download.startTime;
        }
      });

      return { groups: Object.values(groups), individuals };
    };

    const availableServices = useMemo(() => {
      const services = new Set(allDownloads.map(d => d.service));
      return ['all', ...Array.from(services).sort()];
    }, [allDownloads]);

    const availableClients = useMemo(() => {
      const clients = new Set(allDownloads.map(d => d.clientIp));
      return ['all', ...Array.from(clients).sort()];
    }, [allDownloads]);

    const filteredDownloads = useMemo(() => {
      return allDownloads.filter(download => {
        if (selectedService !== 'all' && download.service !== selectedService) {
          return false;
        }
        if (selectedClient !== 'all' && download.clientIp !== selectedClient) {
          return false;
        }
        return true;
      });
    }, [allDownloads, selectedService, selectedClient]);

    const displayCount = 10;
    const groupedItems = useMemo(() => {
      const { groups, individuals } = createGroups(filteredDownloads);

      // Filter out unmapped/unknown individual downloads - only show grouped items
      // Individual downloads without proper game names will be hidden until they're mapped
      const filteredIndividuals = individuals.filter(download => {
        // Show if it has a valid game name (not Unknown or Steam App pattern)
        if (download.gameName &&
            download.gameName !== 'Unknown Steam Game' &&
            !download.gameName.match(/^Steam App \d+$/)) {
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
        const aTime = 'downloads' in a
          ? Math.max(...a.downloads.map((d: any) => new Date(d.startTime).getTime()))
          : new Date(a.startTime).getTime();
        const bTime = 'downloads' in b
          ? Math.max(...b.downloads.map((d: any) => new Date(d.startTime).getTime()))
          : new Date(b.startTime).getTime();
        return bTime - aTime;
      });

      return {
        displayedItems: allItems.slice(0, displayCount),
        totalGroups: allItems.length
      };
    }, [filteredDownloads]);

    const stats = useMemo(() => {
      const totalDownloads = filteredDownloads.length;
      const totalBytes = filteredDownloads.reduce((sum, d) => sum + (d.totalBytes || 0), 0);
      const totalCacheHits = filteredDownloads.reduce((sum, d) => sum + (d.cacheHitBytes || 0), 0);
      const overallHitRate = totalBytes > 0 ? (totalCacheHits / totalBytes) * 100 : 0;

      return { totalDownloads, totalBytes, totalCacheHits, overallHitRate };
    }, [filteredDownloads]);

    return (
      <Card>
        <div className="mb-4">
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-2">
              <h3 className="text-lg font-semibold text-themed-primary">Recent Downloads</h3>
              {onNavigateToDownloads && (
                <button
                  onClick={onNavigateToDownloads}
                  className="p-1.5 rounded-lg transition-all hover:bg-themed-hover"
                  title="View Active Downloads"
                  style={{ color: 'var(--theme-text-secondary)' }}
                >
                  <ArrowRight className="w-5 h-5" />
                </button>
              )}
            </div>
            <div className="flex items-center gap-3">
              {!loading && allDownloads.length > 0 && (
                <>
                  <span className="text-xs text-themed-muted">{stats.totalDownloads} shown</span>
                  <span
                    className={`text-xs px-2 py-0.5 rounded ${
                      stats.overallHitRate > 50 ? 'hit-rate-high' : 'hit-rate-warning'
                    }`}
                  >
                    {formatPercent(stats.overallHitRate)} hit
                  </span>
                </>
              )}
              <span className="text-xs text-themed-muted">{getTimeRangeLabel}</span>
            </div>
          </div>

          {!loading && allDownloads.length > 0 && (
            <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center justify-between w-full">
              <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center flex-1 w-full sm:w-auto">
                <EnhancedDropdown
                  options={availableServices.map(service => ({
                    value: service,
                    label: service === 'all' ? 'All Services' : service.charAt(0).toUpperCase() + service.slice(1)
                  }))}
                  value={selectedService}
                  onChange={setSelectedService}
                  className="w-full sm:w-40"
                />

                <EnhancedDropdown
                  options={availableClients.map(client => ({
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
          {loading ? (
            <div className="flex items-center justify-center h-32 text-themed-muted">
              <div className="flex items-center gap-2">
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-themed-accent"></div>
                <span>Loading all downloads...</span>
              </div>
            </div>
          ) : groupedItems.displayedItems.length > 0 ? (
            groupedItems.displayedItems.map((item, idx) => {
              const isGroup = 'downloads' in item;
              const display = isGroup ? {
                service: item.service,
                name: item.name,
                totalBytes: item.totalBytes,
                cacheHitBytes: item.cacheHitBytes,
                cacheMissBytes: item.cacheMissBytes,
                cacheHitPercent: item.totalBytes > 0 ? (item.cacheHitBytes / item.totalBytes) * 100 : 0,
                startTime: item.lastSeen,
                clientIp: `${item.clientsSet.size} client${item.clientsSet.size !== 1 ? 's' : ''}`,
                count: item.count,
                type: item.type
              } : {
                service: item.service,
                name: item.gameName && item.gameName !== 'Unknown Steam Game' && !item.gameName.match(/^Steam App \d+$/)
                  ? item.gameName
                  : 'Individual Download',
                totalBytes: item.totalBytes,
                cacheHitBytes: item.cacheHitBytes,
                cacheMissBytes: item.cacheMissBytes,
                cacheHitPercent: item.cacheHitPercent,
                startTime: item.startTime,
                clientIp: item.clientIp,
                count: 1,
                type: 'individual'
              };

              return (
                <div
                  key={isGroup ? item.id : (item.id || idx)}
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
                        <div className="text-sm font-medium text-themed-primary truncate">
                          {display.name}
                        </div>
                        {isGroup && (
                          <span className="text-xs px-2 py-0.5 rounded bg-themed-tertiary text-themed-secondary">
                            {display.count}×
                          </span>
                        )}
                      </div>
                      <div className="flex items-center gap-2">
                        <span className="text-xs px-2 py-0.5 rounded bg-themed-accent bg-opacity-10 text-themed-accent font-medium">
                          {display.service}
                        </span>
                        <span className="text-xs text-themed-muted">{display.clientIp}</span>
                      </div>
                    </div>
                    <span className="text-xs text-themed-muted whitespace-nowrap ml-2">
                      {formatDateTime(display.startTime)}
                    </span>
                  </div>
                  <div className="flex justify-between items-center mt-2">
                    <div className="flex items-center gap-3">
                      <span className="text-themed-primary text-sm">
                        {formatBytes(display.totalBytes)}
                      </span>
                      <div className="flex gap-2 text-xs">
                        <span className="cache-hit">↓ {formatBytes(display.cacheHitBytes)}</span>
                        <span className="cache-miss">↑ {formatBytes(display.cacheMissBytes)}</span>
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
            })
          ) : (
            <div className="flex items-center justify-center h-32 text-themed-muted">
              No downloads in the {getTimeRangeLabel.toLowerCase()}
            </div>
          )}
        </div>

        {(groupedItems.totalGroups > displayCount || (selectedService !== 'all' || selectedClient !== 'all')) && (
          <div
            className="mt-3 pt-3 border-t text-center"
            style={{ borderColor: 'var(--theme-border-primary)' }}
          >
            <span className="text-xs text-themed-muted">
              {groupedItems.totalGroups > displayCount && (
                <>Showing {Math.min(displayCount, groupedItems.displayedItems.length)} of {groupedItems.totalGroups} groups • </>
              )}
              {filteredDownloads.length} of {allDownloads.length} downloads
            </span>
          </div>
        )}
      </Card>
    );
  }
);

RecentDownloadsPanel.displayName = 'RecentDownloadsPanel';

export default RecentDownloadsPanel;
