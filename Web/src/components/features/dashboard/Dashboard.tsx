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
  X,
  Move,
  Check
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useStats, useDownloads } from '@contexts/DashboardDataContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import { useSpeed } from '@contexts/SpeedContext';
import { useDraggableCards } from '@hooks/useDraggableCards';
import { formatBytes, formatPercent } from '@utils/formatters';
import { STORAGE_KEYS } from '@utils/constants';
import { type StatCardData, type SparklineDataResponse, type CacheSnapshotResponse } from '../../../types';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import StatCard from '@components/common/StatCard';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpSection } from '@components/ui/HelpPopover';
import ServiceAnalyticsChart from './ServiceAnalyticsChart';
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

const getStatTooltips = (t: (key: string) => string): Record<string, React.ReactNode> => ({
  totalCache: (
    <HelpSection title={t('dashboard.statCards.totalCache.term')}>
      {t('dashboard.statCards.totalCache.description')}
    </HelpSection>
  ),
  usedSpace: (
    <HelpSection title={t('dashboard.statCards.usedSpace.term')}>
      {t('dashboard.statCards.usedSpace.description')}
    </HelpSection>
  ),
  bandwidthSaved: (
    <HelpSection title={t('dashboard.statCards.bandwidthSaved.term')}>
      {t('dashboard.statCards.bandwidthSaved.description')}
    </HelpSection>
  ),
  addedToCache: (
    <HelpSection title={t('dashboard.statCards.addedToCache.term')}>
      {t('dashboard.statCards.addedToCache.description')}
    </HelpSection>
  ),
  totalServed: (
    <HelpSection title={t('dashboard.statCards.totalServed.term')}>
      {t('dashboard.statCards.totalServed.description')}
    </HelpSection>
  ),
  activeDownloads: (
    <HelpSection title={t('dashboard.statCards.activeDownloads.term')}>
      {t('dashboard.statCards.activeDownloads.description')}
    </HelpSection>
  ),
  activeClients: (
    <HelpSection title={t('dashboard.statCards.activeClients.term')}>
      {t('dashboard.statCards.activeClients.description')}
    </HelpSection>
  ),
  cacheHitRatio: (
    <HelpSection title={t('dashboard.statCards.cacheHitRatio.term')}>
      {t('dashboard.statCards.cacheHitRatio.description')}
    </HelpSection>
  )
});

const Dashboard: React.FC = () => {
  const { t } = useTranslation();
  const { cacheInfo, clientStats, serviceStats, dashboardStats, loading } = useStats();
  const { latestDownloads } = useDownloads();
  const { timeRange, getTimeRangeParams, customStartDate, customEndDate, selectedEventIds } = useTimeFilter();
  const { selectedEvent } = useEvents();
  const { speedSnapshot, activeDownloadCount } = useSpeed();
  const statTooltips = useMemo(() => getStatTooltips(t), [t]);

  // Track if initial card animations have completed - prevents re-animation on reorder
  const initialAnimationCompleteRef = useRef(false);
  const [initialAnimationComplete, setInitialAnimationComplete] = useState(false);

  // Track previous stats to prevent values from flashing to 0 during fetches
  const previousStatsRef = useRef({
    bandwidthSaved: 0,
    addedToCache: 0,
    totalServed: 0,
    cacheHitRatio: 0,
    uniqueClients: 0,
    activeClients: 0,
    totalActiveDownloads: 0
  });

  // Determine if we're viewing historical/filtered data (not live)
  // Any non-live mode should disable real-time only stats
  const isHistoricalView = timeRange !== 'live' || selectedEventIds.length > 0;

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
  const prevSparklineDataRef = useRef<SparklineDataResponse | null>(null);

  // Historical cache snapshot data
  const [cacheSnapshot, setCacheSnapshot] = useState<CacheSnapshotResponse | null>(null);
  const prevCacheSnapshotRef = useRef<CacheSnapshotResponse | null>(null);

  // Fetch sparkline data when time range or event filter changes
  useEffect(() => {
    const controller = new AbortController();

    // Store current data as previous (keep showing until new data arrives)
    if (sparklineData) {
      prevSparklineDataRef.current = sparklineData;
    }

    const fetchSparklines = async () => {
      try {
        const { startTime, endTime } = getTimeRangeParams();
        const eventId = selectedEventIds.length > 0 ? selectedEventIds[0] : undefined;
        const data = await ApiService.getSparklineData(controller.signal, startTime, endTime, eventId);
        setSparklineData(data);
        prevSparklineDataRef.current = data;
      } catch (err) {
        // Ignore abort errors
        if (!controller.signal.aborted) {
          console.error('Failed to fetch sparkline data:', err);
        }
      }
    };

    fetchSparklines();

    return () => controller.abort();
  }, [timeRange, getTimeRangeParams, selectedEventIds]);

  // Fetch historical cache snapshot when in historical view
  useEffect(() => {
    const controller = new AbortController();

    // Store current data as previous
    if (cacheSnapshot) {
      prevCacheSnapshotRef.current = cacheSnapshot;
    }

    // Only fetch when in historical view (not live mode)
    if (!isHistoricalView) {
      return;
    }

    const fetchCacheSnapshot = async () => {
      try {
        const { startTime, endTime } = getTimeRangeParams();
        if (startTime && endTime) {
          const data = await ApiService.getCacheSnapshot(controller.signal, startTime, endTime);
          setCacheSnapshot(data);
          prevCacheSnapshotRef.current = data;
        }
      } catch (err) {
        // Ignore abort errors
        if (!controller.signal.aborted) {
          console.error('Failed to fetch cache snapshot:', err);
        }
      }
    };

    fetchCacheSnapshot();

    return () => controller.abort();
  }, [timeRange, getTimeRangeParams, isHistoricalView]);

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
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const dropdownRef = useRef<HTMLDivElement>(null);

  // Use drag-and-drop hook for card reordering
  const {
    cardOrder,
    draggedCard,
    dragOverCard,
    isDragMode,
    isEditMode,
    showDragHint,
    dragHandlers,
    resetCardOrder,
    hideDragHint,
    toggleEditMode,
    exitEditMode
  } = useDraggableCards({
    defaultOrder: DEFAULT_CARD_ORDER,
    storageKey: STORAGE_KEYS.DASHBOARD_CARD_ORDER,
    dragHintStorageKey: 'dashboard-hide-drag-hint'
  });

  const getTimeRangeLabel = useCallback(() => {
    switch (timeRange) {
      case '1h':
        return t('dashboard.timeRanges.1h');
      case '6h':
        return t('dashboard.timeRanges.6h');
      case '12h':
        return t('dashboard.timeRanges.12h');
      case '24h':
        return t('dashboard.timeRanges.24h');
      case '7d':
        return t('dashboard.timeRanges.7d');
      case '30d':
        return t('dashboard.timeRanges.30d');
      case 'live':
        return t('dashboard.timeRanges.live');
      case 'custom':
        return t('dashboard.timeRanges.custom');
      default:
        return t('dashboard.timeRanges.24h');
    }
  }, [timeRange, selectedEvent, t]);

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
    // Use speed data from SpeedContext for real-time accurate active data (from Rust speed tracker)
    const totalDownloads = filteredServiceStats.reduce(
      (sum: number, service: { totalDownloads?: number }) => sum + (service.totalDownloads || 0),
      0
    );

    // Use dashboardStats when available, otherwise keep previous values to prevent flashing to 0
    const hasPeriodData = dashboardStats?.period !== undefined && dashboardStats?.period !== null;

    // Active stats come from SpeedContext which has its own grace period
    // to prevent tab-switch flicker (zero-transition delay in applySpeedSnapshot)
    const activeClients = speedSnapshot?.clientSpeeds?.length ?? 0;
    const totalActiveDownloads = activeDownloadCount;

    const newStats = {
      activeClients,
      totalActiveDownloads,
      totalDownloads,
      bandwidthSaved: hasPeriodData
        ? (dashboardStats.period.bandwidthSaved ?? previousStatsRef.current.bandwidthSaved)
        : previousStatsRef.current.bandwidthSaved,
      addedToCache: hasPeriodData
        ? (dashboardStats.period.addedToCache ?? previousStatsRef.current.addedToCache)
        : previousStatsRef.current.addedToCache,
      totalServed: hasPeriodData
        ? (dashboardStats.period.totalServed ?? previousStatsRef.current.totalServed)
        : previousStatsRef.current.totalServed,
      cacheHitRatio: hasPeriodData
        ? (dashboardStats.period.hitRatio ?? previousStatsRef.current.cacheHitRatio)
        : previousStatsRef.current.cacheHitRatio,
      uniqueClients: dashboardStats?.uniqueClients ?? filteredClientStats.length ?? previousStatsRef.current.uniqueClients
    };

    // Update ref with valid data for next render
    if (hasPeriodData) {
      previousStatsRef.current = {
        bandwidthSaved: newStats.bandwidthSaved,
        addedToCache: newStats.addedToCache,
        totalServed: newStats.totalServed,
        cacheHitRatio: newStats.cacheHitRatio,
        uniqueClients: newStats.uniqueClients,
        activeClients: newStats.activeClients,
        totalActiveDownloads: newStats.totalActiveDownloads
      };
    }

    return newStats;
  }, [filteredServiceStats, dashboardStats, filteredClientStats, speedSnapshot, activeDownloadCount]);

  const allStatCards = useMemo<AllStatCards>(
    () => ({
      totalCache: {
        key: 'totalCache',
        title: t('dashboard.cards.totalCache'),
        value: cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '0 B',
        subtitle: cacheInfo?.configuredCacheSize && cacheInfo.configuredCacheSize > 0
          ? t('dashboard.cards.driveCapacityValue', { size: formatBytes(cacheInfo.driveCapacity) })
          : t('dashboard.cards.driveCapacity'),
        icon: Database,
        color: 'blue' as const,
        visible: cardVisibility.totalCache,
        tooltip: statTooltips.totalCache
      },
      usedSpace: {
        key: 'usedSpace',
        title: isHistoricalView && cacheSnapshot?.hasData ? t('dashboard.cards.usedSpaceEnd') : t('dashboard.cards.usedSpace'),
        value: isHistoricalView
          ? (cacheSnapshot?.hasData ? formatBytes(cacheSnapshot.endUsedSize) : t('common.noDataAvailable'))
          : (cacheInfo ? formatBytes(cacheInfo.usedCacheSize) : '0 B'),
        subtitle: isHistoricalView
          ? (cacheSnapshot?.hasData
            ? t('dashboard.cards.startedAt', { size: formatBytes(cacheSnapshot.startUsedSize) }) + ' • ' + t('dashboard.cards.snapshots', { count: cacheSnapshot.snapshotCount })
            : t('dashboard.cards.noSnapshotsYet'))
          : (cacheInfo ? formatPercent(cacheInfo.usagePercent) : '0%'),
        icon: HardDrive,
        color: 'green' as const,
        visible: cardVisibility.usedSpace,
        tooltip: statTooltips.usedSpace
      },
      bandwidthSaved: {
        key: 'bandwidthSaved',
        title: t('dashboard.cards.bandwidthSaved'),
        value: formatBytes(stats.bandwidthSaved),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: TrendingUp,
        color: 'emerald' as const,
        visible: cardVisibility.bandwidthSaved,
        tooltip: statTooltips.bandwidthSaved
      },
      addedToCache: {
        key: 'addedToCache',
        title: t('dashboard.cards.addedToCache'),
        value: formatBytes(stats.addedToCache),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Zap,
        color: 'purple' as const,
        visible: cardVisibility.addedToCache,
        tooltip: statTooltips.addedToCache
      },
      totalServed: {
        key: 'totalServed',
        title: t('dashboard.cards.totalServed'),
        value: formatBytes(stats.totalServed),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Server,
        color: 'indigo' as const,
        visible: cardVisibility.totalServed,
        tooltip: statTooltips.totalServed
      },
      activeDownloads: {
        key: 'activeDownloads',
        title: t('dashboard.cards.activeDownloads'),
        value: isHistoricalView ? t('dashboard.cards.disabled') : stats.totalActiveDownloads,
        subtitle: isHistoricalView
          ? t('dashboard.cards.liveDataOnly')
          : `${dashboardStats?.period?.downloads || filteredLatestDownloads.length} ${t('dashboard.cards.inPeriod')}`,
        icon: Download,
        color: 'orange' as const,
        visible: cardVisibility.activeDownloads,
        tooltip: statTooltips.activeDownloads
      },
      activeClients: {
        key: 'activeClients',
        title: t('dashboard.cards.activeClients'),
        value: isHistoricalView ? t('dashboard.cards.disabled') : stats.activeClients,
        subtitle: isHistoricalView
          ? t('dashboard.cards.liveDataOnly')
          : `${stats.uniqueClients} ${t('dashboard.cards.uniqueInPeriod')}`,
        icon: Users,
        color: 'yellow' as const,
        visible: cardVisibility.activeClients,
        tooltip: statTooltips.activeClients
      },
      cacheHitRatio: {
        key: 'cacheHitRatio',
        title: t('dashboard.cards.cacheHitRatio'),
        value: formatPercent(Math.round(stats.cacheHitRatio * 1000) / 10),
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Activity,
        color: 'cyan' as const,
        visible: cardVisibility.cacheHitRatio,
        tooltip: statTooltips.cacheHitRatio
      }
    }),
    [
      t,
      cacheInfo,
      cacheSnapshot,
      cardVisibility,
      stats,
      timeRange,
      getTimeRangeLabel,
      dashboardStats,
      filteredLatestDownloads,
      isHistoricalView
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
      className={`space-y-4 ${isEditMode ? 'edit-mode-active' : ''}`}
    >
      {/* Dashboard Header */}
      <div className="flex flex-col sm:flex-row sm:justify-between sm:items-center gap-4">
        <h2 className="text-2xl font-bold text-themed-primary tracking-tight hidden md:block">
          {t('dashboard.title')}
        </h2>
        <div className="flex flex-row items-center gap-2">
          {/* Mobile Edit Mode Toggle */}
          <div className="md:hidden">
            {isEditMode ? (
              <button
                onClick={exitEditMode}
                className="edit-mode-done flex items-center gap-2 px-4 py-2 text-sm themed-border-radius"
              >
                <Check className="w-4 h-4" />
                <span>{t('dashboard.done')}</span>
              </button>
            ) : (
              <Tooltip content={t('tooltips.rearrangeCards')} strategy="overlay">
                <button
                  onClick={toggleEditMode}
                  className="edit-mode-toggle flex items-center gap-2 px-3 py-2 text-sm themed-border-radius border text-themed-secondary bg-themed-secondary border-themed-primary"
                >
                  <Move className="w-4 h-4" />
                  <span>{t('dashboard.edit')}</span>
                </button>
              </Tooltip>
            )}
          </div>

          {/* Hidden Cards Button - only shows when cards are hidden */}
          {hiddenCardsCount > 0 && (
            <div className="relative" ref={dropdownRef}>
              <Tooltip content={t('dashboard.hiddenCardsTooltip', { count: hiddenCardsCount })} strategy="overlay">
                <button
                  onClick={() => setDropdownOpen(!dropdownOpen)}
                  className="hover-btn-trigger flex items-center gap-2 px-3 py-2 text-sm themed-border-radius border text-themed-secondary bg-themed-secondary border-themed-primary"
                >
                  <EyeOff className="w-4 h-4" />
                  <span className="hidden sm:inline">{t('dashboard.hiddenCount', { count: hiddenCardsCount })} {t('dashboard.hidden')}</span>
                  <span className="sm:hidden">{hiddenCardsCount}</span>
                  <ChevronDown
                    className={`w-3 h-3 transition-transform duration-200 ${dropdownOpen ? 'rotate-180' : ''}`}
                  />
                </button>
              </Tooltip>

              {/* Hidden Cards Dropdown */}
              {dropdownOpen && (
                <div
                  className="absolute right-0 mt-2 w-72 sm:w-80 themed-border-radius border shadow-xl z-50 themed-card border-themed-primary"
                >
                  {/* Search - only show if more than 3 hidden cards */}
                  {hiddenCardsCount > 3 && (
                    <div className="p-3 border-b border-themed-primary">
                      <div className="relative">
                        <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-themed-muted" />
                        <input
                          type="text"
                          value={searchQuery}
                          onChange={(e) => setSearchQuery(e.target.value)}
                          placeholder={t('dashboard.searchHiddenCards')}
                          className="w-full pl-10 pr-3 py-2 themed-border-radius text-sm bg-themed-tertiary text-themed-primary border border-themed-primary"
                          autoFocus
                        />
                      </div>
                    </div>
                  )}

                  {/* Show All Button */}
                  <div className="p-2 border-b border-themed-primary">
                    <button
                      onClick={() => {
                        setCardVisibility(DEFAULT_CARD_VISIBILITY);
                        setDropdownOpen(false);
                        setSearchQuery('');
                      }}
                      className="hover-btn w-full px-3 py-2 text-sm themed-border-radius text-left flex items-center gap-2 text-themed-accent"
                    >
                      <Eye className="w-4 h-4" />
                      {t('dashboard.showAllCards')}
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
                            className="hover-btn w-full p-2.5 themed-border-radius flex items-center gap-3 group"
                          >
                            <div
                              className="p-1.5 themed-border-radius group-hover:scale-105 transition-transform"
                              style={getIconStyle(card.color)}
                            >
                              <Icon className="w-4 h-4 text-[var(--theme-button-text)]" />
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
                        {t('dashboard.noCardsMatch', { query: searchQuery })}
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>
          )}

          {/* Reset Layout Button */}
          <Tooltip content={t('tooltips.resetCardLayout')} strategy="overlay">
            <button
              onClick={resetCardOrder}
              className="hover-btn-trigger flex items-center gap-2 px-3 py-2 text-sm transition-colors themed-border-radius border text-themed-secondary bg-themed-secondary border-themed-primary hover:bg-themed-hover hover:text-themed-primary"
            >
              <LayoutGrid className="w-4 h-4" />
              <span className="hidden sm:inline">{t('dashboard.resetLayout')}</span>
            </button>
          </Tooltip>
        </div>
      </div>

      {/* Edit mode instruction banner for mobile */}
      {isEditMode && (
        <div className="md:hidden edit-mode-banner">
          <div
            className="flex items-center justify-between py-3 px-4 themed-border-radius text-sm"
            style={{
              backgroundColor: 'color-mix(in srgb, var(--theme-primary) 15%, transparent)',
              borderLeft: '3px solid var(--theme-primary)'
            }}
          >
            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-primary)' }}>
              {draggedCard ? (
                <>
                  <span className="text-base">👆</span>
                  <span>{t('dashboard.editModeInstructions.dragging')}</span>
                </>
              ) : (
                <>
                  <span className="text-base">✨</span>
                  <span>{t('dashboard.editModeInstructions.initial')}</span>
                </>
              )}
            </div>
          </div>
        </div>
      )}

      {/* First-time hint for mobile (only shows once) */}
      {showDragHint && !isEditMode && (
        <div className="md:hidden">
          <div className="flex items-center justify-between py-3 px-4 themed-border-radius bg-themed-secondary text-themed-muted text-sm">
            <div className="flex items-center gap-2">
              <span>💡</span>
              <span dangerouslySetInnerHTML={{ __html: t('dashboard.dragHint') }} />
            </div>
            <button
              onClick={hideDragHint}
              className="ml-2 p-1 themed-border-radius hover:bg-themed-hover transition-colors flex-shrink-0 text-themed-muted"
              title={t('dashboard.hideThisHint')}
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>
      )}

      {/* Stats Grid */}
      <div
        className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 isolate"
      >
          {visibleCards.map((card: StatCardData, visualIndex: number) => {
            // Check if this is a live-only card that should be disabled in historical view
            // Note: usedSpace now supports historical data via snapshots, so it's never disabled
            const isLiveOnlyCard = card.key === 'activeDownloads' || card.key === 'activeClients';
            const isCardDisabled = isLiveOnlyCard && isHistoricalView;

            return (
            <div
              key={card.key}
              data-card-key={card.key}
              className={`relative group h-full edit-mode-card ${
                isDragMode && draggedCard === card.key ? 'scale-105 shadow-lg card-selected' : ''
              } ${isDragMode && dragOverCard === card.key ? 'translate-y-1' : ''}`}
              style={{
                boxShadow: dragOverCard === card.key ? `0 0 0 2px var(--theme-primary)` : 'none',
                borderRadius: dragOverCard === card.key ? 'var(--theme-border-radius-lg)' : undefined,
                cursor: isEditMode ? 'pointer' : (draggedCard === card.key ? 'grabbing' : 'default'),
                opacity: isCardDisabled ? 0.5 : (isDragMode && draggedCard === card.key ? 0.9 : 1)
              }}
              draggable={!isDragMode && !isEditMode}
              onDragStart={(e) => dragHandlers.onDragStart(e, card.key)}
              onDragEnd={dragHandlers.onDragEnd}
              onDragOver={dragHandlers.onDragOver}
              onDragEnter={(e) => dragHandlers.onDragEnter(e, card.key)}
              onDragLeave={dragHandlers.onDragLeave}
              onDrop={(e) => dragHandlers.onDrop(e, card.key)}
              onClick={() => !isCardDisabled && dragHandlers.onCardTap(card.key)}
            >
              {/* Desktop drag handle - smaller, hover-triggered */}
              {
                <Tooltip
                  content={t('tooltips.dragToReorder')}
                  strategy="overlay"
                  className="absolute top-2 left-2 opacity-0 group-hover:opacity-100 transition-all hidden md:block z-[5]"
                >
                  <div
                    className="p-1 themed-border-radius cursor-grab hover:bg-themed-hover"
                  >
                    <GripVertical className="w-4 h-4 text-themed-muted" />
                  </div>
                </Tooltip>
              }

              {/* Mobile edit mode indicator - shows grab handle in edit mode */}
              {isEditMode && (
                <div
                  className="absolute top-2 left-2 transition-all md:hidden z-[5] edit-mode-handle"
                >
                  <div
                    className="p-1.5 themed-border-radius"
                    style={{
                      backgroundColor: draggedCard === card.key
                        ? 'var(--theme-primary)'
                        : 'color-mix(in srgb, var(--theme-bg-primary) 80%, transparent)',
                      backdropFilter: 'blur(4px)'
                    }}
                  >
                    <GripVertical
                      className="w-4 h-4 transition-colors"
                      style={{
                        color: draggedCard === card.key
                          ? 'var(--theme-button-text)'
                          : 'var(--theme-text-secondary)'
                      }}
                    />
                  </div>
                </div>
              )}

              {/* Selected card overlay in edit mode */}
              {isEditMode && draggedCard === card.key && (
                <div
                  className="absolute inset-0 themed-border-radius md:hidden pointer-events-none"
                  style={{
                    boxShadow: 'inset 0 0 0 2px var(--theme-primary)',
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
                glassmorphism={true}
                animateValue={!loading}
                sparklineData={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.data :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.data :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.data :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.data :
                  undefined
                }
                staggerIndex={initialAnimationComplete ? undefined : visualIndex}
              />

              <Tooltip
                content={t('tooltips.hideThisCard')}
                strategy="overlay"
                className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity"
              >
                <button
                  onClick={() => toggleCardVisibility(card.key)}
                  className="p-1.5 themed-border-radius transition-colors hover:bg-themed-hover"
                >
                  <EyeOff className="w-3.5 h-3.5 text-themed-muted" />
                </button>
              </Tooltip>

              {/* Disabled overlay with tooltip for active cards in historical view */}
              {isCardDisabled && (
                <Tooltip content={t('tooltips.liveDataOnly')} strategy="overlay">
                  <div
                    className="absolute inset-0 z-10 cursor-not-allowed themed-border-radius"
                    style={{ background: 'transparent' }}
                  />
                </Tooltip>
              )}
            </div>
          );
          })}
        </div>

      {/* Charts Row - Pass the actual data arrays */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <ServiceAnalyticsChart serviceStats={filteredServiceStats || []} timeRange={timeRange} glassmorphism={true} />
        <RecentDownloadsPanel downloads={filteredLatestDownloads || []} timeRange={timeRange} glassmorphism={true} />
      </div>

      {/* Analytics Widgets Row */}
      <div
        className="grid grid-cols-1 md:grid-cols-2 gap-4"
      >
        <PeakUsageHours
          glassmorphism={true}
          staggerIndex={8}
        />
        <CacheGrowthTrend
          usedCacheSize={cacheInfo?.usedCacheSize || 0}
          totalCacheSize={cacheInfo?.totalCacheSize || 0}
          glassmorphism={true}
          staggerIndex={9}
        />
      </div>

      {/* Top Clients */}
      <div>
        <TopClientsTable
          clientStats={filteredClientStats || []}
          timeRange={timeRange}
          customStartDate={customStartDate}
          customEndDate={customEndDate}
          glassmorphism={true}
        />
      </div>
    </div>
  );
};

export default Dashboard;
