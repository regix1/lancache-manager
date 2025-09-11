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
  Clock,
  RotateCcw,
  Search,
  GripVertical,
  Loader
} from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { STORAGE_KEYS } from '../../utils/constants';
import { type StatCardData, type DashboardStats } from '../../types';
import StatCard from '../common/StatCard';
import EnhancedServiceChart from './EnhancedServiceChart';
import RecentDownloadsPanel from './RecentDownloadsPanel';
import TopClientsTable from './TopClientsTable';

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
  bandwidthSaved:
    'Amount of internet bandwidth saved by serving files from local cache instead of downloading them again',
  addedToCache: 'New content downloaded and stored in cache for future use',
  totalServed: 'Total amount of data delivered to clients (cache hits + new downloads)',
  cacheHitRatio:
    'Percentage of requests served from cache vs downloaded from internet. Higher is better!'
};

const Dashboard: React.FC = () => {
  const { cacheInfo, activeDownloads, latestDownloads, clientStats, serviceStats } = useData();
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(null);
  const [loading] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [timeFilterOpen, setTimeFilterOpen] = useState(false);
  const [selectedTimeRange, setSelectedTimeRange] = useState('24h');
  const dropdownRef = useRef<HTMLDivElement>(null);
  const timeFilterRef = useRef<HTMLDivElement>(null);

  const [draggedCard, setDraggedCard] = useState<string | null>(null);
  const [dragOverCard, setDragOverCard] = useState<string | null>(null);
  const dragCounter = useRef(0);

  const [cardOrder, setCardOrder] = useState<string[]>(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.DASHBOARD_CARD_ORDER);
    if (saved) {
      try {
        const order = JSON.parse(saved);
        const hasAllCards = DEFAULT_CARD_ORDER.every((card) => order.includes(card));
        if (hasAllCards) {
          return order;
        }
      } catch (e) {
        console.error('Failed to parse card order:', e);
      }
    }
    return DEFAULT_CARD_ORDER;
  });

  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.DASHBOARD_CARD_ORDER, JSON.stringify(cardOrder));
  }, [cardOrder]);

  const timeRanges = [
    { label: 'Last 15 minutes', value: '15m' },
    { label: 'Last 30 minutes', value: '30m' },
    { label: 'Last 1 hour', value: '1h' },
    { label: 'Last 6 hours', value: '6h' },
    { label: 'Last 12 hours', value: '12h' },
    { label: 'Last 24 hours', value: '24h' },
    { label: 'Last 7 days', value: '7d' },
    { label: 'Last 30 days', value: '30d' },
    { label: 'Last 90 days', value: '90d' },
    { label: 'All time', value: 'all' }
  ];

  const getTimeRangeLabel = useCallback((value: string) => {
    const range = timeRanges.find((r) => r.value === value);
    return range ? range.label : 'Last 24 hours';
  }, []);

  const [cardVisibility, setCardVisibility] = useState<CardVisibility>(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY);
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
    localStorage.setItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY, JSON.stringify(cardVisibility));
  }, [cardVisibility]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setDropdownOpen(false);
        setSearchQuery('');
      }
      if (timeFilterRef.current && !timeFilterRef.current.contains(event.target as Node)) {
        setTimeFilterOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const generateDashboardStats = useCallback(
    (downloads: any[], services: any[]): DashboardStats => {
      const totalHits = services.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
      const totalMisses = services.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
      const total = totalHits + totalMisses;

      return {
        totalBandwidthSaved: totalHits,
        totalAddedToCache: totalMisses,
        totalServed: total,
        cacheHitRatio: total > 0 ? totalHits / total : 0,
        activeDownloads: activeDownloads.length,
        uniqueClients: [...new Set(downloads.map((d) => d.clientIp))].length,
        topService: services[0]?.service || 'steam',
        period: {
          duration: selectedTimeRange,
          bandwidthSaved: totalHits,
          addedToCache: totalMisses,
          totalServed: total,
          hitRatio: total > 0 ? totalHits / total : 0,
          downloads: downloads.length
        }
      };
    },
    [selectedTimeRange, activeDownloads]
  );

  // Generate dashboard stats whenever data changes
  useEffect(() => {
    if (latestDownloads && serviceStats) {
      const stats = generateDashboardStats(latestDownloads, serviceStats);
      setDashboardStats(stats);
    }
  }, [latestDownloads, serviceStats, generateDashboardStats]);

  const toggleCardVisibility = useCallback((cardKey: string) => {
    setCardVisibility((prev: CardVisibility) => ({
      ...prev,
      [cardKey]: !prev[cardKey]
    }));
  }, []);

  const handleDragStart = useCallback((e: React.DragEvent, cardKey: string) => {
    setDraggedCard(cardKey);
    e.dataTransfer.effectAllowed = 'move';
  }, []);

  const handleDragEnd = useCallback(() => {
    setDraggedCard(null);
    setDragOverCard(null);
    dragCounter.current = 0;
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
  }, []);

  const handleDragEnter = useCallback(
    (e: React.DragEvent, cardKey: string) => {
      e.preventDefault();
      dragCounter.current++;
      if (cardKey && cardKey !== draggedCard) {
        setDragOverCard(cardKey);
      }
    },
    [draggedCard]
  );

  const handleDragLeave = useCallback(() => {
    dragCounter.current--;
    if (dragCounter.current === 0) {
      setDragOverCard(null);
    }
  }, []);

  const handleDrop = useCallback(
    (e: React.DragEvent, targetCardKey: string) => {
      e.preventDefault();
      e.stopPropagation();

      if (draggedCard && targetCardKey && draggedCard !== targetCardKey) {
        setCardOrder((prevOrder: string[]) => {
          const newOrder = [...prevOrder];
          const draggedIndex = newOrder.indexOf(draggedCard);
          const targetIndex = newOrder.indexOf(targetCardKey);
          newOrder.splice(draggedIndex, 1);
          newOrder.splice(targetIndex, 0, draggedCard);
          return newOrder;
        });
      }

      setDragOverCard(null);
      dragCounter.current = 0;
    },
    [draggedCard]
  );

  const resetCardOrder = useCallback(() => {
    setCardOrder(DEFAULT_CARD_ORDER);
  }, []);

  const stats = useMemo(() => {
    const activeClients = [...new Set(activeDownloads.map((d) => d.clientIp))].length;
    const totalActiveDownloads = activeDownloads.length;
    const totalDownloads = serviceStats.reduce(
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
      uniqueClients: dashboardStats?.uniqueClients || clientStats.length
    };
  }, [activeDownloads, serviceStats, dashboardStats, clientStats]);

  const allStatCards = useMemo<AllStatCards>(
    () => ({
      totalCache: {
        key: 'totalCache',
        title: 'Total Cache',
        value: cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '0 B',
        subtitle: 'Drive capacity',
        icon: Database,
        color: 'blue' as const,
        visible: cardVisibility.totalCache
      },
      usedSpace: {
        key: 'usedSpace',
        title: 'Used Space',
        value: cacheInfo ? formatBytes(cacheInfo.usedCacheSize) : '0 B',
        subtitle: cacheInfo ? formatPercent(cacheInfo.usagePercent) : '0%',
        icon: HardDrive,
        color: 'green' as const,
        visible: cardVisibility.usedSpace
      },
      bandwidthSaved: {
        key: 'bandwidthSaved',
        title: 'Bandwidth Saved',
        value: formatBytes(stats.bandwidthSaved),
        subtitle:
          selectedTimeRange === 'all'
            ? 'All-time saved'
            : getTimeRangeLabel(selectedTimeRange).toLowerCase(),
        icon: TrendingUp,
        color: 'emerald' as const,
        visible: cardVisibility.bandwidthSaved,
        tooltip: StatTooltips.bandwidthSaved
      },
      addedToCache: {
        key: 'addedToCache',
        title: 'Added to Cache',
        value: formatBytes(stats.addedToCache),
        subtitle:
          selectedTimeRange === 'all'
            ? 'All-time cached'
            : getTimeRangeLabel(selectedTimeRange).toLowerCase(),
        icon: Zap,
        color: 'purple' as const,
        visible: cardVisibility.addedToCache,
        tooltip: StatTooltips.addedToCache
      },
      totalServed: {
        key: 'totalServed',
        title: 'Total Served',
        value: formatBytes(stats.totalServed),
        subtitle:
          selectedTimeRange === 'all'
            ? 'All-time served'
            : getTimeRangeLabel(selectedTimeRange).toLowerCase(),
        icon: Server,
        color: 'indigo' as const,
        visible: cardVisibility.totalServed,
        tooltip: StatTooltips.totalServed
      },
      activeDownloads: {
        key: 'activeDownloads',
        title: 'Active Downloads',
        value: stats.totalActiveDownloads,
        subtitle: `${dashboardStats?.period?.downloads || latestDownloads.length} in period`,
        icon: Download,
        color: 'orange' as const,
        visible: cardVisibility.activeDownloads
      },
      activeClients: {
        key: 'activeClients',
        title: 'Active Clients',
        value: stats.uniqueClients,
        subtitle: `${stats.totalDownloads} downloads`,
        icon: Users,
        color: 'yellow' as const,
        visible: cardVisibility.activeClients
      },
      cacheHitRatio: {
        key: 'cacheHitRatio',
        title: 'Cache Hit Ratio',
        value: formatPercent(stats.cacheHitRatio * 100),
        subtitle:
          selectedTimeRange === 'all'
            ? 'Overall'
            : getTimeRangeLabel(selectedTimeRange).toLowerCase(),
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
      selectedTimeRange,
      getTimeRangeLabel,
      dashboardStats,
      latestDownloads
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

  const getIconGradient = (color: string) => {
    const gradients: Record<string, string> = {
      blue: 'from-blue-500 to-blue-600',
      green: 'from-green-500 to-green-600',
      emerald: 'from-emerald-500 to-emerald-600',
      purple: 'from-purple-500 to-purple-600',
      indigo: 'from-indigo-500 to-indigo-600',
      orange: 'from-orange-500 to-orange-600',
      yellow: 'from-yellow-500 to-yellow-600',
      cyan: 'from-cyan-500 to-cyan-600'
    };
    return gradients[color] || 'from-gray-500 to-gray-600';
  };

  return (
    <div className="space-y-6">
      {/* Time Range Filter */}
      <div className="flex justify-between items-center">
        <h2 className="text-2xl font-bold text-themed-primary tracking-tight">Dashboard</h2>
        <div className="flex items-center gap-2">
          <button
            onClick={resetCardOrder}
            className="p-2 transition-colors rounded-lg"
            style={{
              color: 'var(--theme-text-muted)',
              backgroundColor: 'transparent'
            }}
            onMouseEnter={(e) => (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')}
            onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
            title="Reset card layout to default"
          >
            <RotateCcw className="w-4 h-4" />
          </button>

          <div className="relative" ref={timeFilterRef}>
            <button
              onClick={() => setTimeFilterOpen(!timeFilterOpen)}
              className="flex items-center gap-2 px-4 py-2 rounded-lg border transition-colors"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                borderColor: 'var(--theme-border-primary)'
              }}
              onMouseEnter={(e) =>
                (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
              }
              onMouseLeave={(e) =>
                (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
              }
            >
              <Clock className="w-4 h-4" style={{ color: 'var(--theme-text-muted)' }} />
              <span className="text-sm text-themed-secondary">
                {getTimeRangeLabel(selectedTimeRange)}
              </span>
              <ChevronDown
                className={`w-4 h-4 transition-transform ${timeFilterOpen ? 'rotate-180' : ''}`}
                style={{ color: 'var(--theme-text-muted)' }}
              />
            </button>

            {timeFilterOpen && (
              <div
                className="absolute right-0 mt-2 w-56 rounded-lg border shadow-xl z-50"
                style={{
                  backgroundColor: 'var(--theme-bg-secondary)',
                  borderColor: 'var(--theme-border-primary)'
                }}
              >
                <div className="p-2">
                  <div className="text-xs font-semibold px-2 py-1.5 text-themed-muted">
                    Time Range
                  </div>
                  {timeRanges.map((range) => (
                    <button
                      key={range.value}
                      onClick={() => {
                        setSelectedTimeRange(range.value);
                        setTimeFilterOpen(false);
                      }}
                      className={`w-full text-left px-3 py-2 rounded text-sm transition-colors ${
                        selectedTimeRange === range.value
                          ? 'text-themed-accent'
                          : 'text-themed-secondary'
                      }`}
                      style={{
                        backgroundColor:
                          selectedTimeRange === range.value
                            ? 'var(--theme-bg-hover)'
                            : 'transparent'
                      }}
                      onMouseEnter={(e) => {
                        if (selectedTimeRange !== range.value) {
                          e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                        }
                      }}
                      onMouseLeave={(e) => {
                        if (selectedTimeRange !== range.value) {
                          e.currentTarget.style.backgroundColor = 'transparent';
                        }
                      }}
                    >
                      <div className="flex items-center justify-between">
                        <span>{range.label}</span>
                        {selectedTimeRange === range.value && (
                          <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
                            <path
                              fillRule="evenodd"
                              d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                              clipRule="evenodd"
                            />
                          </svg>
                        )}
                      </div>
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Hidden Cards Dropdown */}
      {hiddenCardsCount > 0 && (
        <div className="relative" ref={dropdownRef}>
          <div
            className="rounded-lg px-4 py-2 border flex items-center justify-between"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderColor: 'var(--theme-border-primary)'
            }}
          >
            <span className="text-sm text-themed-muted">
              {hiddenCardsCount} stat card{hiddenCardsCount !== 1 ? 's' : ''} hidden
            </span>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setDropdownOpen(!dropdownOpen)}
                className="flex items-center gap-1 text-xs text-themed-accent transition-colors px-2 py-1 rounded"
                onMouseEnter={(e) =>
                  (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                }
                onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
              >
                Add cards
                <ChevronDown
                  className={`w-3 h-3 transition-transform ${dropdownOpen ? 'rotate-180' : ''}`}
                />
              </button>
              <button
                onClick={() => setCardVisibility(DEFAULT_CARD_VISIBILITY)}
                className="text-xs text-themed-accent transition-colors px-2 py-1 rounded"
                onMouseEnter={(e) =>
                  (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                }
                onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
              >
                Show all
              </button>
            </div>
          </div>

          {dropdownOpen && (
            <div
              className="absolute right-0 mt-2 w-80 rounded-lg border shadow-xl z-50"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                borderColor: 'var(--theme-border-primary)'
              }}
            >
              <div className="p-3 border-b" style={{ borderColor: 'var(--theme-border-primary)' }}>
                <div className="relative">
                  <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-themed-muted" />
                  <input
                    type="text"
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    placeholder="Search hidden cards..."
                    className="w-full pl-10 pr-3 py-2 rounded-lg focus:outline-none text-sm themed-input"
                    style={{
                      backgroundColor: 'var(--theme-bg-hover)',
                      color: 'var(--theme-text-primary)',
                      border: '2px solid var(--theme-primary)'
                    }}
                    autoFocus
                  />
                </div>
              </div>

              <div className="max-h-96 overflow-y-auto p-2">
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
                        className="w-full p-3 rounded-lg transition-colors flex items-center gap-3 group"
                        onMouseEnter={(e) =>
                          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                        }
                        onMouseLeave={(e) =>
                          (e.currentTarget.style.backgroundColor = 'transparent')
                        }
                      >
                        <div
                          className={`p-2 rounded-lg bg-gradient-to-br ${getIconGradient(card.color)} group-hover:scale-110 transition-transform`}
                        >
                          <Icon className="w-5 h-5 text-white" />
                        </div>
                        <div className="flex-1 text-left">
                          <div className="text-sm text-themed-secondary font-medium">
                            {card.title}
                          </div>
                          <div className="text-xs text-themed-muted">{card.subtitle}</div>
                        </div>
                        <Eye className="w-4 h-4 text-themed-muted group-hover:text-themed-accent transition-colors" />
                      </button>
                    );
                  })
                ) : (
                  <div className="px-3 py-6 text-center text-themed-muted text-sm">
                    No hidden cards match "{searchQuery}"
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}

      {/* Loading overlay */}
      {loading && (
        <div className="text-center py-4">
          <div className="inline-flex items-center gap-2 text-themed-muted">
            <Loader className="animate-spin h-5 w-5" />
            <span>Loading {getTimeRangeLabel(selectedTimeRange).toLowerCase()} data...</span>
          </div>
        </div>
      )}

      {/* Stats Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        {visibleCards.map((card: StatCardData) => (
          <div
            key={card.key}
            className="relative group"
            style={{
              boxShadow: dragOverCard === card.key ? `0 0 0 2px var(--theme-primary)` : 'none'
            }}
            draggable
            onDragStart={(e) => handleDragStart(e, card.key)}
            onDragEnd={handleDragEnd}
            onDragOver={handleDragOver}
            onDragEnter={(e) => handleDragEnter(e, card.key)}
            onDragLeave={handleDragLeave}
            onDrop={(e) => handleDrop(e, card.key)}
          >
            <div className="absolute top-2 left-2 opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none">
              <GripVertical className="w-4 h-4 text-themed-muted" />
            </div>

            <StatCard
              title={card.title}
              value={card.value}
              subtitle={card.subtitle}
              icon={card.icon}
              color={card.color}
              tooltip={card.tooltip}
            />

            <button
              onClick={() => toggleCardVisibility(card.key)}
              className="absolute top-2 right-2 p-1.5 rounded-lg opacity-0 group-hover:opacity-100 transition-opacity"
              style={{
                backgroundColor: 'var(--theme-bg-hover)'
              }}
              title="Hide this card"
            >
              <EyeOff className="w-3.5 h-3.5 text-themed-muted" />
            </button>
          </div>
        ))}
      </div>

      {/* Charts Row - Pass the actual data arrays */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <EnhancedServiceChart serviceStats={serviceStats || []} timeRange={selectedTimeRange} />
        <RecentDownloadsPanel downloads={latestDownloads || []} timeRange={selectedTimeRange} />
      </div>

      {/* Top Clients - Pass the actual data arrays */}
      <TopClientsTable
        clientStats={clientStats || []}
        downloads={latestDownloads || []}
        timeRange={selectedTimeRange}
      />
    </div>
  );
};

export default Dashboard;
