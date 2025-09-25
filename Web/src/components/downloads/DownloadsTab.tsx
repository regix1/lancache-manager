import React, { useState, useEffect, useMemo, useRef } from 'react';
import {
  ChevronDown,
  Database,
  AlertTriangle,
  Settings,
  Download as DownloadIcon,
  Loader,
  List,
  Grid3x3,
} from 'lucide-react';
import { useData } from '../../contexts/DataContext'; // Fixed import path
import { formatBytes, formatPercent } from '../../utils/formatters'; // Fixed import path
import { Alert } from '../ui/Alert'; // Fixed import path
import { Card } from '../ui/Card'; // Fixed import path

// Import view components
import CompactView from './CompactView';
import NormalView from './NormalView';

import type { Download, DownloadGroup } from '../../types';

// Storage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small',
  HIDE_LOCALHOST: 'lancache_downloads_hide_localhost',
  HIDE_UNKNOWN_GAMES: 'lancache_downloads_hide_unknown',
  VIEW_MODE: 'lancache_downloads_view_mode',
  SORT_ORDER: 'lancache_downloads_sort_order'
};

// View modes
type ViewMode = 'compact' | 'normal';

// Enhanced Dropdown Component
interface DropdownOption {
  value: string;
  label: string;
}

interface EnhancedDropdownProps {
  options: DropdownOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
}

const EnhancedDropdown: React.FC<EnhancedDropdownProps> = ({
  options,
  value,
  onChange,
  placeholder = 'Select option',
  className = ''
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const selectedOption = options.find((opt) => opt.value === value);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (
        dropdownRef.current &&
        !dropdownRef.current.contains(event.target as Node) &&
        buttonRef.current &&
        !buttonRef.current.contains(event.target as Node)
      ) {
        setIsOpen(false);
      }
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
  }, [isOpen]);

  const handleSelect = (optionValue: string) => {
    onChange(optionValue);
    setIsOpen(false);
  };

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        className="w-full px-3 py-2 rounded-lg border text-[var(--theme-text-primary)] text-left focus:outline-none transition-colors flex items-center justify-between"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: 'var(--theme-border-primary)'
        }}
        onMouseEnter={(e) =>
          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)')
        }
        onMouseLeave={(e) =>
          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
        }
      >
        <span className="truncate">{selectedOption ? selectedOption.label : placeholder}</span>
        <ChevronDown size={16} className={`transition-transform ${isOpen ? 'rotate-180' : ''}`} />
      </button>

      {isOpen && (
        <div
          ref={dropdownRef}
          className="absolute mt-1 w-full rounded-lg border shadow-xl z-[9999]"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)',
            maxHeight: '300px',
            overflowY: 'auto'
          }}
        >
          <div className="py-1">
            {options.map((option) => (
              <button
                key={option.value}
                type="button"
                onClick={() => handleSelect(option.value)}
                className={`w-full px-4 py-2 text-left text-sm hover:bg-[var(--theme-bg-tertiary)] transition-colors ${
                  option.value === value
                    ? 'bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]'
                    : 'text-[var(--theme-text-secondary)]'
                }`}
              >
                {option.label}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

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

const convertGroupsToCSV = (groups: DownloadGroup[]): string => {
  if (!groups || groups.length === 0) return '';

  const headers = [
    'name', 'type', 'service', 'downloadCount', 'totalBytes', 'cacheHitBytes',
    'cacheMissBytes', 'cacheHitPercent', 'uniqueClients', 'firstSeen', 'lastSeen'
  ];
  const csvHeaders = headers.join(',');

  const csvRows = groups.map(group => {
    const cacheHitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    const row = [
      group.name,
      group.type,
      group.service,
      group.count,
      group.totalBytes,
      group.cacheHitBytes,
      group.cacheMissBytes,
      cacheHitPercent.toFixed(1),
      group.clientsSet.size,
      group.firstSeen,
      group.lastSeen
    ];

    return row.map(value => {
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

  // State management
  const [expandedItem, setExpandedItem] = useState<string | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [filterLoading, setFilterLoading] = useState(false);
  const [exportLoading, setExportLoading] = useState(false);
  const [showExportOptions, setShowExportOptions] = useState(false);

  const [settings, setSettings] = useState(() => ({
    showZeroBytes: localStorage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
    showSmallFiles: localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
    hideLocalhost: localStorage.getItem(STORAGE_KEYS.HIDE_LOCALHOST) === 'true',
    hideUnknownGames: localStorage.getItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES) === 'true',
    selectedService: localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
    itemsPerPage:
      localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) === 'unlimited'
        ? 'unlimited' as const
        : parseInt(localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) || '50'),
    viewMode: (localStorage.getItem(STORAGE_KEYS.VIEW_MODE) || 'normal') as ViewMode,
    sortOrder: (localStorage.getItem(STORAGE_KEYS.SORT_ORDER) || 'latest') as 'latest' | 'oldest' | 'largest' | 'smallest' | 'service'
  }));

  // Effect to save settings to localStorage
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, settings.itemsPerPage.toString());
    localStorage.setItem(STORAGE_KEYS.VIEW_MODE, settings.viewMode);
    localStorage.setItem(STORAGE_KEYS.SORT_ORDER, settings.sortOrder);
  }, [settings]);

  // FIXED: Update download count when items per page changes
  useEffect(() => {
    const count = settings.itemsPerPage === 'unlimited' ? 10000 : settings.itemsPerPage;
    if (mockMode && updateMockDataCount) {
      updateMockDataCount(count);
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount(count);
    }
  }, [settings.itemsPerPage, mockMode, updateMockDataCount, updateApiDownloadCount]);

  // Track filter changes and show loading state
  useEffect(() => {
    if (!loading && latestDownloads.length > 0) {
      setFilterLoading(true);

      // Clear loading state after a short delay to show spinner briefly
      const timer = setTimeout(() => {
        setFilterLoading(false);
      }, 300);

      return () => clearTimeout(timer);
    }
  }, [settings.selectedService, settings.sortOrder, settings.showZeroBytes, settings.showSmallFiles, settings.hideLocalhost, settings.hideUnknownGames, settings.viewMode]);

  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map((d) => d.service.toLowerCase()));
    return Array.from(services).sort();
  }, [latestDownloads]);

  const serviceOptions = useMemo(
    () => [
      { value: 'all', label: 'All Services' },
      ...availableServices.map((service) => ({
        value: service,
        label: service.charAt(0).toUpperCase() + service.slice(1)
      }))
    ],
    [availableServices]
  );

  const itemsPerPageOptions = useMemo(
    () => [
      { value: '20', label: '20 items' },
      { value: '50', label: '50 items' },
      { value: '100', label: '100 items' },
      { value: '200', label: '200 items' },
      { value: 'unlimited', label: 'Load All' }
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
      filtered = filtered.filter(
        (d) => {
          if (!d.gameName) return true;
          if (d.gameName === 'Unknown Steam Game') return false;
          if (d.gameName.match(/^Steam App \d+$/)) return false;
          return true;
        }
      );
    }

    if (settings.selectedService !== 'all') {
      filtered = filtered.filter((d) => d.service.toLowerCase() === settings.selectedService);
    }

    return filtered;
  }, [latestDownloads, settings.showZeroBytes, settings.showSmallFiles, settings.hideLocalhost, settings.hideUnknownGames, settings.selectedService]);

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
        groupName = `${download.service} Downloads`;
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
          cacheHitBytes: 0,
          cacheMissBytes: 0,
          clientsSet: new Set<string>(),
          firstSeen: download.startTime,
          lastSeen: download.startTime,
          count: 0
        };
      }

      groups[groupKey].downloads.push(download);
      groups[groupKey].totalBytes += download.totalBytes || 0;
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

    // Convert single-download groups back to individual downloads
    const finalGroups: DownloadGroup[] = [];
    const finalIndividuals: Download[] = [...individuals];

    groups.forEach(group => {
      if (group.count === 1 && group.type === 'game') {
        finalIndividuals.push(group.downloads[0]);
      } else {
        finalGroups.push(group);
      }
    });

    const allItems: (Download | DownloadGroup)[] = [...finalGroups, ...finalIndividuals];

    return allItems.sort((a, b) => {
      const aTime = 'downloads' in a
        ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(a.startTime).getTime();
      const bTime = 'downloads' in b
        ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(b.startTime).getTime();
      return bTime - aTime;
    });
  }, [filteredDownloads, settings.viewMode]);

  const compactViewItems = useMemo((): (Download | DownloadGroup)[] => {
    if (settings.viewMode !== 'compact') return [];

    const { groups, individuals } = createGroups(filteredDownloads);

    // Convert single-download groups back to individual downloads
    const finalGroups: DownloadGroup[] = [];
    const finalIndividuals: Download[] = [...individuals];

    groups.forEach(group => {
      if (group.count === 1 && group.type === 'game') {
        finalIndividuals.push(group.downloads[0]);
      } else {
        finalGroups.push(group);
      }
    });

    const allItems: (Download | DownloadGroup)[] = [...finalGroups, ...finalIndividuals];

    return allItems.sort((a, b) => {
      const aTime = 'downloads' in a
        ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(a.startTime).getTime();
      const bTime = 'downloads' in b
        ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
        : new Date(b.startTime).getTime();
      return bTime - aTime;
    });
  }, [filteredDownloads, settings.viewMode]);

  const itemsToDisplay = useMemo(() => {
    let items = settings.viewMode === 'normal' ? normalViewItems :
                settings.viewMode === 'compact' ? compactViewItems :
                filteredDownloads;

    // Apply sorting
    if (settings.viewMode === 'normal' || settings.viewMode === 'compact') {
      const mixedItems = [...items] as (Download | DownloadGroup)[];
      switch (settings.sortOrder) {
        case 'oldest':
          mixedItems.sort((a, b) => {
            const aTime = 'downloads' in a
              ? Math.min(...a.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(a.startTime).getTime();
            const bTime = 'downloads' in b
              ? Math.min(...b.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(b.startTime).getTime();
            return aTime - bTime;
          });
          break;
        case 'largest':
          mixedItems.sort((a, b) => {
            const aBytes = 'downloads' in a ? a.totalBytes : (a.totalBytes || 0);
            const bBytes = 'downloads' in b ? b.totalBytes : (b.totalBytes || 0);
            return bBytes - aBytes;
          });
          break;
        case 'smallest':
          mixedItems.sort((a, b) => {
            const aBytes = 'downloads' in a ? a.totalBytes : (a.totalBytes || 0);
            const bBytes = 'downloads' in b ? b.totalBytes : (b.totalBytes || 0);
            return aBytes - bBytes;
          });
          break;
        case 'service':
          mixedItems.sort((a, b) => {
            const serviceCompare = a.service.localeCompare(b.service);
            if (serviceCompare !== 0) return serviceCompare;
            const aLatest = 'downloads' in a
              ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(a.startTime).getTime();
            const bLatest = 'downloads' in b
              ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(b.startTime).getTime();
            return bLatest - aLatest;
          });
          break;
        case 'latest':
        default:
          mixedItems.sort((a, b) => {
            const aLatest = 'downloads' in a
              ? Math.max(...a.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(a.startTime).getTime();
            const bLatest = 'downloads' in b
              ? Math.max(...b.downloads.map(d => new Date(d.startTime).getTime()))
              : new Date(b.startTime).getTime();
            return bLatest - aLatest;
          });
          break;
      }
      items = mixedItems;
    }

    // Apply pagination
    if (settings.itemsPerPage !== 'unlimited') {
      return items.slice(0, settings.itemsPerPage);
    }

    return items;
  }, [
    filteredDownloads,
    normalViewItems,
    compactViewItems,
    settings.viewMode,
    settings.sortOrder,
    settings.itemsPerPage
  ]);

  const handleExport = (format: 'json' | 'csv') => {
    setExportLoading(true);
    try {
      let content = '';
      let filename = '';
      let mimeType = '';

      const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5);
      const baseFilename = `lancache_downloads_${timestamp}`;

      if (format === 'csv') {
        // Filter out groups from mixed items for CSV export
        const downloads = itemsToDisplay.filter(item => !('downloads' in item)) as Download[];
        content = convertDownloadsToCSV(downloads);
        filename = `${baseFilename}.csv`;
        mimeType = 'text/csv';
      } else {
        content = JSON.stringify(itemsToDisplay, null, 2);
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

  // Loading state
  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[400px] animate-fade-in">
        <div className="animate-pulse">
          <Loader className="w-8 h-8 animate-spin text-themed-primary" />
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
            <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center flex-1 w-full sm:w-auto">
              <EnhancedDropdown
                options={serviceOptions}
                value={settings.selectedService}
                onChange={(value) =>
                  setSettings({ ...settings, selectedService: value })
                }
                className="w-full sm:w-40"
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
                className="w-full sm:w-32"
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
                className="w-full sm:w-40"
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

          {/* Mobile view mode selector */}
          <div className="flex sm:hidden rounded-lg bg-themed-tertiary p-1">
            <button
              onClick={() => setSettings({ ...settings, viewMode: 'compact' })}
              className={`flex-1 px-2 py-1.5 rounded-md transition-colors ${
                settings.viewMode === 'compact'
                  ? 'bg-primary'
                  : 'text-themed-secondary'
              }`}
              style={{
                color: settings.viewMode === 'compact' ? 'var(--theme-button-text)' : undefined
              }}
            >
              <List size={16} className="mx-auto" />
            </button>
            <button
              onClick={() => setSettings({ ...settings, viewMode: 'normal' })}
              className={`flex-1 px-2 py-1.5 rounded-md transition-colors ${
                settings.viewMode === 'normal'
                  ? 'bg-primary'
                  : 'text-themed-secondary'
              }`}
              style={{
                color: settings.viewMode === 'normal' ? 'var(--theme-button-text)' : undefined
              }}
            >
              <Grid3x3 size={16} className="mx-auto" />
            </button>
          </div>
        </div>

        {settingsOpened && (
          <>
            <div className="border-t border-themed-secondary my-3 animate-fade-in" />
            <div className="space-y-2 animate-slide-in-top">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={settings.showZeroBytes}
                  onChange={(e) =>
                    setSettings({ ...settings, showZeroBytes: e.target.checked })
                  }
                  className="themed-checkbox"
                />
                <span className="text-sm text-themed-secondary">Show metadata (0 bytes)</span>
              </label>

              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={settings.showSmallFiles}
                  onChange={(e) =>
                    setSettings({ ...settings, showSmallFiles: e.target.checked })
                  }
                  className="themed-checkbox"
                />
                <span className="text-sm text-themed-secondary">Show small files (&lt; 1MB)</span>
              </label>

              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={settings.hideLocalhost}
                  onChange={(e) =>
                    setSettings({ ...settings, hideLocalhost: e.target.checked })
                  }
                  className="themed-checkbox"
                />
                <span className="text-sm text-themed-secondary">Hide localhost (127.0.0.1)</span>
              </label>

              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={settings.hideUnknownGames}
                  onChange={(e) =>
                    setSettings({ ...settings, hideUnknownGames: e.target.checked })
                  }
                  className="themed-checkbox"
                />
                <span className="text-sm text-themed-secondary">Hide unknown games</span>
              </label>
            </div>
          </>
        )}
      </Card>

      {/* Stats */}
      {filteredDownloads.length !== serviceFilteredDownloads.length && (
        <Alert color="blue" icon={<Database className="w-5 h-5" />}>
          Showing {filteredDownloads.length} of {serviceFilteredDownloads.length} downloads
          {settings.selectedService !== 'all' && ` for ${settings.selectedService}`}
        </Alert>
      )}

      {/* Downloads list */}
      <div className="relative">
        {/* Loading overlay for filter changes */}
        {filterLoading && (
          <div className="absolute inset-0 bg-[var(--theme-bg-primary)]/80 backdrop-blur-sm flex items-center justify-center z-10 rounded-lg animate-fade-in">
            <div className="flex items-center gap-3 px-4 py-2 rounded-lg bg-[var(--theme-bg-secondary)] border shadow-lg animate-zoom-in" style={{ borderColor: 'var(--theme-border-primary)' }}>
              <Loader className="w-5 h-5 animate-spin text-[var(--theme-primary)]" />
              <span className="text-sm font-medium text-[var(--theme-text-primary)]">Applying filters...</span>
            </div>
          </div>
        )}

        {/* Content based on view mode */}
        <div className="transition-all duration-300 ease-in-out">
          {settings.viewMode === 'compact' && (
            <CompactView
              items={itemsToDisplay as (Download | DownloadGroup)[]}
              expandedItem={expandedItem}
              onItemClick={handleItemClick}
            />
          )}

          {settings.viewMode === 'normal' && (
            <NormalView
              items={itemsToDisplay as (Download | DownloadGroup)[]}
              expandedItem={expandedItem}
              onItemClick={handleItemClick}
            />
          )}

        </div>
      </div>

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