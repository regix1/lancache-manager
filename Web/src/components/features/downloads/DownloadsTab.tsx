import React, { useState, useEffect, useMemo, useRef, useCallback, lazy, Suspense } from 'react';

import { useTranslation } from 'react-i18next';
import {
  Database,
  Settings,
  Download as DownloadIcon,
  List,
  LayoutGrid,
  Grid3x3,
  Table,
  Maximize2,
  RefreshCw
} from 'lucide-react';
import { useDownloads, useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useClientGroups } from '@contexts/useClientGroups';
import { useClientHostnames } from '@contexts/useClientHostnames';
import { storage } from '@utils/storage';
import ApiService, { type RetroDownloadDto } from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { downloadTextFile } from '@utils/downloadTextFile';
import {
  EVICTION_SETTINGS_CHANGED_EVENT,
  type EvictionSettingsChangedDetail
} from '@/components/features/management/sections/managementStorageKeys';
import { useConfig } from '@contexts/useConfig';
import { useAuth } from '@contexts/useAuth';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { useMediaQuery, useIsDesktop } from '@hooks/useMediaQuery';
import { useReaderClock } from '@hooks/useReaderClock';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import { buildClientFilterOptions, findClientFilterGroup } from '@utils/clientFilterOptions';
import { Alert } from '@components/ui/Alert';
import { Card } from '@components/ui/Card';
import { Checkbox } from '@components/ui/Checkbox';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ActionMenuItem } from '@components/ui/ActionMenu';
import { Pagination } from '@components/ui/Pagination';
import { SearchInput } from '@components/ui/SearchInput';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { LoadingState } from '@components/ui/ManagerCard';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useErrorHandler } from '@hooks/useErrorHandler';

// Import view components
import CompactView from './CompactView';
import NormalView from './NormalView';
import type { RetroViewHandle } from './RetroView.types';
const RetroView = lazy(() => import('./RetroView'));
import DownloadsHeader from './DownloadsHeader';
import ActiveDownloadsView from './ActiveDownloadsView';
import { ALL_ITEMS_PAGE_SIZE, useRetroDownloads } from './useRetroDownloads';
import { useMockMode } from '@contexts/useMockMode';

import type { Download, DownloadGroup } from '../../../types';
import {
  formatServiceLabel,
  getServiceDisplayName,
  getServiceFilterKey
} from '@utils/serviceDisplayName';

// Storage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  CLIENT_FILTER: 'lancache_downloads_client',
  SEARCH_QUERY: 'lancache_downloads_search',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  ITEMS_PER_PAGE_RETRO: 'lancache_downloads_items_retro',
  HIDE_METADATA: 'lancache_downloads_hide_metadata',
  HIDE_SMALL_FILES: 'lancache_downloads_hide_small',
  /** @deprecated Legacy show-semantics key — read only for migration */
  LEGACY_SHOW_METADATA: 'lancache_downloads_metadata',
  /** @deprecated Legacy show-semantics key — read only for migration */
  LEGACY_SHOW_SMALL_FILES: 'lancache_downloads_show_small',
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
  GROUP_BY_GAME_RETRO: 'lancache_downloads_group_by_game_retro',
  GROUP_BY_SERVICE_RETRO: 'lancache_downloads_group_by_service_retro',
  EVICTED_DATA_MODE: 'lancache_downloads_evicted_data_mode',
  HIT_MISS_FILTER: 'lancache_downloads_hit_miss_filter'
};

const loadHideMetadata = (): boolean => {
  const saved = storage.getItem(STORAGE_KEYS.HIDE_METADATA);
  if (saved !== null) return saved === 'true';
  const legacyShow = storage.getItem(STORAGE_KEYS.LEGACY_SHOW_METADATA);
  if (legacyShow === null) return false;
  return legacyShow !== 'true';
};

const loadHideSmallFiles = (): boolean => {
  const saved = storage.getItem(STORAGE_KEYS.HIDE_SMALL_FILES);
  if (saved !== null) return saved === 'true';
  const legacyShow = storage.getItem(STORAGE_KEYS.LEGACY_SHOW_SMALL_FILES);
  if (legacyShow === null) return false;
  return legacyShow === 'false';
};

// Server-side eviction display mode (mirrors backend contract).
type EvictedDataMode = 'show' | 'hide' | 'showClean';

const isEvictedDataMode = (value: unknown): value is EvictedDataMode =>
  value === 'show' || value === 'hide' || value === 'showClean';

// Default items per page for each view mode
const DEFAULT_ITEMS_PER_PAGE = {
  compact: 50,
  card: 50,
  normal: 50,
  retro: 100
};

// View modes
type ViewMode = 'compact' | 'card' | 'normal' | 'retro';

// Hit/Miss content filter for the toolbar's cache-status control.
// 'all' (default) shows every row — byte/pixel-identical to pre-feature behavior.
// 'hit' narrows to byte-weighted cacheHitPercent >= 50, 'miss' to < 50 (mutually exclusive,
// exhaustive split; matches the backend Retro predicate's threshold exactly).
type HitMissFilter = 'all' | 'hit' | 'miss';

// Sort order type
type SortOrder =
  | 'recent'
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

// Unmapped Steam content: a Steam download with no resolved game name (PICS did not cover the
// depot, or none was captured). It has no game to map to, so it displays under the synthetic
// "Unknown/Other" group rather than a named game. The grouped page answers this server-side; the
// export reads raw rows and still has to decide it here.
const isUnmappedSteam = (d: Download): boolean =>
  d.service.toLowerCase() === 'steam' &&
  (!d.gameName || d.gameName.trim() === '' || d.gameName.toLowerCase() === d.service.toLowerCase());

// Preset configurations
const PRESETS = {
  pretty: {
    hideMetadata: true,
    hideSmallFiles: true,
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
    bannerOnly: true,
    groupByGameRetro: false,
    groupByServiceRetro: false
  },
  minimal: {
    hideMetadata: true,
    hideSmallFiles: true,
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
    bannerOnly: true,
    groupByGameRetro: false,
    groupByServiceRetro: false
  },
  showAll: {
    hideMetadata: false,
    hideSmallFiles: false,
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
    bannerOnly: true,
    groupByGameRetro: false,
    groupByServiceRetro: false
  },
  default: {
    hideMetadata: false,
    hideSmallFiles: false,
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
    bannerOnly: true,
    groupByGameRetro: false,
    groupByServiceRetro: false
  }
};

// Function to detect current preset
const detectActivePreset = (settings: {
  hideMetadata: boolean;
  hideSmallFiles: boolean;
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
  groupByServiceRetro: boolean;
}): PresetType => {
  const presetKeys = ['pretty', 'minimal', 'showAll', 'default'] as const;

  for (const preset of presetKeys) {
    const presetConfig = PRESETS[preset];
    const matches =
      settings.hideMetadata === presetConfig.hideMetadata &&
      settings.hideSmallFiles === presetConfig.hideSmallFiles &&
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
      settings.groupByGameRetro === presetConfig.groupByGameRetro &&
      settings.groupByServiceRetro === presetConfig.groupByServiceRetro;

    if (matches) return preset;
  }

  return 'custom';
};

// CSV conversion utilities.
// The clock is passed in rather than read here: this helper sits outside the component and cannot
// call a hook, and the module-level preference it would otherwise fall back on only catches up once
// a save echoes back. An export taken in between would carry the clock the user just left.
const convertDownloadsToCSV = (
  downloads: Download[],
  clock: Omit<TimestampSettings, 'style'>
): string => {
  if (downloads.length === 0) return '';

  // UTF-8 BOM for proper special character encoding (™, ®, etc.)
  const BOM = '\uFEFF';

  const headers = [
    'ID',
    'Service',
    'Client IP',
    'Started At',
    'Ended At',
    'Cache Hit Bytes',
    'Cache Miss Bytes',
    'Total Bytes',
    'Cache Hit %',
    'Active',
    'Game Name',
    'Game App ID',
    'Duration (s)',
    'Average Speed (bytes/s)',
    'Depot ID',
    'Epic App ID',
    'Evicted',
    'Datasource'
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
      // Keep seconds here: this is a data file someone may sort or diff, and short downloads
      // start and end inside the same minute.
      download.startTimeUtc
        ? formatTimestamp(download.startTimeUtc, { ...clock, style: 'log' })
        : '',
      download.endTimeUtc ? formatTimestamp(download.endTimeUtc, { ...clock, style: 'log' }) : '',
      download.cacheHitBytes,
      download.cacheMissBytes,
      download.totalBytes,
      download.cacheHitPercent.toFixed(2),
      download.isActive ? 'TRUE' : 'FALSE',
      download.gameName || '',
      download.gameAppId || '',
      download.durationSeconds ?? '',
      download.averageBytesPerSecond.toFixed(2),
      download.depotId ?? '',
      download.epicAppId || '',
      download.isEvicted ? 'TRUE' : 'FALSE',
      download.datasource || ''
    ];
    return row.map(escapeCSV).join(',');
  });

  return BOM + [csvHeaders, ...csvRows].join('\n');
};

// Main Downloads Tab Component
const DownloadsTab: React.FC = () => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const {
    loading,
    serviceOptions: serviceFilterOptions,
    clientOptions: clientIps
  } = useDownloads();
  const { mockMode } = useMockMode();
  const { detectionLookup, detectionByName, detectionByService } = useGameDetection();
  const { timeRange, selectedEventIds, getTimeRangeParams, customStartDate, customEndDate } =
    useTimeFilter();
  const { getGroupForIp, clientGroups } = useClientGroups();
  const { getHostnameForIp } = useClientHostnames();
  const { authMode } = useAuth();
  const isGuest = authMode === 'guest';
  // The view labels are added and removed from the DOM rather than hidden with a class: a hidden
  // label still counts as the trigger's text, and the shared Tooltip suppresses a box that repeats
  // it, which would leave the icon-only segments with no hover explanation below this width.
  const wideLabels = useMediaQuery('(min-width: 1536px)');
  // Same breakpoint RetroView uses to decide whether to draw the resizable header at all.
  const isDesktop = useIsDesktop();
  // Matches the 639.98px card-grid rule in downloads.css: above it the three card sizes get
  // their own column widths, below it there is only room for one card per row or two.
  const cardSizesFitSideBySide = useMediaQuery('(min-width: 640px)');
  // Read from context so an export started right after a clock change uses the clock the user is
  // looking at, rather than the one the module-level preference is still holding.
  const readerClock = useReaderClock();
  const clock = useMemo(() => ({ ...readerClock, forceYear: false }), [readerClock]);

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

  const retroTimeParams = useMemo(() => getTimeRangeParams(), [getTimeRangeParams]);
  const retroEventId = selectedEventIds.length > 0 ? selectedEventIds[0] : undefined;

  // Debounced search for the retro server fetch - typing fires at most one
  // request per pause instead of one request per keystroke. Seeded from the
  // same storage key as settings.searchQuery so the first fetch matches.
  const [debouncedSearchQuery, setDebouncedSearchQuery] = useState(
    () => storage.getItem(STORAGE_KEYS.SEARCH_QUERY) || ''
  );

  // Config from context (guaranteed non-null)
  const { config } = useConfig();

  // Get showDatasourceLabels from centralized SessionPreferencesContext
  const { currentPreferences } = useSessionPreferences();
  const showDatasourceLabels = currentPreferences?.showDatasourceLabels ?? true;

  // Fetch server-side eviction display mode.
  // Seeding strategy:
  //   1. Read the last-known-good value from localStorage - this survives REST failures so a
  //      user who previously opted into 'show'/'showClean' keeps seeing evicted rows even if
  //      /api/state/get-evicted-data-mode starts returning 500.
  //   2. Fall back to 'hide' when nothing is cached: first paint hides evicted rows so the
  //      badge does not flash and then disappear in unlimited-pagination mode.
  //   3. On REST success, persist the response to localStorage for the next mount.
  //   4. On REST failure, keep the cached value (no overwrite) and warn to console.
  const readCachedEvictedDataMode = (): EvictedDataMode => {
    const cached = storage.getItem(STORAGE_KEYS.EVICTED_DATA_MODE);
    return isEvictedDataMode(cached) ? cached : 'hide';
  };
  const [evictedDataMode, setEvictedDataMode] =
    useState<EvictedDataMode>(readCachedEvictedDataMode);
  useEffect(() => {
    // Mock mode has no stored setting behind it, so the cached value read above stands.
    if (mockMode) return;

    const controller = new AbortController();
    ApiService.getEvictionSettings(controller.signal)
      .then((response: { evictedDataMode: string }) => {
        if (isEvictedDataMode(response.evictedDataMode)) {
          setEvictedDataMode(response.evictedDataMode);
          storage.setItem(STORAGE_KEYS.EVICTED_DATA_MODE, response.evictedDataMode);
        }
      })
      .catch((err: unknown) => {
        // Abort errors are expected on unmount; log other failures so the cached value is
        // visible as the in-session fallback. Do NOT overwrite storage - last-known-good stays.
        if (!controller.signal.aborted) {
          console.warn(
            '[DownloadsTab] Failed to load evictedDataMode; using cached/default:',
            getErrorMessage(err)
          );
        }
      });
    return () => controller.abort();
  }, [mockMode]);

  // Listen for in-session eviction-settings saves from StorageSection so the
  // downloads view reflects the new mode without waiting for a remount.
  useEffect(() => {
    const handler = (event: Event): void => {
      const detail = (event as CustomEvent<EvictionSettingsChangedDetail>).detail;
      if (isEvictedDataMode(detail.evictedDataMode)) {
        setEvictedDataMode(detail.evictedDataMode);
        storage.setItem(STORAGE_KEYS.EVICTED_DATA_MODE, detail.evictedDataMode);
      }
    };
    window.addEventListener(EVICTION_SETTINGS_CHANGED_EVENT, handler);
    return () => {
      window.removeEventListener(EVICTION_SETTINGS_CHANGED_EVENT, handler);
    };
  }, []);

  // Compute whether to show datasource labels (show if any datasources are configured)
  const hasMultipleDatasources = config.dataSources.length >= 1;

  // State management
  const [expandedItem, setExpandedItem] = useState<string | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [exportLoading, setExportLoading] = useState(false);
  const [imageCacheClearing, setImageCacheClearing] = useState(false);
  // How many rows the retro table's own fetch found. Null until it has answered, so a control that
  // reads a count of zero cannot act on it in the render before that table has fetched.
  const [retroTotalItems, setRetroTotalItems] = useState<number | null>(null);
  // Whether the retro table's own fetch is running, so the toolbar's busy indicator shows for a
  // page turn there the way it does for the page's own fetch.
  const [retroFetching, setRetroFetching] = useState(false);

  // Page number is component state. It used to live in the URL, which meant the page and the page
  // size each had two owners; the size pair fought the retro cap below and rewrote each other
  // without settling. Paging is a within-visit position, so it starts at 1 on arrival.
  const [currentPage, setCurrentPage] = useState(1);
  const nonRetroContentRef = useRef<HTMLDivElement>(null);
  const currentPageRef = useRef(currentPage);

  const settingsRef = useRef<HTMLDivElement>(null);
  const retroViewRef = useRef<RetroViewHandle>(null);
  const exportAbort = useRef<AbortController | null>(null);
  useEffect(() => () => exportAbort.current?.abort(), []);

  // Retro view: store previous non-retro itemsPerPage so we can restore when switching away.
  // A stored 'unlimited' names the old "All", which asked for the whole table in one request, so
  // it reads back as the view's default size rather than turning itself into the walked one.
  const previousNonRetroItemsPerPage = useRef<number>(
    (() => {
      const saved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
      if (saved && saved !== 'unlimited') return parseInt(saved);
      return DEFAULT_ITEMS_PER_PAGE.normal;
    })()
  );

  const [settings, setSettings] = useState(() => {
    const savedViewMode = (storage.getItem(STORAGE_KEYS.VIEW_MODE) || 'normal') as ViewMode;

    // Get the appropriate items per page based on view mode
    const getItemsPerPage = (viewMode: ViewMode): number => {
      if (viewMode === 'retro') {
        const retroSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO);
        if (retroSaved && retroSaved !== 'unlimited') {
          const parsed = parseInt(retroSaved);
          // This table is offered 20, 50 and 100 only, but the persist effect writes the size the
          // page is already showing under this key on the render that switches here - before the
          // cap below runs. So a 200, or the 0 that means All, can be sitting in storage, and All
          // here would walk every page of a table whose rows are the widest in the app.
          return parsed > 100 || parsed === ALL_ITEMS_PAGE_SIZE ? 100 : parsed;
        }
        return DEFAULT_ITEMS_PER_PAGE.retro;
      } else {
        const standardSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
        if (standardSaved && standardSaved !== 'unlimited') return parseInt(standardSaved);
        return DEFAULT_ITEMS_PER_PAGE[viewMode];
      }
    };

    return {
      hideMetadata: loadHideMetadata(),
      hideSmallFiles: loadHideSmallFiles(),
      hideLocalhost: storage.getItem(STORAGE_KEYS.HIDE_LOCALHOST) === 'true',
      hideUnknownGames: storage.getItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES) === 'true',
      hideEvicted: storage.getItem(STORAGE_KEYS.HIDE_EVICTED) === 'true',
      hitMissFilter: (storage.getItem(STORAGE_KEYS.HIT_MISS_FILTER) || 'all') as HitMissFilter,
      selectedService: storage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
      selectedClient: storage.getItem(STORAGE_KEYS.CLIENT_FILTER) || 'all',
      searchQuery: storage.getItem(STORAGE_KEYS.SEARCH_QUERY) || '',
      itemsPerPage: getItemsPerPage(savedViewMode),
      viewMode: savedViewMode,
      // Migrate removed "latest" (old Frequent First / buggy Newest) → chronological Newest
      sortOrder: (() => {
        const stored = storage.getItem(STORAGE_KEYS.SORT_ORDER);
        if (!stored || stored === 'latest') return 'recent';
        return stored as SortOrder;
      })(),
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
      bannerOnly: storage.getItem(STORAGE_KEYS.BANNER_ONLY) !== 'false',
      groupByGameRetro: storage.getItem(STORAGE_KEYS.GROUP_BY_GAME_RETRO) === 'true',
      groupByServiceRetro: storage.getItem(STORAGE_KEYS.GROUP_BY_SERVICE_RETRO) === 'true'
    };
  });

  // Keep the debounced retro search trailing the live input by 300ms.
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearchQuery(settings.searchQuery), 300);
    return () => clearTimeout(timer);
  }, [settings.searchQuery]);

  // A dropdown entry can name a client group, which stands for several addresses, and the server
  // takes a comma-separated list. The group is expanded once here for both the page fetch below
  // and the retro view. A selection naming a group that has been deleted, or one whose members
  // have all been removed, falls through to the address branch and matches nothing, rather than
  // quietly dropping the filter.
  const serverClientFilter = useMemo(() => {
    if (settings.selectedClient === 'all') return 'all';
    const memberIps = findClientFilterGroup(settings.selectedClient, clientGroups)?.memberIps.join(
      ','
    );
    return memberIps || settings.selectedClient;
  }, [settings.selectedClient, clientGroups]);

  // One page of grouped rows, filtered, grouped, sorted and sliced in the database. The whole
  // table used to be fetched and all of that done here, so every reader held every row in memory.
  // Switched off while the retro table is showing: that table fetches its own page, and running
  // both meant every page turn, filter change and refresh grouped the whole table twice. The row
  // count the export button needs comes back from the table itself instead.
  const serverPage = useRetroDownloads({
    // Off while the retro table is showing, because that table fetches its own page. Mock mode is
    // the exception: its rows are generated on the client, so there is no second server round trip
    // to save, and the export reads the page's rows rather than the retro table's.
    enabled: activeTab === 'recent' && (settings.viewMode !== 'retro' || mockMode),
    page: currentPage,
    pageSize: settings.itemsPerPage,
    sort: settings.sortOrder,
    service: settings.selectedService,
    client: serverClientFilter,
    search: debouncedSearchQuery,
    hideLocalhost: settings.hideLocalhost,
    hideMetadata: settings.hideMetadata,
    hideSmallFiles: settings.hideSmallFiles,
    // The same two halves the browser used to OR together: the reader's own checkbox, and the
    // stored mode when it is hiding evicted rows for everyone. Sending the mode as well as the
    // checkbox is what refetches the page when someone changes it in Management, which is the
    // moment the rows on screen stop matching what the server would send.
    hideEvicted: settings.hideEvicted || evictedDataMode === 'hide',
    hideUnknown: settings.hideUnknownGames,
    // A download that is still running is the row a reader on a cache box is most likely watching,
    // so it is listed here as it downloads rather than appearing only once it finishes. The retro
    // table leaves it out: that one is a history view.
    includeActive: true,
    hitMiss: settings.hitMissFilter,
    // The grouped views key a bucket on the game identity alone, so one title seen under two
    // services stays one row. Always on: it is the grouping these views have always used.
    groupByGame: true,
    mergeAcrossServices: true,
    groupUnknownGames: settings.groupUnknownGames,
    groupByFrequency: settings.groupByFrequency,
    startTime: retroTimeParams.startTime,
    endTime: retroTimeParams.endTime,
    eventId: retroEventId
  });

  // Page size has one owner: `settings.itemsPerPage`, persisted to localStorage by the effect
  // further down. It used to be mirrored into a `pageSize` URL param by a pair of effects, each
  // depending on one side while reading both. With the retro cap below as a third writer they
  // never settled: the URL pushed the saved size back, the cap forced it down again, and the value
  // visibly flickered between the two.
  // Retro alone is kept mounted behind display:none once visited. It is the only one of the four
  // with anything to lose by unmounting: its own page fetch, its lazy chunk, and a canvas pass that
  // measures every column against the theme's font. The other three take their rows from this
  // component and are pure renderers, so keeping them mounted bought nothing and cost twice over -
  // a page turn re-rendered every card in all three to repaint one, and the two behind
  // display:none went on holding a page of rows apiece that nothing was reading.
  const retroEverMounted = useRef(settings.viewMode === 'retro');

  // The ref above is written from an effect, which runs after the render that switched the view and
  // does not schedule another one. Reading it alone therefore left retro empty until an unrelated
  // state change repainted the page.
  const showRetroView = retroEverMounted.current || settings.viewMode === 'retro';

  // Effect to save settings to localStorage
  useEffect(() => {
    storage.setItem(STORAGE_KEYS.HIDE_METADATA, settings.hideMetadata.toString());
    storage.setItem(STORAGE_KEYS.HIDE_SMALL_FILES, settings.hideSmallFiles.toString());
    storage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    storage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
    storage.setItem(STORAGE_KEYS.HIDE_EVICTED, settings.hideEvicted.toString());
    storage.setItem(STORAGE_KEYS.HIT_MISS_FILTER, settings.hitMissFilter);
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
    storage.setItem(STORAGE_KEYS.GROUP_BY_SERVICE_RETRO, settings.groupByServiceRetro.toString());
  }, [settings]);

  // Track previous view mode to detect changes
  const prevViewModeRef = useRef(settings.viewMode);

  // Effect to switch items per page when view mode changes
  useEffect(() => {
    if (prevViewModeRef.current !== settings.viewMode) {
      const prevMode = prevViewModeRef.current;
      const newMode = settings.viewMode;

      // Mark retro as ever-mounted for display:none pattern
      if (newMode === 'retro') retroEverMounted.current = true;

      // When switching AWAY from retro, save current retro value and restore previous non-retro value
      if (prevMode === 'retro' && newMode !== 'retro') {
        // Restore the previously saved non-retro itemsPerPage
        const restored = previousNonRetroItemsPerPage.current;
        prevViewModeRef.current = newMode;
        if (settings.itemsPerPage !== restored) {
          setSettings((prev) => ({ ...prev, itemsPerPage: restored }));
        }
        return;
      }

      prevViewModeRef.current = newMode;

      // Load the saved items per page for the new view mode
      let newItemsPerPage: number;
      if (newMode === 'retro') {
        // When switching TO retro: save current non-retro itemsPerPage, then cap retro
        previousNonRetroItemsPerPage.current = settings.itemsPerPage;

        const retroSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO);
        if (retroSaved && retroSaved !== 'unlimited') {
          const parsed = parseInt(retroSaved);
          // Cap at 100 when switching to retro. The 0 that means All is capped too: it is not one
          // of the sizes this table is offered, and it would walk every page of the widest rows in
          // the app.
          newItemsPerPage = parsed > 100 || parsed === ALL_ITEMS_PAGE_SIZE ? 100 : parsed;
        } else {
          newItemsPerPage = DEFAULT_ITEMS_PER_PAGE.retro;
        }
      } else {
        const standardSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
        if (standardSaved && standardSaved !== 'unlimited') {
          newItemsPerPage = parseInt(standardSaved);
        } else {
          newItemsPerPage = DEFAULT_ITEMS_PER_PAGE[newMode];
        }
      }

      // Only update if the items per page would actually change
      if (settings.itemsPerPage !== newItemsPerPage) {
        setSettings((prev) => ({ ...prev, itemsPerPage: newItemsPerPage }));
      }
    }
  }, [settings.viewMode, settings.itemsPerPage]);

  // Note: Downloads are now always fetched from the context - no need to manage mock data count here

  // Note: Filter changes are handled client-side via useMemo, no loading state needed.
  // Showing a loading overlay for instant client-side filtering causes unnecessary flicker.
  // See Checkbox.tsx for the pattern to follow when filtering data.

  // Both dropdowns are built from the distinct services and clients the server reports over the
  // whole visible table, not from the rows on screen. The retro view fetches its own page and the
  // other views page their own rows, so deriving either list from what is currently displayed
  // would leave the dropdown offering only the services the current page happens to contain.
  const availableServices = useMemo(() => {
    const services = new Set(serviceFilterOptions.map((option) => option.service.toLowerCase()));
    return Array.from(services).sort();
  }, [serviceFilterOptions]);

  const availableClients = useMemo(() => Array.from(new Set(clientIps)).sort(), [clientIps]);

  // Services that only ever cached files under a megabyte are demoted below the divider rather
  // than removed. The server reports the flag per raw service name, so two aliases of one service
  // are folded here and the group counts as large-file when either alias is.
  const filteredAvailableServices = useMemo(() => {
    const largeFileServices = new Set(
      serviceFilterOptions
        .filter((option) => option.hasLargeFiles)
        .map((option) => option.service.toLowerCase())
    );
    return availableServices.filter((service) => largeFileServices.has(service));
  }, [availableServices, serviceFilterOptions]);

  const serviceOptions = useMemo(() => {
    // Group raw service names by their folded display name (e.g. "xbox" and
    // "xboxlive" both fold to "Xbox") so the dropdown shows one entry per
    // displayed name instead of one per raw alias.
    const groups = new Map<string, { service: string; visible: boolean }>();
    availableServices.forEach((service) => {
      const key = getServiceFilterKey(service);
      const isVisible = filteredAvailableServices.includes(service);
      const existing = groups.get(key);
      if (!existing) {
        groups.set(key, { service, visible: isVisible });
      } else if (isVisible) {
        existing.visible = true;
      }
    });

    const visibleEntries = Array.from(groups.entries()).filter(([, g]) => g.visible);
    const hiddenEntries = Array.from(groups.entries()).filter(([, g]) => !g.visible);

    const baseOptions = [
      { value: 'all', label: t('downloads.tab.filters.allServices') },
      ...visibleEntries.map(([key, g]) => ({ value: key, label: formatServiceLabel(g.service) }))
    ];

    if (hiddenEntries.length > 0) {
      baseOptions.push(
        { value: 'divider', label: t('downloads.tab.filters.smallFilesOnly') },
        ...hiddenEntries.map(([key, g]) => ({ value: key, label: formatServiceLabel(g.service) }))
      );
    }

    return baseOptions;
  }, [filteredAvailableServices, availableServices, t]);

  const clientOptions = useMemo(
    () =>
      buildClientFilterOptions(
        availableClients,
        getGroupForIp,
        getHostnameForIp,
        t('downloads.tab.filters.allClients')
      ),
    [availableClients, getGroupForIp, getHostnameForIp, t]
  );

  // "All" is a page size the hook answers by walking the endpoint's own pages and accumulating
  // them, so the reader sees every row while no single request asks the server for more than the
  // 200 it serves. The retro table is left out of it for the reason 200 is already left out: its
  // rows carry banners, tooltips and a wide column set, and it caps at 100.
  const itemsPerPageOptions = useMemo(() => {
    const options = [
      { value: '20', label: '20' },
      { value: '50', label: '50' },
      { value: '100', label: '100' },
      { value: '200', label: '200' },
      { value: String(ALL_ITEMS_PAGE_SIZE), label: t('downloads.tab.filters.allItems') }
    ];
    if (settings.viewMode === 'retro') {
      return options.filter(
        (opt) => opt.value !== '200' && opt.value !== String(ALL_ITEMS_PAGE_SIZE)
      );
    }
    return options;
  }, [settings.viewMode, t]);

  // Handler for items-per-page changes - writes settings, which the effect below persists.
  const handleItemsPerPageChange = (value: string) => {
    setSettings((prev) => ({ ...prev, itemsPerPage: parseInt(value) }));
  };

  // One grouped row in the shape the card, compact and normal renderers accept.
  //
  // The server answers everything about the membership - whether every member is evicted, whether
  // any member resolved a real name, how many sessions and clients the group holds - because a
  // collapsed row carries one download and cannot answer any of it from that. The row ids are the
  // same strings the browser used to mint, so an expanded group stays expanded across a refetch.
  //
  // The title is built here rather than taken from the row: it is translated, and the server has no
  // locale. The first two branches mirror the two kinds of id the server sends. The third names a
  // Steam group by its app id: the server knows which app it is but no member resolved a title, so
  // the row carries the app id and says no member has a real name. A group whose title merely reads
  // like an app id is a resolved name and keeps it.
  const toDownloadGroup = useCallback(
    (row: RetroDownloadDto, downloads: Download[]): DownloadGroup => {
      let name: string;
      if (row.id === 'unknown-other') {
        name = t('downloads.tab.groups.unknownOther');
      } else if (row.id.startsWith('service-')) {
        const displayService = getServiceDisplayName(row.service);
        name =
          getServiceFilterKey(row.service) === 'epicgames'
            ? 'Epic Games'
            : t('downloads.tab.groups.serviceDownloads', {
                service: displayService.charAt(0).toUpperCase() + displayService.slice(1)
              });
      } else if (row.steamAppId && !row.hasRealGameName) {
        name = t('downloads.tab.groups.steamApp', { appId: row.steamAppId });
      } else {
        name = row.appName;
      }

      return {
        id: row.id,
        name,
        // The views give "metadata" to a zero-byte group, and a completed zero-byte session never
        // reaches this endpoint, so a row is either a game bucket or a content one.
        type: row.groupType === 'game' ? 'game' : 'content',
        service: row.service,
        downloads,
        // Every session in the group. `downloads` is the newest one alone until the reader opens
        // the group, so the event badges are counted from these ids rather than from that one row.
        downloadIds: row.downloadIds,
        totalBytes: row.totalBytes,
        totalDownloaded: row.totalBytes,
        cacheHitBytes: row.cacheHitBytes,
        cacheMissBytes: row.cacheMissBytes,
        clientsSet: new Set(row.clientIps),
        firstSeen: row.startTimeUtc,
        // The newest member's START time. The row also carries the group's latest END time, which
        // is what the retro table shows in its own column and is a different instant.
        lastSeen: row.lastStartTimeUtc,
        count: row.requestCount,
        isEvicted: row.isEvicted,
        isPartiallyEvicted: row.isPartiallyEvicted,
        hasRealGameName: row.hasRealGameName
      };
    },
    [t]
  );

  // A grouped row names its members but carries only the newest one, so the sessions of the group
  // the reader opens are fetched on their own. Collapsing clears them; a refetch of the page
  // refreshes them, so an open group's sessions stay as current as its header.
  const [expandedMembers, setExpandedMembers] = useState<{
    groupId: string;
    downloads: Download[];
  } | null>(null);

  useEffect(() => {
    if (expandedItem === null) {
      setExpandedMembers(null);
      return;
    }
    const row = serverPage.items.find((item) => item.id === expandedItem);
    // The open group is not on this page - a page change or a filter dropped it. Nothing renders
    // it, so there is nothing to fetch.
    if (!row) return;

    // Mock mode has no member rows to ask for: the generated page carries the group's newest
    // download and nothing behind it, so opening a group shows that one session.
    if (mockMode) {
      setExpandedMembers({
        groupId: row.id,
        downloads: row.primaryDownload ? [row.primaryDownload] : []
      });
      return;
    }

    const controller = new AbortController();
    ApiService.getDownloadsByIds(row.downloadIds, controller.signal)
      .then((downloads) => setExpandedMembers({ groupId: row.id, downloads }))
      .catch((err: unknown) => {
        // notifyError drops an aborted request itself, so switching pages mid-fetch is silent.
        notifyError(t('downloads.tab.errors.loadFailed'), err, {
          logLabel: '[DownloadsTab] Failed to load the sessions of the expanded group'
        });
      });
    return () => controller.abort();
  }, [expandedItem, mockMode, serverPage.items, notifyError, t]);

  // The page's rows, in the order the server returned them. No client-side sort and no slice is
  // left: the sort order, the frequency bucketing, the grouping and the page boundary are all
  // decided in the query, so what arrives is what renders.
  const itemsToDisplay = useMemo<DownloadGroup[]>(
    () =>
      serverPage.items.map((row) => {
        const members =
          expandedMembers && expandedMembers.groupId === row.id
            ? expandedMembers.downloads
            : row.primaryDownload
              ? [row.primaryDownload]
              : [];
        return toDownloadGroup(row, members);
      }),
    [serverPage.items, expandedMembers, toDownloadGroup]
  );

  // Floor of 1: an empty result would otherwise report 0 pages and put the pager out of range of
  // its own clamp.
  const totalPages = Math.max(1, serverPage.totalPages);

  // Wiping the logs, or any filter that shrinks the set, can leave the view on a page past the end.
  // The pager below only renders while totalPages > 1, so on the way down to a single page it
  // unmounts and takes with it the only control that could get back.
  //
  // Waiting for the response to echo the page that was asked for is what keeps a deep page working:
  // until then the total on hand still belongs to the previous request, and page 30 would be
  // clamped away before its own rows ever arrived.
  //
  // Retro is excluded, and that exclusion is the point rather than an oversight: it fetches its own
  // page under its own grouping, so its page count is a different number from this one, and it runs
  // the same clamp against it. Measuring it here would drag a legitimate deep retro page back.
  useEffect(() => {
    if (settings.viewMode === 'retro') return;
    if (serverPage.currentPage === currentPage && currentPage > totalPages) {
      setCurrentPage(totalPages);
    }
  }, [settings.viewMode, serverPage.currentPage, currentPage, totalPages]);

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
    settings.hideMetadata,
    settings.hideSmallFiles,
    settings.hideLocalhost,
    settings.hideUnknownGames,
    settings.hideEvicted,
    settings.hitMissFilter,
    settings.viewMode,
    settings.itemsPerPage,
    settings.groupByGameRetro,
    settings.groupByServiceRetro,
    // Both of these change how many rows the list has, so they belong here with the other
    // filters. Without them a toggle could collapse the list under the current page.
    settings.groupUnknownGames,
    settings.groupByFrequency,
    timeRange,
    customStartDate,
    customEndDate,
    selectedEventIds
  ]);

  // Click outside / Escape handler to close settings dropdown
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

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setSettingsOpened(false);
      }
    };

    if (settingsOpened) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
  }, [settingsOpened]);

  useEffect(() => {
    currentPageRef.current = currentPage;
  }, [currentPage]);

  // The rule the retro table already fades on: dim the rows while a fetch the reader asked for is
  // running, and leave a background refresh alone. Written straight to the DOM rather than through
  // state so the pagination bar above the rows does not repaint with every page turn.
  useEffect(() => {
    nonRetroContentRef.current?.classList.toggle(
      'page-fading',
      serverPage.isFetching && !serverPage.isLoading
    );
  }, [serverPage.isFetching, serverPage.isLoading]);

  // Every view keeps its previous rows on screen and fades while the next page is fetched, so the
  // page turns immediately and no artificial delay is paid before the request starts.
  const handlePageChange = useCallback((newPage: number) => {
    if (newPage === currentPageRef.current) return;

    // The open group's members belong to the page being left, and the row that carries them is not
    // on the next one. Collapsing here is also what keeps a newly mounted card from scrolling
    // itself into view under the reader on the rare page that repeats the same group id.
    setExpandedItem(null);
    currentPageRef.current = newPage;
    setCurrentPage(newPage);
  }, []);

  // The one reader left of the row-level route. The views hold a page at a time now, so the rows
  // an export needs are no longer on hand and are fetched when the button is pressed. They arrive
  // narrowed only by the time range and the tagged event, so the toolbar's own filters are applied
  // here - over rows, the way they were applied before the grouped page moved them into the query.
  const handleExport = async (format: 'json' | 'csv') => {
    setExportLoading(true);
    // A big table is hundreds of sequential requests. Leaving the page aborts what is left of them
    // rather than fetching rows into a page nobody is on, and writing the file there.
    const controller = new AbortController();
    exportAbort.current = controller;
    try {
      // Mock mode has no row-level route behind it, so the export is the sessions the page is
      // showing rather than a request for every row in the range.
      const rows = mockMode
        ? itemsToDisplay.flatMap((group) => group.downloads)
        : await ApiService.getDownloadRows(
            retroTimeParams.startTime,
            retroTimeParams.endTime,
            retroEventId,
            controller.signal
          );

      const selectedClientIps = serverClientFilter === 'all' ? null : serverClientFilter.split(',');
      const query = settings.searchQuery.toLowerCase().trim();
      // Unmapped Steam downloads have no resolved game name and their service is "steam", so none
      // of their fields carry the word "unknown". They display under the synthetic "Unknown/Other"
      // group, so that label is searchable too.
      const queryMatchesUnknownLabel =
        query !== '' && ('unknown/other'.startsWith(query) || 'other'.startsWith(query));

      const rowsForExport = rows.filter((d) => {
        if (settings.hideMetadata && d.totalBytes === 0) return false;
        if (settings.hideSmallFiles && d.totalBytes > 0 && d.totalBytes < 1048576) return false;
        if (settings.hideLocalhost && (d.clientIp === '127.0.0.1' || d.clientIp === '::1')) {
          return false;
        }
        if ((settings.hideEvicted || evictedDataMode === 'hide') && d.isEvicted) return false;
        if (settings.hideUnknownGames && isUnmappedSteam(d)) return false;
        if (settings.hitMissFilter === 'hit' && d.cacheHitPercent < 50) return false;
        if (settings.hitMissFilter === 'miss' && d.cacheHitPercent >= 50) return false;
        if (
          settings.selectedService !== 'all' &&
          getServiceFilterKey(d.service) !== settings.selectedService
        ) {
          return false;
        }
        if (selectedClientIps && !selectedClientIps.includes(d.clientIp)) return false;
        if (query) {
          return (
            (!!d.gameName && d.gameName.toLowerCase().includes(query)) ||
            d.service.toLowerCase().includes(query) ||
            d.clientIp.toLowerCase().includes(query) ||
            (!!d.depotId && String(d.depotId).includes(query)) ||
            (!!d.gameAppId && String(d.gameAppId).includes(query)) ||
            (queryMatchesUnknownLabel && isUnmappedSteam(d))
          );
        }
        return true;
      });

      const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5);
      const content =
        format === 'csv'
          ? convertDownloadsToCSV(rowsForExport, clock)
          : JSON.stringify(rowsForExport, null, 2);

      downloadTextFile(
        content,
        `lancache_downloads_${timestamp}.${format}`,
        format === 'csv' ? 'text/csv;charset=utf-8' : 'application/json'
      );
    } catch (error) {
      // Leaving the page aborted the walk. That is not a failure to report.
      if (controller.signal.aborted) return;
      notifyError(t('downloads.tab.errors.exportFailed'), error, { logLabel: 'Export failed' });
    } finally {
      setExportLoading(false);
    }
  };

  const handleClearImageCache = async () => {
    setImageCacheClearing(true);
    try {
      // The request only starts the re-fetch. Epic's URL refresh and every download now happen on
      // that background pass, and GameImagesUpdated bumps the version when it finishes, so this
      // handler has nothing left to wait for or to bump itself.
      await ApiService.clearImageCache();
    } catch (error) {
      notifyError(t('downloads.tab.errors.clearImageCacheFailed'), error, {
        logLabel: '[handleClearImageCache] Failed to clear image cache'
      });
    } finally {
      setImageCacheClearing(false);
    }
  };

  // Stable across renders. The three views below are memoized and every other prop they take
  // already is, so a handler rebuilt on each render was on its own enough to re-render all three
  // of them - the two behind display:none included - every time anything on this page changed.
  const handleItemClick = useCallback((id: string) => {
    setExpandedItem((current) => (current === id ? null : id));
  }, []);

  // Loading state with skeleton loader. The page fetch only reports loading on its first run, so a
  // background refresh replaces the rows underneath without the skeleton reappearing.
  if (loading || serverPage.isLoading) {
    return (
      <div className="space-y-4 animate-fade-in" role="status" aria-live="polite" aria-busy="true">
        <span className="sr-only">{t('common.loading')}</span>
        {/* Skeleton Controls */}
        <Card padding="sm">
          <div className="flex flex-col gap-3">
            <div className="flex flex-wrap gap-2">
              <div className="h-10 rounded w-full sm:w-40 skeleton-shimmer" />
              <div className="h-10 rounded flex-1 sm:flex-initial sm:w-32 min-w-0 skeleton-shimmer" />
              <div className="h-10 rounded flex-1 sm:flex-initial sm:w-40 min-w-0 skeleton-shimmer" />
            </div>
          </div>
        </Card>

        {/* Skeleton Content */}
        <div className="space-y-2">
          {[1, 2, 3, 4, 5].map((i) => (
            <div key={i} className="h-16 bg-[var(--theme-bg-secondary)] rounded" aria-hidden="true">
              <div className="p-3 flex items-center gap-3">
                <div className="h-6 w-16 rounded skeleton-shimmer" />
                <div className="h-4 rounded flex-1 max-w-[200px] skeleton-shimmer" />
                <div className="ml-auto flex gap-3">
                  <div className="h-4 w-20 rounded skeleton-shimmer" />
                  <div className="h-4 w-12 rounded skeleton-shimmer" />
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  // The count the server answers with is the count AFTER the toolbar's filters, so an empty result
  // on its own cannot tell "nothing recorded yet" from "the filters hid everything". Only the first
  // gets the notice below, because that notice replaces the whole toolbar: shown for the second it
  // would take away the very controls needed to undo the filter. The stored eviction mode is
  // deliberately not counted - it defaults to hiding evicted rows, so counting it would replace the
  // first-run notice with an empty table on a fresh install.
  // Whichever fetch is running: the page's own, or the retro table's when that one is showing.
  const visibleTotalItems = settings.viewMode === 'retro' ? retroTotalItems : serverPage.totalItems;

  const hasNarrowingFilter =
    settings.hideMetadata ||
    settings.hideSmallFiles ||
    settings.hideLocalhost ||
    settings.hideUnknownGames ||
    settings.hideEvicted ||
    settings.hitMissFilter !== 'all' ||
    settings.selectedService !== 'all' ||
    settings.selectedClient !== 'all' ||
    settings.searchQuery.trim() !== '';

  // Empty state (only show for Recent tab when no data). A failed fetch takes the error branch
  // instead, so a request that never returned rows is not reported as an empty table.
  //
  // The retro table is left out because it is the one view that fetches for itself. Returning here
  // renders instead of everything below, so it would unmount the table, abort its request and drop
  // its refresh subscription, and the first download to land would then never clear the notice.
  // It draws the same message inside its own frame and keeps fetching, so the rows appear by
  // themselves when they arrive.
  if (
    visibleTotalItems === 0 &&
    !hasNarrowingFilter &&
    activeTab === 'recent' &&
    settings.viewMode !== 'retro'
  ) {
    return (
      <div className="space-y-4 animate-fade-in">
        <DownloadsHeader activeTab={activeTab} onTabChange={setActiveTab} />
        {serverPage.error ? (
          <Alert color="red">{t('downloads.tab.errors.loadFailed')}</Alert>
        ) : (
          <Alert color="blue" icon={<Database className="w-5 h-5" />}>
            {t('downloads.tab.emptyRecorded')}
          </Alert>
        )}
      </div>
    );
  }

  // One toolbar menu for both layouts, so a phone gets the same actions a wide screen does.
  const toolbarActions = (
    <SectionActionsMenu label={t('common.moreActions')}>
      {(close) => (
        <>
          <ActionMenuItem
            icon={<DownloadIcon className="w-3.5 h-3.5" />}
            disabled={exportLoading || visibleTotalItems === 0}
            onClick={() => {
              handleExport('json');
              close();
            }}
          >
            {t('downloads.tab.export.json')}
          </ActionMenuItem>
          <ActionMenuItem
            icon={<DownloadIcon className="w-3.5 h-3.5" />}
            disabled={exportLoading || visibleTotalItems === 0}
            onClick={() => {
              handleExport('csv');
              close();
            }}
          >
            {t('downloads.tab.export.csv')}
          </ActionMenuItem>

          {settings.viewMode === 'retro' && isDesktop && (
            <ActionMenuItem
              icon={<Maximize2 className="w-3.5 h-3.5" />}
              onClick={() => {
                retroViewRef.current?.resetWidths();
                close();
              }}
            >
              {t('downloads.tab.tooltips.fitColumns')}
            </ActionMenuItem>
          )}

          {!isGuest && (
            <ActionMenuItem
              icon={
                <RefreshCw className={`w-3.5 h-3.5${imageCacheClearing ? ' animate-spin' : ''}`} />
              }
              disabled={imageCacheClearing}
              onClick={() => {
                handleClearImageCache();
                close();
              }}
            >
              {t('downloads.tab.tooltips.refreshImages')}
            </ActionMenuItem>
          )}

          <div data-settings-button="true">
            <ActionMenuItem
              icon={<Settings className="w-3.5 h-3.5" />}
              onClick={() => {
                setSettingsOpened(!settingsOpened);
                close();
              }}
            >
              {t('downloads.tab.tooltips.settings')}
            </ActionMenuItem>
          </div>
        </>
      )}
    </SectionActionsMenu>
  );

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
          <Card padding="sm" className="transition duration-300">
            <div className="flex flex-col gap-3">
              {/* Search input + the toolbar actions menu on phones */}
              <div className="downloads-search-row">
                <div className="search-input-wrapper">
                  <SearchInput
                    value={settings.searchQuery}
                    onChange={(e) => setSettings({ ...settings, searchQuery: e.target.value })}
                    placeholder={t('downloads.tab.searchPlaceholder')}
                    onClear={() => setSettings({ ...settings, searchQuery: '' })}
                  />
                </div>
                {/* A filter, a sort or a page size is a server round trip that rebuilds the whole
                    grouped list, and the rows already on screen stay there while it runs. */}
                {(serverPage.isFetching || retroFetching) && <LoadingSpinner size="xs" inline />}
                {/* Same menu the wide layout gets. A phone-only settings gear put a control
                    here that exists nowhere else in the app, and hid Export and Refresh
                    Images from phones entirely. */}
                <div data-settings-button="true" className="inline-flex sm:hidden flex-shrink-0">
                  {/* The menu closes on the click that starts an export, so this is the only
                      thing on screen that says the walk behind it is still running. */}
                  {exportLoading && <LoadingSpinner size="xs" inline />}
                  {toolbarActions}
                </div>
              </div>

              {/* Dropdowns and View Controls */}
              <div className="flex flex-col sm:flex-row sm:flex-wrap gap-2 items-start sm:items-center justify-between w-full">
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

                {/* Mobile: Second row with items per page and sort */}
                <div className="flex sm:hidden gap-2 w-full items-center">
                  <EnhancedDropdown
                    options={itemsPerPageOptions}
                    value={settings.itemsPerPage.toString()}
                    onChange={handleItemsPerPageChange}
                    prefix={t('downloads.tab.filters.showPrefix')}
                    className="flex-1 min-w-0"
                  />
                  <EnhancedDropdown
                    options={[
                      { value: 'recent', label: t('downloads.tab.sort.recent') },
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
                </div>

                {/* Mobile: Third row with view mode and hit/miss filter */}
                <div className="downloads-view-row sm:hidden">
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
                        tooltip: t('downloads.tab.view.card')
                      },
                      {
                        value: 'normal',
                        icon: <Grid3x3 />,
                        tooltip: t('downloads.tab.view.normal')
                      },
                      { value: 'retro', icon: <Table />, tooltip: t('downloads.tab.view.retro') }
                    ]}
                    value={settings.viewMode}
                    onChange={(value) => setSettings({ ...settings, viewMode: value as ViewMode })}
                    size="md"
                  />

                  {/* Hit/Miss content filter */}
                  <SegmentedControl
                    options={[
                      {
                        value: 'all',
                        label: t('downloads.tab.filters.hitMissAll'),
                        tooltip: t('downloads.tab.filters.hitMissAllTooltip')
                      },
                      {
                        value: 'hit',
                        label: t('downloads.tab.filters.hitMissHit'),
                        tooltip: t('downloads.tab.filters.hitMissHitTooltip')
                      },
                      {
                        value: 'miss',
                        label: t('downloads.tab.filters.hitMissMiss'),
                        tooltip: t('downloads.tab.filters.hitMissMissTooltip')
                      }
                    ]}
                    value={settings.hitMissFilter}
                    onChange={(value) =>
                      setSettings({ ...settings, hitMissFilter: value as HitMissFilter })
                    }
                    size="md"
                  />
                </div>

                {/* Desktop: All controls in one row */}
                <div className="hidden sm:flex sm:flex-wrap gap-2 items-center">
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
                    value={settings.itemsPerPage.toString()}
                    onChange={handleItemsPerPageChange}
                    prefix={t('downloads.tab.filters.showPrefix')}
                    className="w-28"
                  />

                  <EnhancedDropdown
                    options={[
                      { value: 'recent', label: t('downloads.tab.sort.recent') },
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
                    className="downloads-sort-trigger"
                  />
                </div>

                {/* Desktop view controls. The auto left margin keeps this cluster on the right
                    edge even at widths where it drops below the dropdowns: a wrapped flex line
                    justifies on its own, so justify-between alone would park it on the left. */}
                <div className="hidden sm:flex sm:flex-wrap gap-2 justify-end w-auto flex-shrink-0 ml-auto">
                  {/* View Mode Toggle */}
                  <SegmentedControl
                    options={[
                      {
                        value: 'compact',
                        label: t('downloads.tab.view.compact'),
                        icon: <List />,
                        tooltip: t('downloads.tab.view.compact')
                      },
                      {
                        value: 'card',
                        label: t('downloads.tab.view.card'),
                        icon: <LayoutGrid />,
                        tooltip: t('downloads.tab.view.card')
                      },
                      {
                        value: 'normal',
                        label: t('downloads.tab.view.normal'),
                        icon: <Grid3x3 />,
                        tooltip: t('downloads.tab.view.normal')
                      },
                      {
                        value: 'retro',
                        label: t('downloads.tab.view.retro'),
                        icon: <Table />,
                        tooltip: t('downloads.tab.view.retro')
                      }
                    ]}
                    value={settings.viewMode}
                    onChange={(value) => setSettings({ ...settings, viewMode: value as ViewMode })}
                    size="md"
                    showLabels={wideLabels}
                  />

                  {/* Hit/Miss content filter */}
                  <SegmentedControl
                    options={[
                      {
                        value: 'all',
                        label: t('downloads.tab.filters.hitMissAll'),
                        tooltip: t('downloads.tab.filters.hitMissAllTooltip')
                      },
                      {
                        value: 'hit',
                        label: t('downloads.tab.filters.hitMissHit'),
                        tooltip: t('downloads.tab.filters.hitMissHitTooltip')
                      },
                      {
                        value: 'miss',
                        label: t('downloads.tab.filters.hitMissMiss'),
                        tooltip: t('downloads.tab.filters.hitMissMissTooltip')
                      }
                    ]}
                    value={settings.hitMissFilter}
                    onChange={(value) =>
                      setSettings({ ...settings, hitMissFilter: value as HitMissFilter })
                    }
                    size="md"
                  />

                  {/* Toolbar actions. The settings marker sits on the trigger as well as on the
                      settings item: the panel's outside-click handler matches that selector, so
                      without it on the trigger, opening this menu would shut the panel and the
                      item could never toggle it back off. */}
                  <div data-settings-button="true" className="inline-flex">
                    {exportLoading && <LoadingSpinner size="xs" inline />}
                    {toolbarActions}
                  </div>
                </div>
              </div>
            </div>

            <div
              ref={settingsRef}
              id="downloads-settings-panel"
              className={`downloads-settings-panel${settingsOpened ? ' is-open' : ''}`}
            >
              <div className="downloads-settings-panel-inner">
                <div className="downloads-settings-panel-content space-y-4">
                  {/* Quick Presets - Mobile-friendly segmented control */}
                  <div>
                    <div className="caps-label mb-2">{t('downloads.tab.presets.title')}</div>
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
                      <div className="caps-label mb-2">{t('downloads.tab.sections.filters')}</div>
                      <Checkbox
                        checked={settings.hideMetadata}
                        onChange={(e) =>
                          setSettings({ ...settings, hideMetadata: e.target.checked })
                        }
                        label={t('downloads.tab.filters.hideMetadata')}
                      />
                      <Checkbox
                        checked={settings.hideSmallFiles}
                        onChange={(e) =>
                          setSettings({ ...settings, hideSmallFiles: e.target.checked })
                        }
                        label={t('downloads.tab.filters.hideSmallFiles')}
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
                      <div className="caps-label mb-2">{t('downloads.tab.sections.display')}</div>
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
                      {settings.viewMode === 'retro' && (
                        <Checkbox
                          checked={settings.groupByServiceRetro}
                          onChange={(e) =>
                            setSettings({ ...settings, groupByServiceRetro: e.target.checked })
                          }
                          label={t('downloads.tab.display.groupByServiceRetro')}
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
                            {t('downloads.tab.display.cardSize')}
                          </span>
                          {/* Large is dropped below 640px: the grid there fits two cards or one, so
                              large and medium both draw one per row and the third segment picks a
                              size that does not exist. A saved large shows as medium until the
                              window is wide enough to tell them apart, and the setting itself is
                              left alone so the choice survives the trip back to a desktop. */}
                          <SegmentedControl
                            options={[
                              {
                                value: 'small',
                                label: t('downloads.tab.display.cardSizeOptions.small')
                              },
                              {
                                value: 'medium',
                                label: t('downloads.tab.display.cardSizeOptions.medium')
                              },
                              ...(cardSizesFitSideBySide
                                ? [
                                    {
                                      value: 'large',
                                      label: t('downloads.tab.display.cardSizeOptions.large')
                                    }
                                  ]
                                : [])
                            ]}
                            value={
                              cardSizesFitSideBySide || settings.cardSize !== 'large'
                                ? settings.cardSize
                                : 'medium'
                            }
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
                          label={t('downloads.tab.display.bannerOnly')}
                        />
                      )}
                    </div>

                    {/* Behavior Column - card view with banner-only exposes no behavior options, so drop the empty column */}
                    {(settings.viewMode !== 'card' || !settings.bannerOnly) && (
                      <div className="space-y-1">
                        <div className="caps-label mb-2">
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
                            label={t('downloads.tab.behavior.showCacheHitBar')}
                          />
                        )}
                        {(settings.viewMode === 'normal' ||
                          (settings.viewMode === 'card' && !settings.bannerOnly)) && (
                          <Checkbox
                            checked={settings.showEventBadges}
                            onChange={(e) =>
                              setSettings({ ...settings, showEventBadges: e.target.checked })
                            }
                            label={t('downloads.tab.behavior.showEventBadges')}
                          />
                        )}
                        {settings.viewMode === 'retro' && (
                          <Checkbox
                            checked={settings.showTimestamps}
                            onChange={(e) =>
                              setSettings({ ...settings, showTimestamps: e.target.checked })
                            }
                            label={t('downloads.tab.behavior.showTimestamps')}
                          />
                        )}
                        {settings.viewMode === 'retro' && (
                          <Checkbox
                            checked={settings.showBannerColumn}
                            onChange={(e) =>
                              setSettings({ ...settings, showBannerColumn: e.target.checked })
                            }
                            label={t('downloads.tab.behavior.showBannerColumn')}
                          />
                        )}
                      </div>
                    )}
                  </div>
                </div>
              </div>
            </div>
          </Card>

          {/* Help message for empty time ranges */}
          {visibleTotalItems === 0 && timeRange !== 'live' && (
            <Alert color="yellow">
              <div className="flex flex-col gap-2">
                <div className="font-medium">{t('downloads.tab.emptyRange.title')}</div>
                <div className="text-sm opacity-90">
                  {t('downloads.tab.emptyRange.description')}
                </div>
              </div>
            </Alert>
          )}

          {/* A request that failed leaves the rows from the last one that worked on screen, and
              without this there is nothing to tell those apart from a fresh answer. It matters most
              under "All", where the walk can fail on its seventh request of twelve and the rows
              still showing are a page of fifty. The empty-table branch above returns before this,
              so only one of the two ever draws. */}
          {serverPage.error && <Alert color="red">{t('downloads.tab.errors.loadFailed')}</Alert>}

          {/* Sticky Pagination Controls (above content) - retro view manages its own pagination */}
          {settings.viewMode !== 'retro' && totalPages > 1 && (
            <div className="pagination-sticky">
              <div className="p-2 rounded-lg bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)]">
                <Pagination
                  currentPage={currentPage}
                  totalPages={totalPages}
                  totalItems={serverPage.totalItems}
                  totalDownloads={serverPage.totalDownloads}
                  itemsPerPage={settings.itemsPerPage}
                  onPageChange={handlePageChange}
                  itemLabel={t('ui.pagination.items')}
                  showCard={false}
                />
              </div>
            </div>
          )}

          {/* Downloads list */}
          {/* Retro stays mounted behind display:none like the other views, so
              switching back is instant (previous rows + background refetch)
              instead of a full remount that refetches from scratch. */}
          <div
            className="space-y-4"
            style={{ display: settings.viewMode === 'retro' ? 'block' : 'none' }}
          >
            {showRetroView && (
              <Suspense
                fallback={
                  <div className="py-4">
                    <LoadingState shape="table" rows={5} />
                  </div>
                }
              >
                <RetroView
                  ref={retroViewRef}
                  sortOrder={settings.sortOrder}
                  itemsPerPage={settings.itemsPerPage}
                  currentPage={currentPage}
                  onPageChange={handlePageChange}
                  showTimestamps={settings.showTimestamps}
                  showBannerColumn={settings.showBannerColumn}
                  aestheticMode={settings.aestheticMode}
                  showDatasourceLabels={showDatasourceLabels}
                  hasMultipleDatasources={hasMultipleDatasources}
                  groupByGame={settings.groupByGameRetro}
                  groupByService={settings.groupByServiceRetro}
                  detectionLookup={detectionLookup}
                  detectionByName={detectionByName}
                  detectionByService={detectionByService}
                  serverMode={settings.viewMode === 'retro'}
                  onTotalItemsChange={setRetroTotalItems}
                  onFetchingChange={setRetroFetching}
                  filterService={settings.selectedService}
                  filterClient={serverClientFilter}
                  filterSearch={debouncedSearchQuery}
                  filterHideLocalhost={settings.hideLocalhost}
                  filterHideMetadata={settings.hideMetadata}
                  filterHideSmallFiles={settings.hideSmallFiles}
                  filterHideEvicted={settings.hideEvicted || evictedDataMode === 'hide'}
                  filterHideUnknown={settings.hideUnknownGames}
                  filterHitMiss={settings.hitMissFilter}
                  filterStartTime={retroTimeParams.startTime}
                  filterEndTime={retroTimeParams.endTime}
                  filterEventId={retroEventId}
                />
              </Suspense>
            )}
          </div>

          <div
            className="relative overflow-x-hidden page-content-transition"
            ref={nonRetroContentRef}
            style={{ display: settings.viewMode === 'retro' ? 'none' : 'block' }}
          >
            {/* One view renders at a time. Their rows come from this component, so the one being
                switched to draws from the page already in hand rather than fetching again. */}
            {settings.viewMode === 'compact' && (
              <CompactView
                items={itemsToDisplay}
                expandedItem={expandedItem}
                onItemClick={handleItemClick}
                aestheticMode={settings.aestheticMode}
                groupByFrequency={settings.groupByFrequency}
                enableScrollIntoView={settings.enableScrollIntoView}
                showDatasourceLabels={showDatasourceLabels}
                hasMultipleDatasources={hasMultipleDatasources}
                detectionLookup={detectionLookup}
                detectionByName={detectionByName}
                detectionByService={detectionByService}
              />
            )}

            {settings.viewMode === 'card' && (
              <NormalView
                items={itemsToDisplay}
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

            {settings.viewMode === 'normal' && (
              <NormalView
                items={itemsToDisplay}
                expandedItem={expandedItem}
                onItemClick={handleItemClick}
                aestheticMode={settings.aestheticMode}
                fullHeightBanners={settings.fullHeightBanners}
                groupByFrequency={settings.groupByFrequency}
                enableScrollIntoView={settings.enableScrollIntoView}
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
        </>
      )}
    </div>
  );
};

export default DownloadsTab;
