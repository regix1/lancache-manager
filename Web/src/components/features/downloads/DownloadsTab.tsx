import React, { useState, useEffect, useMemo, useRef } from 'react';
import {
  Database,
  Settings,
  Download as DownloadIcon,
  Loader2,
  List,
  Grid3x3,
  Table,
  Search,
  X
} from 'lucide-react';
import { useDownloads } from '@contexts/DownloadsContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import preferencesService from '@services/preferences.service';
import { formatDateTime } from '@utils/formatters';
import { Alert } from '@components/ui/Alert';
import { Card } from '@components/ui/Card';
import { Checkbox } from '@components/ui/Checkbox';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ActionMenu, ActionMenuItem } from '@components/ui/ActionMenu';
import { Pagination } from '@components/ui/Pagination';

// Import view components
import CompactView from './CompactView';
import NormalView from './NormalView';
import RetroView from './RetroView';
import DownloadsHeader from './DownloadsHeader';
import ActiveDownloadsView from './ActiveDownloadsView';

import type { Download, DownloadGroup, Config } from '../../../types';

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
  VIEW_MODE: 'lancache_downloads_view_mode',
  SORT_ORDER: 'lancache_downloads_sort_order',
  AESTHETIC_MODE: 'lancache_downloads_aesthetic_mode',
  FULL_HEIGHT_BANNERS: 'lancache_downloads_full_height_banners',
  ENABLE_SCROLL_INTO_VIEW: 'lancache_downloads_scroll_into_view',
  GROUP_UNKNOWN_GAMES: 'lancache_downloads_group_unknown'
};

// Default items per page for each view mode
const DEFAULT_ITEMS_PER_PAGE = {
  compact: 50,
  normal: 50,
  retro: 100
};

// View modes
type ViewMode = 'compact' | 'normal' | 'retro';

// Sort order type
type SortOrder = 'latest' | 'oldest' | 'largest' | 'smallest' | 'service' | 'efficiency' | 'efficiency-low' | 'sessions' | 'alphabetical';

// Preset type
type PresetType = 'pretty' | 'minimal' | 'showAll' | 'default' | 'custom';

// Preset configurations
const PRESETS = {
  pretty: {
    showZeroBytes: false,
    showSmallFiles: false,
    hideLocalhost: true,
    hideUnknownGames: true,
    groupUnknownGames: false,
    aestheticMode: false,
    fullHeightBanners: true,
    groupByFrequency: false,
    enableScrollIntoView: true
  },
  minimal: {
    showZeroBytes: false,
    showSmallFiles: false,
    hideLocalhost: true,
    hideUnknownGames: true,
    groupUnknownGames: false,
    aestheticMode: true,
    fullHeightBanners: false,
    groupByFrequency: true,
    enableScrollIntoView: true
  },
  showAll: {
    showZeroBytes: true,
    showSmallFiles: true,
    hideLocalhost: false,
    hideUnknownGames: false,
    groupUnknownGames: true,
    aestheticMode: false,
    fullHeightBanners: false,
    groupByFrequency: true,
    enableScrollIntoView: true
  },
  default: {
    showZeroBytes: false,
    showSmallFiles: true,
    hideLocalhost: false,
    hideUnknownGames: false,
    groupUnknownGames: false,
    aestheticMode: false,
    fullHeightBanners: false,
    groupByFrequency: true,
    enableScrollIntoView: true
  }
};

// Function to detect current preset
const detectActivePreset = (settings: {
  showZeroBytes: boolean;
  showSmallFiles: boolean;
  hideLocalhost: boolean;
  hideUnknownGames: boolean;
  groupUnknownGames: boolean;
  aestheticMode: boolean;
  fullHeightBanners: boolean;
  groupByFrequency: boolean;
  enableScrollIntoView: boolean;
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
      settings.enableScrollIntoView === presetConfig.enableScrollIntoView;

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
  const { latestDownloads = [], loading } = useDownloads();
  const { timeRange } = useTimeFilter();

  // Active/Recent tab state
  const [activeTab, setActiveTab] = useState<'active' | 'recent'>('recent');

  // Datasource display state
  const [config, setConfig] = useState<Config | null>(null);
  const [showDatasourceLabels, setShowDatasourceLabels] = useState(true);

  // Fetch config and preferences for datasource display
  useEffect(() => {
    const loadDatasourceSettings = async () => {
      try {
        const [configData, prefs] = await Promise.all([
          ApiService.getConfig(),
          preferencesService.getPreferences()
        ]);
        setConfig(configData);
        setShowDatasourceLabels(prefs.showDatasourceLabels ?? true);
      } catch (err) {
        console.error('Failed to load datasource settings:', err);
      }
    };
    loadDatasourceSettings();

    // Listen for preference changes
    const handlePreferenceChange = (event: CustomEvent<{ key: string; value: unknown }>) => {
      if (event.detail.key === 'showDatasourceLabels') {
        setShowDatasourceLabels(event.detail.value as boolean);
      }
    };
    window.addEventListener('preference-changed', handlePreferenceChange as EventListener);

    return () => {
      window.removeEventListener('preference-changed', handlePreferenceChange as EventListener);
    };
  }, []);

  // Compute whether to show datasource labels (show if any datasources are configured)
  const hasMultipleDatasources = (config?.dataSources?.length ?? 0) >= 1;

  // State management
  const [expandedItem, setExpandedItem] = useState<string | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [filterLoading, setFilterLoading] = useState(false);
  const [exportLoading, setExportLoading] = useState(false);
  const [showExportOptions, setShowExportOptions] = useState(false);
  const [currentPage, setCurrentPage] = useState(1);
  // Retro view manages its own pagination since it groups by depot
  const [retroTotalPages, setRetroTotalPages] = useState(1);
  const [retroTotalItems, setRetroTotalItems] = useState(0);
  const contentRef = useRef<HTMLDivElement>(null);
  const settingsRef = useRef<HTMLDivElement>(null);

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
      groupUnknownGames: storage.getItem(STORAGE_KEYS.GROUP_UNKNOWN_GAMES) === 'true'
    };
  });

  // Effect to save settings to localStorage
  useEffect(() => {
    storage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    storage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
    storage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    storage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
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
  }, [settings]);

  // Track previous view mode to detect changes
  const prevViewModeRef = useRef(settings.viewMode);

  // Effect to switch items per page when view mode changes
  useEffect(() => {
    if (prevViewModeRef.current !== settings.viewMode) {
      const newMode = settings.viewMode;
      prevViewModeRef.current = newMode;

      // Load the saved items per page for the new view mode
      let newItemsPerPage: number | 'unlimited';
      if (newMode === 'retro') {
        const retroSaved = storage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO);
        if (retroSaved === 'unlimited') {
          newItemsPerPage = 'unlimited';
        } else if (retroSaved) {
          newItemsPerPage = parseInt(retroSaved);
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
        setSettings(prev => ({ ...prev, itemsPerPage: newItemsPerPage }));
      }
    }
  }, [settings.viewMode]);

  // Note: Downloads are now always fetched from the context - no need to manage mock data count here

  // Track filter changes and show loading state
  useEffect(() => {
    if (!loading && latestDownloads.length > 0) {
      setFilterLoading(true);

      const timer = setTimeout(() => {
        setFilterLoading(false);
      }, 300);

      return () => clearTimeout(timer);
    }
  }, [
    settings.selectedService,
    settings.selectedClient,
    settings.searchQuery,
    settings.sortOrder,
    settings.showZeroBytes,
    settings.showSmallFiles,
    settings.hideLocalhost,
    settings.hideUnknownGames,
    settings.viewMode
  ]);

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
      { value: 'all', label: 'All Services' },
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
        { value: 'divider', label: 'Small Files Only' },
        ...hiddenServices.map((service) => ({
          value: service,
          label: `${service.charAt(0).toUpperCase() + service.slice(1)}`
        }))
      );
    }

    return baseOptions;
  }, [filteredAvailableServices, availableServices, latestDownloads]);

  const clientOptions = useMemo(
    () => [
      { value: 'all', label: 'All Clients' },
      ...availableClients.map((client) => ({
        value: client,
        label: client
      }))
    ],
    [availableClients]
  );

  const itemsPerPageOptions = useMemo(
    () => [
      { value: '20', label: '20' },
      { value: '50', label: '50' },
      { value: '100', label: '100' },
      { value: '200', label: '200' },
      { value: 'unlimited', label: 'All' }
    ],
    []
  );

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

    if (settings.hideUnknownGames) {
      filtered = filtered.filter((d) => {
        // Always show active downloads, even if they're unknown (they may not be mapped yet)
        if (d.isActive) {
          return true;
        }

        // Non-Steam services (like wsus, epic, etc.) don't use game names, so don't filter them
        const serviceLower = d.service.toLowerCase();
        if (serviceLower !== 'steam') {
          return true; // Keep all non-Steam services
        }

        // For Steam downloads, check the game name
        const rawName = typeof d.gameName === 'string' ? d.gameName : '';
        const trimmedName = rawName.trim();
        const gameNameLower = trimmedName.toLowerCase();

        if (!trimmedName) {
          return false; // Hide Steam downloads without a game name
        }

        // Hide "Unknown Steam Game" or any variation with "unknown" in the name
        if (gameNameLower.includes('unknown')) {
          return false;
        }

        // Hide unmapped Steam apps (e.g., "Steam App 12345")
        if (/^steam app \d+$/i.test(trimmedName)) {
          return false;
        }

        return true;
      });
    }

    if (settings.selectedService !== 'all') {
      filtered = filtered.filter((d) => d.service.toLowerCase() === settings.selectedService);
    }

    if (settings.selectedClient !== 'all') {
      filtered = filtered.filter((d) => d.clientIp === settings.selectedClient);
    }

    // Apply search filter
    if (settings.searchQuery.trim()) {
      const query = settings.searchQuery.toLowerCase().trim();
      filtered = filtered.filter((d) =>
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
    settings.hideUnknownGames,
    settings.selectedService,
    settings.selectedClient,
    settings.searchQuery
  ]);

  // Removed serviceFilteredDownloads - now using latestDownloads.length directly for total count

  // Grouping logic for different view modes
  const createGroups = (
    downloads: Download[],
    groupUnknown = false
  ): { groups: DownloadGroup[]; individuals: Download[] } => {
    const groups: Record<string, DownloadGroup> = {};
    const individuals: Download[] = [];

    downloads.forEach((download) => {
      let groupKey: string;
      let groupName: string;
      let groupType: 'game' | 'metadata' | 'content';

      // Check if this is an unknown game
      const isUnknownGame =
        download.service.toLowerCase() === 'steam' &&
        (!download.gameName ||
          download.gameName.trim() === '' ||
          download.gameName === 'Unknown Steam Game' ||
          download.gameName.toLowerCase().includes('unknown') ||
          download.gameName.match(/^Steam App \d+$/));

      if (
        download.gameName &&
        download.gameName !== 'Unknown Steam Game' &&
        !download.gameName.match(/^Steam App \d+$/)
      ) {
        groupKey = `game-${download.gameName}`;
        groupName = download.gameName;
        groupType = 'game';
      } else if (groupUnknown && isUnknownGame) {
        // Group all unknown games together when the setting is enabled
        groupKey = 'unknown-steam-games';
        groupName = 'Unknown Games';
        groupType = 'content';
      } else if (download.gameName && download.gameName.match(/^Steam App \d+$/)) {
        groupKey = 'unmapped-steam-apps';
        groupName = 'Unmapped Steam Apps';
        groupType = 'content';
      } else if (download.service.toLowerCase() !== 'steam') {
        groupKey = `service-${download.service.toLowerCase()}`;
        groupName = `${download.service} downloads`;
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
  };

  const normalViewItems = useMemo((): (Download | DownloadGroup)[] => {
    if (settings.viewMode !== 'normal') return [];

    const { groups, individuals } = createGroups(filteredDownloads, settings.groupUnknownGames);

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
    settings.viewMode,
    settings.groupByFrequency,
    settings.hideUnknownGames,
    settings.groupUnknownGames
  ]);

  const compactViewItems = useMemo((): (Download | DownloadGroup)[] => {
    if (settings.viewMode !== 'compact') return [];

    const { groups, individuals } = createGroups(filteredDownloads, settings.groupUnknownGames);

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
    settings.viewMode,
    settings.groupByFrequency,
    settings.hideUnknownGames,
    settings.groupUnknownGames
  ]);

  const allItemsSorted = useMemo(() => {
    let items =
      settings.viewMode === 'normal'
        ? normalViewItems
        : settings.viewMode === 'compact'
          ? compactViewItems
          : filteredDownloads;

    // Define the sort function
    const sortFn = (a: Download | DownloadGroup, b: Download | DownloadGroup) => {
      switch (settings.sortOrder) {
        case 'oldest':
          const aTime =
            'downloads' in a
              ? Math.min(...a.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(a.startTimeUtc).getTime();
          const bTime =
            'downloads' in b
              ? Math.min(...b.downloads.map((d) => new Date(d.startTimeUtc).getTime()))
              : new Date(b.startTimeUtc).getTime();
          return aTime - bTime;
        case 'largest':
          const aBytes = 'downloads' in a ? a.totalBytes : a.totalBytes || 0;
          const bBytes = 'downloads' in b ? b.totalBytes : b.totalBytes || 0;
          return bBytes - aBytes;
        case 'smallest':
          const aBytesSmall = 'downloads' in a ? a.totalBytes : a.totalBytes || 0;
          const bBytesSmall = 'downloads' in b ? b.totalBytes : b.totalBytes || 0;
          return aBytesSmall - bBytesSmall;
        case 'service':
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
        case 'efficiency':
          // Sort by cache hit percentage (highest first)
          const aEfficiency =
            'downloads' in a
              ? a.totalBytes > 0 ? (a.cacheHitBytes / a.totalBytes) * 100 : 0
              : (a.totalBytes || 0) > 0 ? ((a.cacheHitBytes || 0) / (a.totalBytes || 1)) * 100 : 0;
          const bEfficiency =
            'downloads' in b
              ? b.totalBytes > 0 ? (b.cacheHitBytes / b.totalBytes) * 100 : 0
              : (b.totalBytes || 0) > 0 ? ((b.cacheHitBytes || 0) / (b.totalBytes || 1)) * 100 : 0;
          return bEfficiency - aEfficiency;
        case 'efficiency-low':
          // Sort by cache hit percentage (lowest first)
          const aEffLow =
            'downloads' in a
              ? a.totalBytes > 0 ? (a.cacheHitBytes / a.totalBytes) * 100 : 0
              : (a.totalBytes || 0) > 0 ? ((a.cacheHitBytes || 0) / (a.totalBytes || 1)) * 100 : 0;
          const bEffLow =
            'downloads' in b
              ? b.totalBytes > 0 ? (b.cacheHitBytes / b.totalBytes) * 100 : 0
              : (b.totalBytes || 0) > 0 ? ((b.cacheHitBytes || 0) / (b.totalBytes || 1)) * 100 : 0;
          return aEffLow - bEffLow;
        case 'sessions':
          // Sort by number of download sessions (most first)
          const aSessions = 'downloads' in a ? a.downloads.length : 1;
          const bSessions = 'downloads' in b ? b.downloads.length : 1;
          return bSessions - aSessions;
        case 'alphabetical':
          // Sort by name alphabetically
          const aName = 'downloads' in a ? a.name : (a.gameName || a.service);
          const bName = 'downloads' in b ? b.name : (b.gameName || b.service);
          return aName.localeCompare(bName);
        case 'latest':
        default:
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
    };

    // Apply sorting
    if (settings.viewMode === 'normal' || settings.viewMode === 'compact') {
      const mixedItems = [...items] as (Download | DownloadGroup)[];

      // When sorting by service, alphabetical, efficiency, or sessions - sort all items together without frequency grouping
      const skipFrequencyGrouping = ['service', 'alphabetical', 'efficiency', 'efficiency-low', 'sessions'].includes(settings.sortOrder);
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

    // Apply pagination based on settings.itemsPerPage
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
      if (contentRef.current) {
        contentRef.current.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    }
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

  // Handle page changes with smooth scroll
  const handlePageChange = (newPage: number) => {
    if (newPage === currentPage) return;

    setCurrentPage(newPage);
    if (contentRef.current) {
      contentRef.current.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  };

  // Callback for retro view to report its pagination info
  const handleRetroTotalPagesChange = React.useCallback((totalPages: number, totalItems: number) => {
    setRetroTotalPages(totalPages);
    setRetroTotalItems(totalItems);
  }, []);

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
          settings.viewMode === 'normal' || settings.viewMode === 'compact'
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
          No downloads recorded yet. Downloads will appear here as clients request content.
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
          {/* Mobile view controls at top */}
          <div className="flex sm:hidden items-center justify-between">
            <span className="text-sm font-medium text-themed-primary">Downloads</span>
            <button
              onClick={() => setSettingsOpened(!settingsOpened)}
              className="p-1.5 rounded hover:bg-themed-hover transition-colors"
              title="Settings"
              data-settings-button="true"
            >
              <Settings size={18} />
            </button>
          </div>

          {/* Search Input */}
          <div className="relative w-full sm:max-w-xs">
            <Search
              size={16}
              className="absolute left-3 top-1/2 -translate-y-1/2 text-[var(--theme-text-muted)]"
            />
            <input
              type="text"
              value={settings.searchQuery}
              onChange={(e) => setSettings({ ...settings, searchQuery: e.target.value })}
              placeholder="Search games, clients, depots..."
              className="w-full pl-9 pr-8 py-2 text-sm rounded-lg border bg-[var(--theme-bg-primary)] text-[var(--theme-text-primary)] placeholder:text-[var(--theme-text-muted)] focus:outline-none focus:ring-2 focus:ring-[var(--theme-primary)]/50 focus:border-[var(--theme-primary)] transition-all"
              style={{ borderColor: 'var(--theme-border-primary)' }}
            />
            {settings.searchQuery && (
              <button
                onClick={() => setSettings({ ...settings, searchQuery: '' })}
                className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-muted)] hover:text-[var(--theme-text-primary)] transition-colors"
              >
                <X size={14} />
              </button>
            )}
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
                onChange={(value) =>
                  setSettings({
                    ...settings,
                    itemsPerPage: value === 'unlimited' ? 'unlimited' : parseInt(value)
                  })
                }
                prefix="Show:"
                className="flex-1 min-w-0"
              />
              <EnhancedDropdown
                options={[
                  { value: 'latest', label: 'Newest' },
                  { value: 'oldest', label: 'Oldest' },
                  { value: 'largest', label: 'Largest' },
                  { value: 'smallest', label: 'Smallest' },
                  { value: 'efficiency', label: 'Best Cache' },
                  { value: 'efficiency-low', label: 'Worst Cache' },
                  { value: 'sessions', label: 'Sessions' },
                  { value: 'alphabetical', label: 'A-Z' },
                  { value: 'service', label: 'Service' }
                ]}
                value={settings.sortOrder}
                onChange={(value) => setSettings({ ...settings, sortOrder: value as SortOrder })}
                prefix="Sort:"
                className="flex-1 min-w-0"
              />
              {/* View mode toggle inline with dropdowns */}
              <div className="flex rounded-lg bg-themed-tertiary p-0.5 flex-shrink-0">
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'compact' })}
                  className={`px-2 py-1 rounded-md transition-colors ${
                    settings.viewMode === 'compact' ? 'bg-primary' : 'text-themed-secondary'
                  }`}
                  style={{
                    color: settings.viewMode === 'compact' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Compact"
                >
                  <List size={14} />
                </button>
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'normal' })}
                  className={`px-2 py-1 rounded-md transition-colors ${
                    settings.viewMode === 'normal' ? 'bg-primary' : 'text-themed-secondary'
                  }`}
                  style={{
                    color: settings.viewMode === 'normal' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Normal"
                >
                  <Grid3x3 size={14} />
                </button>
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'retro' })}
                  className={`px-2 py-1 rounded-md transition-colors ${
                    settings.viewMode === 'retro' ? 'bg-primary' : 'text-themed-secondary'
                  }`}
                  style={{
                    color: settings.viewMode === 'retro' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Retro"
                >
                  <Table size={14} />
                </button>
              </div>
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
                onChange={(value) =>
                  setSettings({
                    ...settings,
                    itemsPerPage: value === 'unlimited' ? 'unlimited' : parseInt(value)
                  })
                }
                prefix="Show:"
                className="w-28"
              />

              <EnhancedDropdown
                options={[
                  { value: 'latest', label: 'Newest' },
                  { value: 'oldest', label: 'Oldest' },
                  { value: 'largest', label: 'Largest' },
                  { value: 'smallest', label: 'Smallest' },
                  { value: 'efficiency', label: 'Best Cache' },
                  { value: 'efficiency-low', label: 'Worst Cache' },
                  { value: 'sessions', label: 'Sessions' },
                  { value: 'alphabetical', label: 'A-Z' },
                  { value: 'service', label: 'Service' }
                ]}
                value={settings.sortOrder}
                onChange={(value) => setSettings({ ...settings, sortOrder: value as SortOrder })}
                prefix="Sort:"
                className="w-28 md:w-32 lg:w-36"
              />
            </div>

            {/* Desktop view controls */}
            <div className="hidden sm:flex gap-2 justify-end w-auto flex-shrink-0">
              {/* View Mode Toggle */}
              <div className="flex rounded-lg bg-themed-tertiary p-1">
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'compact' })}
                  className={`px-2 lg:px-3 py-1.5 rounded-md transition-colors flex items-center gap-1 ${
                    settings.viewMode === 'compact'
                      ? 'bg-primary'
                      : 'text-themed-secondary hover:text-themed-primary'
                  }`}
                  style={{
                    color: settings.viewMode === 'compact' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Compact View"
                >
                  <List size={16} />
                  <span className="text-xs hidden lg:inline">Compact</span>
                </button>
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'normal' })}
                  className={`px-2 lg:px-3 py-1.5 rounded-md transition-colors flex items-center gap-1 ${
                    settings.viewMode === 'normal'
                      ? 'bg-primary'
                      : 'text-themed-secondary hover:text-themed-primary'
                  }`}
                  style={{
                    color: settings.viewMode === 'normal' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Normal View"
                >
                  <Grid3x3 size={16} />
                  <span className="text-xs hidden lg:inline">Normal</span>
                </button>
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'retro' })}
                  className={`px-2 lg:px-3 py-1.5 rounded-md transition-colors flex items-center gap-1 ${
                    settings.viewMode === 'retro'
                      ? 'bg-primary'
                      : 'text-themed-secondary hover:text-themed-primary'
                  }`}
                  style={{
                    color: settings.viewMode === 'retro' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Retro View"
                >
                  <Table size={16} />
                  <span className="text-xs hidden lg:inline">Retro</span>
                </button>
              </div>

              {/* Export Button */}
              <ActionMenu
                isOpen={showExportOptions}
                onClose={() => setShowExportOptions(false)}
                width="w-48"
                trigger={
                  <button
                    onClick={() => setShowExportOptions(!showExportOptions)}
                    className="p-2 rounded hover:bg-themed-hover transition-colors"
                    title="Export Data"
                    disabled={exportLoading || itemsToDisplay.length === 0}
                  >
                    {exportLoading ? (
                      <Loader2 className="w-4 h-4 animate-spin" />
                    ) : (
                      <DownloadIcon size={18} />
                    )}
                  </button>
                }
              >
                <ActionMenuItem
                  onClick={() => {
                    handleExport('json');
                    setShowExportOptions(false);
                  }}
                >
                  Export JSON
                </ActionMenuItem>
                <ActionMenuItem
                  onClick={() => {
                    handleExport('csv');
                    setShowExportOptions(false);
                  }}
                >
                  Export CSV
                </ActionMenuItem>
              </ActionMenu>

              <button
                onClick={() => setSettingsOpened(!settingsOpened)}
                className="p-2 rounded hover:bg-themed-hover transition-colors"
                title="Settings"
                data-settings-button="true"
              >
                <Settings size={18} />
              </button>
            </div>
          </div>
        </div>

        <div ref={settingsRef}>
          {settingsOpened && (
            <>
              <div
                className="border-t my-3 animate-fade-in"
                style={{ borderColor: 'var(--theme-border-secondary)' }}
              />
              <div className="space-y-4 animate-slide-in-top">
                {/* Quick Presets - Mobile-friendly segmented control */}
                <div>
                  <div className="text-xs font-semibold uppercase tracking-wide mb-2" style={{ color: 'var(--theme-text-muted)' }}>
                    Quick Presets
                  </div>
                  {(() => {
                    const activePreset = detectActivePreset(settings);
                    const presetButtons: { key: PresetType; label: string }[] = [
                      { key: 'pretty', label: 'Pretty' },
                      { key: 'minimal', label: 'Minimal' },
                      { key: 'showAll', label: 'Show All' },
                      { key: 'default', label: 'Default' },
                      { key: 'custom', label: 'Custom' }
                    ];

                    return (
                      <div
                        className="inline-flex rounded-lg p-1 w-full sm:w-auto overflow-x-auto"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        {presetButtons.map(({ key, label }) => {
                          const isActive = activePreset === key;
                          return (
                            <button
                              key={key}
                              onClick={() => {
                                if (key !== 'custom') {
                                  setSettings({ ...settings, ...PRESETS[key as keyof typeof PRESETS] });
                                }
                              }}
                              disabled={key === 'custom'}
                              className={`flex-1 sm:flex-none px-3 py-1.5 text-xs font-medium rounded-md transition-all whitespace-nowrap ${
                                isActive ? 'shadow-sm' : key === 'custom' ? '' : 'hover:bg-[var(--theme-bg-secondary)]'
                              } ${key === 'custom' && !isActive ? 'opacity-50' : ''}`}
                              style={{
                                backgroundColor: isActive ? 'var(--theme-primary)' : 'transparent',
                                color: isActive ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
                                cursor: key === 'custom' ? 'default' : 'pointer'
                              }}
                            >
                              {label}
                            </button>
                          );
                        })}
                      </div>
                    );
                  })()}
                </div>

                {/* Settings Grid - Responsive with collapsible sections on mobile */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4 sm:gap-x-6 sm:gap-y-1">
                  {/* Filters Column */}
                  <div className="space-y-1">
                    <div className="text-xs font-semibold uppercase tracking-wide mb-2" style={{ color: 'var(--theme-text-muted)' }}>
                      Filters
                    </div>
                    <Checkbox
                      checked={settings.showZeroBytes}
                      onChange={(e) => setSettings({ ...settings, showZeroBytes: e.target.checked })}
                      label="Show metadata (0 bytes)"
                    />
                    <Checkbox
                      checked={settings.showSmallFiles}
                      onChange={(e) => setSettings({ ...settings, showSmallFiles: e.target.checked })}
                      label="Show small files"
                    />
                    <Checkbox
                      checked={settings.hideLocalhost}
                      onChange={(e) => setSettings({ ...settings, hideLocalhost: e.target.checked })}
                      label="Hide localhost"
                    />
                    <Checkbox
                      checked={settings.hideUnknownGames}
                      onChange={(e) => setSettings({ ...settings, hideUnknownGames: e.target.checked })}
                      label="Hide unknown games"
                    />
                  </div>

                  {/* Display Column */}
                  <div className="space-y-1">
                    <div className="text-xs font-semibold uppercase tracking-wide mb-2" style={{ color: 'var(--theme-text-muted)' }}>
                      Display
                    </div>
                    <Checkbox
                      checked={settings.aestheticMode}
                      onChange={(e) => setSettings({ ...settings, aestheticMode: e.target.checked })}
                      label="Minimal mode"
                    />
                    <Checkbox
                      checked={settings.fullHeightBanners}
                      onChange={(e) => setSettings({ ...settings, fullHeightBanners: e.target.checked })}
                      label="Full-height banners"
                    />
                  </div>

                  {/* Behavior Column */}
                  <div className="space-y-1">
                    <div className="text-xs font-semibold uppercase tracking-wide mb-2" style={{ color: 'var(--theme-text-muted)' }}>
                      Behavior
                    </div>
                    <Checkbox
                      checked={settings.groupUnknownGames}
                      onChange={(e) => setSettings({ ...settings, groupUnknownGames: e.target.checked })}
                      label="Group unknown games"
                    />
                    <Checkbox
                      checked={settings.groupByFrequency}
                      onChange={(e) => setSettings({ ...settings, groupByFrequency: e.target.checked })}
                      label="Group by frequency"
                    />
                    <Checkbox
                      checked={settings.enableScrollIntoView}
                      onChange={(e) => setSettings({ ...settings, enableScrollIntoView: e.target.checked })}
                      label="Scroll on expand"
                    />
                  </div>
                </div>
              </div>
            </>
          )}
        </div>
      </Card>

      {/* Stats */}
      <Alert color="blue" icon={<Database className="w-5 h-5" />}>
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
          <div className="flex flex-col sm:flex-row sm:items-center gap-1 sm:gap-2 text-sm">
            <span className="whitespace-nowrap">
              {settings.itemsPerPage !== 'unlimited' && (
                <span className="font-medium">
                  Page {currentPage} of {settings.viewMode === 'retro' ? retroTotalPages : totalPages}
                </span>
              )}
            </span>
            <span className="flex flex-wrap items-center gap-1">
              {settings.itemsPerPage !== 'unlimited' && <span className="hidden sm:inline">-</span>}
              <span>
                Showing {settings.viewMode === 'retro'
                  ? `${Math.min(
                      settings.itemsPerPage === 'unlimited' ? retroTotalItems : (settings.itemsPerPage as number),
                      retroTotalItems - (currentPage - 1) * (settings.itemsPerPage === 'unlimited' ? retroTotalItems : (settings.itemsPerPage as number))
                    )} of ${retroTotalItems} depot groups`
                  : `${itemsToDisplay.length} of ${allItemsSorted.length} groups`
                }
              </span>
              <span className="whitespace-nowrap">
                ({filteredDownloads.length}{' '}
                {filteredDownloads.length === 1 ? 'download' : 'downloads'}
                {filteredDownloads.length !== latestDownloads.length &&
                  ` of ${latestDownloads.length} total`}
                )
              </span>
            </span>
            {(settings.selectedService !== 'all' || settings.selectedClient !== 'all' || settings.searchQuery) && (
              <span className="flex flex-wrap gap-1 text-xs sm:text-sm">
                {settings.searchQuery && (
                  <span className="whitespace-nowrap">• Search: "{settings.searchQuery}"</span>
                )}
                {settings.selectedService !== 'all' && (
                  <span className="whitespace-nowrap">• Service: {settings.selectedService}</span>
                )}
                {settings.selectedClient !== 'all' && (
                  <span className="whitespace-nowrap">• Client: {settings.selectedClient}</span>
                )}
              </span>
            )}
          </div>
          {(settings.selectedService !== 'all' || settings.selectedClient !== 'all' || settings.searchQuery) && (
            <button
              onClick={() =>
                setSettings({ ...settings, selectedService: 'all', selectedClient: 'all', searchQuery: '' })
              }
              className="text-xs px-3 py-1.5 rounded bg-themed-accent text-[var(--theme-button-text)] hover:opacity-80 transition-opacity whitespace-nowrap self-start sm:self-auto"
            >
              Clear Filters
            </button>
          )}
        </div>
      </Alert>

      {/* Help message for empty time ranges */}
      {filteredDownloads.length === 0 && timeRange !== 'live' && (
        <Alert color="yellow">
          <div className="flex flex-col gap-2">
            <div className="font-medium">No downloads found in selected time range</div>
            <div className="text-sm opacity-90">
              The dashboard shows <strong>active downloads</strong> (currently in progress), while
              this list shows downloads that <strong>started</strong> within the selected time
              period. Try switching to "Live" to see all downloads, or adjust your time range.
            </div>
          </div>
        </Alert>
      )}

      {/* Downloads list */}
      <div className="relative" ref={contentRef}>
        {/* Loading overlay for filter changes */}
        {filterLoading && (
          <div className="absolute inset-0 bg-[var(--theme-bg-primary)]/60 backdrop-blur-sm flex items-center justify-center z-10 rounded-lg transition-opacity duration-300">
            <div
              className="flex flex-col items-center gap-3 px-6 py-4 rounded-lg bg-[var(--theme-bg-secondary)] border shadow-xl"
              style={{
                borderColor: 'var(--theme-border-primary)',
                animation: 'slideUp 0.3s ease-out'
              }}
            >
              <Loader2 className="w-6 h-6 animate-spin text-[var(--theme-primary)]" />
              <span className="text-sm font-medium text-[var(--theme-text-primary)]">
                Updating...
              </span>
            </div>
          </div>
        )}

        {/* Content based on view mode with fade transition */}
        <div className="relative">
          <div
            className={`transition-opacity duration-300 ${
              settings.viewMode === 'compact'
                ? 'opacity-100'
                : 'opacity-0 absolute inset-0 pointer-events-none'
            }`}
          >
            {settings.viewMode === 'compact' && (
              <CompactView
                items={itemsToDisplay as (Download | DownloadGroup)[]}
                expandedItem={expandedItem}
                onItemClick={handleItemClick}
                aestheticMode={settings.aestheticMode}
                groupByFrequency={settings.groupByFrequency}
                enableScrollIntoView={settings.enableScrollIntoView}
                showDatasourceLabels={showDatasourceLabels}
                hasMultipleDatasources={hasMultipleDatasources}
              />
            )}
          </div>

          <div
            className={`transition-opacity duration-300 ${
              settings.viewMode === 'normal'
                ? 'opacity-100'
                : 'opacity-0 absolute inset-0 pointer-events-none'
            }`}
          >
            {settings.viewMode === 'normal' && (
              <NormalView
                items={itemsToDisplay as (Download | DownloadGroup)[]}
                expandedItem={expandedItem}
                onItemClick={handleItemClick}
                aestheticMode={settings.aestheticMode}
                fullHeightBanners={settings.fullHeightBanners}
                groupByFrequency={settings.groupByFrequency}
                enableScrollIntoView={settings.enableScrollIntoView}
                showDatasourceLabels={showDatasourceLabels}
                hasMultipleDatasources={hasMultipleDatasources}
              />
            )}
          </div>

          <div
            className={`transition-opacity duration-300 ${
              settings.viewMode === 'retro'
                ? 'opacity-100'
                : 'opacity-0 absolute inset-0 pointer-events-none'
            }`}
          >
            {settings.viewMode === 'retro' && (
              <RetroView
                items={allItemsSorted as (Download | DownloadGroup)[]}
                aestheticMode={settings.aestheticMode}
                itemsPerPage={settings.itemsPerPage}
                currentPage={currentPage}
                onTotalPagesChange={handleRetroTotalPagesChange}
                sortOrder={settings.sortOrder}
                showDatasourceLabels={showDatasourceLabels}
                hasMultipleDatasources={hasMultipleDatasources}
              />
            )}
          </div>
        </div>
      </div>

      {/* Pagination Controls */}
      {settings.itemsPerPage !== 'unlimited' && (
        <Pagination
          currentPage={currentPage}
          totalPages={settings.viewMode === 'retro' ? retroTotalPages : totalPages}
          totalItems={settings.viewMode === 'retro' ? retroTotalItems : allItemsSorted.length}
          itemsPerPage={typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 20}
          onPageChange={handlePageChange}
          itemLabel={settings.viewMode === 'retro' ? 'depot groups' : 'items'}
          showCard={false}
        />
      )}

      {/* Performance warning */}
      {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 500 && (
        <Alert color="yellow">
          Loading {itemsToDisplay.length} items. Consider using pagination for better performance.
        </Alert>
      )}
        </>
      )}
    </div>
  );
};

export default DownloadsTab;
