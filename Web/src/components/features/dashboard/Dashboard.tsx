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
  Files,
  Eye,
  EyeOff,
  ChevronDown,
  Search,
  GripVertical,
  LayoutGrid,
  X,
  Move,
  Check,
  Sparkles
} from 'lucide-react';
import { useTranslation, Trans } from 'react-i18next';
import {
  useStats,
  useDownloads,
  useGameDetection,
  useSparklines,
  useCacheSnapshot
} from '@contexts/DashboardDataContext/hooks';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useEvents } from '@contexts/useEvents';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { useDraggableCards } from '@hooks/useDraggableCards';
import { useExitPresence, DROPDOWN_EXIT_MS } from '@hooks/useExitPresence';
import { formatBytes, formatCount, formatPercent } from '@utils/formatters';
import { buildGamesOnDiskDisplayStats } from '@utils/gameDetection';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { STORAGE_KEYS } from '@utils/constants';
import { type StatCardData } from '../../../types';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import {
  EVICTION_SETTINGS_CHANGED_EVENT,
  type EvictionSettingsChangedDetail
} from '@/components/features/management/sections/managementStorageKeys';
import StatCard from '@components/common/StatCard';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { HelpSection } from '@components/ui/HelpPopover';
import ServiceAnalyticsChart from './ServiceAnalyticsChart';
import RecentDownloadsPanel from './RecentDownloadsPanel';
import TopClientsTable from './TopClientsTable';
// Widget imports
import PeakUsageHours from './widgets/PeakUsageHours';
import CacheGrowthTrend from './widgets/CacheGrowthTrend';
import Badge from '@components/ui/Badge';

type CardLayout = 'balanced' | '4-column' | '3-column';
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
  cacheHitRatio: true,
  cacheFiles: false,
  gamesOnDisk: false
};

const DEFAULT_CARD_ORDER: string[] = [
  'bandwidthSaved',
  'cacheHitRatio',
  'activeDownloads',
  'usedSpace',
  'totalCache',
  'addedToCache',
  'totalServed',
  'activeClients',
  'cacheFiles',
  'gamesOnDisk'
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
  ),
  cacheFiles: (
    <HelpSection title={t('dashboard.statCards.cacheFiles.term')}>
      {t('dashboard.statCards.cacheFiles.description')}
    </HelpSection>
  ),
  gamesOnDisk: (
    <HelpSection title={t('dashboard.statCards.gamesOnDisk.term')}>
      {t('dashboard.statCards.gamesOnDisk.description')}
    </HelpSection>
  )
});

const Dashboard: React.FC = () => {
  const { t } = useTranslation();
  const { cacheInfo, clientStats, serviceStats, dashboardStats, loading } = useStats();
  const { latestDownloads } = useDownloads();
  const { gameDetectionData, detectionLookup, detectionByName, detectionByService } =
    useGameDetection();
  const { timeRange, getTimeRangeParams, customStartDate, customEndDate, selectedEventIds } =
    useTimeFilter();
  const { selectedEvent: _selectedEvent } = useEvents();
  const { speedSnapshot, activeDownloadCount } = useSpeed();
  const statTooltips = useMemo(() => getStatTooltips(t), [t]);

  // Eviction mode - determines whether evicted games are included in "Games on Disk"
  const [evictedDataMode, setEvictedDataMode] = useState<string>('show');
  useEffect(() => {
    const controller = new AbortController();
    ApiService.getEvictionSettings(controller.signal)
      .then((response: { evictedDataMode: string }) => {
        setEvictedDataMode(response.evictedDataMode);
      })
      .catch(() => {
        /* ignore abort / network errors */
      });
    return () => controller.abort();
  }, []);

  // Listen for in-session eviction-settings saves so the dashboard reflects
  // the new mode without waiting for a remount.
  useEffect(() => {
    const handler = (event: Event): void => {
      const detail = (event as CustomEvent<EvictionSettingsChangedDetail>).detail;
      setEvictedDataMode(detail.evictedDataMode);
    };
    window.addEventListener(EVICTION_SETTINGS_CHANGED_EVENT, handler);
    return () => {
      window.removeEventListener(EVICTION_SETTINGS_CHANGED_EVENT, handler);
    };
  }, []);

  // Derive evicted count directly from gameDetectionData (is_evicted === true OR partial eviction via evicted_downloads_count > 0)
  const evictedGamesCount = useMemo(
    () =>
      gameDetectionData?.games?.filter(
        (game) => game.is_evicted === true || (game.evicted_downloads_count ?? 0) > 0
      ).length ?? 0,
    [gameDetectionData]
  );

  const [cardLayout, setCardLayout] = useState<CardLayout>(() => {
    const savedLayout = localStorage.getItem('dashboard-card-layout') as CardLayout | null;
    return savedLayout === 'balanced' || savedLayout === '3-column' || savedLayout === '4-column'
      ? savedLayout
      : 'balanced';
  });
  const handleCardLayoutChange = (value: string) => {
    setCardLayout(value as CardLayout);
    localStorage.setItem('dashboard-card-layout', value);
  };

  const getStatCardsGridClass = useCallback((layout: CardLayout, visibleCount: number) => {
    if (layout === '3-column') {
      return 'stat-cards-3col';
    }

    if (layout === '4-column') {
      return 'stat-cards-4col';
    }

    if (visibleCount === 2) {
      return 'stat-cards-2col';
    }

    if (visibleCount > 0 && visibleCount % 5 === 0) {
      return 'stat-cards-5col';
    }

    if (visibleCount > 0 && visibleCount % 3 === 0) {
      return 'stat-cards-3col';
    }

    return 'stat-cards-4col';
  }, []);

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

  // Sparkline and cache snapshot data from context (batched endpoint)
  const { sparklines: sparklineData } = useSparklines();
  const { cacheSnapshot } = useCacheSnapshot();

  // Filter out evicted downloads when eviction mode is 'hide' (server may still return them until refetch)
  const filteredLatestDownloads = useMemo(() => {
    if (evictedDataMode !== 'hide') {
      return latestDownloads;
    }
    return latestDownloads.filter((download) => !download.isEvicted);
  }, [latestDownloads, evictedDataMode]);

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
  const { present: dropdownPresent, closing: dropdownClosing } = useExitPresence(
    dropdownOpen,
    DROPDOWN_EXIT_MS
  );
  const [searchQuery, setSearchQuery] = useState('');
  const [isChartExpanded, setIsChartExpanded] = useState<boolean>(true);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const isInitialVisibilityMount = useRef(true);

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
  }, [timeRange, t]);

  const [cardVisibility, setCardVisibility] = useState<CardVisibility>(() => {
    const saved = storage.getItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY);
    if (saved) {
      try {
        const parsed = JSON.parse(saved) as CardVisibility;
        const merged = { ...DEFAULT_CARD_VISIBILITY, ...parsed };
        // Force hidden-by-default cards to stay hidden unless the user
        // explicitly saved a value for them (i.e. the key exists in their
        // persisted state).  This prevents stale localStorage from a
        // previous version from overriding a new card's default.
        for (const key of Object.keys(DEFAULT_CARD_VISIBILITY)) {
          if (DEFAULT_CARD_VISIBILITY[key] === false && !(key in parsed)) {
            merged[key] = false;
          }
        }
        return merged;
      } catch (e) {
        // Corrupt/stale localStorage - explicit silent fallback to defaults. This runs inside a
        // useState lazy initializer (no hooks available), and re-rendering with the safe default
        // is the correct outcome; nothing here is worth interrupting the user for.
        console.error('Failed to parse card visibility settings:', e);
        return DEFAULT_CARD_VISIBILITY;
      }
    }
    return DEFAULT_CARD_VISIBILITY;
  });

  useEffect(() => {
    if (isInitialVisibilityMount.current) {
      isInitialVisibilityMount.current = false;
      return;
    }
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

  const formattedCacheScanTime = useFormattedDateTime(cacheInfo?.cacheScanTimestampUtc);
  const periodLabel = getTimeRangeLabel().toLowerCase();

  const stats = useMemo(() => {
    // Use dashboardStats when available, otherwise keep previous values to prevent flashing to 0
    const hasPeriodData = dashboardStats?.period !== undefined && dashboardStats?.period !== null;

    // Active stats come from SpeedContext which has its own grace period
    // to prevent tab-switch flicker (zero-transition delay in applySpeedSnapshot)
    const activeClients = speedSnapshot?.clientSpeeds?.length ?? 0;
    const totalActiveDownloads = activeDownloadCount;

    const newStats = {
      activeClients,
      totalActiveDownloads,
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
      uniqueClients: hasPeriodData
        ? (dashboardStats.uniqueClients ?? previousStatsRef.current.uniqueClients)
        : previousStatsRef.current.uniqueClients,
      periodDownloads: hasPeriodData ? (dashboardStats.period.downloads ?? 0) : 0
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
  }, [dashboardStats, speedSnapshot, activeDownloadCount]);

  // Compute "Games on Disk" aggregate from detection data.
  // Hold the previous stable value in a ref so the card doesn't jump
  // while game detection is running and returning intermediate results.
  const formattedLastDetectionTime = useFormattedDateTime(gameDetectionData?.lastDetectionTime);
  const gamesOnDiskStats = useMemo(() => {
    if (!gameDetectionData?.hasCachedResults) {
      return null;
    }

    const includeEvicted = evictedDataMode === 'show' || evictedDataMode === 'showClean';
    return buildGamesOnDiskDisplayStats(gameDetectionData, {
      showEvictedBadge: includeEvicted,
      evictedCount: evictedGamesCount
    });
  }, [gameDetectionData, evictedDataMode, evictedGamesCount]);

  const unmappedCacheBytes = useMemo(() => {
    const cacheScanTotal = cacheInfo?.cacheScanTotalBytes;
    const identifiedTotal = gameDetectionData?.identified_cache_bytes;
    if (
      cacheScanTotal === undefined ||
      identifiedTotal === undefined ||
      cacheScanTotal <= identifiedTotal
    ) {
      return null;
    }

    return cacheScanTotal - identifiedTotal;
  }, [cacheInfo?.cacheScanTotalBytes, gameDetectionData?.identified_cache_bytes]);

  const allStatCards = useMemo<AllStatCards>(
    () => ({
      totalCache: {
        key: 'totalCache',
        title: t('dashboard.cards.totalCache'),
        value: cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '—',
        subtitle:
          cacheInfo?.configuredCacheSize && cacheInfo.configuredCacheSize > 0
            ? t('dashboard.cards.driveCapacityValue', {
                size: formatBytes(cacheInfo.driveCapacity)
              })
            : t('dashboard.cards.fullDiskLimit'),
        icon: Database,
        color: 'blue' as const,
        visible: cardVisibility.totalCache,
        tooltip: statTooltips.totalCache
      },
      usedSpace: {
        key: 'usedSpace',
        title:
          isHistoricalView && cacheSnapshot?.hasData
            ? t('dashboard.cards.usedSpaceEnd')
            : t('dashboard.cards.usedSpace'),
        value: isHistoricalView
          ? cacheSnapshot?.hasData
            ? formatBytes(cacheSnapshot.endUsedSize)
            : t('common.noDataAvailable')
          : cacheInfo
            ? formatBytes(cacheInfo.usedCacheSize)
            : '—',
        subtitle: isHistoricalView
          ? cacheSnapshot?.hasData
            ? t('dashboard.cards.startedAt', { size: formatBytes(cacheSnapshot.startUsedSize) }) +
              ' • ' +
              t('dashboard.cards.snapshots', { count: cacheSnapshot.snapshotCount })
            : t('dashboard.cards.noSnapshotsYet')
          : cacheInfo
            ? cacheInfo.configuredCacheSize > 0 &&
              cacheInfo.usedCacheSize > cacheInfo.totalCacheSize
              ? t('dashboard.cards.overConfiguredLimit', {
                  percent: formatPercent(cacheInfo.usagePercent)
                })
              : formatPercent(cacheInfo.usagePercent)
            : '—',
        icon: HardDrive,
        color: 'blue' as const,
        visible: cardVisibility.usedSpace,
        tooltip: statTooltips.usedSpace
      },
      bandwidthSaved: {
        key: 'bandwidthSaved',
        title: t('dashboard.cards.bandwidthSaved'),
        value: stats.bandwidthSaved != null && !loading ? formatBytes(stats.bandwidthSaved) : '—',
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: TrendingUp,
        color: 'green' as const,
        visible: cardVisibility.bandwidthSaved,
        tooltip: statTooltips.bandwidthSaved
      },
      addedToCache: {
        key: 'addedToCache',
        title: t('dashboard.cards.addedToCache'),
        value: stats.addedToCache != null && !loading ? formatBytes(stats.addedToCache) : '—',
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Zap,
        color: 'teal' as const,
        visible: cardVisibility.addedToCache,
        tooltip: statTooltips.addedToCache
      },
      totalServed: {
        key: 'totalServed',
        title: t('dashboard.cards.totalServed'),
        value: stats.totalServed != null && !loading ? formatBytes(stats.totalServed) : '—',
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Server,
        color: 'teal' as const,
        visible: cardVisibility.totalServed,
        tooltip: statTooltips.totalServed
      },
      activeDownloads: {
        key: 'activeDownloads',
        title: t('dashboard.cards.activeDownloads'),
        value: isHistoricalView ? t('dashboard.cards.disabled') : stats.totalActiveDownloads,
        subtitle: isHistoricalView
          ? t('dashboard.cards.liveDataOnly')
          : [
              t('dashboard.cards.liveNow'),
              t('dashboard.cards.downloadsInRange', {
                count: stats.periodDownloads,
                period: periodLabel
              })
            ].join(' · '),
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
          : [
              t('dashboard.cards.liveNow'),
              t('dashboard.cards.uniqueClientsInRange', {
                count: stats.uniqueClients,
                period: periodLabel
              })
            ].join(' · '),
        icon: Users,
        color: 'orange' as const,
        visible: cardVisibility.activeClients,
        tooltip: statTooltips.activeClients
      },
      cacheHitRatio: {
        key: 'cacheHitRatio',
        title: t('dashboard.cards.cacheHitRatio'),
        value: stats.cacheHitRatio != null && !loading ? formatPercent(stats.cacheHitRatio) : '—',
        subtitle: getTimeRangeLabel().toLowerCase(),
        icon: Activity,
        color: 'green' as const,
        visible: cardVisibility.cacheHitRatio,
        tooltip: statTooltips.cacheHitRatio
      },
      cacheFiles: {
        key: 'cacheFiles',
        title: t('dashboard.cards.cacheFiles'),
        value: cacheInfo ? formatCount(cacheInfo.totalFiles) : '—',
        subtitle: [
          t('dashboard.cards.filesOnDisk'),
          formattedCacheScanTime
            ? t('dashboard.cards.scannedAt', { time: formattedCacheScanTime })
            : null
        ]
          .filter(Boolean)
          .join(' • '),
        badge: cacheInfo?.scanMayBeStale ? (
          <Badge variant="warning">{t('dashboard.cards.staleScanData')}</Badge>
        ) : undefined,
        icon: Files,
        color: 'teal' as const,
        visible: cardVisibility.cacheFiles,
        tooltip: statTooltips.cacheFiles
      },
      gamesOnDisk: {
        key: 'gamesOnDisk',
        title: t('dashboard.cards.gamesOnDisk'),
        value: gamesOnDiskStats ? formatBytes(gamesOnDiskStats.totalSize) : '-',
        subtitle: gamesOnDiskStats
          ? [
              t('dashboard.cards.gamesDetected', { count: gamesOnDiskStats.gameCount }),
              formattedLastDetectionTime
                ? t('dashboard.cards.scannedAt', { time: formattedLastDetectionTime })
                : null,
              unmappedCacheBytes && unmappedCacheBytes > 0
                ? t('dashboard.statCards.unmappedCache', { size: formatBytes(unmappedCacheBytes) })
                : null
            ]
              .filter(Boolean)
              .join(' • ')
          : t('dashboard.cards.noScanData'),
        badge:
          cacheInfo?.scanMayBeStale || gamesOnDiskStats?.includesEvicted ? (
            <>
              {cacheInfo?.scanMayBeStale ? (
                <Badge variant="warning">{t('dashboard.cards.staleScanData')}</Badge>
              ) : null}
              {gamesOnDiskStats?.includesEvicted ? (
                <Badge variant="warning">
                  {t('dashboard.cards.evictedIncluded', { count: gamesOnDiskStats.evictedCount })}
                </Badge>
              ) : null}
            </>
          ) : undefined,
        icon: HardDrive,
        color: 'blue' as const,
        visible: cardVisibility.gamesOnDisk ?? false,
        tooltip: statTooltips.gamesOnDisk
      }
    }),
    [
      t,
      cacheInfo,
      cacheSnapshot,
      cardVisibility,
      stats,
      loading,
      periodLabel,
      getTimeRangeLabel,
      isHistoricalView,
      statTooltips,
      gamesOnDiskStats,
      formattedLastDetectionTime,
      formattedCacheScanTime,
      unmappedCacheBytes
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

  return (
    <div className={`space-y-4 animate-fadeIn ${isEditMode ? 'edit-mode-active' : ''}`}>
      {/* Dashboard Header */}
      <div className="flex flex-col sm:flex-row sm:justify-between sm:items-center gap-4">
        <h2 className="text-2xl font-bold text-themed-primary tracking-tight hidden md:block">
          {t('dashboard.title')}
        </h2>
        <div className="flex flex-row items-center gap-2">
          {/* Mobile Edit Mode Toggle */}
          <div className="md:hidden">
            {isEditMode ? (
              <Button
                variant="filled"
                color="green"
                size="md"
                onClick={exitEditMode}
                leftSection={<Check className="w-4 h-4" />}
              >
                {t('dashboard.done')}
              </Button>
            ) : (
              <Tooltip content={t('tooltips.rearrangeCards')} strategy="overlay">
                <Button
                  variant="filled"
                  color="gray"
                  size="md"
                  onClick={toggleEditMode}
                  leftSection={<Move className="w-4 h-4" />}
                >
                  {t('dashboard.edit')}
                </Button>
              </Tooltip>
            )}
          </div>

          {/* Card Layout Toggle - hidden on mobile since cards are always single column */}
          <div className="hidden md:block">
            <SegmentedControl
              options={[
                { value: 'balanced', label: 'Balanced' },
                { value: '4-column', label: '4 Column' },
                { value: '3-column', label: '3 Column' }
              ]}
              value={cardLayout}
              onChange={handleCardLayoutChange}
              size="md"
            />
          </div>

          {/* Hidden Cards Button - only shows when cards are hidden */}
          {hiddenCardsCount > 0 && (
            <div className="relative" ref={dropdownRef}>
              <Tooltip
                content={t('dashboard.hiddenCardsTooltip', { count: hiddenCardsCount })}
                strategy="overlay"
              >
                {/* min-h-10 (2.5rem) so this icon-only button stays 40px to match the
                    md Card Layout toggle on its left and the Reset Layout button on its
                    right - with no text child, px-4 py-2 + a 16px icon alone only reaches
                    ~32px. */}
                <Button
                  variant="filled"
                  color="gray"
                  size="md"
                  onClick={() => setDropdownOpen(!dropdownOpen)}
                  aria-label={t('dashboard.hidden')}
                  leftSection={<EyeOff className="w-4 h-4" />}
                  rightSection={
                    <ChevronDown
                      className={`w-3 h-3 transition-transform duration-200 ${dropdownOpen ? 'rotate-180' : ''}`}
                    />
                  }
                  className="min-h-10"
                />
              </Tooltip>

              {/* Hidden Cards Dropdown */}
              {dropdownPresent && (
                <div
                  className={`dash-hidden-dropdown absolute left-0 sm:left-auto sm:right-0 mt-2 w-72 sm:w-80 themed-border-radius border shadow-xl z-50 themed-card border-themed-primary${dropdownClosing ? ' dash-hidden-dropdown--closing' : ''}`}
                >
                  {/* Search - only show if more than 3 hidden cards */}
                  {hiddenCardsCount > 3 && (
                    <div className="p-3 border-b border-themed-primary">
                      <div className="relative">
                        <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-themed-muted" />
                        <input
                          type="search"
                          value={searchQuery}
                          onChange={(e) => setSearchQuery(e.target.value)}
                          placeholder={t('dashboard.searchHiddenCards')}
                          aria-label={t('dashboard.searchHiddenCards')}
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
                        setCardVisibility((prev: CardVisibility) => {
                          const allVisible: CardVisibility = { ...prev };
                          for (const key of Object.keys(allVisible)) {
                            allVisible[key] = true;
                          }
                          return allVisible;
                        });
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
                              className="stat-card-icon p-1.5 themed-border-radius"
                              data-color={card.color}
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
            {/* min-h-10 (2.5rem) so this stays 40px on mobile once its label collapses to
                icon-only (hidden below sm) - without it, padding+icon alone only reaches ~32px,
                shorter than the Edit/Done button next to it which always shows text. */}
            <Button
              variant="filled"
              color="gray"
              size="md"
              onClick={resetCardOrder}
              leftSection={<LayoutGrid className="w-4 h-4" />}
              className="min-h-10"
            >
              <span className="hidden sm:inline">{t('dashboard.resetLayout')}</span>
            </Button>
          </Tooltip>
        </div>
      </div>

      {/* Edit mode instruction banner for mobile */}
      {isEditMode && (
        <div className="md:hidden edit-mode-banner">
          <div className="edit-mode-instruction-banner flex items-center justify-between py-3 px-4 themed-border-radius text-sm">
            <div className="edit-mode-instruction-text flex items-center gap-2">
              {draggedCard ? (
                <>
                  <Move className="w-4 h-4 flex-shrink-0" />
                  <span>{t('dashboard.editModeInstructions.dragging')}</span>
                </>
              ) : (
                <>
                  <Sparkles className="w-4 h-4 flex-shrink-0" />
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
              <span>
                <Trans i18nKey="dashboard.dragHint" components={{ strong: <strong /> }} />
              </span>
            </div>
            <button
              onClick={hideDragHint}
              className="ml-2 p-1 themed-border-radius hover:bg-themed-hover transition-colors flex-shrink-0 text-themed-muted"
              title={t('dashboard.hideThisHint')}
              aria-label={t('dashboard.hideThisHint')}
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>
      )}

      {/* Stats Grid */}
      <div className={getStatCardsGridClass(cardLayout, visibleCards.length)}>
        {visibleCards.map((card: StatCardData) => {
          // Check if this is a live-only card that should be disabled in historical view
          // Note: usedSpace now supports historical data via snapshots, so it's never disabled
          const isLiveOnlyCard = card.key === 'activeDownloads' || card.key === 'activeClients';
          const isCardDisabled = isLiveOnlyCard && isHistoricalView;

          const cardSparklineData =
            card.key === 'bandwidthSaved'
              ? sparklineData?.bandwidthSaved?.data
              : card.key === 'cacheHitRatio'
                ? sparklineData?.cacheHitRatio?.data
                : card.key === 'totalServed'
                  ? sparklineData?.totalServed?.data
                  : card.key === 'addedToCache'
                    ? sparklineData?.addedToCache?.data
                    : undefined;

          return (
            <div
              key={card.key}
              data-card-key={card.key}
              className={`relative group h-full edit-mode-card ${
                isDragMode && draggedCard === card.key ? 'scale-105 shadow-lg card-selected' : ''
              } ${isDragMode && dragOverCard === card.key ? 'translate-y-1' : ''} ${
                dragOverCard === card.key ? 'drag-over' : ''
              } ${isEditMode ? 'cursor-edit' : draggedCard === card.key ? 'cursor-grabbing' : ''} ${
                isCardDisabled
                  ? 'card-disabled'
                  : isDragMode && draggedCard === card.key
                    ? 'card-dragging'
                    : ''
              }`}
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
                  className="absolute top-2 left-2 opacity-0 group-hover:opacity-100 transition-opacity hidden md:block z-[5]"
                >
                  <div className="p-1 themed-border-radius cursor-grab hover:bg-themed-hover">
                    <GripVertical className="w-4 h-4 text-themed-muted" />
                  </div>
                </Tooltip>
              }

              {/* Mobile edit mode indicator - shows grab handle in edit mode */}
              {isEditMode && (
                <div className="absolute top-2 left-2 md:hidden z-[5] edit-mode-handle">
                  <div
                    className={`edit-mode-handle-inner p-1.5 themed-border-radius ${
                      draggedCard === card.key ? 'handle-active' : ''
                    }`}
                  >
                    <GripVertical
                      className={`edit-mode-handle-icon w-4 h-4 transition-colors ${
                        draggedCard === card.key ? 'handle-icon-active' : ''
                      }`}
                    />
                  </div>
                </div>
              )}

              {/* Selected card overlay in edit mode */}
              {isEditMode && draggedCard === card.key && (
                <div className="edit-mode-selected-overlay absolute inset-0 themed-border-radius md:hidden pointer-events-none" />
              )}

              <StatCard
                title={card.title}
                value={card.value}
                subtitle={card.subtitle}
                badge={card.badge}
                icon={card.icon}
                color={card.color}
                tooltip={card.tooltip}
                loading={loading}
                animateValue={!loading}
                sparklineData={cardSparklineData}
              />

              <Tooltip
                content={t('tooltips.hideThisCard')}
                strategy="overlay"
                className="absolute top-2 right-2 z-20 sm:opacity-0 sm:group-hover:opacity-100 sm:focus-within:opacity-100 transition-opacity"
              >
                <button
                  onClick={(event) => {
                    event.stopPropagation();
                    toggleCardVisibility(card.key);
                  }}
                  className="p-2.5 themed-border-radius transition-colors hover:bg-themed-hover focus-visible:opacity-100"
                  aria-label={t('tooltips.hideThisCard')}
                >
                  <EyeOff className="w-3.5 h-3.5 text-themed-muted" />
                </button>
              </Tooltip>

              {/* Disabled overlay with tooltip for active cards in historical view */}
              {isCardDisabled && (
                <Tooltip content={t('tooltips.liveDataOnly')} strategy="overlay">
                  <div className="card-disabled-overlay pointer-events-none absolute inset-0 z-10 cursor-not-allowed themed-border-radius" />
                </Tooltip>
              )}
            </div>
          );
        })}
      </div>

      {/* Charts Row - Pass the actual data arrays */}
      <div className="dashboard-analytics-row">
        <div
          className={`dashboard-analytics-pane transition-[width] duration-300 ${isChartExpanded ? 'dashboard-analytics-pane-chart-expanded' : 'dashboard-analytics-pane-chart-collapsed'}`}
        >
          <div className="w-full h-full">
            <ServiceAnalyticsChart
              serviceStats={serviceStats}
              loading={loading}
              onExpandedChange={setIsChartExpanded}
            />
          </div>
        </div>
        <div
          className={`dashboard-analytics-pane transition-[width] duration-300 ${isChartExpanded ? 'dashboard-analytics-pane-downloads-expanded' : 'dashboard-analytics-pane-downloads-collapsed'}`}
        >
          <div className="w-full h-full">
            <RecentDownloadsPanel
              downloads={filteredLatestDownloads}
              loading={loading}
              timeRange={timeRange}
              detectionLookup={detectionLookup}
              detectionByName={detectionByName}
              detectionByService={detectionByService}
            />
          </div>
        </div>
      </div>

      {/* Analytics Widgets Row */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <PeakUsageHours />
        <CacheGrowthTrend
          usedCacheSize={cacheInfo?.usedCacheSize || 0}
          totalCacheSize={cacheInfo?.totalCacheSize || 0}
        />
      </div>

      {/* Top Clients */}
      <div>
        <TopClientsTable
          clientStats={filteredClientStats}
          timeRange={timeRange}
          customStartDate={customStartDate}
          customEndDate={customEndDate}
          loading={loading}
        />
      </div>
    </div>
  );
};

export default Dashboard;
