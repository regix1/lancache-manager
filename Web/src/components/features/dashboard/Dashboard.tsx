import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import {
  HardDrive,
  Download,
  Users,
  Database,
  TrendingUp,
  Zap,
  Server,
  Activity,
  Eye,
  EyeOff,
  ChevronDown,
  Search,
  GripVertical,
  LayoutGrid,
  X
} from 'lucide-react';
import { useStats } from '@contexts/StatsContext';
import { useDownloads } from '@contexts/DownloadsContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { useDraggableCards } from '@hooks/useDraggableCards';
import { formatBytes, formatPercent } from '@utils/formatters';
import { STORAGE_KEYS } from '@utils/constants';
import { type StatCardData, type SparklineDataResponse } from '../../../types';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import StatCard from '@components/common/StatCard';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpDefinition } from '@components/ui/HelpPopover';
import EnhancedServiceChart from './EnhancedServiceChart';
import RecentDownloadsPanel from './RecentDownloadsPanel';
import TopClientsTable from './TopClientsTable';
// Widget imports
import PeakUsageHours from './widgets/PeakUsageHours';
import CacheGrowthTrend from './widgets/CacheGrowthTrend';

type CardVisibility = Record<string, boolean>;
type AllStatCards = Record<string, StatCardData>;

const DEFAULT_CARD_VISIBILITY: CardVisibility = {
  totalCache: true,
  usedSpace: true,
  bandwidthSaved: true,
  addedToCache: true,
  totalServed: true,
  activeDownloads: true,
  activeClients: true,
  cacheHitRatio: true
};

const DEFAULT_CARD_ORDER: string[] = [
  'totalCache',
  'usedSpace',
  'bandwidthSaved',
  'addedToCache',
  'totalServed',
  'activeDownloads',
  'activeClients',
  'cacheHitRatio'
];

const StatTooltips: Record<string, string> = {
  totalCache: 'Total storage capacity of your LANCache system',
  usedSpace: 'Amount of storage currently occupied by cached content',
  bandwidthSaved: 'Internet bandwidth saved by serving from cache',
  addedToCache: 'New content downloaded and cached',
  totalServed: 'Total data delivered to clients',
  activeDownloads: 'Number of downloads currently in progress',
  activeClients: 'Number of unique client devices that have accessed the cache',
  cacheHitRatio: 'Cache hits vs internet downloads. Higher is better!'
};

// Trend help content for cards with sparklines
const TrendHelpContent: Record<string, React.ReactNode> = {
  bandwidthSaved: (
    <div className="space-y-1.5">
      <HelpDefinition term="↑ Up" termColor="green">More bandwidth saved recently</HelpDefinition>
      <HelpDefinition term="↓ Down" termColor="orange">Less bandwidth saved recently</HelpDefinition>
      <div className="text-[10px] mt-2 pt-2 border-t" style={{ borderColor: 'var(--theme-border)', color: 'var(--theme-text-muted)' }}>
        Compares recent activity to earlier in the selected time period
      </div>
    </div>
  ),
  addedToCache: (
    <div className="space-y-1.5">
      <HelpDefinition term="↑ Up" termColor="green">More new content being cached</HelpDefinition>
      <HelpDefinition term="↓ Down" termColor="orange">Less new content being cached</HelpDefinition>
      <div className="text-[10px] mt-2 pt-2 border-t" style={{ borderColor: 'var(--theme-border)', color: 'var(--theme-text-muted)' }}>
        Compares recent activity to earlier in the selected time period
      </div>
    </div>
  ),
  totalServed: (
    <div className="space-y-1.5">
      <HelpDefinition term="↑ Up" termColor="green">More data served recently</HelpDefinition>
      <HelpDefinition term="↓ Down" termColor="orange">Less data served recently</HelpDefinition>
      <div className="text-[10px] mt-2 pt-2 border-t" style={{ borderColor: 'var(--theme-border)', color: 'var(--theme-text-muted)' }}>
        Compares recent activity to earlier in the selected time period
      </div>
    </div>
  ),
  cacheHitRatio: (
    <div className="space-y-1.5">
      <HelpDefinition term="↑ Up" termColor="green">Hit ratio improving</HelpDefinition>
      <HelpDefinition term="↓ Down" termColor="orange">Hit ratio declining</HelpDefinition>
      <div className="text-[10px] mt-2 pt-2 border-t" style={{ borderColor: 'var(--theme-border)', color: 'var(--theme-text-muted)' }}>
        Shows change in percentage points (not percent change)
      </div>
    </div>
  ),
};

const Dashboard: React.FC = () => {
  const { cacheInfo, clientStats, serviceStats, dashboardStats, loading } = useStats();
  const { activeDownloads, latestDownloads } = useDownloads();
  const { timeRange, getTimeRangeParams, customStartDate, customEndDate } = useTimeFilter();

  // Track if initial card animations have completed - prevents re-animation on reorder
  const initialAnimationCompleteRef = useRef(false);
  const [initialAnimationComplete, setInitialAnimationComplete] = useState(false);

  // Mark initial animation as complete after entrance animations finish
  useEffect(() => {
    if (!initialAnimationCompleteRef.current) {
      const timer = setTimeout(() => {
        initialAnimationCompleteRef.current = true;
        setInitialAnimationComplete(true);
      }, 800); // Allow time for staggered entrance animations to complete
      return () => clearTimeout(timer);
    }
  }, []);

  // Sparkline data from API
  const [sparklineData, setSparklineData] = useState<SparklineDataResponse | null>(null);

  // Fetch sparkline data when time range changes
  useEffect(() => {
    const controller = new AbortController();

    const fetchSparklines = async () => {
      try {
        const period = timeRange === 'custom' ? 'all' : timeRange;
        const data = await ApiService.getSparklineData(period, controller.signal);
        setSparklineData(data);
      } catch (err) {
        // Ignore abort errors
        if (!controller.signal.aborted) {
          console.error('Failed to fetch sparkline data:', err);
        }
      }
    };

    fetchSparklines();

    return () => controller.abort();
  }, [timeRange]);

  // Filter out services with only small files (< 1MB) and 0-byte files from dashboard data
  const filteredLatestDownloads = useMemo(() => {
    return latestDownloads.filter((download) => {
      // Filter out 0-byte files
      if (download.totalBytes === 0) {
        return false;
      }
      // Filter out small files (< 1MB)
      if (download.totalBytes < 1024 * 1024) {
        return false;
      }
      return true;
    });
  }, [latestDownloads]);

  // For active downloads, don't filter by size - show them all
  // They're currently downloading and will grow in size
  const filteredActiveDownloads = activeDownloads;

  const filteredServiceStats = useMemo(() => {
    return serviceStats.filter((service) => {
      // Filter out services that only have small files
      const serviceDownloads = latestDownloads.filter(
        (d) => d.service.toLowerCase() === service.service.toLowerCase()
      );
      const hasLargeFiles = serviceDownloads.some((d) => d.totalBytes > 1024 * 1024);
      return hasLargeFiles;
    });
  }, [serviceStats, latestDownloads]);

  // Filter client stats based on date range
  const filteredClientStats = useMemo(() => {
    if (!clientStats || clientStats.length === 0) {
      return [];
    }

    // For 'live' mode, show all clients
    if (timeRange === 'live') {
      return clientStats;
    }

    // Get the time range parameters
    const { startTime, endTime } = getTimeRangeParams();

    // Filter clients based on lastActivityUtc date
    return clientStats.filter((client) => {
      if (!client.lastActivityUtc) {
        return false;
      }

      // Parse the lastActivityUtc date
      const lastActivityDate = new Date(client.lastActivityUtc);
      const lastActivityTimestamp = Math.floor(lastActivityDate.getTime() / 1000);

      // Check if lastActivity is within the selected range
      if (startTime && endTime) {
        return lastActivityTimestamp >= startTime && lastActivityTimestamp <= endTime;
      }

      return true;
    });
  }, [clientStats, timeRange, getTimeRangeParams]);
  const [showLoading, setShowLoading] = useState(false); // Delayed loading state
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const dropdownRef = useRef<HTMLDivElement>(null);

  // Delay showing loading state to avoid flashing for quick API responses
  useEffect(() => {
    if (loading) {
      const timer = setTimeout(() => {
        setShowLoading(true);
      }, 200); // Wait 200ms before showing skeleton
      return () => clearTimeout(timer);
    } else {
      setShowLoading(false); // Hide immediately when done
    }
  }, [loading]);

  // Use drag-and-drop hook for card reordering
  const {
    cardOrder,
    draggedCard,
    dragOverCard,
    isDragMode,
    showDragHint,
    dragHandlers,
    resetCardOrder,
    hideDragHint
  } = useDraggableCards({
    defaultOrder: DEFAULT_CARD_ORDER,
    storageKey: STORAGE_KEYS.DASHBOARD_CARD_ORDER,
    dragHintStorageKey: 'dashboard-hide-drag-hint'
  });

  const getTimeRangeLabel = useCallback(() => {
    switch (timeRange) {
      case '1h':
        return 'Last hour';
      case '6h':
        return 'Last 6 hours';
      case '12h':
        return 'Last 12 hours';
      case '24h':
        return 'Last 24 hours';
      case '7d':
        return 'Last 7 days';
      case '30d':
        return 'Last 30 days';
      case 'live':
        return 'Live data';
      case 'custom':
        return 'Custom range';
      default:
        return 'Last 24 hours';
    }
  }, [timeRange]);

  const [cardVisibility, setCardVisibility] = useState<CardVisibility>(() => {
    const saved = storage.getItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY);
    if (saved) {
      try {
        return { ...DEFAULT_CARD_VISIBILITY, ...JSON.parse(saved) };
      } catch (e) {
        console.error('Failed to parse card visibility settings:', e);
        return DEFAULT_CARD_VISIBILITY;
      }
    }
    return DEFAULT_CARD_VISIBILITY;
  });

  useEffect(() => {
    storage.setItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY, JSON.stringify(cardVisibility));
  }, [cardVisibility]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setDropdownOpen(false);
        setSearchQuery('');
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  // Dashboard stats now come from the context which fetches them from the API with proper time filtering

  const toggleCardVisibility = useCallback((cardKey: string) => {
    setCardVisibility((prev: CardVisibility) => ({
      ...prev,
      [cardKey]: !prev[cardKey]
    }));
  }, []);

  const stats = useMemo(() => {
    const activeClients = [...new Set(filteredActiveDownloads.map((d) => d.clientIp))].length;
    const totalActiveDownloads = filteredActiveDownloads.length;
    const totalDownloads = filteredServiceStats.reduce(
      (sum, service) => sum + (service.totalDownloads || 0),
      0
    );

    return {
      activeClients,
      totalActiveDownloads,
      totalDownloads,
      bandwidthSaved: dashboardStats?.period?.bandwidthSaved || 0,
      addedToCache: dashboardStats?.period?.addedToCache || 0,
      totalServed: dashboardStats?.period?.totalServed || 0,
      cacheHitRatio: dashboardStats?.period?.hitRatio || 0,
      uniqueClients: dashboardStats?.uniqueClients || filteredClientStats.length
    };
  }, [filteredActiveDownloads, filteredServiceStats, dashboardStats, filteredClientStats]);

  const allStatCards = useMemo<AllStatCards>(
    () => ({
      totalCache: {
        key: 'totalCache',
        title: 'Total Cache',
        value: cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '0 B',
        subtitle: 'Drive capacity',
        icon: Database,
        color: 'blue' as const,
        visible: cardVisibility.totalCache,
        tooltip: StatTooltips.totalCache
      },
      usedSpace: {
        key: 'usedSpace',
        title: 'Used Space',
        value: cacheInfo ? formatBytes(cacheInfo.usedCacheSize) : '0 B',
        subtitle: cacheInfo ? formatPercent(cacheInfo.usagePercent) : '0%',
        icon: HardDrive,
        color: 'green' as const,
        visible: cardVisibility.usedSpace,
        tooltip: StatTooltips.usedSpace
      },
      bandwidthSaved: {
        key: 'bandwidthSaved',
        title: 'Bandwidth Saved',
        value: formatBytes(stats.bandwidthSaved),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: TrendingUp,
        color: 'emerald' as const,
        visible: cardVisibility.bandwidthSaved,
        tooltip: StatTooltips.bandwidthSaved
      },
      addedToCache: {
        key: 'addedToCache',
        title: 'Added to Cache',
        value: formatBytes(stats.addedToCache),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Zap,
        color: 'purple' as const,
        visible: cardVisibility.addedToCache,
        tooltip: StatTooltips.addedToCache
      },
      totalServed: {
        key: 'totalServed',
        title: 'Total Served',
        value: formatBytes(stats.totalServed),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Server,
        color: 'indigo' as const,
        visible: cardVisibility.totalServed,
        tooltip: StatTooltips.totalServed
      },
      activeDownloads: {
        key: 'activeDownloads',
        title: 'Active Downloads',
        value: stats.totalActiveDownloads,
        subtitle: `${dashboardStats?.period?.downloads || filteredLatestDownloads.length} in period`,
        icon: Download,
        color: 'orange' as const,
        visible: cardVisibility.activeDownloads,
        tooltip: StatTooltips.activeDownloads
      },
      activeClients: {
        key: 'activeClients',
        title: 'Active Clients',
        value: stats.uniqueClients,
        subtitle: `${stats.totalDownloads} downloads`,
        icon: Users,
        color: 'yellow' as const,
        visible: cardVisibility.activeClients,
        tooltip: StatTooltips.activeClients
      },
      cacheHitRatio: {
        key: 'cacheHitRatio',
        title: 'Cache Hit Ratio',
        value: formatPercent(stats.cacheHitRatio * 100),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Activity,
        color: 'cyan' as const,
        visible: cardVisibility.cacheHitRatio,
        tooltip: StatTooltips.cacheHitRatio
      }
    }),
    [
      cacheInfo,
      cardVisibility,
      stats,
      timeRange,
      getTimeRangeLabel,
      dashboardStats,
      filteredLatestDownloads
    ]
  );

  const orderedStatCards = useMemo(() => {
    return cardOrder
      .map((key: string) => allStatCards[key])
      .filter((card: StatCardData | undefined): card is StatCardData => card !== undefined);
  }, [cardOrder, allStatCards]);

  const visibleCards = orderedStatCards.filter((card: StatCardData) => card.visible);
  const hiddenCards = orderedStatCards.filter((card: StatCardData) => !card.visible);
  const hiddenCardsCount = hiddenCards.length;

  const filteredHiddenCards = hiddenCards.filter(
    (card: StatCardData) =>
      card.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      (card.subtitle && card.subtitle.toLowerCase().includes(searchQuery.toLowerCase()))
  );

  const getIconStyle = (color: string): React.CSSProperties => {
    const iconColors: Record<string, string> = {
      blue: 'var(--theme-icon-blue)',
      green: 'var(--theme-icon-green)',
      emerald: 'var(--theme-icon-emerald)',
      purple: 'var(--theme-icon-purple)',
      indigo: 'var(--theme-icon-indigo)',
      orange: 'var(--theme-icon-orange)',
      yellow: 'var(--theme-icon-yellow)',
      cyan: 'var(--theme-icon-cyan)'
    };

    return {
      backgroundColor: iconColors[color] || 'var(--theme-icon-gray)'
    };
  };

  return (
    <div
      className="space-y-6"
      onClick={(e) => {
        // Cancel drag mode if clicking outside of cards
        if (isDragMode && !(e.target as Element).closest('[data-card-key]')) {
          dragHandlers.onTouchEnd();
        }
      }}
    >
      {/* Dashboard Header */}
      <div className="flex flex-col sm:flex-row sm:justify-between sm:items-center gap-4">
        <h2 className="text-2xl font-bold text-themed-primary tracking-tight hidden md:block">
          Dashboard
        </h2>
        <div className="flex flex-row items-center gap-2">
          {/* Hidden Cards Button - only shows when cards are hidden */}
          {hiddenCardsCount > 0 && (
            <div className="relative" ref={dropdownRef}>
              <Tooltip content={`${hiddenCardsCount} hidden card${hiddenCardsCount !== 1 ? 's' : ''} - click to restore`} strategy="overlay">
                <button
                  onClick={() => setDropdownOpen(!dropdownOpen)}
                  className="flex items-center gap-2 px-3 py-2 text-sm transition-colors rounded-lg border"
                  style={{
                    color: 'var(--theme-text-secondary)',
                    backgroundColor: 'var(--theme-bg-secondary)',
                    borderColor: 'var(--theme-border-primary)'
                  }}
                  onMouseEnter={(e) => {
                    e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                    e.currentTarget.style.color = 'var(--theme-text-primary)';
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)';
                    e.currentTarget.style.color = 'var(--theme-text-secondary)';
                  }}
                >
                  <EyeOff className="w-4 h-4" />
                  <span className="hidden sm:inline">{hiddenCardsCount} Hidden</span>
                  <span className="sm:hidden">{hiddenCardsCount}</span>
                  <ChevronDown
                    className={`w-3 h-3 transition-transform duration-200 ${dropdownOpen ? 'rotate-180' : ''}`}
                  />
                </button>
              </Tooltip>

              {/* Hidden Cards Dropdown */}
              {dropdownOpen && (
                <div
                  className="absolute right-0 mt-2 w-72 sm:w-80 rounded-lg border shadow-xl z-50"
                  style={{
                    backgroundColor: 'var(--theme-card-bg)',
                    borderColor: 'var(--theme-card-border)'
                  }}
                >
                  {/* Search - only show if more than 3 hidden cards */}
                  {hiddenCardsCount > 3 && (
                    <div className="p-3 border-b" style={{ borderColor: 'var(--theme-border-primary)' }}>
                      <div className="relative">
                        <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-themed-muted" />
                        <input
                          type="text"
                          value={searchQuery}
                          onChange={(e) => setSearchQuery(e.target.value)}
                          placeholder="Search hidden cards..."
                          className="w-full pl-10 pr-3 py-2 rounded-lg text-sm"
                          style={{
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            color: 'var(--theme-text-primary)',
                            border: '1px solid var(--theme-border-primary)'
                          }}
                          autoFocus
                        />
                      </div>
                    </div>
                  )}

                  {/* Show All Button */}
                  <div className="p-2 border-b" style={{ borderColor: 'var(--theme-border-primary)' }}>
                    <button
                      onClick={() => {
                        setCardVisibility(DEFAULT_CARD_VISIBILITY);
                        setDropdownOpen(false);
                        setSearchQuery('');
                      }}
                      className="w-full px-3 py-2 text-sm rounded-lg transition-colors text-left flex items-center gap-2"
                      style={{ color: 'var(--theme-primary)' }}
                      onMouseEnter={(e) => {
                        e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                      }}
                      onMouseLeave={(e) => {
                        e.currentTarget.style.backgroundColor = 'transparent';
                      }}
                    >
                      <Eye className="w-4 h-4" />
                      Show all cards
                    </button>
                  </div>

                  {/* Hidden Cards List */}
                  <div className="max-h-64 overflow-y-auto p-2">
                    {filteredHiddenCards.length > 0 ? (
                      filteredHiddenCards.map((card: StatCardData) => {
                        const Icon = card.icon;
                        return (
                          <button
                            key={card.key}
                            onClick={() => {
                              toggleCardVisibility(card.key);
                              if (hiddenCardsCount === 1) {
                                setDropdownOpen(false);
                                setSearchQuery('');
                              }
                            }}
                            className="w-full p-2.5 rounded-lg transition-colors flex items-center gap-3 group"
                            onMouseEnter={(e) => {
                              e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                            }}
                            onMouseLeave={(e) => {
                              e.currentTarget.style.backgroundColor = 'transparent';
                            }}
                          >
                            <div
                              className="p-1.5 rounded-lg group-hover:scale-105 transition-transform"
                              style={getIconStyle(card.color)}
                            >
                              <Icon className="w-4 h-4 text-white" />
                            </div>
                            <div className="flex-1 text-left min-w-0">
                              <div className="text-sm text-themed-primary font-medium truncate">
                                {card.title}
                              </div>
                            </div>
                            <Eye className="w-4 h-4 text-themed-muted group-hover:text-themed-primary transition-colors flex-shrink-0" />
                          </button>
                        );
                      })
                    ) : (
                      <div className="px-3 py-4 text-center text-themed-muted text-sm">
                        No cards match "{searchQuery}"
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>
          )}

          {/* Reset Layout Button */}
          <Tooltip content="Reset card layout to default order" strategy="overlay">
            <button
              onClick={resetCardOrder}
              className="flex items-center gap-2 px-3 py-2 text-sm transition-colors rounded-lg border"
              style={{
                color: 'var(--theme-text-secondary)',
                backgroundColor: 'var(--theme-bg-secondary)',
                borderColor: 'var(--theme-border-primary)'
              }}
              onMouseEnter={(e) => {
                e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                e.currentTarget.style.color = 'var(--theme-text-primary)';
              }}
              onMouseLeave={(e) => {
                e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)';
                e.currentTarget.style.color = 'var(--theme-text-secondary)';
              }}
            >
              <LayoutGrid className="w-4 h-4" />
              <span className="hidden sm:inline">Reset Layout</span>
            </button>
          </Tooltip>
        </div>
      </div>

      {/* Touch instruction for mobile */}
      {showDragHint && (
        <div className="md:hidden">
          {!isDragMode ? (
            <div className="flex items-center justify-between py-3 px-4 rounded-lg bg-themed-secondary text-themed-muted text-sm">
              <div className="flex items-center gap-2">
                <span>💡</span>
                <span>
                  Hold any card for 1 second to select it, then tap another card to swap positions
                </span>
              </div>
              <button
                onClick={hideDragHint}
                className="ml-2 p-1 rounded hover:bg-themed-hover transition-colors flex-shrink-0"
                style={{ color: 'var(--theme-text-muted)' }}
                title="Hide this hint"
              >
                <X className="w-4 h-4" />
              </button>
            </div>
          ) : (
            <div className="text-center py-2 px-4 rounded-lg bg-primary text-white text-sm animate-pulse">
              🔄 Drag mode active - Tap another card to move here, or tap anywhere to cancel
            </div>
          )}
        </div>
      )}

      {/* Stats Grid */}
      {showLoading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 animate-fadeIn">
          {[1, 2, 3, 4, 5, 6, 7, 8].map((i) => (
            <div
              key={i}
              className="rounded-lg p-4 border animate-pulse glass-card"
              style={{
                height: '160px'
              }}
            >
              <div
                className="h-4 rounded"
                style={{ backgroundColor: 'var(--theme-bg-hover)', width: '60%' }}
              ></div>
              <div
                className="h-8 rounded mt-2"
                style={{ backgroundColor: 'var(--theme-bg-hover)', width: '80%' }}
              ></div>
              <div
                className="h-3 rounded mt-2"
                style={{ backgroundColor: 'var(--theme-bg-hover)', width: '40%' }}
              ></div>
              <div
                className="h-8 rounded mt-3"
                style={{ backgroundColor: 'var(--theme-bg-hover)', width: '100%' }}
              ></div>
            </div>
          ))}
        </div>
      ) : (
        <div
          className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 animate-fadeIn isolate"
        >
          {visibleCards.map((card: StatCardData, visualIndex: number) => (
            <div
              key={card.key}
              data-card-key={card.key}
              className={`relative group transition-all duration-200 ${
                isDragMode && draggedCard === card.key ? 'scale-105 shadow-lg' : ''
              } ${isDragMode && dragOverCard === card.key ? 'transform translate-y-1' : ''}`}
              style={{
                boxShadow: dragOverCard === card.key ? `0 0 0 2px var(--theme-primary)` : 'none',
                cursor: draggedCard === card.key ? 'grabbing' : 'default',
                opacity: isDragMode && draggedCard === card.key ? 0.8 : 1
              }}
              draggable={!isDragMode}
              onDragStart={(e) => dragHandlers.onDragStart(e, card.key)}
              onDragEnd={dragHandlers.onDragEnd}
              onDragOver={dragHandlers.onDragOver}
              onDragEnter={(e) => dragHandlers.onDragEnter(e, card.key)}
              onDragLeave={dragHandlers.onDragLeave}
              onDrop={(e) => dragHandlers.onDrop(e, card.key)}
              onTouchStart={() => dragHandlers.onTouchStart(card.key)}
              onTouchEnd={dragHandlers.onTouchEnd}
              onClick={() => dragHandlers.onCardTap(card.key)}
            >
              {/* Desktop drag handle - smaller, hover-triggered */}
              {
                <Tooltip
                  content="Drag to reorder"
                  strategy="overlay"
                  className="absolute top-2 left-2 opacity-0 group-hover:opacity-100 transition-all hidden md:block z-[5]"
                >
                  <div
                    className="p-1 rounded"
                    style={{
                      cursor: 'grab'
                    }}
                    onMouseEnter={(e) => {
                      e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                    }}
                    onMouseLeave={(e) => {
                      e.currentTarget.style.backgroundColor = 'transparent';
                    }}
                  >
                    <GripVertical className="w-4 h-4 text-themed-muted" />
                  </div>
                </Tooltip>
              }

              {/* Mobile drag handle - small, transparent, always visible in top-left */}
              {
                <Tooltip
                  content="Hold to reorder"
                  strategy="overlay"
                  className="absolute top-2 left-2 transition-all md:hidden opacity-60 z-[5]"
                >
                  <div
                    className="p-1 rounded"
                    style={{
                      cursor: 'grab',
                      backgroundColor: 'transparent'
                    }}
                  >
                    <GripVertical
                      className="w-4 h-4 transition-colors"
                      style={{ color: 'var(--theme-drag-handle)' }}
                    />
                  </div>
                </Tooltip>
              }

              {/* Touch feedback overlay */}
              {isDragMode && draggedCard === card.key && (
                <div
                  className="absolute inset-0 rounded-lg border-2 border-dashed md:hidden"
                  style={{
                    borderColor: 'var(--theme-primary)',
                    backgroundColor: 'rgba(var(--theme-primary-rgb), 0.1)',
                    zIndex: 5
                  }}
                />
              )}

              <StatCard
                title={card.title}
                value={card.value}
                subtitle={card.subtitle}
                icon={card.icon}
                color={card.color}
                glassmorphism={true}
                animateValue={true}
                sparklineData={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.data :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.data :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.data :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.data :
                  undefined
                }
                trend={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.trend :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.trend :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.trend :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.trend :
                  undefined
                }
                percentChange={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.percentChange :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.percentChange :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.percentChange :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.percentChange :
                  undefined
                }
                trendHelp={TrendHelpContent[card.key]}
                staggerIndex={initialAnimationComplete ? undefined : visualIndex}
              />

              <Tooltip
                content="Hide this card"
                strategy="overlay"
                className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity"
              >
                <button
                  onClick={() => toggleCardVisibility(card.key)}
                  className="p-1.5 rounded-lg transition-colors"
                  style={{
                    backgroundColor: 'transparent'
                  }}
                  onMouseEnter={(e) => {
                    e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.backgroundColor = 'transparent';
                  }}
                >
                  <EyeOff className="w-3.5 h-3.5 text-themed-muted" />
                </button>
              </Tooltip>
            </div>
          ))}
        </div>
      )}

      {/* Charts Row - Pass the actual data arrays */}
      {showLoading ? (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 animate-fadeIn">
          {[1, 2].map((i) => (
            <div
              key={i}
              className="rounded-lg p-6 border animate-pulse glass-card"
              style={{
                height: '400px'
              }}
            >
              <div
                className="h-6 rounded mb-4"
                style={{ backgroundColor: 'var(--theme-bg-hover)', width: '40%' }}
              ></div>
              <div
                className="h-full rounded"
                style={{ backgroundColor: 'var(--theme-bg-hover)' }}
              ></div>
            </div>
          ))}
        </div>
      ) : (
        <div
          key={`charts-${timeRange}-${customStartDate?.getTime()}-${customEndDate?.getTime()}`}
          className="grid grid-cols-1 lg:grid-cols-2 gap-6 animate-fadeIn"
        >
          <EnhancedServiceChart serviceStats={filteredServiceStats || []} timeRange={timeRange} glassmorphism={true} />
          <RecentDownloadsPanel downloads={filteredLatestDownloads || []} timeRange={timeRange} glassmorphism={true} />
        </div>
      )}

      {/* Analytics Widgets Row */}
      {showLoading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 animate-fadeIn">
          {[1, 2, 3, 4].map((i) => (
            <div
              key={i}
              className="rounded-lg p-4 border animate-pulse glass-card"
              style={{
                height: '200px'
              }}
            >
              <div
                className="h-5 rounded mb-3"
                style={{ backgroundColor: 'var(--theme-bg-hover)', width: '50%' }}
              ></div>
              <div className="space-y-2">
                {[1, 2, 3].map((j) => (
                  <div
                    key={j}
                    className="h-8 rounded"
                    style={{ backgroundColor: 'var(--theme-bg-hover)' }}
                  ></div>
                ))}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <div
          className="grid grid-cols-1 md:grid-cols-2 gap-4 animate-fadeIn"
        >
          <PeakUsageHours
            period={timeRange === 'custom' ? 'all' : timeRange}
            glassmorphism={true}
            staggerIndex={8}
          />
          <CacheGrowthTrend
            usedCacheSize={cacheInfo?.usedCacheSize || 0}
            totalCacheSize={cacheInfo?.totalCacheSize || 0}
            period={timeRange === 'custom' ? 'all' : timeRange}
            glassmorphism={true}
            staggerIndex={9}
          />
        </div>
      )}

      {/* Top Clients - Pass the filtered data arrays */}
      {showLoading ? (
        <div
          className="rounded-lg p-6 border animate-pulse animate-fadeIn glass-card"
          style={{
            height: '400px'
          }}
        >
          <div
            className="h-6 rounded mb-4"
            style={{ backgroundColor: 'var(--theme-bg-hover)', width: '30%' }}
          ></div>
          <div className="space-y-3">
            {[1, 2, 3, 4, 5].map((i) => (
              <div
                key={i}
                className="h-12 rounded"
                style={{ backgroundColor: 'var(--theme-bg-hover)' }}
              ></div>
            ))}
          </div>
        </div>
      ) : (
        <div
          key={`top-clients-${timeRange}-${customStartDate?.getTime()}-${customEndDate?.getTime()}`}
          className="animate-fadeIn"
        >
          <TopClientsTable
            clientStats={filteredClientStats || []}
            timeRange={timeRange}
            customStartDate={customStartDate}
            customEndDate={customEndDate}
            glassmorphism={true}
          />
        </div>
      )}
    </div>
  );
};

export default Dashboard;
