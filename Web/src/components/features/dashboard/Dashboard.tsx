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
import { useStats } from '@contexts/StatsContext';
import { useDownloads } from '@contexts/DownloadsContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import { useSignalR } from '@contexts/SignalRContext';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import { useDraggableCards } from '@hooks/useDraggableCards';
import { formatBytes, formatPercent } from '@utils/formatters';
import { STORAGE_KEYS } from '@utils/constants';
import { type StatCardData, type SparklineDataResponse, type DownloadSpeedSnapshot, type CacheSnapshotResponse } from '../../../types';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import StatCard from '@components/common/StatCard';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpDefinition } from '@components/ui/HelpPopover';
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
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.totalCache.term')} termColor="blue">
        {t('dashboard.statCards.totalCache.description')}
      </HelpDefinition>
    </div>
  ),
  usedSpace: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.usedSpace.term')} termColor="blue">
        {t('dashboard.statCards.usedSpace.description')}
      </HelpDefinition>
    </div>
  ),
  bandwidthSaved: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.bandwidthSaved.term')} termColor="green">
        {t('dashboard.statCards.bandwidthSaved.description')}
      </HelpDefinition>
    </div>
  ),
  addedToCache: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.addedToCache.term')} termColor="purple">
        {t('dashboard.statCards.addedToCache.description')}
      </HelpDefinition>
    </div>
  ),
  totalServed: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.totalServed.term')} termColor="blue">
        {t('dashboard.statCards.totalServed.description')}
      </HelpDefinition>
    </div>
  ),
  activeDownloads: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.activeDownloads.term')} termColor="orange">
        {t('dashboard.statCards.activeDownloads.description')}
      </HelpDefinition>
    </div>
  ),
  activeClients: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.activeClients.term')} termColor="orange">
        {t('dashboard.statCards.activeClients.description')}
      </HelpDefinition>
    </div>
  ),
  cacheHitRatio: (
    <div className="space-y-1.5">
      <HelpDefinition term={t('dashboard.statCards.cacheHitRatio.term')} termColor="green">
        {t('dashboard.statCards.cacheHitRatio.description')}
      </HelpDefinition>
    </div>
  )
});

const Dashboard: React.FC = () => {
  const { t } = useTranslation();
  const { cacheInfo, clientStats, serviceStats, dashboardStats, loading } = useStats();
  const { latestDownloads } = useDownloads();
  const { timeRange, getTimeRangeParams, customStartDate, customEndDate, selectedEventIds } = useTimeFilter();
  const { selectedEvent } = useEvents();
  const signalR = useSignalR();
  const { getRefreshInterval } = useRefreshRate();
  const statTooltips = useMemo(() => getStatTooltips(t), [t]);

  // Track if initial card animations have completed - prevents re-animation on reorder
  const initialAnimationCompleteRef = useRef(false);
  const [initialAnimationComplete, setInitialAnimationComplete] = useState(false);

  // Real-time speed snapshot for accurate active downloads count
  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);

  // Determine if we're viewing historical/filtered data (not live)
  // Any non-live mode should disable real-time only stats
  const isHistoricalView = timeRange !== 'live';

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

  // Historical cache snapshot data
  const [cacheSnapshot, setCacheSnapshot] = useState<CacheSnapshotResponse | null>(null);

  // Fetch sparkline data when time range or event filter changes
  useEffect(() => {
    const controller = new AbortController();

    // Clear old sparkline data immediately to prevent stale data display
    setSparklineData(null);

    const fetchSparklines = async () => {
      try {
        const { startTime, endTime } = getTimeRangeParams();
        const eventId = selectedEventIds.length > 0 ? selectedEventIds[0] : undefined;
        const data = await ApiService.getSparklineData(controller.signal, startTime, endTime, eventId);
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
  }, [timeRange, getTimeRangeParams, selectedEventIds]);

  // Fetch historical cache snapshot when in historical view
  useEffect(() => {
    const controller = new AbortController();

    // Clear cache snapshot when switching views
    setCacheSnapshot(null);

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

  // Fetch real-time speeds - uses SignalR with user-controlled throttling
  const lastSpeedUpdateRef = useRef<number>(0);
  const pendingSpeedUpdateRef = useRef<NodeJS.Timeout | null>(null);
  const lastActiveCountRef = useRef<number | null>(null);

  // Function to fetch speeds (used for initial load and visibility change)
  const fetchSpeeds = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      setSpeedSnapshot(data);
      lastActiveCountRef.current = data?.gameSpeeds?.length ?? 0;
    } catch (err) {
      console.error('Failed to fetch speeds:', err);
    }
  }, []);

  useEffect(() => {
    // Initial fetch
    fetchSpeeds();

    // SignalR handler with debouncing and user-controlled throttling
    const handleSpeedUpdate = (speedData: DownloadSpeedSnapshot) => {
      // Clear any pending update
      if (pendingSpeedUpdateRef.current) {
        clearTimeout(pendingSpeedUpdateRef.current);
      }

      const newCount = speedData.gameSpeeds?.length ?? 0;

      // ALWAYS accept updates immediately when active games count changes
      // This ensures "download finished" events are never throttled
      const countChanged = lastActiveCountRef.current !== null &&
        lastActiveCountRef.current !== newCount;

      if (countChanged) {
        lastSpeedUpdateRef.current = Date.now();
        lastActiveCountRef.current = newCount;
        setSpeedSnapshot(speedData);
        return;
      }

      // Debounce: wait 100ms for more events
      pendingSpeedUpdateRef.current = setTimeout(() => {
        const maxRefreshRate = getRefreshInterval();
        const now = Date.now();
        const timeSinceLastUpdate = now - lastSpeedUpdateRef.current;

        // User's setting controls max refresh rate
        // LIVE mode (0) = minimum 500ms to prevent UI thrashing
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

        if (timeSinceLastUpdate >= minInterval) {
          lastSpeedUpdateRef.current = now;
          lastActiveCountRef.current = newCount;
          setSpeedSnapshot(speedData);
        }
        pendingSpeedUpdateRef.current = null;
      }, 100);
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);

    return () => {
      signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
      if (pendingSpeedUpdateRef.current) {
        clearTimeout(pendingSpeedUpdateRef.current);
      }
    };
  }, [signalR, getRefreshInterval, fetchSpeeds]);

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
  const [showLoading, setShowLoading] = useState(false); // Delayed loading state
  const [hasInitialData, setHasInitialData] = useState(false); // Track if we've loaded data at least once
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const dropdownRef = useRef<HTMLDivElement>(null);

  // Track when we first get data - after that, never show skeletons again
  useEffect(() => {
    if (dashboardStats && !hasInitialData) {
      setHasInitialData(true);
    }
  }, [dashboardStats, hasInitialData]);

  // Delay showing loading state to avoid flashing for quick API responses
  // Only show skeleton loading on initial load - subsequent loads use subtle opacity
  useEffect(() => {
    if (loading && !hasInitialData) {
      const timer = setTimeout(() => {
        setShowLoading(true);
      }, 200); // Wait 200ms before showing skeleton
      return () => clearTimeout(timer);
    } else {
      setShowLoading(false); // Hide immediately when done
    }
  }, [loading, hasInitialData]);

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
    // Use speed snapshot for real-time accurate active data (from Rust speed tracker)
    const activeClients = speedSnapshot?.clientSpeeds?.length ?? 0;
    const totalActiveDownloads = speedSnapshot?.gameSpeeds?.length ?? 0;
    const totalDownloads = filteredServiceStats.reduce(
      (sum, service) => sum + (service.totalDownloads || 0),
      0
    );

    // Validate that the period data matches the current timeRange
    // This prevents showing stale data when switching time ranges
    // 'live' mode corresponds to 'all' duration, other modes match directly
    // For 'custom' mode, the backend returns dynamic durations like "12h" or "5d",
    // so we just check that we have period data (not 'all' which means no filter)
    let periodMatchesTimeRange = false;
    if (timeRange === 'live') {
      periodMatchesTimeRange = dashboardStats?.period?.duration === 'all';
    } else if (timeRange === 'custom') {
      // For custom ranges, accept any duration that's not 'all' (since they always have time bounds)
      // The duration will be dynamically calculated like "12h" or "5d"
      periodMatchesTimeRange = !!dashboardStats?.period?.duration && dashboardStats.period.duration !== 'all';
    } else {
      // For preset ranges (1h, 6h, 24h, 7d, 30d), match exactly
      periodMatchesTimeRange = dashboardStats?.period?.duration === timeRange;
    }

    // While loading, show old values to allow smooth animation transitions
    // Once loading completes, validation ensures correct data is shown
    const showOldValuesWhileLoading = loading && dashboardStats?.period;
    const shouldShowValues = periodMatchesTimeRange || showOldValuesWhileLoading;

    return {
      activeClients,
      totalActiveDownloads,
      totalDownloads,
      bandwidthSaved: shouldShowValues ? (dashboardStats?.period?.bandwidthSaved || 0) : 0,
      addedToCache: shouldShowValues ? (dashboardStats?.period?.addedToCache || 0) : 0,
      totalServed: shouldShowValues ? (dashboardStats?.period?.totalServed || 0) : 0,
      cacheHitRatio: shouldShowValues ? (dashboardStats?.period?.hitRatio || 0) : 0,
      uniqueClients: shouldShowValues ? (dashboardStats?.uniqueClients || filteredClientStats.length) : 0
    };
  }, [filteredServiceStats, dashboardStats, filteredClientStats, timeRange, loading, speedSnapshot]);

  const allStatCards = useMemo<AllStatCards>(
    () => ({
      totalCache: {
        key: 'totalCache',
        title: t('dashboard.cards.totalCache'),
        value: cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '0 B',
        subtitle: t('dashboard.cards.driveCapacity'),
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
        value: formatPercent(stats.cacheHitRatio * 100),
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
                className="edit-mode-done flex items-center gap-2 px-4 py-2 text-sm rounded-lg"
              >
                <Check className="w-4 h-4" />
                <span>{t('dashboard.done')}</span>
              </button>
            ) : (
              <Tooltip content={t('tooltips.rearrangeCards')} strategy="overlay">
                <button
                  onClick={toggleEditMode}
                  className="edit-mode-toggle flex items-center gap-2 px-3 py-2 text-sm rounded-lg border text-themed-secondary bg-themed-secondary border-themed-primary"
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
                  className="hover-btn-trigger flex items-center gap-2 px-3 py-2 text-sm rounded-lg border text-themed-secondary bg-themed-secondary border-themed-primary"
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
                  className="absolute right-0 mt-2 w-72 sm:w-80 rounded-lg border shadow-xl z-50 themed-card border-themed-primary"
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
                          className="w-full pl-10 pr-3 py-2 rounded-lg text-sm bg-themed-tertiary text-themed-primary border border-themed-primary"
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
                      className="hover-btn w-full px-3 py-2 text-sm rounded-lg text-left flex items-center gap-2 text-themed-accent"
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
                            className="hover-btn w-full p-2.5 rounded-lg flex items-center gap-3 group"
                          >
                            <div
                              className="p-1.5 rounded-lg group-hover:scale-105 transition-transform"
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
              className="hover-btn-trigger flex items-center gap-2 px-3 py-2 text-sm transition-colors rounded-lg border text-themed-secondary bg-themed-secondary border-themed-primary hover:bg-themed-hover hover:text-themed-primary"
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
            className="flex items-center justify-between py-3 px-4 rounded-lg text-sm"
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
          <div className="flex items-center justify-between py-3 px-4 rounded-lg bg-themed-secondary text-themed-muted text-sm">
            <div className="flex items-center gap-2">
              <span>💡</span>
              <span dangerouslySetInnerHTML={{ __html: t('dashboard.dragHint') }} />
            </div>
            <button
              onClick={hideDragHint}
              className="ml-2 p-1 rounded hover:bg-themed-hover transition-colors flex-shrink-0 text-themed-muted"
              title={t('dashboard.hideThisHint')}
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>
      )}

      {/* Stats Grid */}
      {showLoading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 animate-fadeIn">
          {[1, 2, 3, 4, 5, 6, 7, 8].map((i) => (
            <div
              key={i}
              className="rounded-lg p-4 border animate-pulse glass-card h-40"
            >
              <div className="h-4 rounded w-3/5 bg-themed-hover" />
              <div className="h-8 rounded mt-2 w-4/5 bg-themed-hover" />
              <div className="h-3 rounded mt-2 w-2/5 bg-themed-hover" />
              <div className="h-8 rounded mt-3 w-full bg-themed-hover" />
            </div>
          ))}
        </div>
      ) : (
        <div
          className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 animate-fadeIn isolate transition-opacity duration-300"
          style={{ opacity: loading ? 0.7 : 1 }}
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
                    className="p-1 rounded cursor-grab hover:bg-themed-hover"
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
                    className="p-1.5 rounded-lg"
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
                  className="absolute inset-0 rounded-lg md:hidden pointer-events-none"
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
                animateValue={true}
                sparklineData={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.data :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.data :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.data :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.data :
                  undefined
                }
                predictedSparklineData={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.predictedData :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.predictedData :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.predictedData :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.predictedData :
                  undefined
                }
                trend={
                  card.key === 'bandwidthSaved' ? sparklineData?.bandwidthSaved?.trend :
                  card.key === 'cacheHitRatio' ? sparklineData?.cacheHitRatio?.trend :
                  card.key === 'totalServed' ? sparklineData?.totalServed?.trend :
                  card.key === 'addedToCache' ? sparklineData?.addedToCache?.trend :
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
                  className="p-1.5 rounded-lg transition-colors hover:bg-themed-hover"
                >
                  <EyeOff className="w-3.5 h-3.5 text-themed-muted" />
                </button>
              </Tooltip>

              {/* Disabled overlay with tooltip for active cards in historical view */}
              {isCardDisabled && (
                <Tooltip content={t('tooltips.liveDataOnly')} strategy="overlay">
                  <div
                    className="absolute inset-0 z-10 cursor-not-allowed rounded-lg"
                    style={{ background: 'transparent' }}
                  />
                </Tooltip>
              )}
            </div>
          );
          })}
        </div>
      )}

      {/* Charts Row - Pass the actual data arrays */}
      {showLoading ? (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4 animate-fadeIn">
          {[1, 2].map((i) => (
            <div
              key={i}
              className="rounded-lg p-6 border animate-pulse glass-card flex flex-col h-[400px]"
            >
              <div className="h-6 rounded mb-4 flex-shrink-0 w-2/5 bg-themed-hover" />
              <div className="flex-1 rounded bg-themed-hover" />
            </div>
          ))}
        </div>
      ) : (
        <div
          key={`charts-${timeRange}-${customStartDate?.getTime()}-${customEndDate?.getTime()}`}
          className="grid grid-cols-1 lg:grid-cols-2 gap-4 animate-fadeIn"
        >
          <ServiceAnalyticsChart serviceStats={filteredServiceStats || []} timeRange={timeRange} glassmorphism={true} />
          <RecentDownloadsPanel downloads={filteredLatestDownloads || []} timeRange={timeRange} glassmorphism={true} />
        </div>
      )}

      {/* Analytics Widgets Row */}
      {showLoading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 animate-fadeIn">
          {[1, 2, 3, 4].map((i) => (
            <div
              key={i}
              className="rounded-lg p-4 border animate-pulse glass-card h-[200px]"
            >
              <div className="h-5 rounded mb-3 w-1/2 bg-themed-hover" />
              <div className="space-y-2">
                {[1, 2, 3].map((j) => (
                  <div key={j} className="h-8 rounded bg-themed-hover" />
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
      )}

      {/* Top Clients - Pass the filtered data arrays */}
      {showLoading ? (
        <div className="rounded-lg p-6 border animate-pulse animate-fadeIn glass-card flex flex-col h-[400px]">
          <div className="h-6 rounded mb-4 flex-shrink-0 w-[30%] bg-themed-hover" />
          <div className="flex-1 flex flex-col justify-start gap-3">
            {[1, 2, 3, 4, 5].map((i) => (
              <div key={i} className="h-12 rounded flex-shrink-0 bg-themed-hover" />
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
