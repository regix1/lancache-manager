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
  Loader,
  LayoutGrid,
  X
} from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { useTimeFilter } from '../../contexts/TimeFilterContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { STORAGE_KEYS } from '../../utils/constants';
import { type StatCardData } from '../../types';
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
  totalCache: 'Total storage capacity of your LANCache system',
  usedSpace: 'Amount of storage currently occupied by cached content',
  bandwidthSaved:
    'Amount of internet bandwidth saved by serving files from local cache instead of downloading them again',
  addedToCache: 'New content downloaded and stored in cache for future use',
  totalServed: 'Total amount of data delivered to clients (cache hits + new downloads)',
  activeDownloads: 'Number of downloads currently in progress',
  activeClients: 'Number of unique client devices that have accessed the cache',
  cacheHitRatio:
    'Percentage of requests served from cache vs downloaded from internet. Higher is better!'
};

const Dashboard: React.FC = () => {
  const { cacheInfo, activeDownloads, latestDownloads, clientStats, serviceStats, dashboardStats } = useData();
  const { timeRange } = useTimeFilter();
  const [loading] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const dropdownRef = useRef<HTMLDivElement>(null);

  const [draggedCard, setDraggedCard] = useState<string | null>(null);
  const [dragOverCard, setDragOverCard] = useState<string | null>(null);
  const [isDragMode, setIsDragMode] = useState(false);
  const [holdTimeout, setHoldTimeout] = useState<NodeJS.Timeout | null>(null);
  const [showDragHint, setShowDragHint] = useState(() => {
    return localStorage.getItem('dashboard-hide-drag-hint') !== 'true';
  });
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


  const getTimeRangeLabel = useCallback(() => {
    switch (timeRange) {
      case '1h': return 'Last hour';
      case '6h': return 'Last 6 hours';
      case '12h': return 'Last 12 hours';
      case '24h': return 'Last 24 hours';
      case '7d': return 'Last 7 days';
      case '30d': return 'Last 30 days';
      case 'all': return 'All time';
      case 'custom': return 'Custom range';
      default: return 'Last 24 hours';
    }
  }, [timeRange]);

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

  const handleDragStart = useCallback((e: React.DragEvent, cardKey: string) => {
    setDraggedCard(cardKey);
    e.dataTransfer.effectAllowed = 'move';
  }, []);

  const handleDragEnd = useCallback(() => {
    setDraggedCard(null);
    setDragOverCard(null);
    setIsDragMode(false);
    dragCounter.current = 0;
  }, []);

  // Touch-friendly select and swap handlers
  const handleTouchStart = useCallback((cardKey: string) => {
    const timeout = setTimeout(() => {
      // If we already have a selected card, swap them
      if (draggedCard && draggedCard !== cardKey) {
        // Perform the swap
        setCardOrder((prevOrder: string[]) => {
          const newOrder = [...prevOrder];
          const draggedIndex = newOrder.indexOf(draggedCard);
          const targetIndex = newOrder.indexOf(cardKey);
          newOrder.splice(draggedIndex, 1);
          newOrder.splice(targetIndex, 0, draggedCard);
          return newOrder;
        });

        // Add haptic feedback for successful swap
        if (navigator.vibrate) {
          navigator.vibrate([50, 50, 50]);
        }

        // Clear selection
        setDraggedCard(null);
        setIsDragMode(false);
      } else {
        // Select this card
        setIsDragMode(true);
        setDraggedCard(cardKey);
        // Add haptic feedback if available
        if (navigator.vibrate) {
          navigator.vibrate(50);
        }
      }
    }, 500); // 500ms hold to activate selection mode
    setHoldTimeout(timeout);
  }, [draggedCard]);

  const handleTouchEnd = useCallback(() => {
    if (holdTimeout) {
      clearTimeout(holdTimeout);
      setHoldTimeout(null);
    }
  }, [holdTimeout]);

  const handleCardTap = useCallback((cardKey: string) => {
    if (isDragMode && draggedCard) {
      if (cardKey !== draggedCard) {
        // Swap the cards
        setCardOrder((prevOrder: string[]) => {
          const newOrder = [...prevOrder];
          const draggedIndex = newOrder.indexOf(draggedCard);
          const targetIndex = newOrder.indexOf(cardKey);
          newOrder.splice(draggedIndex, 1);
          newOrder.splice(targetIndex, 0, draggedCard);
          return newOrder;
        });

        // Add haptic feedback for successful swap
        if (navigator.vibrate) {
          navigator.vibrate([50, 50, 50]);
        }
      }

      // Clear selection
      setDraggedCard(null);
      setIsDragMode(false);
      setDragOverCard(null);
    }
  }, [isDragMode, draggedCard]);

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

  const hideDragHint = useCallback(() => {
    setShowDragHint(false);
    localStorage.setItem('dashboard-hide-drag-hint', 'true');
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
        subtitle: `${dashboardStats?.period?.downloads || latestDownloads.length} in period`,
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
    <div
      className="space-y-6"
      onClick={(e) => {
        // Cancel drag mode if clicking outside of cards
        if (isDragMode && !(e.target as Element).closest('[data-card-key]')) {
          handleTouchEnd();
        }
      }}
    >
      {/* Time Range Filter */}
      <div className="flex flex-col sm:flex-row sm:justify-between sm:items-center gap-4">
        <h2 className="text-2xl font-bold text-themed-primary tracking-tight hidden md:block">Dashboard</h2>
        <div className="flex flex-col sm:flex-row items-start sm:items-center gap-2 w-full sm:w-auto">
          <button
            onClick={resetCardOrder}
            className="flex items-center gap-2 px-3 py-2 text-sm transition-colors rounded-lg border order-2 sm:order-1 w-full sm:w-auto justify-center sm:justify-start"
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
            title="Reset card layout to default order"
          >
            <LayoutGrid className="w-4 h-4" />
            <span className="hidden sm:inline">Reset Layout</span>
            <span className="sm:hidden">Reset Card Layout</span>
          </button>

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
                className="flex items-center gap-1 text-xs text-themed-accent transition-colors px-2 py-1 rounded-lg"
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
                className="text-xs text-themed-accent transition-colors px-2 py-1 rounded-lg"
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
            <span>Loading {getTimeRangeLabel().toLowerCase()} data...</span>
          </div>
        </div>
      )}

      {/* Touch instruction for mobile */}
      {showDragHint && (
        <div className="md:hidden">
          {!isDragMode ? (
            <div className="flex items-center justify-between py-3 px-4 rounded-lg bg-themed-secondary text-themed-muted text-sm">
              <div className="flex items-center gap-2">
                <span>💡</span>
                <span>Hold any card for 0.5 seconds to select it, then tap another card to swap positions</span>
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
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        {visibleCards.map((card: StatCardData) => (
          <div
            key={card.key}
            data-card-key={card.key}
            className={`relative group transition-all duration-200 ${
              isDragMode && draggedCard === card.key ? 'scale-105 shadow-lg' : ''
            } ${
              isDragMode && dragOverCard === card.key ? 'transform translate-y-1' : ''
            }`}
            style={{
              boxShadow: dragOverCard === card.key ? `0 0 0 2px var(--theme-primary)` : 'none',
              cursor: draggedCard === card.key ? 'grabbing' : 'default',
              opacity: isDragMode && draggedCard === card.key ? 0.8 : 1
            }}
            draggable={!isDragMode}
            onDragStart={(e) => handleDragStart(e, card.key)}
            onDragEnd={handleDragEnd}
            onDragOver={handleDragOver}
            onDragEnter={(e) => handleDragEnter(e, card.key)}
            onDragLeave={handleDragLeave}
            onDrop={(e) => handleDrop(e, card.key)}
            onTouchStart={() => handleTouchStart(card.key)}
            onTouchEnd={handleTouchEnd}
            onClick={() => handleCardTap(card.key)}
          >
            {/* Desktop drag handle - smaller, hover-triggered */}
            {(
              <div
                className="absolute top-2 left-2 opacity-0 group-hover:opacity-100 transition-all p-1 rounded hidden md:block"
                style={{
                  cursor: 'grab',
                  zIndex: 5
                }}
                title="Drag to reorder"
                onMouseEnter={(e) => {
                  e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.backgroundColor = 'transparent';
                }}
              >
                <GripVertical
                  className="w-4 h-4 transition-colors"
                  style={{ color: 'var(--theme-drag-handle)' }}
                  onMouseEnter={(e) => {
                    e.currentTarget.style.color = 'var(--theme-drag-handle-hover)';
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.color = 'var(--theme-drag-handle)';
                  }}
                />
              </div>
            )}

            {/* Mobile drag handle - small, transparent, always visible in top-left */}
            {(
              <div
                className="absolute top-2 left-2 transition-all p-1 rounded md:hidden opacity-60"
                style={{
                  cursor: 'grab',
                  zIndex: 5,
                  backgroundColor: 'transparent'
                }}
                title="Hold to reorder"
              >
                <GripVertical
                  className="w-4 h-4 transition-colors"
                  style={{ color: 'var(--theme-drag-handle)' }}
                />
              </div>
            )}

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
        <EnhancedServiceChart serviceStats={serviceStats || []} timeRange={timeRange} />
        <RecentDownloadsPanel downloads={latestDownloads || []} timeRange={timeRange} />
      </div>

      {/* Top Clients - Pass the actual data arrays */}
      <TopClientsTable
        clientStats={clientStats || []}
        downloads={latestDownloads || []}
        timeRange={timeRange}
      />
    </div>
  );
};

export default Dashboard;
