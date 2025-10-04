import React, { useState, useEffect, useMemo, useRef } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  ChevronsRight,
  Database,
  AlertTriangle,
  Settings,
  Download as DownloadIcon,
  Loader,
  List,
  Grid3x3,
} from 'lucide-react';
import { useData } from '../../contexts/DataContext'; // Fixed import path
import { useTimeFilter } from '../../contexts/TimeFilterContext';
import { Alert } from '../ui/Alert'; // Fixed import path
import { Card } from '../ui/Card'; // Fixed import path
import { Checkbox } from '../ui/Checkbox';
import { EnhancedDropdown } from '../ui/EnhancedDropdown';

// Import view components
import CompactView from './CompactView';
import NormalView from './NormalView';

import type { Download, DownloadGroup } from '../../types';

// Storage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  CLIENT_FILTER: 'lancache_downloads_client',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small',
  HIDE_LOCALHOST: 'lancache_downloads_hide_localhost',
  HIDE_UNKNOWN_GAMES: 'lancache_downloads_hide_unknown',
  VIEW_MODE: 'lancache_downloads_view_mode',
  SORT_ORDER: 'lancache_downloads_sort_order',
  AESTHETIC_MODE: 'lancache_downloads_aesthetic_mode'
};

// View modes
type ViewMode = 'compact' | 'normal';

// CSV conversion utilities
const convertDownloadsToCSV = (downloads: Download[]): string => {
  if (!downloads || downloads.length === 0) return '';

  const headers = [
    'id', 'service', 'clientIp', 'startTime', 'endTime', 'cacheHitBytes',
    'cacheMissBytes', 'totalBytes', 'cacheHitPercent', 'isActive', 'gameName', 'gameAppId'
  ];
  const csvHeaders = headers.join(',');

  const csvRows = downloads.map(download => {
    return headers.map(header => {
      const value = download[header as keyof Download];
      if (value === null || value === undefined) return '';
      if (typeof value === 'string' && value.includes(',')) {
        return `"${value.replace(/"/g, '""')}"`;
      }
      return value;
    }).join(',');
  });

  return [csvHeaders, ...csvRows].join('\n');
};

// Main Downloads Tab Component
const DownloadsTab: React.FC = () => {
  const {
    latestDownloads = [],
    loading,
    mockMode,
    updateMockDataCount,
    updateApiDownloadCount
  } = useData();
  const { timeRange } = useTimeFilter();

  // State management
  const [expandedItem, setExpandedItem] = useState<string | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [filterLoading, setFilterLoading] = useState(false);
  const [exportLoading, setExportLoading] = useState(false);
  const [showExportOptions, setShowExportOptions] = useState(false);
  const [currentPage, setCurrentPage] = useState(1);
  const contentRef = useRef<HTMLDivElement>(null);

  const [settings, setSettings] = useState(() => ({
    showZeroBytes: localStorage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
    showSmallFiles: localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
    hideLocalhost: localStorage.getItem(STORAGE_KEYS.HIDE_LOCALHOST) === 'true',
    hideUnknownGames: localStorage.getItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES) === 'true',
    selectedService: localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
    selectedClient: localStorage.getItem(STORAGE_KEYS.CLIENT_FILTER) || 'all',
    itemsPerPage:
      localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) === 'unlimited'
        ? 'unlimited' as const
        : parseInt(localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) || '50'),
    viewMode: (localStorage.getItem(STORAGE_KEYS.VIEW_MODE) || 'normal') as ViewMode,
    sortOrder: (localStorage.getItem(STORAGE_KEYS.SORT_ORDER) || 'latest') as 'latest' | 'oldest' | 'largest' | 'smallest' | 'service',
    aestheticMode: localStorage.getItem(STORAGE_KEYS.AESTHETIC_MODE) === 'true',
    groupByFrequency: localStorage.getItem('lancache_downloads_group_by_frequency') !== 'false'
  }));

  // Effect to save settings to localStorage
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(STORAGE_KEYS.CLIENT_FILTER, settings.selectedClient);
    localStorage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, settings.itemsPerPage.toString());
    localStorage.setItem(STORAGE_KEYS.VIEW_MODE, settings.viewMode);
    localStorage.setItem(STORAGE_KEYS.SORT_ORDER, settings.sortOrder);
    localStorage.setItem(STORAGE_KEYS.AESTHETIC_MODE, settings.aestheticMode.toString());
    localStorage.setItem('lancache_downloads_group_by_frequency', settings.groupByFrequency.toString());
  }, [settings]);

  // Always fetch unlimited downloads from API to ensure we have all for grouping
  useEffect(() => {
    if (mockMode && updateMockDataCount) {
      updateMockDataCount('unlimited');
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount('unlimited');
    }
  }, [mockMode, updateMockDataCount, updateApiDownloadCount]);

  // Track filter changes and show loading state
  useEffect(() => {
    if (!loading && latestDownloads.length > 0) {
      setFilterLoading(true);

      const timer = setTimeout(() => {
        setFilterLoading(false);
      }, 300);

      return () => clearTimeout(timer);
    }
  }, [settings.selectedService, settings.selectedClient, settings.sortOrder, settings.showZeroBytes, settings.showSmallFiles, settings.hideLocalhost, settings.hideUnknownGames, settings.viewMode]);

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
    return availableServices.filter(service => {
      // Check if this service has any downloads > 1MB
      const serviceDownloads = latestDownloads.filter(d => d.service.toLowerCase() === service);
      const hasLargeFiles = serviceDownloads.some(d => d.totalBytes > 1024 * 1024); // 1MB

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
    const hiddenServices = availableServices.filter(service => !filteredAvailableServices.includes(service));
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

  const clientOptions = useMemo(() => [
    { value: 'all', label: 'All Clients' },
    ...availableClients.map((client) => ({
      value: client,
      label: client
    }))
  ], [availableClients]);

  const itemsPerPageOptions = useMemo(
    () => [
      { value: '20', label: '20 groups' },
      { value: '50', label: '50 groups' },
      { value: '100', label: '100 groups' },
      { value: '200', label: '200 groups' },
      { value: 'unlimited', label: 'Show All' }
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
      filtered = filtered.filter(
        (d) => d.clientIp !== '127.0.0.1' && d.clientIp !== '::1'
      );
    }

    if (settings.hideUnknownGames) {
      filtered = filtered.filter((d) => {
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

    return filtered;
  }, [latestDownloads, settings.showZeroBytes, settings.showSmallFiles, settings.hideLocalhost, settings.hideUnknownGames, settings.selectedService, settings.selectedClient]);

  const serviceFilteredDownloads = useMemo(() => {
    if (!Array.isArray(latestDownloads)) {
      return [];
    }

    if (settings.selectedService === 'all') {
      return latestDownloads;
    }

    return latestDownloads.filter((d) => d.service.toLowerCase() === settings.selectedService);
  }, [latestDownloads, settings.selectedService]);

  // Grouping logic for different view modes
  const createGroups = (downloads: Download[]): { groups: DownloadGroup[], individuals: Download[] } => {
    const groups: Record<string, DownloadGroup> = {};
    const individuals: Download[] = [];

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
          firstSeen: download.startTime,
          lastSeen: download.startTime,
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

      if (download.startTime < groups[groupKey].firstSeen) {
        groups[groupKey].firstSeen = download.startTime;
      }
      if (download.startTime > groups[groupKey].lastSeen) {
        groups[groupKey].lastSeen = download.startTime;
      }
    });

    return { groups: Object.values(groups), individuals };
  };


  const normalViewItems = useMemo((): (Download | DownloadGroup)[] => {
    if (settings.viewMode !== 'normal') return [];

    const { groups, individuals } = createGroups(filteredDownloads);

    // Filter out groups with "unknown" in the name if hideUnknownGames is enabled
    let filteredGroups = groups;
    if (settings.hideUnknownGames) {
      filteredGroups = groups.filter(g => {
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
        const aIsMultiple = ('downloads' in a && a.downloads.length > 1);
        const bIsMultiple = ('downloads' in b && b.downloads.length > 1);

        if (aIsMultiple && !bIsMultiple) return -1; // Multiple downloads first
        if (!aIsMultiple && bIsMultiple) return 1;  // Single downloads/individuals after

        const aIsSingle = ('downloads' in a && a.downloads.length === 1);
        const bIsSingle = ('downloads' in b && b.downloads.length === 1);

        if (aIsSingle && !bIsSingle && !bIsMultiple) return -1; // Single downloads before individuals
        if (!aIsSingle && bIsSingle && !aIsMultiple) return 1;  // Individuals after single downloads
      }

      // Then sort by time within each category (or just by time if groupByFrequency is off)
      const aTime = 'downloads' in a
        ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(a.startTime).getTime();
      const bTime = 'downloads' in b
        ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(b.startTime).getTime();
      return bTime - aTime;
    });
  }, [filteredDownloads, settings.viewMode, settings.groupByFrequency, settings.hideUnknownGames]);

  const compactViewItems = useMemo((): (Download | DownloadGroup)[] => {
    if (settings.viewMode !== 'compact') return [];

    const { groups, individuals } = createGroups(filteredDownloads);

    // Filter out groups with "unknown" in the name if hideUnknownGames is enabled
    let filteredGroups = groups;
    if (settings.hideUnknownGames) {
      filteredGroups = groups.filter(g => {
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
        const aIsMultiple = ('downloads' in a && a.downloads.length > 1);
        const bIsMultiple = ('downloads' in b && b.downloads.length > 1);

        if (aIsMultiple && !bIsMultiple) return -1; // Multiple downloads first
        if (!aIsMultiple && bIsMultiple) return 1;  // Single downloads/individuals after

        const aIsSingle = ('downloads' in a && a.downloads.length === 1);
        const bIsSingle = ('downloads' in b && b.downloads.length === 1);

        if (aIsSingle && !bIsSingle && !bIsMultiple) return -1; // Single downloads before individuals
        if (!aIsSingle && bIsSingle && !aIsMultiple) return 1;  // Individuals after single downloads
      }

      // Then sort by time within each category (or just by time if groupByFrequency is off)
      const aTime = 'downloads' in a
        ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(a.startTime).getTime();
      const bTime = 'downloads' in b
        ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(b.startTime).getTime();
      return bTime - aTime;
    });
  }, [filteredDownloads, settings.viewMode, settings.groupByFrequency, settings.hideUnknownGames]);

  const allItemsSorted = useMemo(() => {
    let items = settings.viewMode === 'normal' ? normalViewItems :
                settings.viewMode === 'compact' ? compactViewItems :
                filteredDownloads;

    // Apply sorting while preserving Multiple vs Single categorization
    if (settings.viewMode === 'normal' || settings.viewMode === 'compact') {
      const mixedItems = [...items] as (Download | DownloadGroup)[];

      // Separate items into categories first
      const multipleDownloads = mixedItems.filter(item => 'downloads' in item && item.downloads.length > 1);
      const singleDownloads = mixedItems.filter(item => 'downloads' in item && item.downloads.length === 1);
      const individuals = mixedItems.filter(item => !('downloads' in item));

      // Sort each category separately
      const sortFn = (a: Download | DownloadGroup, b: Download | DownloadGroup) => {
        switch (settings.sortOrder) {
          case 'oldest':
            const aTime = 'downloads' in a
              ? Math.min(...a.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(a.startTime).getTime();
            const bTime = 'downloads' in b
              ? Math.min(...b.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(b.startTime).getTime();
            return aTime - bTime;
          case 'largest':
            const aBytes = 'downloads' in a ? a.totalBytes : (a.totalBytes || 0);
            const bBytes = 'downloads' in b ? b.totalBytes : (b.totalBytes || 0);
            return bBytes - aBytes;
          case 'smallest':
            const aBytesSmall = 'downloads' in a ? a.totalBytes : (a.totalBytes || 0);
            const bBytesSmall = 'downloads' in b ? b.totalBytes : (b.totalBytes || 0);
            return aBytesSmall - bBytesSmall;
          case 'service':
            const serviceCompare = a.service.localeCompare(b.service);
            if (serviceCompare !== 0) return serviceCompare;
            const aLatest = 'downloads' in a
              ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(a.startTime).getTime();
            const bLatest = 'downloads' in b
              ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(b.startTime).getTime();
            return bLatest - aLatest;
          case 'latest':
          default:
            const aLatestDefault = 'downloads' in a
              ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(a.startTime).getTime();
            const bLatestDefault = 'downloads' in b
              ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(b.startTime).getTime();
            return bLatestDefault - aLatestDefault;
        }
      };

      // Sort each category
      multipleDownloads.sort(sortFn);
      singleDownloads.sort(sortFn);
      individuals.sort(sortFn);

      // Combine categories in the correct order
      items = [...multipleDownloads, ...singleDownloads, ...individuals];
    }

    return items;
  }, [
    filteredDownloads,
    normalViewItems,
    compactViewItems,
    settings.viewMode,
    settings.sortOrder
  ]);

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
  }, [settings.selectedService, settings.selectedClient, settings.sortOrder, settings.showZeroBytes, settings.showSmallFiles, settings.hideLocalhost, settings.hideUnknownGames, settings.viewMode, settings.itemsPerPage]);

  // Handle page changes with smooth scroll
  const handlePageChange = (newPage: number) => {
    if (newPage === currentPage) return;

    setCurrentPage(newPage);
    if (contentRef.current) {
      contentRef.current.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  };

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
          (settings.viewMode === 'normal' || settings.viewMode === 'compact'
            ? (itemsForExport as (Download | DownloadGroup)[]).flatMap((item) =>
                'downloads' in item ? item.downloads : [item]
              )
            : (itemsForExport as Download[]));

        content = convertDownloadsToCSV(downloadsForExport);
        filename = `${baseFilename}.csv`;
        mimeType = 'text/csv';
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
            <div key={i} className="h-16 bg-[var(--theme-bg-secondary)] rounded animate-pulse"
                 style={{ animationDelay: `${i * 100}ms` }}>
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

  // Empty state
  if (latestDownloads.length === 0) {
    return (
      <Alert color="blue" icon={<Database className="w-5 h-5" />}>
        No downloads recorded yet. Downloads will appear here as clients request content.
      </Alert>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
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
            >
              <Settings size={18} />
            </button>
          </div>

          {/* Dropdowns and View Controls */}
          <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center justify-between w-full">
            {/* Mobile: First row with service and client filters */}
            <div className="flex sm:hidden gap-2 w-full">
              <EnhancedDropdown
                options={serviceOptions}
                value={settings.selectedService}
                onChange={(value) =>
                  setSettings({ ...settings, selectedService: value })
                }
                className="flex-1 min-w-0"
              />
              <EnhancedDropdown
                options={clientOptions}
                value={settings.selectedClient}
                onChange={(value) =>
                  setSettings({ ...settings, selectedClient: value })
                }
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
                className="flex-1 min-w-0"
              />
              <EnhancedDropdown
                options={[
                  { value: 'latest', label: 'Newest' },
                  { value: 'oldest', label: 'Oldest' },
                  { value: 'largest', label: 'Largest' },
                  { value: 'smallest', label: 'Smallest' },
                  { value: 'service', label: 'Service' }
                ]}
                value={settings.sortOrder}
                onChange={(value) =>
                  setSettings({ ...settings, sortOrder: value as any })
                }
                className="flex-1 min-w-0"
              />
              {/* View mode toggle inline with dropdowns */}
              <div className="flex rounded-lg bg-themed-tertiary p-0.5 flex-shrink-0">
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'compact' })}
                  className={`px-2 py-1 rounded-md transition-colors ${
                    settings.viewMode === 'compact'
                      ? 'bg-primary'
                      : 'text-themed-secondary'
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
                    settings.viewMode === 'normal'
                      ? 'bg-primary'
                      : 'text-themed-secondary'
                  }`}
                  style={{
                    color: settings.viewMode === 'normal' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Normal"
                >
                  <Grid3x3 size={14} />
                </button>
              </div>
            </div>

            {/* Desktop: All controls in one row */}
            <div className="hidden sm:flex gap-2 items-center flex-1 w-full">
              <EnhancedDropdown
                options={serviceOptions}
                value={settings.selectedService}
                onChange={(value) =>
                  setSettings({ ...settings, selectedService: value })
                }
                className="w-40"
              />

              <EnhancedDropdown
                options={clientOptions}
                value={settings.selectedClient}
                onChange={(value) =>
                  setSettings({ ...settings, selectedClient: value })
                }
                className="w-48"
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
                className="w-32"
              />

              <EnhancedDropdown
                options={[
                  { value: 'latest', label: 'Date (Newest)' },
                  { value: 'oldest', label: 'Date (Oldest)' },
                  { value: 'largest', label: 'Size (Largest)' },
                  { value: 'smallest', label: 'Size (Smallest)' },
                  { value: 'service', label: 'By Service' }
                ]}
                value={settings.sortOrder}
                onChange={(value) =>
                  setSettings({ ...settings, sortOrder: value as any })
                }
                className="w-40"
              />
            </div>

            {/* Desktop view controls */}
            <div className="hidden sm:flex gap-2 justify-end w-auto">
              {/* View Mode Toggle */}
              <div className="flex rounded-lg bg-themed-tertiary p-1">
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'compact' })}
                  className={`px-3 py-1.5 rounded-md transition-colors flex items-center gap-1 ${
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
                  <span className="text-xs">Compact</span>
                </button>
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'normal' })}
                  className={`px-3 py-1.5 rounded-md transition-colors flex items-center gap-1 ${
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
                  <span className="text-xs">Normal</span>
                </button>
              </div>

              {/* Export Button */}
              <div className="relative">
                <button
                  onClick={() => setShowExportOptions(!showExportOptions)}
                  className="p-2 rounded hover:bg-themed-hover transition-colors"
                  title="Export Data"
                  disabled={exportLoading || itemsToDisplay.length === 0}
                >
                  {exportLoading ? (
                    <Loader className="w-4 h-4 animate-spin" />
                  ) : (
                    <DownloadIcon size={18} />
                  )}
                </button>
                {showExportOptions && (
                  <div
                    className="absolute right-0 mt-2 w-48 rounded-lg shadow-lg bg-themed-primary border z-50"
                    style={{
                      borderColor: 'var(--theme-border-primary)'
                    }}
                  >
                    <div className="py-1">
                      <button
                        onClick={() => {
                          handleExport('json');
                          setShowExportOptions(false);
                        }}
                        className="w-full text-left px-4 py-2 text-sm hover:bg-themed-hover transition-colors text-themed-secondary"
                      >
                        Export JSON
                      </button>
                      <button
                        onClick={() => {
                          handleExport('csv');
                          setShowExportOptions(false);
                        }}
                        className="w-full text-left px-4 py-2 text-sm hover:bg-themed-hover transition-colors text-themed-secondary"
                      >
                        Export CSV
                      </button>
                    </div>
                  </div>
                )}
              </div>

              <button
                onClick={() => setSettingsOpened(!settingsOpened)}
                className="p-2 rounded hover:bg-themed-hover transition-colors"
                title="Settings"
              >
                <Settings size={18} />
              </button>
            </div>
          </div>
        </div>

        {settingsOpened && (
          <>
            <div className="border-t my-3 animate-fade-in" style={{ borderColor: 'var(--theme-border-secondary)' }} />
            <div className="space-y-2 animate-slide-in-top">
              <Checkbox
                checked={settings.showZeroBytes}
                onChange={(e) =>
                  setSettings({ ...settings, showZeroBytes: e.target.checked })
                }
                label="Show metadata (0 bytes)"
              />

              <Checkbox
                checked={settings.showSmallFiles}
                onChange={(e) =>
                  setSettings({ ...settings, showSmallFiles: e.target.checked })
                }
                label="Show small files (< 1MB)"
              />

              <Checkbox
                checked={settings.hideLocalhost}
                onChange={(e) =>
                  setSettings({ ...settings, hideLocalhost: e.target.checked })
                }
                label="Hide localhost (127.0.0.1)"
              />

              <Checkbox
                checked={settings.hideUnknownGames}
                onChange={(e) =>
                  setSettings({ ...settings, hideUnknownGames: e.target.checked })
                }
                label="Hide unknown games"
              />

              <Checkbox
                checked={settings.aestheticMode}
                onChange={(e) =>
                  setSettings({ ...settings, aestheticMode: e.target.checked })
                }
                label="Aesthetic mode"
              />

              <Checkbox
                checked={settings.groupByFrequency}
                onChange={(e) =>
                  setSettings({ ...settings, groupByFrequency: e.target.checked })
                }
                label="Group downloads by frequency"
              />
            </div>
          </>
        )}
      </Card>

      {/* Stats */}
      <Alert color="blue" icon={<Database className="w-5 h-5" />}>
        <div className="flex items-center justify-between flex-wrap gap-2">
          <span>
            {settings.itemsPerPage !== 'unlimited' && `Page ${currentPage} of ${totalPages} - `}
            Showing {itemsToDisplay.length} of {allItemsSorted.length} groups
            ({filteredDownloads.length} {filteredDownloads.length === 1 ? 'download' : 'downloads'})
            {filteredDownloads.length !== serviceFilteredDownloads.length &&
              ` of ${serviceFilteredDownloads.length} total`}
            {(settings.selectedService !== 'all' || settings.selectedClient !== 'all') && (
              <span className="ml-1">
                {settings.selectedService !== 'all' && ` • Service: ${settings.selectedService}`}
                {settings.selectedClient !== 'all' && ` • Client: ${settings.selectedClient}`}
              </span>
            )}
          </span>
          {(settings.selectedService !== 'all' || settings.selectedClient !== 'all') && (
            <button
              onClick={() => setSettings({ ...settings, selectedService: 'all', selectedClient: 'all' })}
              className="text-xs px-2 py-1 rounded bg-themed-accent text-white hover:opacity-80 transition-opacity"
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
              The dashboard shows <strong>active downloads</strong> (currently in progress), while this list shows downloads that <strong>started</strong> within the selected time period.
              Try switching to "Live" to see all downloads, or adjust your time range.
            </div>
          </div>
        </Alert>
      )}

      {/* Downloads list */}
      <div className="relative" ref={contentRef}>
        {/* Loading overlay for filter changes */}
        {filterLoading && (
          <div className="absolute inset-0 bg-[var(--theme-bg-primary)]/60 backdrop-blur-sm flex items-center justify-center z-10 rounded-lg transition-opacity duration-300">
            <div className="flex flex-col items-center gap-3 px-6 py-4 rounded-lg bg-[var(--theme-bg-secondary)] border shadow-xl"
                 style={{ borderColor: 'var(--theme-border-primary)', animation: 'slideUp 0.3s ease-out' }}>
              <Loader className="w-6 h-6 animate-spin text-[var(--theme-primary)]" />
              <span className="text-sm font-medium text-[var(--theme-text-primary)]">Updating...</span>
            </div>
          </div>
        )}

        {/* Content based on view mode with fade transition */}
        <div className="relative">
          <div className={`transition-opacity duration-300 ${
            settings.viewMode === 'compact' ? 'opacity-100' : 'opacity-0 absolute inset-0 pointer-events-none'
          }`}>
            {settings.viewMode === 'compact' && (
              <CompactView
                items={itemsToDisplay as (Download | DownloadGroup)[]}
                expandedItem={expandedItem}
                onItemClick={handleItemClick}
                aestheticMode={settings.aestheticMode}
                groupByFrequency={settings.groupByFrequency}
              />
            )}
          </div>

          <div className={`transition-opacity duration-300 ${
            settings.viewMode === 'normal' ? 'opacity-100' : 'opacity-0 absolute inset-0 pointer-events-none'
          }`}>
            {settings.viewMode === 'normal' && (
              <NormalView
                items={itemsToDisplay as (Download | DownloadGroup)[]}
                expandedItem={expandedItem}
                onItemClick={handleItemClick}
                aestheticMode={settings.aestheticMode}
                groupByFrequency={settings.groupByFrequency}
              />
            )}
          </div>
        </div>
      </div>

      {/* Pagination Controls - Fixed Position */}
      {totalPages > 1 && settings.itemsPerPage !== 'unlimited' && (
        <div className="sticky bottom-0 mt-4 z-20" style={{
          backgroundColor: 'var(--theme-bg-primary)',
          paddingTop: '8px',
          paddingBottom: '8px',
          boxShadow: '0 -4px 12px rgba(0,0,0,0.1)'
        }}>
          <Card padding="sm" className="max-w-4xl mx-auto">
            <div className="flex flex-col sm:flex-row items-center justify-between gap-3">
              {/* Page Info */}
              <div className="flex items-center gap-4">
                <span className="text-sm font-medium" style={{ color: 'var(--theme-text-primary)' }}>
                  Page {currentPage} of {totalPages}
                </span>
                <span className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  {((currentPage - 1) * (typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 20) + 1)} - {Math.min(currentPage * (typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 20), allItemsSorted.length)} of {allItemsSorted.length} items
                </span>
              </div>

              {/* Navigation Controls */}
              <div className="flex items-center gap-2">
                {/* First Page */}
                <button
                  onClick={() => handlePageChange(1)}
                  disabled={currentPage === 1}
                  className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    color: 'var(--theme-text-primary)',
                    border: '1px solid var(--theme-border-primary)'
                  }}
                  title="First page"
                  aria-label="Go to first page"
                >
                  <ChevronsLeft size={16} />
                </button>

                {/* Previous Page */}
                <button
                  onClick={() => handlePageChange(Math.max(1, currentPage - 1))}
                  disabled={currentPage === 1}
                  className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    color: 'var(--theme-text-primary)',
                    border: '1px solid var(--theme-border-primary)'
                  }}
                  title="Previous page"
                  aria-label="Go to previous page"
                >
                  <ChevronLeft size={16} />
                </button>

                {/* Page Numbers Container */}
                <div className="flex items-center gap-1 px-2">
                  {/* For small number of pages, show all */}
                  {totalPages <= 7 ? (
                    Array.from({ length: totalPages }, (_, i) => i + 1).map(pageNum => (
                      <button
                        key={pageNum}
                        onClick={() => handlePageChange(pageNum)}
                        className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                          currentPage === pageNum ? 'shadow-md' : 'hover:bg-opacity-80'
                        }`}
                        style={{
                          backgroundColor: currentPage === pageNum ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                          color: currentPage === pageNum ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
                          border: currentPage === pageNum ? '1px solid var(--theme-primary)' : '1px solid var(--theme-border-secondary)'
                        }}
                        aria-label={`Go to page ${pageNum}`}
                        aria-current={currentPage === pageNum ? 'page' : undefined}
                      >
                        {pageNum}
                      </button>
                    ))
                  ) : (
                    <>
                      {/* Complex pagination for many pages */}
                      <button
                        onClick={() => handlePageChange(1)}
                        className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                          currentPage === 1 ? 'shadow-md' : 'hover:bg-opacity-80'
                        }`}
                        style={{
                          backgroundColor: currentPage === 1 ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                          color: currentPage === 1 ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
                          border: currentPage === 1 ? '1px solid var(--theme-primary)' : '1px solid var(--theme-border-secondary)'
                        }}
                        aria-label="Go to page 1"
                        aria-current={currentPage === 1 ? 'page' : undefined}
                      >
                        1
                      </button>

                      {currentPage > 3 && (
                        <span className="px-2" style={{ color: 'var(--theme-text-muted)' }}>•••</span>
                      )}

                      {Array.from({ length: 5 }, (_, i) => {
                        const pageNum = currentPage - 2 + i;
                        if (pageNum <= 1 || pageNum >= totalPages) return null;
                        return (
                          <button
                            key={pageNum}
                            onClick={() => handlePageChange(pageNum)}
                            className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                              currentPage === pageNum ? 'shadow-md' : 'hover:bg-opacity-80'
                            }`}
                            style={{
                              backgroundColor: currentPage === pageNum ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                              color: currentPage === pageNum ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
                              border: currentPage === pageNum ? '1px solid var(--theme-primary)' : '1px solid var(--theme-border-secondary)'
                            }}
                            aria-label={`Go to page ${pageNum}`}
                            aria-current={currentPage === pageNum ? 'page' : undefined}
                          >
                            {pageNum}
                          </button>
                        );
                      }).filter(Boolean)}

                      {currentPage < totalPages - 2 && (
                        <span className="px-2" style={{ color: 'var(--theme-text-muted)' }}>•••</span>
                      )}

                      <button
                        onClick={() => handlePageChange(totalPages)}
                        className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                          currentPage === totalPages ? 'shadow-md' : 'hover:bg-opacity-80'
                        }`}
                        style={{
                          backgroundColor: currentPage === totalPages ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                          color: currentPage === totalPages ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
                          border: currentPage === totalPages ? '1px solid var(--theme-primary)' : '1px solid var(--theme-border-secondary)'
                        }}
                        aria-label={`Go to page ${totalPages}`}
                        aria-current={currentPage === totalPages ? 'page' : undefined}
                      >
                        {totalPages}
                      </button>
                    </>
                  )}
                </div>

                {/* Next Page */}
                <button
                  onClick={() => handlePageChange(Math.min(totalPages, currentPage + 1))}
                  disabled={currentPage === totalPages}
                  className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    color: 'var(--theme-text-primary)',
                    border: '1px solid var(--theme-border-primary)'
                  }}
                  title="Next page"
                  aria-label="Go to next page"
                >
                  <ChevronRight size={16} />
                </button>

                {/* Last Page */}
                <button
                  onClick={() => handlePageChange(totalPages)}
                  disabled={currentPage === totalPages}
                  className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    color: 'var(--theme-text-primary)',
                    border: '1px solid var(--theme-border-primary)'
                  }}
                  title="Last page"
                  aria-label="Go to last page"
                >
                  <ChevronsRight size={16} />
                </button>

                {/* Quick Page Jump (for many pages) */}
                {totalPages > 10 && (
                  <>
                    <div className="border-l mx-2 h-6" style={{ borderColor: 'var(--theme-border-secondary)' }} />
                    <select
                      value={currentPage}
                      onChange={(e) => handlePageChange(parseInt(e.target.value))}
                      className="px-3 py-1 rounded-lg text-sm font-medium transition-colors"
                      style={{
                        backgroundColor: 'var(--theme-bg-secondary)',
                        color: 'var(--theme-text-primary)',
                        border: '1px solid var(--theme-border-primary)'
                      }}
                      aria-label="Jump to page"
                    >
                      <option value="" disabled>Jump to...</option>
                      {Array.from({ length: totalPages }, (_, i) => i + 1).map(pageNum => (
                        <option key={pageNum} value={pageNum}>
                          Page {pageNum}
                        </option>
                      ))}
                    </select>
                  </>
                )}
              </div>
            </div>
          </Card>
        </div>
      )}

      {/* Performance warning */}
      {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 500 && (
        <Alert color="yellow" icon={<AlertTriangle className="w-5 h-5" />}>
          Loading {itemsToDisplay.length} items. Consider using pagination for better performance.
        </Alert>
      )}
    </div>
  );
};

export default DownloadsTab;


