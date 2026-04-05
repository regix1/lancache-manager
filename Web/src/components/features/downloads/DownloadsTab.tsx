import React, {
  useState,
  useEffect,
  useMemo,
  useRef,
  useCallback,
  useTransition,
  lazy,
  Suspense
} from 'react';

import { useTranslation } from 'react-i18next';
import {
  Database,
  Settings,
  Download as DownloadIcon,
  List,
  LayoutGrid,
  Grid3x3,
  Table,
  Search,
  X,
  Maximize2,
  RefreshCw
} from 'lucide-react';
import { useDownloads, useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useClientGroups } from '@contexts/useClientGroups';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import { useConfig } from '@contexts/useConfig';
import { useAuth } from '@contexts/useAuth';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { formatDateTime } from '@utils/formatters';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Checkbox } from '@components/ui/Checkbox';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ActionMenu, ActionMenuItem } from '@components/ui/ActionMenu';
import { Pagination } from '@components/ui/Pagination';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { Tooltip } from '@components/ui/Tooltip';
import { ImageCacheContext } from '@components/common/ImageCacheContext';
import LoadingSpinner from '@components/common/LoadingSpinner';

// Import view components
import CompactView from './CompactView';
import NormalView from './NormalView';
import type { RetroViewHandle } from './RetroView';
const RetroView = lazy(() => import('./RetroView'));
import DownloadsHeader from './DownloadsHeader';
import ActiveDownloadsView from './ActiveDownloadsView';

import type { Download, DownloadGroup } from '../../../types';

// Storage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  CLIENT_FILTER: 'lancache_downloads_client',
  SEARCH_QUERY: 'lancache_downloads_search',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  ITEMS_PER_PAGE_RETRO: 'lancache_downloads_items_retro',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small',
  HIDE_LOCALHOST: 'lancache_downloads_hide_localhost',
  HIDE_UNKNOWN_GAMES: 'lancache_downloads_hide_unknown',
  HIDE_EVICTED: 'lancache_downloads_hide_evicted',
  VIEW_MODE: 'lancache_downloads_view_mode',
  SORT_ORDER: 'lancache_downloads_sort_order',
  AESTHETIC_MODE: 'lancache_downloads_aesthetic_mode',
  FULL_HEIGHT_BANNERS: 'lancache_downloads_full_height_banners',
  ENABLE_SCROLL_INTO_VIEW: 'lancache_downloads_scroll_into_view',
  GROUP_UNKNOWN_GAMES: 'lancache_downloads_group_unknown',
  CARD_SIZE: 'lancache_downloads_card_size',
  SHOW_CACHE_HIT_BAR: 'lancache_downloads_show_cache_hit_bar',
  SHOW_EVENT_BADGES: 'lancache_downloads_show_event_badges',
  SHOW_TIMESTAMPS: 'lancache_downloads_show_timestamps',
  SHOW_BANNER_COLUMN: 'lancache_downloads_show_banner_column',
  BANNER_ONLY: 'downloads_banner_only',
  GROUP_BY_GAME_RETRO: 'lancache_downloads_group_by_game_retro'
};

// Default items per page for each view mode
const DEFAULT_ITEMS_PER_PAGE = {
  compact: 50,
  card: 50,
  normal: 50,
  retro: 100
};

// View modes
type ViewMode = 'compact' | 'card' | 'normal' | 'retro';

// Sort order type
type SortOrder =
  | 'latest'
  | 'oldest'
  | 'largest'
  | 'smallest'
  | 'service'
  | 'efficiency'
  | 'efficiency-low'
  | 'sessions'
  | 'alphabetical';

// Preset type
type PresetType = 'pretty' | 'minimal' | 'showAll' | 'default' | 'custom';

// Preset configurations
const PRESETS = {
  pretty: {
    showZeroBytes: false,
    showSmallFiles: false,
    hideLocalhost: true,
    hideUnknownGames: false,
    hideEvicted: false,
    groupUnknownGames: false,
    aestheticMode: false,
    fullHeightBanners: true,
    groupByFrequency: true,
    enableScrollIntoView: true,
    cardSize: 'medium' as const,
    showCacheHitBar: true,
    showEventBadges: true,
    showTimestamps: true,
    showBannerColumn: true,
    bannerOnly: false,
    groupByGameRetro: false
  },
  minimal: {
    showZeroBytes: false,
    showSmallFiles: false,
    hideLocalhost: true,
    hideUnknownGames: false,
    hideEvicted: false,
    groupUnknownGames: false,
    aestheticMode: true,
    fullHeightBanners: false,
    groupByFrequency: true,
    enableScrollIntoView: false,
    cardSize: 'medium' as const,
    showCacheHitBar: false,
    showEventBadges: false,
    showTimestamps: false,
    showBannerColumn: false,
    bannerOnly: false,
    groupByGameRetro: false
  },
  showAll: {
    showZeroBytes: true,
    showSmallFiles: true,
    hideLocalhost: false,
    hideUnknownGames: false,
    hideEvicted: false,
    groupUnknownGames: true,
    aestheticMode: false,
    fullHeightBanners: false,
    groupByFrequency: true,
    enableScrollIntoView: true,
    cardSize: 'medium' as const,
    showCacheHitBar: true,
    showEventBadges: true,
    showTimestamps: true,
    showBannerColumn: true,
    bannerOnly: false,
    groupByGameRetro: false
  },
  default: {
    showZeroBytes: false,
    showSmallFiles: true,
    hideLocalhost: false,
    hideUnknownGames: false,
    hideEvicted: false,
    groupUnknownGames: false,
    aestheticMode: false,
    fullHeightBanners: false,
    groupByFrequency: true,
    enableScrollIntoView: true,
    cardSize: 'medium' as const,
    showCacheHitBar: true,
    showEventBadges: true,
    showTimestamps: true,
    showBannerColumn: true,
    bannerOnly: false,
    groupByGameRetro: false
  }
};

// Function to detect current preset
const detectActivePreset = (settings: {
  showZeroBytes: boolean;
  showSmallFiles: boolean;
  hideLocalhost: boolean;
  hideUnknownGames: boolean;
  hideEvicted: boolean;
  groupUnknownGames: boolean;
  aestheticMode: boolean;
  fullHeightBanners: boolean;
  groupByFrequency: boolean;
  enableScrollIntoView: boolean;
  cardSize: 'small' | 'medium' | 'large';
  showCacheHitBar: boolean;
  showEventBadges: boolean;
  showTimestamps: boolean;
  showBannerColumn: boolean;
  bannerOnly: boolean;
  groupByGameRetro: boolean;
}): PresetType => {
  const presetKeys = ['pretty', 'minimal', 'showAll', 'default'] as const;

  for (const preset of presetKeys) {
    const presetConfig = PRESETS[preset];
    const matches =
      settings.showZeroBytes === presetConfig.showZeroBytes &&
      settings.showSmallFiles === presetConfig.showSmallFiles &&
      settings.hideLocalhost === presetConfig.hideLocalhost &&
      settings.hideUnknownGames === presetConfig.hideUnknownGames &&
      settings.groupUnknownGames === presetConfig.groupUnknownGames &&
      settings.aestheticMode === presetConfig.aestheticMode &&
      settings.fullHeightBanners === presetConfig.fullHeightBanners &&
      settings.groupByFrequency === presetConfig.groupByFrequency &&
      settings.enableScrollIntoView === presetConfig.enableScrollIntoView &&
      settings.cardSize === presetConfig.cardSize &&
      settings.showCacheHitBar === presetConfig.showCacheHitBar &&
      settings.showEventBadges === presetConfig.showEventBadges &&
      settings.showTimestamps === presetConfig.showTimestamps &&
      settings.showBannerColumn === presetConfig.showBannerColumn &&
      settings.bannerOnly === presetConfig.bannerOnly &&
      settings.groupByGameRetro === presetConfig.groupByGameRetro;

    if (matches) return preset;
  }

  return 'custom';
};

// CSV conversion utilities
const convertDownloadsToCSV = (downloads: Download[]): string => {
  if (!downloads || downloads.length === 0) return '';

  // UTF-8 BOM for proper special character encoding (™, ®, etc.)
  const BOM = '\uFEFF';

  const headers = [
    'id',
    'service',
    'clientIp',
    'startTime',
    'endTime',
    'cacheHitBytes',
    'cacheMissBytes',
    'totalBytes',
    'cacheHitPercent',
    'isActive',
    'gameName',
    'gameAppId'
  ];
  const csvHeaders = headers.join(',');

  // Helper to escape CSV values
  const escapeCSV = (value: unknown): string => {
    if (value === null || value === undefined) return '';
    const str = String(value);
    // Escape if contains comma, quote, or newline
    if (str.includes(',') || str.includes('"') || str.includes('\n') || str.includes('\r')) {
      return `"${str.replace(/"/g, '""')}"`;
    }
    return str;
  };

  const csvRows = downloads.map((download) => {
    const row = [
      download.id,
      download.service,
      download.clientIp,
      // Format timestamps using the formatDateTime utility (respects timezone preference)
      download.startTimeUtc ? formatDateTime(download.startTimeUtc) : '',
      download.endTimeUtc ? formatDateTime(download.endTimeUtc) : '',
      download.cacheHitBytes,
      download.cacheMissBytes,
      download.totalBytes,
      download.cacheHitPercent.toFixed(2),
      download.isActive ? 'TRUE' : 'FALSE',
      download.gameName || '',
      download.gameAppId || ''
    ];
    return row.map(escapeCSV).join(',');
  });

  return BOM + [csvHeaders, ...csvRows].join('\n');
};

// Main Downloads Tab Component
const DownloadsTab: React.FC = () => {
  const { t } = useTranslation();
  const { latestDownloads = [], loading } = useDownloads();
  const { detectionLookup, detectionByName, detectionByService } = useGameDetection();
  const { timeRange, selectedEventIds } = useTimeFilter();
  const { getGroupForIp } = useClientGroups();
  const { authMode } = useAuth();
  const isGuest = authMode === 'guest';
  const { on, off } = useSignalR();

  // Active/Recent tab state
  const [activeTab, setActiveTab] = useState<'active' | 'recent'>('recent');

  // Determine if we're viewing historical data (not live)
  // Any time range other than 'live' is historical (including presets like 12h, 24h, 7d, etc.)
  const isHistoricalView = timeRange !== 'live' || selectedEventIds.length > 0;

  // Auto-switch to Recent tab when user switches to historical view while on Active tab
  useEffect(() => {
    if (isHistoricalView && activeTab === 'active') {
      setActiveTab('recent');
    }
  }, [isHistoricalView, activeTab]);

  // Config from context (guaranteed non-null)
  const { config } = useConfig();

  // Get showDatasourceLabels from centralized SessionPreferencesContext
  const { currentPreferences } = useSessionPreferences();
  const showDatasourceLabels = currentPreferences?.showDatasourceLabels ?? true;

  // Load the backend cache generation so image URLs are cache-busted correctly
  useEffect(() => {
    ApiService.getImageCacheVersion().then((v) => {
      if (v > 0) setImageCacheVersion(v);
    });
  }, []);

  // Compute whether to show datasource labels (show if any datasources are configured)
  const hasMultipleDatasources = (config.dataSources?.length ?? 0) >= 1;

  // State management
  const [expandedItem, setExpandedItem] = useState<string | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [exportLoading, setExportLoading] = useState(false);
  const [showExportOptions, setShowExportOptions] = useState(false);
  const [imageCacheClearing, setImageCacheClearing] = useState(false);
  const [imageCacheVersion, setImageCacheVersion] = useState(0);

  // Subscribe to GameImagesUpdated SignalR event to auto-refresh game images when backend finishes fetching them
  useEffect(() => {
    const handleGameImagesUpdated = () => {
      setImageCacheVersion((prev) => prev + 1);
    };
    on('GameImagesUpdated', handleGameImagesUpdated);
    return () => {
      off('GameImagesUpdated', handleGameImagesUpdated);
    };
  }, [on, off]);

  const [currentPage, setCurrentPage] = useState(1);
  const nonRetroContentRef = useRef<HTMLDivElement>(null);
  const currentPageRef = useRef(currentPage);
  const pageChangeTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const suppressExpandScrollTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const fadeResetFrameRef = useRef<number | null>(null);

  const settingsRef = useRef<HTMLDivElement>(null);
  const retroViewRef = useRef<RetroViewHandle>(null);

  // Retro view: store previous non-retro itemsPerPage so we can restore when switching away
  const previousNonRetroItemsPerPage = useRef<number | 'unlimited'>(
    (() => {
      const saved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
      if (saved === 'unlimited') return 'unlimited';
      if (saved) return parseInt(saved);
      return DEFAULT_ITEMS_PER_PAGE.normal;
    })()
  );

  const [settings, setSettings] = useState(() => {
    const savedViewMode = (storage.getItem(STORAGE_KEYS.VIEW_MODE) || 'normal') as ViewMode;

    // Get the appropriate items per page based on view mode
    const getItemsPerPage = (viewMode: ViewMode): number | 'unlimited' => {
      if (viewMode === 'retro') {
        const retroSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO);
        if (retroSaved === 'unlimited') return 'unlimited';
        if (retroSaved) return parseInt(retroSaved);
        return DEFAULT_ITEMS_PER_PAGE.retro;
      } else {
        const standardSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
        if (standardSaved === 'unlimited') return 'unlimited';
        if (standardSaved) return parseInt(standardSaved);
        return DEFAULT_ITEMS_PER_PAGE[viewMode];
      }
    };

    return {
      showZeroBytes: storage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
      showSmallFiles: storage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
      hideLocalhost: storage.getItem(STORAGE_KEYS.HIDE_LOCALHOST) === 'true',
      hideUnknownGames: storage.getItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES) === 'true',
      hideEvicted: storage.getItem(STORAGE_KEYS.HIDE_EVICTED) === 'true',
      selectedService: storage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
      selectedClient: storage.getItem(STORAGE_KEYS.CLIENT_FILTER) || 'all',
      searchQuery: storage.getItem(STORAGE_KEYS.SEARCH_QUERY) || '',
      itemsPerPage: getItemsPerPage(savedViewMode),
      viewMode: savedViewMode,
      sortOrder: (storage.getItem(STORAGE_KEYS.SORT_ORDER) || 'latest') as
        | 'latest'
        | 'oldest'
        | 'largest'
        | 'smallest'
        | 'service'
        | 'efficiency'
        | 'efficiency-low'
        | 'sessions'
        | 'alphabetical',
      aestheticMode: storage.getItem(STORAGE_KEYS.AESTHETIC_MODE) === 'true',
      fullHeightBanners: storage.getItem(STORAGE_KEYS.FULL_HEIGHT_BANNERS) === 'true',
      groupByFrequency: storage.getItem('lancache_downloads_group_by_frequency') !== 'false',
      enableScrollIntoView: storage.getItem(STORAGE_KEYS.ENABLE_SCROLL_INTO_VIEW) !== 'false',
      groupUnknownGames: storage.getItem(STORAGE_KEYS.GROUP_UNKNOWN_GAMES) === 'true',
      cardSize: (storage.getItem(STORAGE_KEYS.CARD_SIZE) || 'medium') as
        | 'small'
        | 'medium'
        | 'large',
      showCacheHitBar: storage.getItem(STORAGE_KEYS.SHOW_CACHE_HIT_BAR) !== 'false',
      showEventBadges: storage.getItem(STORAGE_KEYS.SHOW_EVENT_BADGES) !== 'false',
      showTimestamps: storage.getItem(STORAGE_KEYS.SHOW_TIMESTAMPS) !== 'false',
      showBannerColumn: storage.getItem(STORAGE_KEYS.SHOW_BANNER_COLUMN) !== 'false',
      bannerOnly: storage.getItem(STORAGE_KEYS.BANNER_ONLY) === 'true',
      groupByGameRetro: storage.getItem(STORAGE_KEYS.GROUP_BY_GAME_RETRO) === 'true'
    };
  });

  // useTransition for view mode switching - prevents UI jank
  const [, startTransition] = useTransition();

  // hasEverMounted refs for display:none pattern - keep views mounted once visited
  const compactEverMounted = useRef(settings.viewMode === 'compact');
  const cardEverMounted = useRef(settings.viewMode === 'card');
  const normalEverMounted = useRef(settings.viewMode === 'normal');
  const retroEverMounted = useRef(settings.viewMode === 'retro');

  // Effect to save settings to localStorage
  useEffect(() => {
    storage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    storage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
    storage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    storage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
    storage.setItem(STORAGE_KEYS.HIDE_EVICTED, settings.hideEvicted.toString());
    storage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    storage.setItem(STORAGE_KEYS.CLIENT_FILTER, settings.selectedClient);
    storage.setItem(STORAGE_KEYS.SEARCH_QUERY, settings.searchQuery);
    // Save items per page to the appropriate key based on view mode
    if (settings.viewMode === 'retro') {
      storage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO, settings.itemsPerPage.toString());
    } else {
      storage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, settings.itemsPerPage.toString());
    }
    storage.setItem(STORAGE_KEYS.VIEW_MODE, settings.viewMode);
    storage.setItem(STORAGE_KEYS.SORT_ORDER, settings.sortOrder);
    storage.setItem(STORAGE_KEYS.AESTHETIC_MODE, settings.aestheticMode.toString());
    storage.setItem(STORAGE_KEYS.FULL_HEIGHT_BANNERS, settings.fullHeightBanners.toString());
    storage.setItem('lancache_downloads_group_by_frequency', settings.groupByFrequency.toString());
    storage.setItem(STORAGE_KEYS.ENABLE_SCROLL_INTO_VIEW, settings.enableScrollIntoView.toString());
    storage.setItem(STORAGE_KEYS.GROUP_UNKNOWN_GAMES, settings.groupUnknownGames.toString());
    storage.setItem(STORAGE_KEYS.CARD_SIZE, settings.cardSize);
    storage.setItem(STORAGE_KEYS.SHOW_CACHE_HIT_BAR, settings.showCacheHitBar.toString());
    storage.setItem(STORAGE_KEYS.SHOW_EVENT_BADGES, settings.showEventBadges.toString());
    storage.setItem(STORAGE_KEYS.SHOW_TIMESTAMPS, settings.showTimestamps.toString());
    storage.setItem(STORAGE_KEYS.SHOW_BANNER_COLUMN, settings.showBannerColumn.toString());
    storage.setItem(STORAGE_KEYS.BANNER_ONLY, settings.bannerOnly.toString());
    storage.setItem(STORAGE_KEYS.GROUP_BY_GAME_RETRO, settings.groupByGameRetro.toString());
  }, [settings]);

  // Track previous view mode to detect changes
  const prevViewModeRef = useRef(settings.viewMode);
  const [_isViewTransitioning, setIsViewTransitioning] = useState(false);

  // Effect to switch items per page when view mode changes
  useEffect(() => {
    if (prevViewModeRef.current !== settings.viewMode) {
      const prevMode = prevViewModeRef.current;
      const newMode = settings.viewMode;

      // Mark view as ever-mounted for display:none pattern
      if (newMode === 'compact') compactEverMounted.current = true;
      if (newMode === 'card') cardEverMounted.current = true;
      if (newMode === 'normal') normalEverMounted.current = true;
      if (newMode === 'retro') retroEverMounted.current = true;

      // Trigger opacity transition for view-mode switch
      setIsViewTransitioning(true);
      const timer = setTimeout(() => setIsViewTransitioning(false), 350);

      // When switching AWAY from retro, save current retro value and restore previous non-retro value
      if (prevMode === 'retro' && newMode !== 'retro') {
        // Restore the previously saved non-retro itemsPerPage
        const restored = previousNonRetroItemsPerPage.current;
        prevViewModeRef.current = newMode;
        if (settings.itemsPerPage !== restored) {
          setSettings((prev) => ({ ...prev, itemsPerPage: restored }));
        }
        return () => clearTimeout(timer);
      }

      prevViewModeRef.current = newMode;

      // Load the saved items per page for the new view mode
      let newItemsPerPage: number | 'unlimited';
      if (newMode === 'retro') {
        // When switching TO retro: save current non-retro itemsPerPage, then cap retro
        previousNonRetroItemsPerPage.current = settings.itemsPerPage;

        const retroSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO);
        if (retroSaved === 'unlimited') {
          // Retro saved as unlimited — cap to 100 instead
          newItemsPerPage = 100;
        } else if (retroSaved) {
          const parsed = parseInt(retroSaved);
          // Cap at 100 when switching to retro
          newItemsPerPage = parsed > 100 ? 100 : parsed;
        } else {
          newItemsPerPage = DEFAULT_ITEMS_PER_PAGE.retro;
        }
      } else {
        const standardSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
        if (standardSaved === 'unlimited') {
          newItemsPerPage = 'unlimited';
        } else if (standardSaved) {
          newItemsPerPage = parseInt(standardSaved);
        } else {
          newItemsPerPage = DEFAULT_ITEMS_PER_PAGE[newMode];
        }
      }

      // Only update if the items per page would actually change
      if (settings.itemsPerPage !== newItemsPerPage) {
        setSettings((prev) => ({ ...prev, itemsPerPage: newItemsPerPage }));
      }

      return () => clearTimeout(timer);
    }
  }, [settings.viewMode, settings.itemsPerPage]);

  // Note: Downloads are now always fetched from the context - no need to manage mock data count here

  // Note: Filter changes are handled client-side via useMemo, no loading state needed.
  // Showing a loading overlay for instant client-side filtering causes unnecessary flicker.
  // See Checkbox.tsx for the pattern to follow when filtering data.

  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map((d) => d.service.toLowerCase()));
    return Array.from(services).sort();
  }, [latestDownloads]);

  const availableClients = useMemo(() => {
    const clients = new Set(latestDownloads.map((d) => d.clientIp));
    return Array.from(clients).sort();
  }, [latestDownloads]);

  // Filter out services that only have small files (< 1MB) from the dropdown
  const filteredAvailableServices = useMemo(() => {
    return availableServices.filter((service) => {
      // Check if this service has any downloads > 1MB
      const serviceDownloads = latestDownloads.filter((d) => d.service.toLowerCase() === service);
      const hasLargeFiles = serviceDownloads.some((d) => d.totalBytes > 1024 * 1024); // 1MB

      return hasLargeFiles;
    });
  }, [availableServices, latestDownloads]);

  const serviceOptions = useMemo(() => {
    const baseOptions = [
      { value: 'all', label: t('downloads.tab.filters.allServices') },
      ...filteredAvailableServices.map((service) => ({
        value: service,
        label: service.charAt(0).toUpperCase() + service.slice(1)
      }))
    ];

    // Add hidden services option if there are any filtered out
    const hiddenServices = availableServices.filter(
      (service) => !filteredAvailableServices.includes(service)
    );
    if (hiddenServices.length > 0) {
      baseOptions.push(
        { value: 'divider', label: t('downloads.tab.filters.smallFilesOnly') },
        ...hiddenServices.map((service) => ({
          value: service,
          label: `${service.charAt(0).toUpperCase() + service.slice(1)}`
        }))
      );
    }

    return baseOptions;
  }, [filteredAvailableServices, availableServices, t]);

  const { clientGroups } = useClientGroups();

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
      { value: 'all', label: t('downloads.tab.filters.allClients') }
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

  const itemsPerPageOptions = useMemo(() => {
    const options = [
      { value: '20', label: '20' },
      { value: '50', label: '50' },
      { value: '100', label: '100' },
      { value: '200', label: '200' },
      { value: 'unlimited', label: t('downloads.tab.filters.allItems') }
    ];
    if (settings.viewMode === 'retro') {
      return options.filter((opt) => opt.value !== 'unlimited' && opt.value !== '200');
    }
    return options;
  }, [t, settings.viewMode]);

  // Handler for items-per-page changes
  const handleItemsPerPageChange = (value: string) => {
    setSettings((prev) => ({
      ...prev,
      itemsPerPage: value === 'unlimited' ? 'unlimited' : parseInt(value)
    }));
  };

  const filteredDownloads = useMemo(() => {
    if (!Array.isArray(latestDownloads)) {
      console.error('latestDownloads is not an array:', latestDownloads);
      return [];
    }
    let filtered = [...latestDownloads];

    if (!settings.showZeroBytes) {
      filtered = filtered.filter((d) => (d.totalBytes || 0) > 0);
    }

    if (!settings.showSmallFiles) {
      filtered = filtered.filter(
        (d) => (d.totalBytes || 0) === 0 || (d.totalBytes || 0) >= 1048576
      );
    }

    if (settings.hideLocalhost) {
      filtered = filtered.filter((d) => d.clientIp !== '127.0.0.1' && d.clientIp !== '::1');
    }

    if (settings.hideEvicted) {
      filtered = filtered.filter((d) => !d.isEvicted);
    }

    if (settings.selectedService !== 'all') {
      filtered = filtered.filter((d) => d.service.toLowerCase() === settings.selectedService);
    }

    if (settings.selectedClient !== 'all') {
      // Check if it's a group selection (e.g., "group-123")
      if (settings.selectedClient.startsWith('group-')) {
        const groupId = parseInt(settings.selectedClient.replace('group-', ''), 10);
        const group = clientGroups.find((g) => g.id === groupId);
        if (group) {
          // Filter by any IP in the group
          filtered = filtered.filter((d) => group.memberIps.includes(d.clientIp));
        }
      } else {
        // Filter by exact IP
        filtered = filtered.filter((d) => d.clientIp === settings.selectedClient);
      }
    }

    // Apply search filter
    if (settings.searchQuery.trim()) {
      const query = settings.searchQuery.toLowerCase().trim();
      filtered = filtered.filter(
        (d) =>
          (d.gameName && d.gameName.toLowerCase().includes(query)) ||
          d.service.toLowerCase().includes(query) ||
          d.clientIp.toLowerCase().includes(query) ||
          (d.depotId && String(d.depotId).includes(query)) ||
          (d.gameAppId && String(d.gameAppId).includes(query))
      );
    }

    return filtered;
  }, [
    latestDownloads,
    settings.showZeroBytes,
    settings.showSmallFiles,
    settings.hideLocalhost,
    settings.hideEvicted,
    settings.selectedService,
    settings.selectedClient,
    settings.searchQuery,
    clientGroups
  ]);

  // Removed serviceFilteredDownloads - now using latestDownloads.length directly for total count

  // Grouping logic for different view modes
  const createGroups = (
    downloads: Download[],
    groupUnknown = false,
    hideUnknownGames = false
  ): { groups: DownloadGroup[]; individuals: Download[] } => {
    const groups: Record<string, DownloadGroup> = {};
    const individuals: Download[] = [];

    downloads.forEach((download) => {
      let groupKey: string;
      let groupName: string;
      let groupType: 'game' | 'metadata' | 'content';

      // Check if this is an unknown game (platform-agnostic)
      // Catches: null/undefined gameName (Rust processor), empty string, or gameName === service name (backend fallback)
      const isUnknownGame =
        !download.gameName ||
        download.gameName.trim() === '' ||
        download.gameName.toLowerCase() === download.service.toLowerCase();

      // Check if we have a valid game (either by appId or by name)
      const hasValidGameAppId = !!download.gameAppId;
      const hasValidGameName = !isUnknownGame && !!download.gameName;

      if (hasValidGameAppId || hasValidGameName) {
        // Use gameAppId for grouping when available (prevents duplicates from name variations)
        // Fall back to gameName only if no appId exists
        groupKey = hasValidGameAppId
          ? `game-appid-${download.gameAppId}`
          : `game-${download.gameName}`;
        groupName = download.gameName || `Steam App ${download.gameAppId}`;
        groupType = 'game';
      } else if (!hideUnknownGames && groupUnknown && isUnknownGame) {
        // Group all unknown games together when the setting is enabled
        // (skip when hideUnknownGames is true - those go to service-level group instead)
        groupKey = 'unknown-steam-games';
        groupName = 'Unknown Games';
        groupType = 'content';
      } else if ((download.service ?? '').toLowerCase() !== 'steam') {
        const svcLower = (download.service ?? '').toLowerCase();
        groupKey = `service-${svcLower}`;
        groupName =
          svcLower === 'epicgames'
            ? 'Epic Games'
            : `${(download.service ?? '').charAt(0).toUpperCase() + (download.service ?? '').slice(1)} Downloads`;
        groupType = download.totalBytes === 0 ? 'metadata' : 'content';
      } else {
        // Unmapped Steam downloads - group at service level like other services
        groupKey = 'service-steam';
        groupName = 'Steam Downloads';
        groupType = 'content';
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
  };

  const normalViewItems = useMemo((): (Download | DownloadGroup)[] => {
    const { groups, individuals } = createGroups(
      filteredDownloads,
      settings.groupUnknownGames,
      settings.hideUnknownGames
    );

    // Filter out groups with "unknown" in the name if hideUnknownGames is enabled
    let filteredGroups = groups;
    if (settings.hideUnknownGames) {
      filteredGroups = groups.filter((g) => {
        const groupNameLower = g.name.toLowerCase().trim();
        const hasUnknown = groupNameLower.includes('unknown');
        const isUnmappedApps = g.name === 'Unmapped Steam Apps';
        const shouldKeep = !hasUnknown && !isUnmappedApps;
        return shouldKeep;
      });
    }

    // Keep ALL groups as expandable groups, including single downloads
    const allItems: (Download | DownloadGroup)[] = [...filteredGroups, ...individuals];

    return allItems.sort((a, b) => {
      // If groupByFrequency is disabled, skip the frequency-based sorting
      if (settings.groupByFrequency) {
        // First sort by whether it's a group with multiple downloads vs single/individual
        const aIsMultiple = 'downloads' in a && a.downloads.length > 1;
        const bIsMultiple = 'downloads' in b && b.downloads.length > 1;

        if (aIsMultiple && !bIsMultiple) return -1; // Multiple downloads first
        if (!aIsMultiple && bIsMultiple) return 1; // Single downloads/individuals after

        const aIsSingle = 'downloads' in a && a.downloads.length === 1;
        const bIsSingle = 'downloads' in b && b.downloads.length === 1;

        if (aIsSingle && !bIsSingle && !bIsMultiple) return -1; // Single downloads before individuals
        if (!aIsSingle && bIsSingle && !aIsMultiple) return 1; // Individuals after single downloads
      }

      // Then sort by time within each category (or just by time if groupByFrequency is off)
      const aTime =
        'downloads' in a
          ? Math.max(...a.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
          : new Date(a.startTimeUtc).getTime();
      const bTime =
        'downloads' in b
          ? Math.max(...b.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
          : new Date(b.startTimeUtc).getTime();
      return bTime - aTime;
    });
  }, [
    filteredDownloads,
    settings.groupByFrequency,
    settings.hideUnknownGames,
    settings.groupUnknownGames
  ]);

  // Compact and Normal share the same grouping logic, so reuse normalViewItems
  const compactViewItems = normalViewItems;

  const allItemsSorted = useMemo(() => {
    let items =
      settings.viewMode === 'normal' || settings.viewMode === 'card'
        ? normalViewItems
        : settings.viewMode === 'compact'
          ? compactViewItems
          : filteredDownloads;

    // Define the sort function
    const sortFn = (a: Download | DownloadGroup, b: Download | DownloadGroup) => {
      switch (settings.sortOrder) {
        case 'oldest': {
          const aTime =
            'downloads' in a
              ? Math.min(...a.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(a.startTimeUtc).getTime();
          const bTime =
            'downloads' in b
              ? Math.min(...b.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(b.startTimeUtc).getTime();
          return aTime - bTime;
        }
        case 'largest': {
          const aBytes = 'downloads' in a ? a.totalBytes : a.totalBytes || 0;
          const bBytes = 'downloads' in b ? b.totalBytes : b.totalBytes || 0;
          return bBytes - aBytes;
        }
        case 'smallest': {
          const aBytesSmall = 'downloads' in a ? a.totalBytes : a.totalBytes || 0;
          const bBytesSmall = 'downloads' in b ? b.totalBytes : b.totalBytes || 0;
          return aBytesSmall - bBytesSmall;
        }
        case 'service': {
          const serviceCompare = a.service.localeCompare(b.service);
          if (serviceCompare !== 0) return serviceCompare;
          const aLatest =
            'downloads' in a
              ? Math.max(...a.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(a.startTimeUtc).getTime();
          const bLatest =
            'downloads' in b
              ? Math.max(...b.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(b.startTimeUtc).getTime();
          return bLatest - aLatest;
        }
        case 'efficiency': {
          // Sort by cache hit percentage (highest first)
          const aEfficiency =
            'downloads' in a
              ? a.totalBytes > 0
                ? (a.cacheHitBytes / a.totalBytes) * 100
                : 0
              : (a.totalBytes || 0) > 0
                ? ((a.cacheHitBytes || 0) / (a.totalBytes || 1)) * 100
                : 0;
          const bEfficiency =
            'downloads' in b
              ? b.totalBytes > 0
                ? (b.cacheHitBytes / b.totalBytes) * 100
                : 0
              : (b.totalBytes || 0) > 0
                ? ((b.cacheHitBytes || 0) / (b.totalBytes || 1)) * 100
                : 0;
          return bEfficiency - aEfficiency;
        }
        case 'efficiency-low': {
          // Sort by cache hit percentage (lowest first)
          const aEffLow =
            'downloads' in a
              ? a.totalBytes > 0
                ? (a.cacheHitBytes / a.totalBytes) * 100
                : 0
              : (a.totalBytes || 0) > 0
                ? ((a.cacheHitBytes || 0) / (a.totalBytes || 1)) * 100
                : 0;
          const bEffLow =
            'downloads' in b
              ? b.totalBytes > 0
                ? (b.cacheHitBytes / b.totalBytes) * 100
                : 0
              : (b.totalBytes || 0) > 0
                ? ((b.cacheHitBytes || 0) / (b.totalBytes || 1)) * 100
                : 0;
          return aEffLow - bEffLow;
        }
        case 'sessions': {
          // Sort by number of download sessions (most first)
          const aSessions = 'downloads' in a ? a.count : 1;
          const bSessions = 'downloads' in b ? b.count : 1;
          return bSessions - aSessions;
        }
        case 'alphabetical': {
          // Sort by name alphabetically
          const aName = 'downloads' in a ? a.name : a.gameName || a.service;
          const bName = 'downloads' in b ? b.name : b.gameName || b.service;
          return aName.localeCompare(bName);
        }
        case 'latest':
        default: {
          const aLatestDefault =
            'downloads' in a
              ? Math.max(...a.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(a.startTimeUtc).getTime();
          const bLatestDefault =
            'downloads' in b
              ? Math.max(...b.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(b.startTimeUtc).getTime();
          return bLatestDefault - aLatestDefault;
        }
      }
    };

    // Apply sorting
    if (
      settings.viewMode === 'normal' ||
      settings.viewMode === 'card' ||
      settings.viewMode === 'compact'
    ) {
      const mixedItems = [...items] as (Download | DownloadGroup)[];

      // When sorting by service, alphabetical, efficiency, or sessions - sort all items together without frequency grouping
      const skipFrequencyGrouping = [
        'service',
        'alphabetical',
        'efficiency',
        'efficiency-low',
        'sessions'
      ].includes(settings.sortOrder);
      if (skipFrequencyGrouping) {
        mixedItems.sort(sortFn);
        items = mixedItems;
      } else {
        // For other sort orders, preserve Multiple vs Single categorization
        const multipleDownloads = mixedItems.filter(
          (item) => 'downloads' in item && item.downloads.length > 1
        );
        const singleDownloads = mixedItems.filter(
          (item) => 'downloads' in item && item.downloads.length === 1
        );
        const individuals = mixedItems.filter((item) => !('downloads' in item));

        // Sort each category
        multipleDownloads.sort(sortFn);
        singleDownloads.sort(sortFn);
        individuals.sort(sortFn);

        // Combine categories in the correct order
        items = [...multipleDownloads, ...singleDownloads, ...individuals];
      }
    }

    return items;
  }, [filteredDownloads, normalViewItems, compactViewItems, settings.viewMode, settings.sortOrder]);

  const itemsToDisplay = useMemo(() => {
    if (settings.itemsPerPage === 'unlimited') {
      return allItemsSorted;
    }

    const itemsPerPageNum = typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 20;
    const startIndex = (currentPage - 1) * itemsPerPageNum;
    const endIndex = startIndex + itemsPerPageNum;
    return allItemsSorted.slice(startIndex, endIndex);
  }, [allItemsSorted, currentPage, settings.itemsPerPage]);

  const totalPages = useMemo(() => {
    if (settings.itemsPerPage === 'unlimited') return 1;
    const itemsPerPageNum = typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 20;
    return Math.ceil(allItemsSorted.length / itemsPerPageNum);
  }, [allItemsSorted.length, settings.itemsPerPage]);

  // Reset to page 1 when filters change
  useEffect(() => {
    if (currentPage !== 1) {
      setCurrentPage(1);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    settings.selectedService,
    settings.selectedClient,
    settings.searchQuery,
    settings.sortOrder,
    settings.showZeroBytes,
    settings.showSmallFiles,
    settings.hideLocalhost,
    settings.hideUnknownGames,
    settings.viewMode,
    settings.itemsPerPage
  ]);

  // Click outside handler to close settings dropdown
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as HTMLElement;

      // Check if click is on settings button or its children
      const isSettingsButton = target.closest('[data-settings-button="true"]');

      // Check if click is inside settings dropdown
      const isInsideDropdown = settingsRef.current && settingsRef.current.contains(target);

      // Close dropdown if click is outside both the button and dropdown
      if (settingsOpened && !isSettingsButton && !isInsideDropdown) {
        setSettingsOpened(false);
      }
    };

    if (settingsOpened) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
      };
    }
  }, [settingsOpened]);

  // Suppress scroll-into-view during page changes so pagination scroll isn't fought
  const [suppressExpandScroll, setSuppressExpandScroll] = useState(false);

  useEffect(() => {
    currentPageRef.current = currentPage;
  }, [currentPage]);

  useEffect(() => {
    const nonRetroEl = nonRetroContentRef.current;
    const retroEl = retroViewRef.current;
    return () => {
      if (pageChangeTimeoutRef.current !== null) {
        clearTimeout(pageChangeTimeoutRef.current);
      }
      if (suppressExpandScrollTimeoutRef.current !== null) {
        clearTimeout(suppressExpandScrollTimeoutRef.current);
      }
      if (fadeResetFrameRef.current !== null) {
        cancelAnimationFrame(fadeResetFrameRef.current);
      }
      nonRetroEl?.classList.remove('page-fading');
      retroEl?.setPageFading(false);
    };
  }, []);

  const setContentFade = useCallback(
    (fading: boolean) => {
      if (settings.viewMode === 'retro') {
        nonRetroContentRef.current?.classList.remove('page-fading');
        retroViewRef.current?.setPageFading(fading);
        return;
      }

      retroViewRef.current?.setPageFading(false);
      nonRetroContentRef.current?.classList.toggle('page-fading', fading);
    },
    [settings.viewMode]
  );

  // Handle page changes with a DOM-only fade so the pagination bar doesn't repaint.
  const handlePageChange = useCallback(
    (newPage: number) => {
      if (newPage === currentPageRef.current) return;

      if (pageChangeTimeoutRef.current !== null) {
        clearTimeout(pageChangeTimeoutRef.current);
      }
      if (suppressExpandScrollTimeoutRef.current !== null) {
        clearTimeout(suppressExpandScrollTimeoutRef.current);
      }
      if (fadeResetFrameRef.current !== null) {
        cancelAnimationFrame(fadeResetFrameRef.current);
        fadeResetFrameRef.current = null;
      }

      // Suppress scroll-into-view on newly mounted items during page transition.
      setSuppressExpandScroll(true);
      suppressExpandScrollTimeoutRef.current = setTimeout(() => {
        setSuppressExpandScroll(false);
        suppressExpandScrollTimeoutRef.current = null;
      }, 600);

      setContentFade(true);

      pageChangeTimeoutRef.current = setTimeout(() => {
        currentPageRef.current = newPage;
        setCurrentPage(newPage);
        pageChangeTimeoutRef.current = null;

        fadeResetFrameRef.current = requestAnimationFrame(() => {
          setContentFade(false);
          fadeResetFrameRef.current = null;
        });
      }, 150);
    },
    [setContentFade]
  );

  const handleExport = (format: 'json' | 'csv') => {
    setExportLoading(true);
    try {
      const itemsForExport = allItemsSorted;
      let content = '';
      let filename = '';
      let mimeType = '';

      const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5);
      const baseFilename = `lancache_downloads_${timestamp}`;

      if (format === 'csv') {
        const downloadsForExport =
          settings.viewMode === 'normal' ||
          settings.viewMode === 'card' ||
          settings.viewMode === 'compact'
            ? (itemsForExport as (Download | DownloadGroup)[]).flatMap((item) =>
                'downloads' in item ? item.downloads : [item]
              )
            : (itemsForExport as Download[]);

        content = convertDownloadsToCSV(downloadsForExport);
        filename = `${baseFilename}.csv`;
        mimeType = 'text/csv;charset=utf-8';
      } else {
        const jsonReplacer = (_key: string, value: unknown) => {
          if (value instanceof Set) {
            return Array.from(value);
          }
          return value;
        };

        content = JSON.stringify(itemsForExport, jsonReplacer, 2);
        filename = `${baseFilename}.json`;
        mimeType = 'application/json';
      }

      const blob = new Blob([content], { type: mimeType });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = filename;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);
    } catch (error) {
      console.error('Export failed:', error);
    } finally {
      setExportLoading(false);
      setShowExportOptions(false);
    }
  };

  const handleClearImageCache = async () => {
    setImageCacheClearing(true);
    try {
      const result = await ApiService.clearImageCache();
      // Use backend generation so the version survives page reload / SPA navigation
      setImageCacheVersion(result.cacheGeneration);

      // If Epic URLs weren't refreshed (auth may still be in progress),
      // do a delayed second bump to catch URLs populated by auto-reconnect
      if (result.epicImageUrlsRefreshed === 0) {
        console.warn(
          '[handleClearImageCache] Epic URLs not refreshed yet - scheduling delayed retry'
        );
        setTimeout(() => {
          setImageCacheVersion((v) => v + 1);
        }, 6000);
      }
    } catch (error) {
      console.error('[handleClearImageCache] Failed to clear image cache:', error);
    } finally {
      setImageCacheClearing(false);
    }
  };

  const handleItemClick = (id: string) => {
    setExpandedItem(expandedItem === id ? null : id);
  };

  // Loading state with skeleton loader
  if (loading) {
    return (
      <div className="space-y-4 animate-fade-in">
        {/* Skeleton Controls */}
        <Card padding="sm" className="animate-pulse">
          <div className="flex flex-col gap-3">
            <div className="flex gap-2">
              <div className="h-10 bg-[var(--theme-bg-tertiary)] rounded w-40"></div>
              <div className="h-10 bg-[var(--theme-bg-tertiary)] rounded w-32"></div>
              <div className="h-10 bg-[var(--theme-bg-tertiary)] rounded w-40"></div>
            </div>
          </div>
        </Card>

        {/* Skeleton Content */}
        <div className="space-y-2">
          {[1, 2, 3, 4, 5].map((i) => (
            <div
              key={i}
              className="h-16 bg-[var(--theme-bg-secondary)] rounded animate-pulse"
              style={{ animationDelay: `${i * 100}ms` }}
            >
              <div className="p-3 flex items-center gap-3">
                <div className="h-6 w-16 bg-[var(--theme-bg-tertiary)] rounded"></div>
                <div className="h-4 bg-[var(--theme-bg-tertiary)] rounded flex-1 max-w-[200px]"></div>
                <div className="ml-auto flex gap-3">
                  <div className="h-4 w-20 bg-[var(--theme-bg-tertiary)] rounded"></div>
                  <div className="h-4 w-12 bg-[var(--theme-bg-tertiary)] rounded"></div>
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  // Empty state (only show for Recent tab when no data)
  if (latestDownloads.length === 0 && activeTab === 'recent') {
    return (
      <div className="space-y-4 animate-fade-in">
        <DownloadsHeader activeTab={activeTab} onTabChange={setActiveTab} />
        <Alert color="blue" icon={<Database className="w-5 h-5" />}>
          {t('downloads.tab.emptyRecorded')}
        </Alert>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Downloads Header with Speed Display and Tab Toggle */}
      <DownloadsHeader activeTab={activeTab} onTabChange={setActiveTab} />

      {/* Active Downloads View */}
      {activeTab === 'active' && (
        <Card padding="md">
          <ActiveDownloadsView />
        </Card>
      )}

      {/* Recent Downloads View */}
      {activeTab === 'recent' && (
        <>
          {/* Controls */}
          <Card padding="sm" className="transition-all duration-300">
            <div className="flex flex-col gap-3">
              {/* Search Input + Settings gear on mobile */}
              <div className="downloads-search-row">
                <div className="search-input-wrapper relative sm:max-w-xs">
                  <Search
                    size={16}
                    className="absolute left-3 top-1/2 -translate-y-1/2 text-[var(--theme-text-muted)]"
                  />
                  <input
                    type="text"
                    value={settings.searchQuery}
                    onChange={(e) => setSettings({ ...settings, searchQuery: e.target.value })}
                    placeholder={t('downloads.tab.searchPlaceholder')}
                    className="w-full pl-9 pr-8 py-2 text-sm rounded-lg border border-[var(--theme-border-primary)] bg-[var(--theme-bg-primary)] text-[var(--theme-text-primary)] placeholder:text-[var(--theme-text-muted)] focus:outline-none focus:ring-2 focus:ring-[var(--theme-primary)]/50 focus:border-[var(--theme-primary)] transition-all"
                  />
                  {settings.searchQuery && (
                    <Button
                      variant="subtle"
                      size="xs"
                      onClick={() => setSettings({ ...settings, searchQuery: '' })}
                      className="absolute right-2 top-1/2 -translate-y-1/2 !p-1"
                    >
                      <X size={14} />
                    </Button>
                  )}
                </div>
                <Tooltip content={t('downloads.tab.tooltips.settings')} position="bottom">
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={() => setSettingsOpened(!settingsOpened)}
                    data-settings-button="true"
                    className="sm:hidden flex-shrink-0"
                  >
                    <Settings size={18} />
                  </Button>
                </Tooltip>
              </div>

              {/* Dropdowns and View Controls */}
              <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center justify-between w-full">
                {/* Mobile: First row with service and client filters */}
                <div className="flex sm:hidden gap-2 w-full">
                  <EnhancedDropdown
                    options={serviceOptions}
                    value={settings.selectedService}
                    onChange={(value) => setSettings({ ...settings, selectedService: value })}
                    className="flex-1 min-w-0"
                  />
                  <EnhancedDropdown
                    options={clientOptions}
                    value={settings.selectedClient}
                    onChange={(value) => setSettings({ ...settings, selectedClient: value })}
                    className="flex-1 min-w-0"
                  />
                </div>

                {/* Mobile: Second row with items per page, sort, and view mode */}
                <div className="flex sm:hidden gap-2 w-full items-center">
                  <EnhancedDropdown
                    options={itemsPerPageOptions}
                    value={
                      settings.itemsPerPage === 'unlimited'
                        ? 'unlimited'
                        : settings.itemsPerPage.toString()
                    }
                    onChange={handleItemsPerPageChange}
                    prefix={t('downloads.tab.filters.showPrefix')}
                    className="flex-1 min-w-0"
                  />
                  <EnhancedDropdown
                    options={[
                      { value: 'latest', label: t('downloads.tab.sort.latest') },
                      { value: 'oldest', label: t('downloads.tab.sort.oldest') },
                      { value: 'largest', label: t('downloads.tab.sort.largest') },
                      { value: 'smallest', label: t('downloads.tab.sort.smallest') },
                      { value: 'efficiency', label: t('downloads.tab.sort.bestCache') },
                      { value: 'efficiency-low', label: t('downloads.tab.sort.worstCache') },
                      { value: 'sessions', label: t('downloads.tab.sort.sessions') },
                      { value: 'alphabetical', label: t('downloads.tab.sort.alphabetical') },
                      { value: 'service', label: t('downloads.tab.sort.service') }
                    ]}
                    value={settings.sortOrder}
                    onChange={(value) =>
                      setSettings({ ...settings, sortOrder: value as SortOrder })
                    }
                    prefix={t('downloads.tab.sort.prefix')}
                    className="flex-1 min-w-0"
                  />
                  {/* View mode toggle inline with dropdowns */}
                  <SegmentedControl
                    options={[
                      {
                        value: 'compact',
                        icon: <List />,
                        tooltip: t('downloads.tab.view.compact')
                      },
                      {
                        value: 'card',
                        icon: <LayoutGrid />,
                        tooltip: t('downloads.tab.view.card', 'Card')
                      },
                      {
                        value: 'normal',
                        icon: <Grid3x3 />,
                        tooltip: t('downloads.tab.view.normal')
                      },
                      { value: 'retro', icon: <Table />, tooltip: t('downloads.tab.view.retro') }
                    ]}
                    value={settings.viewMode}
                    onChange={(value) =>
                      startTransition(() =>
                        setSettings({ ...settings, viewMode: value as ViewMode })
                      )
                    }
                    size="sm"
                    className="flex-shrink-0"
                  />
                </div>

                {/* Desktop: All controls in one row */}
                <div className="hidden sm:flex gap-2 items-center">
                  <EnhancedDropdown
                    options={serviceOptions}
                    value={settings.selectedService}
                    onChange={(value) => setSettings({ ...settings, selectedService: value })}
                    className="w-28 md:w-32 lg:w-36"
                  />

                  <EnhancedDropdown
                    options={clientOptions}
                    value={settings.selectedClient}
                    onChange={(value) => setSettings({ ...settings, selectedClient: value })}
                    className="w-28 md:w-32 lg:w-36"
                  />

                  <EnhancedDropdown
                    options={itemsPerPageOptions}
                    value={
                      settings.itemsPerPage === 'unlimited'
                        ? 'unlimited'
                        : settings.itemsPerPage.toString()
                    }
                    onChange={handleItemsPerPageChange}
                    prefix={t('downloads.tab.filters.showPrefix')}
                    className="w-28"
                  />

                  <EnhancedDropdown
                    options={[
                      { value: 'latest', label: t('downloads.tab.sort.latest') },
                      { value: 'oldest', label: t('downloads.tab.sort.oldest') },
                      { value: 'largest', label: t('downloads.tab.sort.largest') },
                      { value: 'smallest', label: t('downloads.tab.sort.smallest') },
                      { value: 'efficiency', label: t('downloads.tab.sort.bestCache') },
                      { value: 'efficiency-low', label: t('downloads.tab.sort.worstCache') },
                      { value: 'sessions', label: t('downloads.tab.sort.sessions') },
                      { value: 'alphabetical', label: t('downloads.tab.sort.alphabetical') },
                      { value: 'service', label: t('downloads.tab.sort.service') }
                    ]}
                    value={settings.sortOrder}
                    onChange={(value) =>
                      setSettings({ ...settings, sortOrder: value as SortOrder })
                    }
                    prefix={t('downloads.tab.sort.prefix')}
                    className="w-28 md:w-32 lg:w-36"
                  />
                </div>

                {/* Desktop view controls */}
                <div className="hidden sm:flex gap-2 justify-end w-auto flex-shrink-0">
                  {/* View Mode Toggle */}
                  <SegmentedControl
                    options={[
                      { value: 'compact', label: t('downloads.tab.view.compact'), icon: <List /> },
                      {
                        value: 'card',
                        label: t('downloads.tab.view.card', 'Card'),
                        icon: <LayoutGrid />
                      },
                      { value: 'normal', label: t('downloads.tab.view.normal'), icon: <Grid3x3 /> },
                      { value: 'retro', label: t('downloads.tab.view.retro'), icon: <Table /> }
                    ]}
                    value={settings.viewMode}
                    onChange={(value) =>
                      startTransition(() =>
                        setSettings({ ...settings, viewMode: value as ViewMode })
                      )
                    }
                    size="md"
                    showLabels="responsive"
                  />

                  {/* Export Button */}
                  <ActionMenu
                    isOpen={showExportOptions}
                    onClose={() => setShowExportOptions(false)}
                    width="w-48"
                    trigger={
                      <Tooltip content={t('downloads.tab.tooltips.export')} position="bottom">
                        <Button
                          variant="subtle"
                          size="sm"
                          onClick={() => setShowExportOptions(!showExportOptions)}
                          disabled={exportLoading || itemsToDisplay.length === 0}
                          loading={exportLoading}
                        >
                          <DownloadIcon size={18} />
                        </Button>
                      </Tooltip>
                    }
                  >
                    <ActionMenuItem
                      onClick={() => {
                        handleExport('json');
                        setShowExportOptions(false);
                      }}
                    >
                      {t('downloads.tab.export.json')}
                    </ActionMenuItem>
                    <ActionMenuItem
                      onClick={() => {
                        handleExport('csv');
                        setShowExportOptions(false);
                      }}
                    >
                      {t('downloads.tab.export.csv')}
                    </ActionMenuItem>
                  </ActionMenu>

                  {settings.viewMode === 'retro' && (
                    <Tooltip content={t('downloads.tab.tooltips.fitColumns')} position="bottom">
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => retroViewRef.current?.resetWidths()}
                      >
                        <Maximize2 size={18} />
                      </Button>
                    </Tooltip>
                  )}

                  {!isGuest && (
                    <Tooltip content={t('downloads.tab.tooltips.refreshImages')} position="bottom">
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={handleClearImageCache}
                        disabled={imageCacheClearing}
                      >
                        <RefreshCw size={18} className={imageCacheClearing ? 'animate-spin' : ''} />
                      </Button>
                    </Tooltip>
                  )}

                  <Tooltip content={t('downloads.tab.tooltips.settings')} position="bottom">
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => setSettingsOpened(!settingsOpened)}
                      data-settings-button="true"
                    >
                      <Settings size={18} />
                    </Button>
                  </Tooltip>
                </div>
              </div>
            </div>

            <div ref={settingsRef}>
              {settingsOpened && (
                <>
                  <div className="border-t border-[var(--theme-border-secondary)] my-3 animate-fade-in" />
                  <div className="space-y-4 animate-slide-in-top">
                    {/* Quick Presets - Mobile-friendly segmented control */}
                    <div>
                      <div className="text-xs font-semibold uppercase tracking-wide mb-2 text-[var(--theme-text-muted)]">
                        {t('downloads.tab.presets.title')}
                      </div>
                      {(() => {
                        const activePreset = detectActivePreset(settings);
                        return (
                          <SegmentedControl
                            options={[
                              { value: 'pretty', label: t('downloads.tab.presets.pretty') },
                              { value: 'minimal', label: t('downloads.tab.presets.minimal') },
                              { value: 'showAll', label: t('downloads.tab.presets.showAll') },
                              { value: 'default', label: t('downloads.tab.presets.default') },
                              {
                                value: 'custom',
                                label: t('downloads.tab.presets.custom'),
                                disabled: true
                              }
                            ]}
                            value={activePreset}
                            onChange={(value) => {
                              if (value !== 'custom') {
                                setSettings({
                                  ...settings,
                                  ...PRESETS[value as keyof typeof PRESETS]
                                });
                              }
                            }}
                            size="sm"
                            showLabels={true}
                            fullWidth
                            className="sm:w-auto"
                          />
                        );
                      })()}
                    </div>

                    {/* Settings Grid - Responsive with collapsible sections on mobile */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4 sm:gap-x-6 sm:gap-y-1">
                      {/* Filters Column */}
                      <div className="space-y-1">
                        <div className="text-xs font-semibold uppercase tracking-wide mb-2 text-[var(--theme-text-muted)]">
                          {t('downloads.tab.sections.filters')}
                        </div>
                        <Checkbox
                          checked={settings.showZeroBytes}
                          onChange={(e) =>
                            setSettings({ ...settings, showZeroBytes: e.target.checked })
                          }
                          label={t('downloads.tab.filters.showMetadata')}
                        />
                        <Checkbox
                          checked={settings.showSmallFiles}
                          onChange={(e) =>
                            setSettings({ ...settings, showSmallFiles: e.target.checked })
                          }
                          label={t('downloads.tab.filters.showSmallFiles')}
                        />
                        <Checkbox
                          checked={settings.hideLocalhost}
                          onChange={(e) =>
                            setSettings({ ...settings, hideLocalhost: e.target.checked })
                          }
                          label={t('downloads.tab.filters.hideLocalhost')}
                        />
                        <Checkbox
                          checked={settings.hideUnknownGames}
                          onChange={(e) =>
                            setSettings({ ...settings, hideUnknownGames: e.target.checked })
                          }
                          label={t('downloads.tab.filters.hideUnknownGames')}
                        />
                        <Checkbox
                          checked={settings.hideEvicted}
                          onChange={(e) =>
                            setSettings({ ...settings, hideEvicted: e.target.checked })
                          }
                          label={t('downloads.tab.filters.hideEvicted')}
                        />
                      </div>

                      {/* Display Column */}
                      <div className="space-y-1">
                        <div className="text-xs font-semibold uppercase tracking-wide mb-2 text-[var(--theme-text-muted)]">
                          {t('downloads.tab.sections.display')}
                        </div>
                        {['compact', 'normal'].includes(settings.viewMode) && (
                          <Checkbox
                            checked={settings.aestheticMode}
                            onChange={(e) =>
                              setSettings({
                                ...settings,
                                aestheticMode: e.target.checked,
                                ...(e.target.checked ? { fullHeightBanners: false } : {})
                              })
                            }
                            label={t('downloads.tab.display.minimalMode')}
                          />
                        )}
                        {settings.viewMode === 'normal' && (
                          <Checkbox
                            checked={settings.fullHeightBanners}
                            onChange={(e) =>
                              setSettings({
                                ...settings,
                                fullHeightBanners: e.target.checked,
                                ...(e.target.checked ? { aestheticMode: false } : {})
                              })
                            }
                            label={t('downloads.tab.display.fullHeightBanners')}
                          />
                        )}
                        {settings.viewMode === 'retro' && (
                          <Checkbox
                            checked={settings.groupByGameRetro}
                            onChange={(e) =>
                              setSettings({ ...settings, groupByGameRetro: e.target.checked })
                            }
                            label={t('downloads.tab.display.groupByGameRetro')}
                          />
                        )}
                        {['compact', 'card', 'normal'].includes(settings.viewMode) && (
                          <Checkbox
                            checked={settings.groupUnknownGames}
                            onChange={(e) =>
                              setSettings({ ...settings, groupUnknownGames: e.target.checked })
                            }
                            label={t('downloads.tab.behavior.groupUnknown')}
                          />
                        )}
                        {['compact', 'normal'].includes(settings.viewMode) && (
                          <Checkbox
                            checked={settings.groupByFrequency}
                            onChange={(e) =>
                              setSettings({ ...settings, groupByFrequency: e.target.checked })
                            }
                            label={t('downloads.tab.behavior.groupByFrequency')}
                          />
                        )}
                        {settings.viewMode === 'card' && (
                          <div className="flex items-center gap-2 py-1">
                            <span className="text-sm text-[var(--theme-text-secondary)]">
                              Card size
                            </span>
                            <SegmentedControl
                              options={[
                                { value: 'small', label: 'S' },
                                { value: 'medium', label: 'M' },
                                { value: 'large', label: 'L' }
                              ]}
                              value={settings.cardSize}
                              onChange={(value) =>
                                setSettings({
                                  ...settings,
                                  cardSize: value as 'small' | 'medium' | 'large'
                                })
                              }
                              size="sm"
                            />
                          </div>
                        )}
                        {settings.viewMode === 'card' && (
                          <Checkbox
                            checked={settings.bannerOnly}
                            onChange={(e) =>
                              setSettings({ ...settings, bannerOnly: e.target.checked })
                            }
                            label="Banner only"
                          />
                        )}
                      </div>

                      {/* Behavior Column */}
                      <div className="space-y-1">
                        <div className="text-xs font-semibold uppercase tracking-wide mb-2 text-[var(--theme-text-muted)]">
                          {t('downloads.tab.sections.behavior')}
                        </div>
                        {['compact', 'normal'].includes(settings.viewMode) && (
                          <Checkbox
                            checked={settings.enableScrollIntoView}
                            onChange={(e) =>
                              setSettings({ ...settings, enableScrollIntoView: e.target.checked })
                            }
                            label={t('downloads.tab.behavior.scrollOnExpand')}
                          />
                        )}
                        {(settings.viewMode === 'normal' ||
                          (settings.viewMode === 'card' && !settings.bannerOnly)) && (
                          <Checkbox
                            checked={settings.showCacheHitBar}
                            onChange={(e) =>
                              setSettings({ ...settings, showCacheHitBar: e.target.checked })
                            }
                            label="Show cache hit bar"
                          />
                        )}
                        {(settings.viewMode === 'normal' ||
                          (settings.viewMode === 'card' && !settings.bannerOnly)) && (
                          <Checkbox
                            checked={settings.showEventBadges}
                            onChange={(e) =>
                              setSettings({ ...settings, showEventBadges: e.target.checked })
                            }
                            label="Show event badges"
                          />
                        )}
                        {settings.viewMode === 'retro' && (
                          <Checkbox
                            checked={settings.showTimestamps}
                            onChange={(e) =>
                              setSettings({ ...settings, showTimestamps: e.target.checked })
                            }
                            label="Show timestamps"
                          />
                        )}
                        {settings.viewMode === 'retro' && (
                          <Checkbox
                            checked={settings.showBannerColumn}
                            onChange={(e) =>
                              setSettings({ ...settings, showBannerColumn: e.target.checked })
                            }
                            label="Show banner column"
                          />
                        )}
                      </div>
                    </div>
                  </div>
                </>
              )}
            </div>
          </Card>

          {/* Help message for empty time ranges */}
          {filteredDownloads.length === 0 && timeRange !== 'live' && (
            <Alert color="yellow">
              <div className="flex flex-col gap-2">
                <div className="font-medium">{t('downloads.tab.emptyRange.title')}</div>
                <div className="text-sm opacity-90">
                  {t('downloads.tab.emptyRange.description')}
                </div>
              </div>
            </Alert>
          )}

          {/* Sticky Pagination Controls (above content) — retro view manages its own pagination */}
          {settings.viewMode !== 'retro' &&
            settings.itemsPerPage !== 'unlimited' &&
            totalPages > 1 && (
              <div className="pagination-sticky">
                <div className="p-2 rounded-lg bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)]">
                  <Pagination
                    currentPage={currentPage}
                    totalPages={totalPages}
                    totalItems={allItemsSorted.length}
                    itemsPerPage={
                      typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 20
                    }
                    onPageChange={handlePageChange}
                    itemLabel="items"
                    showCard={false}
                    totalDownloads={filteredDownloads.length}
                  />
                </div>
              </div>
            )}

          {/* Downloads list */}
          <ImageCacheContext.Provider value={imageCacheVersion}>
            {settings.viewMode === 'retro' ? (
              <Suspense
                fallback={
                  <div className="flex justify-center py-8">
                    <LoadingSpinner inline size="lg" />
                  </div>
                }
              >
                {retroEverMounted.current && (
                  <RetroView
                    ref={retroViewRef}
                    items={allItemsSorted}
                    sortOrder={settings.sortOrder}
                    itemsPerPage={
                      typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 100
                    }
                    currentPage={currentPage}
                    onPageChange={handlePageChange}
                    showTimestamps={settings.showTimestamps}
                    showBannerColumn={settings.showBannerColumn}
                    aestheticMode={settings.aestheticMode}
                    showDatasourceLabels={showDatasourceLabels}
                    hasMultipleDatasources={hasMultipleDatasources}
                    groupByGame={settings.groupByGameRetro}
                    detectionLookup={detectionLookup}
                    detectionByName={detectionByName}
                    detectionByService={detectionByService}
                  />
                )}
              </Suspense>
            ) : (
              <div
                className="relative overflow-x-hidden page-content-transition"
                ref={nonRetroContentRef}
              >
                {/* Content based on view mode with display:none pattern for instant switching */}
                <div style={{ display: settings.viewMode === 'compact' ? 'block' : 'none' }}>
                  {compactEverMounted.current && (
                    <CompactView
                      items={itemsToDisplay as (Download | DownloadGroup)[]}
                      expandedItem={expandedItem}
                      onItemClick={handleItemClick}
                      aestheticMode={settings.aestheticMode}
                      groupByFrequency={settings.groupByFrequency}
                      enableScrollIntoView={settings.enableScrollIntoView && !suppressExpandScroll}
                      showDatasourceLabels={showDatasourceLabels}
                      hasMultipleDatasources={hasMultipleDatasources}
                      detectionLookup={detectionLookup}
                      detectionByName={detectionByName}
                      detectionByService={detectionByService}
                    />
                  )}
                </div>

                <div style={{ display: settings.viewMode === 'card' ? 'block' : 'none' }}>
                  {cardEverMounted.current && (
                    <NormalView
                      items={itemsToDisplay as (Download | DownloadGroup)[]}
                      expandedItem={expandedItem}
                      onItemClick={handleItemClick}
                      aestheticMode={false}
                      fullHeightBanners={false}
                      groupByFrequency={false}
                      enableScrollIntoView={false}
                      showDatasourceLabels={showDatasourceLabels}
                      hasMultipleDatasources={hasMultipleDatasources}
                      cardGridLayout={true}
                      cardSize={settings.cardSize}
                      showCacheHitBar={settings.showCacheHitBar}
                      showEventBadges={settings.showEventBadges}
                      bannerOnly={settings.bannerOnly}
                      detectionLookup={detectionLookup}
                      detectionByName={detectionByName}
                      detectionByService={detectionByService}
                    />
                  )}
                </div>

                <div style={{ display: settings.viewMode === 'normal' ? 'block' : 'none' }}>
                  {normalEverMounted.current && (
                    <NormalView
                      items={itemsToDisplay as (Download | DownloadGroup)[]}
                      expandedItem={expandedItem}
                      onItemClick={handleItemClick}
                      aestheticMode={settings.aestheticMode}
                      fullHeightBanners={settings.fullHeightBanners}
                      groupByFrequency={settings.groupByFrequency}
                      enableScrollIntoView={settings.enableScrollIntoView && !suppressExpandScroll}
                      showDatasourceLabels={showDatasourceLabels}
                      hasMultipleDatasources={hasMultipleDatasources}
                      cardGridLayout={false}
                      cardSize={settings.cardSize}
                      showCacheHitBar={settings.showCacheHitBar}
                      showEventBadges={settings.showEventBadges}
                      bannerOnly={settings.bannerOnly}
                      detectionLookup={detectionLookup}
                      detectionByName={detectionByName}
                      detectionByService={detectionByService}
                    />
                  )}
                </div>
              </div>
            )}
          </ImageCacheContext.Provider>

          {/* Performance warning */}
          {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 500 && (
            <Alert color="yellow">
              Loading {itemsToDisplay.length} items. Consider using pagination for better
              performance.
            </Alert>
          )}
        </>
      )}
    </div>
  );
};

export default DownloadsTab;
