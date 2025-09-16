import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import {
  ChevronRight,
  ChevronDown,
  Gamepad2,
  ExternalLink,
  Database,
  CloudOff,
  Check,
  AlertTriangle,
  Layers,
  Users,
  Settings,
  Download as DownloadIcon,
  Loader,
  List,
  Grid3x3,
  Clock
} from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { API_BASE } from '../../utils/constants';
import VirtualizedList from '../common/VirtualizedList';
import { Alert } from '../ui/Alert';
import { Card } from '../ui/Card';
import type {
  Download,
  GameInfo,
  DownloadGroup
} from '../../types';

// Storage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small',
  HIDE_LOCALHOST: 'lancache_downloads_hide_localhost',
  HIDE_UNKNOWN_GAMES: 'lancache_downloads_hide_unknown',
  VIEW_MODE: 'lancache_downloads_view_mode',
  SORT_ORDER: 'lancache_downloads_sort_order'
};

// Helper function to format relative time
const formatRelativeTime = (dateString: string): string => {
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffSecs = Math.floor(diffMs / 1000);
  const diffMins = Math.floor(diffSecs / 60);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffDays > 0) return `${diffDays}d ago`;
  if (diffHours > 0) return `${diffHours}h ago`;
  if (diffMins > 0) return `${diffMins}m ago`;
  return 'Just now';
};

// Enhanced Image component with built-in fallback
interface ImageWithFallbackProps {
  src: string;
  fallback?: React.ReactNode;
  alt: string;
  className?: string;
  style?: React.CSSProperties;
  onLoad?: () => void;
  onError?: () => void;
}

const ImageWithFallback: React.FC<ImageWithFallbackProps> = ({
  src,
  fallback,
  alt,
  className = '',
  style = {},
  onLoad,
  onError
}) => {
  const [hasError, setHasError] = useState(false);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    // Reset states when src changes
    setHasError(false);
    setIsLoading(true);
  }, [src]);

  const handleLoad = () => {
    setIsLoading(false);
    onLoad?.();
  };

  const handleError = () => {
    setHasError(true);
    setIsLoading(false);
    onError?.();
  };

  if (!src || hasError) {
    return (
      <>
        {fallback || (
          <div 
            className={`${className} flex items-center justify-center`}
            style={{
              ...style,
              backgroundColor: 'var(--theme-bg-tertiary)',
              border: '1px solid var(--theme-border-primary)'
            }}
          >
            <Gamepad2 
              className="w-12 h-12"
              style={{ color: 'var(--theme-text-muted)' }}
            />
          </div>
        )}
      </>
    );
  }

  return (
    <div className="relative">
      {isLoading && (
        <div 
          className={`${className} flex items-center justify-center absolute inset-0`}
          style={{
            ...style,
            backgroundColor: 'var(--theme-bg-tertiary)'
          }}
        >
          <Loader className="w-6 h-6 animate-spin" />
        </div>
      )}
      <img
        src={src}
        alt={alt}
        className={className}
        style={{
          ...style,
          opacity: isLoading ? 0 : 1,
          transition: 'opacity 0.3s'
        }}
        onLoad={handleLoad}
        onError={handleError}
      />
    </div>
  );
};

// Enhanced Dropdown with Portal rendering
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

  // Close dropdown when clicking outside
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

  const handleKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      setIsOpen(!isOpen);
    }
  };

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        onKeyDown={handleKeyDown}
        className="w-full px-3 py-2 rounded-lg themed-input text-themed-primary text-left focus:outline-none hover:bg-themed-hover transition-colors flex items-center justify-between"
        aria-haspopup="listbox"
        aria-expanded={isOpen}
      >
        <span className="truncate">{selectedOption ? selectedOption.label : placeholder}</span>
        <ChevronDown size={16} className={`transition-transform ${isOpen ? 'rotate-180' : ''}`} />
      </button>
      
      {isOpen && (
        <div
          ref={dropdownRef}
          className="absolute mt-1 w-full rounded-lg themed-card shadow-xl border border-themed-border z-[9999]"
          style={{
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
                className={`w-full px-4 py-2 text-left text-sm hover:bg-themed-hover transition-colors ${
                  option.value === value
                    ? 'bg-themed-hover text-themed-accent'
                    : 'text-themed-secondary'
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
  const [expandedDownload, setExpandedDownload] = useState<number | null>(null);
  const [expandedGroup, setExpandedGroup] = useState<string | null>(null);
  const [gameInfo, setGameInfo] = useState<Record<number, GameInfo>>({});
  const [loadingGame, setLoadingGame] = useState<number | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);

  const [settings, setSettings] = useState(() => ({
    showZeroBytes: localStorage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
    showSmallFiles: localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
    hideLocalhost: localStorage.getItem(STORAGE_KEYS.HIDE_LOCALHOST) === 'true',
    hideUnknownGames: localStorage.getItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES) === 'true',
    selectedService: localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
    groupGames: localStorage.getItem(STORAGE_KEYS.GROUP_GAMES) === 'true',
    itemsPerPage:
      localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) === 'unlimited'
        ? 'unlimited' as const
        : parseInt(localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) || '50'),
    viewMode: (localStorage.getItem(STORAGE_KEYS.VIEW_MODE) || 'compact') as 'compact' | 'normal',
    sortOrder: (localStorage.getItem(STORAGE_KEYS.SORT_ORDER) || 'latest') as 'latest' | 'oldest' | 'largest' | 'smallest' | 'service'
  }));

  // Helper functions
  const getDownloadTypeInfo = (download: Download) => {
    const totalBytes = download.totalBytes || 0;
    const isMissingData = totalBytes === 0;
    const cachedBytes = download.cacheHitBytes || 0;
    const isCached = cachedBytes > 0;
    const cachePercentage = totalBytes > 0 ? (cachedBytes / totalBytes) * 100 : 0;

    if (isMissingData) {
      return {
        type: 'metadata' as const,
        icon: Database,
        iconColor: 'text-purple-400',
        description: 'Metadata/Configuration',
        label: 'Metadata'
      };
    } else if (cachePercentage === 100) {
      return {
        type: 'content' as const,
        icon: Check,
        iconColor: 'text-green-400',
        description: 'Fully Cached',
        label: 'Cached'
      };
    } else if (isCached) {
      return {
        type: 'content' as const,
        icon: DownloadIcon,
        iconColor: 'text-yellow-400',
        description: `${cachePercentage.toFixed(0)}% Cached`,
        label: 'Partial'
      };
    } else {
      return {
        type: 'content' as const,
        icon: CloudOff,
        iconColor: 'text-gray-400',
        description: 'Not Cached',
        label: 'Uncached'
      };
    }
  };

  const fetchGameInfo = async (download: Download) => {
    // Since there's no Steam API endpoint, we'll create mock game info from the download data
    if (!download.id || !download.gameName || download.service.toLowerCase() !== 'steam') {
      return;
    }

    if (gameInfo[download.id]) {
      return;
    }

    setLoadingGame(download.id);

    // Simulate loading delay
    await new Promise(resolve => setTimeout(resolve, 300));

    // Create game info from download data
    const mockGameInfo: GameInfo = {
      downloadId: download.id!,
      service: download.service,
      appId: download.gameAppId || 0,
      gameName: download.gameName,
      headerImage: `${API_BASE}/gameimages/${download.gameAppId}/header/`,
      description: `${download.gameName} - Downloaded via ${download.service}`,
      gameType: 'game'
    };

    setGameInfo(prev => ({
      ...prev,
      [download.id!]: mockGameInfo
    }));
    
    setLoadingGame(null);
  };

  // Pre-populate game info for Steam games
  useEffect(() => {
    const steamDownloads = latestDownloads.filter(
      d => d.service.toLowerCase() === 'steam' && 
      d.gameName && 
      d.gameName !== 'Unknown Steam Game' &&
      d.gameAppId
    );
    
    const preloadedInfo: Record<number, GameInfo> = {};
    steamDownloads.forEach(download => {
      if (download.id && !gameInfo[download.id]) {
        preloadedInfo[download.id] = {
          downloadId: download.id,
          service: download.service,
          appId: download.gameAppId || 0,
          gameName: download.gameName || 'Unknown Game',
          headerImage: `${API_BASE}/gameimages/${download.gameAppId}/header/`,
          description: `${download.gameName} - Downloaded via Steam`,
          gameType: 'game'
        };
      }
    });
    
    if (Object.keys(preloadedInfo).length > 0) {
      setGameInfo(prev => ({ ...prev, ...preloadedInfo }));
    }
  }, [latestDownloads]);

  // Update download count when items per page changes
  useEffect(() => {
    const count = settings.itemsPerPage === 'unlimited' ? 10000 : settings.itemsPerPage;
    if (mockMode && updateMockDataCount) {
      updateMockDataCount(count);
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount(count);
    }
  }, [settings.itemsPerPage, mockMode, updateMockDataCount, updateApiDownloadCount]);

  // Persist settings
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(
      STORAGE_KEYS.ITEMS_PER_PAGE,
      settings.itemsPerPage === 'unlimited' ? 'unlimited' : settings.itemsPerPage.toString()
    );
    localStorage.setItem(STORAGE_KEYS.GROUP_GAMES, settings.groupGames.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
    localStorage.setItem(STORAGE_KEYS.VIEW_MODE, settings.viewMode);
    localStorage.setItem(STORAGE_KEYS.SORT_ORDER, settings.sortOrder);
  }, [settings]);

  // Memoized values
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
          // Hide "Unknown Steam Game" and "Steam App XXXXX" patterns
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
  }, [latestDownloads, settings]);

  const groupedDownloads = useMemo((): DownloadGroup[] | null => {
    if (!settings.groupGames) return null;

    const groups: Record<string, DownloadGroup> = {};

    filteredDownloads.forEach((download) => {
      let groupKey: string;
      let groupName: string;
      let groupType: 'game' | 'metadata' | 'content';

      if (download.gameName &&
          download.gameName !== 'Unknown Steam Game' &&
          !download.gameName.match(/^Steam App \d+$/)) {
        groupKey = `game-${download.gameName}`;
        groupName = download.gameName;
        groupType = 'game';
      } else if ((download.totalBytes || 0) === 0) {
        groupKey = `metadata-${download.service}`;
        groupName = `${download.service} Metadata`;
        groupType = 'metadata';
      } else {
        groupKey = `content-${download.service}`;
        groupName = `${download.service} Content`;
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
      
      // Update first and last seen times
      if (download.startTime < groups[groupKey].firstSeen) {
        groups[groupKey].firstSeen = download.startTime;
      }
      if (download.startTime > groups[groupKey].lastSeen) {
        groups[groupKey].lastSeen = download.startTime;
      }
    });

    return Object.values(groups).sort((a, b) => {
      if (a.type === 'game' && b.type !== 'game') return -1;
      if (a.type !== 'game' && b.type === 'game') return 1;
      return b.totalBytes - a.totalBytes;
    });
  }, [filteredDownloads, settings.groupGames]);

  const itemsToDisplay = useMemo(() => {
    try {
    let items = settings.groupGames ? groupedDownloads || [] : filteredDownloads;

    // Apply sorting
    if (!settings.groupGames) {
      // Sort individual downloads
      const downloads = [...items] as Download[];
      switch (settings.sortOrder) {
        case 'oldest':
          downloads.sort((a, b) => new Date(a.startTime).getTime() - new Date(b.startTime).getTime());
          break;
        case 'largest':
          downloads.sort((a, b) => (b.totalBytes || 0) - (a.totalBytes || 0));
          break;
        case 'smallest':
          downloads.sort((a, b) => (a.totalBytes || 0) - (b.totalBytes || 0));
          break;
        case 'service':
          downloads.sort((a, b) => {
            const serviceCompare = a.service.localeCompare(b.service);
            if (serviceCompare !== 0) return serviceCompare;
            return new Date(b.startTime).getTime() - new Date(a.startTime).getTime();
          });
          break;
        case 'latest':
        default:
          downloads.sort((a, b) => new Date(b.startTime).getTime() - new Date(a.startTime).getTime());
          break;
      }
      items = downloads;
    } else {
      // Sort grouped downloads
      const groups = [...items] as DownloadGroup[];
      switch (settings.sortOrder) {
        case 'oldest':
          groups.sort((a, b) => new Date(a.firstSeen).getTime() - new Date(b.firstSeen).getTime());
          break;
        case 'largest':
          groups.sort((a, b) => b.totalBytes - a.totalBytes);
          break;
        case 'smallest':
          groups.sort((a, b) => a.totalBytes - b.totalBytes);
          break;
        case 'service':
          groups.sort((a, b) => {
            const serviceCompare = a.service.localeCompare(b.service);
            if (serviceCompare !== 0) return serviceCompare;
            return new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime();
          });
          break;
        case 'latest':
        default:
          groups.sort((a, b) => new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime());
          break;
      }
      items = groups;
    }

    if (settings.itemsPerPage === 'unlimited') {
      // No cap - load all available items
      return items;
    }
    const limit = typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 50;
    return items.slice(0, limit);
    } catch (error) {
      console.error('Error preparing items to display:', error);
      return [];
    }
  }, [settings.groupGames, settings.itemsPerPage, groupedDownloads, filteredDownloads, settings.sortOrder]);

  // Event handlers
  const handleDownloadClick = async (download: Download) => {
    const isSteam = download.service.toLowerCase() === 'steam';
    const hasData = (download.totalBytes || 0) > 0;
    const canExpand = isSteam && hasData && download.gameName &&
                       download.gameName !== 'Unknown Steam Game' &&
                       !download.gameName.match(/^Steam App \d+$/);
    
    if (!canExpand) return;
    
    const newExpanded = expandedDownload === download.id ? null : download.id;
    setExpandedDownload(newExpanded);
    
    if (newExpanded && !gameInfo[download.id!]) {
      await fetchGameInfo(download);
    }
  };

  const handleGroupClick = (groupId: string) => {
    setExpandedGroup(expandedGroup === groupId ? null : groupId);
  };

  // Render functions
  const renderGroup = useCallback((group: DownloadGroup) => {
    const isExpanded = expandedGroup === group.id;
    const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;

    return (
      <Card key={group.id} padding="sm">
        <div onClick={() => handleGroupClick(group.id)} className="cursor-pointer">
          <div className="flex items-center justify-between py-1">
            <div className="flex items-center gap-3">
              <div className="flex items-center gap-2">
                <ChevronRight
                  size={16}
                  className={`transition-transform ${isExpanded ? 'rotate-90' : ''}`}
                />
                <div className="w-6 h-6 rounded bg-themed-secondary flex items-center justify-center">
                  <Layers size={14} className="text-themed-accent" />
                </div>
                <span className="text-sm font-medium text-themed-accent">{group.name}</span>
              </div>
              <span className="text-xs text-themed-muted">({group.count} items)</span>
            </div>
            
            <div className="flex items-center gap-6">
              <div className="text-right">
                <div className="text-sm font-medium text-themed-primary">
                  {formatBytes(group.totalBytes)}
                </div>
                {group.totalBytes > 0 && (
                  <div className="text-xs text-themed-muted">
                    {formatPercent(hitPercent)} cached
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>

        {isExpanded && (
          <>
            <div className="border-t border-themed-secondary my-4" />
            <div className="max-h-96 overflow-y-auto">
              <div className="space-y-1">
                {group.downloads.map((d) => (
                  <div key={d.id} className="p-3 rounded bg-themed-secondary">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-3">
                        <span className={`text-xs font-medium service-${d.service.toLowerCase()}`}>
                          {d.service}
                        </span>
                        <span className="text-xs text-themed-muted">{d.clientIp}</span>
                      </div>
                      <div className="text-xs text-themed-muted">
                        {formatBytes(d.totalBytes || 0)}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </>
        )}
      </Card>
    );
  }, [expandedGroup]);

  const renderDownload = useCallback((download: Download) => {
    try {
      if (!download) {
        console.error('Undefined download in renderDownload');
        return null;
      }
      const isExpanded = expandedDownload === download.id;
    const isSteam = download.service.toLowerCase() === 'steam';
    const hasData = (download.totalBytes || 0) > 0;
    const canExpand = isSteam && hasData && download.gameName &&
                       download.gameName !== 'Unknown Steam Game' &&
                       !download.gameName.match(/^Steam App \d+$/);
    const downloadType = getDownloadTypeInfo(download);
    const IconComponent = downloadType.icon;
    const game = download.id ? gameInfo[download.id] : undefined;

    // Normal view - show more details inline
    if (settings.viewMode === 'normal') {
      // For Steam games with names, show the full experience
      if (isSteam && download.gameName &&
          download.gameName !== 'Unknown Steam Game' &&
          !download.gameName.match(/^Steam App \d+$/)) {
      return (
        <Card key={download.id} padding="md" className="mb-3">
          <div className="flex gap-4 items-start">
            {/* Game header image */}
            <div className="flex-shrink-0">
              <ImageWithFallback
                src={`${API_BASE}/gameimages/${download.gameAppId}/header/`}
                alt={download.gameName || 'Game'}
                className="rounded shadow-md"
                style={{ width: '184px', height: '88px', objectFit: 'cover' }}
                fallback={
                  <div
                    className="rounded flex items-center justify-center shadow-md"
                    style={{
                      width: '184px',
                      height: '88px',
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      border: '1px solid var(--theme-border-primary)'
                    }}
                  >
                    <Gamepad2
                      className="w-10 h-10"
                      style={{ color: 'var(--theme-text-muted)' }}
                    />
                  </div>
                }
              />
            </div>

            {/* Game details */}
            <div className="flex-1 min-w-0">
              <div className="flex items-start justify-between">
                <div className="flex-1">
                  <div className="flex items-center gap-3 mb-1">
                    <span className={`text-sm font-medium service-${download.service.toLowerCase()}`}>
                      {download.service}
                    </span>
                    <h3 className="text-base font-semibold text-themed-primary truncate">
                      {download.gameName}
                    </h3>
                  </div>

                  <div className="flex items-center gap-4 text-sm text-themed-secondary mb-2">
                    <div className="flex items-center gap-2">
                      <IconComponent size={14} className={downloadType.iconColor} />
                      <span>{downloadType.description}</span>
                    </div>
                    <div className="flex items-center gap-2">
                      <Users size={14} />
                      <span>{download.clientIp}</span>
                    </div>
                    <div className="flex items-center gap-2">
                      <Clock size={14} />
                      <span>{formatRelativeTime(download.startTime)}</span>
                    </div>
                  </div>

                  <div className="flex items-center gap-6">
                    <div>
                      <span className="text-xs text-themed-muted">Size: </span>
                      <span className="text-sm font-medium text-themed-primary">
                        {formatBytes(download.totalBytes || 0)}
                      </span>
                    </div>
                    {download.totalBytes && download.totalBytes > 0 && (
                      <div>
                        <span className="text-xs text-themed-muted">Cache Hit: </span>
                        <span className="text-sm font-medium text-themed-primary">
                          {formatPercent((download.cacheHitBytes || 0) / download.totalBytes * 100)}
                        </span>
                      </div>
                    )}
                    {download.cacheHitBytes > 0 && (
                      <div>
                        <span className="text-xs text-themed-muted">Saved: </span>
                        <span className="text-sm font-medium text-green-500">
                          {formatBytes(download.cacheHitBytes)}
                        </span>
                      </div>
                    )}
                  </div>
                </div>

                {/* Steam link button */}
                <div className="ml-4">
                  <a
                    href={`https://store.steampowered.com/app/${download.gameAppId}`}
                    target="_blank"
                    rel="noopener noreferrer"
                    onClick={(e) => e.stopPropagation()}
                    className="inline-flex items-center gap-2 px-3 py-1.5 rounded bg-themed-secondary hover:bg-themed-hover transition-colors text-xs text-themed-accent"
                  >
                    <ExternalLink size={14} />
                    <span>View on Steam</span>
                  </a>
                </div>
              </div>
            </div>
          </div>
        </Card>
      );
      }

      // Normal view for non-Steam or other downloads
      return (
        <Card key={download.id} padding="md" className="mb-3">
          <div className="flex items-start justify-between">
            <div className="flex-1">
              <div className="flex items-center gap-3 mb-2">
                <span className={`text-sm font-medium service-${download.service.toLowerCase()}`}>
                  {download.service}
                </span>
                {download.gameName && download.gameName !== 'Unknown Steam Game' && (
                  <h3 className="text-base font-semibold text-themed-primary">
                    {download.gameName}
                  </h3>
                )}
                <div className="flex items-center gap-2">
                  <IconComponent size={14} className={downloadType.iconColor} />
                  <span className="text-sm text-themed-secondary">{downloadType.description}</span>
                </div>
              </div>

              <div className="flex items-center gap-6 text-sm text-themed-secondary">
                <div className="flex items-center gap-2">
                  <Users size={14} />
                  <span>{download.clientIp}</span>
                </div>
                <div className="flex items-center gap-2">
                  <Clock size={14} />
                  <span>{formatRelativeTime(download.startTime)}</span>
                </div>
                <div>
                  <span className="text-xs text-themed-muted">Size: </span>
                  <span className="font-medium text-themed-primary">
                    {formatBytes(download.totalBytes || 0)}
                  </span>
                </div>
                {download.totalBytes && download.totalBytes > 0 && (
                  <div>
                    <span className="text-xs text-themed-muted">Cache Hit: </span>
                    <span className="font-medium text-themed-primary">
                      {formatPercent((download.cacheHitBytes || 0) / download.totalBytes * 100)}
                    </span>
                  </div>
                )}
                {download.cacheHitBytes > 0 && (
                  <div>
                    <span className="text-xs text-themed-muted">Saved: </span>
                    <span className="font-medium text-green-500">
                      {formatBytes(download.cacheHitBytes)}
                    </span>
                  </div>
                )}
              </div>
            </div>
          </div>
        </Card>
      );
    }

    // Compact view - original expandable layout
    return (
      <Card key={download.id} padding="sm">
        <div onClick={() => canExpand ? handleDownloadClick(download) : undefined} className={canExpand ? 'cursor-pointer' : ''}>
          <div className="flex items-center justify-between py-1">
            <div className="flex items-center gap-3">
              <div className="flex items-center gap-2">
                {canExpand && (
                  <ChevronRight
                    size={16}
                    className={`transition-transform ${isExpanded ? 'rotate-90' : ''}`}
                  />
                )}
                <span className={`text-sm font-medium service-${download.service.toLowerCase()}`}>
                  {download.service}
                </span>
              </div>

              {download.gameName && download.gameName !== 'Unknown Steam Game' && (
                <span className="text-sm text-themed-primary font-medium">
                  {download.gameName}
                </span>
              )}

              <div className="flex items-center gap-2">
                <IconComponent size={14} className={downloadType.iconColor} />
                <span className="text-xs text-themed-muted">{downloadType.description}</span>
              </div>
            </div>

            <div className="flex items-center gap-6">
              <div className="flex items-center gap-2">
                <Users size={14} className="text-themed-muted" />
                <span className="text-xs text-themed-muted">{download.clientIp}</span>
              </div>

              <div className="text-right">
                <div className="text-sm font-medium text-themed-primary">
                  {formatBytes(download.totalBytes || 0)}
                </div>
                {download.totalBytes && download.totalBytes > 0 && (
                  <div className="text-xs text-themed-muted">
                    {formatPercent((download.cacheHitBytes || 0) / download.totalBytes * 100)} hit
                  </div>
                )}
              </div>
            </div>
          </div>

          {isExpanded && canExpand && (
            <>
              <div className="border-t border-themed-secondary my-4" />
              {loadingGame === download.id ? (
                <div className="flex justify-center py-4">
                  <Loader className="w-6 h-6 animate-spin" />
                </div>
              ) : game ? (
                <div className="flex gap-6 items-start">
                  <div className="flex-shrink-0">
                    <ImageWithFallback
                      src={game.headerImage || ''}
                      alt={game.gameName || 'Game'}
                      className="rounded shadow-lg"
                      style={{ width: '224px', height: '107px', objectFit: 'cover' }}
                      fallback={
                        <div 
                          className="rounded flex items-center justify-center shadow-lg"
                          style={{ 
                            width: '224px',
                            height: '107px',
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            border: '1px solid var(--theme-border-primary)'
                          }}
                        >
                          <Gamepad2 
                            className="w-12 h-12"
                            style={{ color: 'var(--theme-text-muted)' }}
                          />
                        </div>
                      }
                    />
                  </div>
                  <div className="flex-1 min-w-0">
                    <h3 className="text-lg font-semibold text-themed-primary mb-3 truncate">
                      {game.gameName || 'Unknown Game'}
                    </h3>
                    {game.description && (
                      <p className="text-sm text-themed-secondary mb-4 line-clamp-2">
                        {game.description}
                      </p>
                    )}
                    <div className="flex flex-wrap gap-4 text-xs text-themed-muted">
                      {game.gameType && (
                        <span>Type: {game.gameType}</span>
                      )}
                    </div>
                    {isSteam && (
                      <a
                        href={`https://store.steampowered.com/app/${game.appId}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        onClick={(e) => e.stopPropagation()}
                        className="inline-flex items-center gap-2 mt-4 px-3 py-1 rounded bg-themed-secondary hover:bg-themed-hover transition-colors text-xs text-themed-accent"
                      >
                        View on Steam
                        <ExternalLink size={12} />
                      </a>
                    )}
                  </div>
                </div>
              ) : (
                <div className="flex gap-6 items-start">
                  <div className="flex-shrink-0">
                    <div 
                      className="rounded flex items-center justify-center shadow-lg"
                      style={{ 
                        width: '224px',
                        height: '107px',
                        backgroundColor: 'var(--theme-bg-tertiary)',
                        border: '1px solid var(--theme-border-primary)'
                      }}
                    >
                      <Gamepad2 
                        className="w-12 h-12"
                        style={{ color: 'var(--theme-text-muted)' }}
                      />
                    </div>
                  </div>
                  <div className="flex-1 min-w-0">
                    <h3 className="text-lg font-semibold text-themed-primary mb-3 truncate">
                      {download.gameName || 'Loading...'}
                    </h3>
                    <p className="text-sm text-themed-secondary mb-4">
                      Loading game information...
                    </p>
                    {isSteam && download.gameAppId && (
                      <a
                        href={`https://store.steampowered.com/app/${download.gameAppId}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        onClick={(e) => e.stopPropagation()}
                        className="inline-flex items-center gap-2 mt-4 px-3 py-1 rounded bg-themed-secondary hover:bg-themed-hover transition-colors text-xs text-themed-accent"
                      >
                        View on Steam
                        <ExternalLink size={12} />
                      </a>
                    )}
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </Card>
    );
    } catch (error) {
      console.error('Error rendering download:', error, download);
      return (
        <Card key={download?.id || Math.random()} padding="sm">
          <div className="text-themed-muted">Error rendering download</div>
        </Card>
      );
    }
  }, [expandedDownload, gameInfo, loadingGame, settings.viewMode]);

  const renderVirtualItem = useCallback((item: any) => {
    if ('downloads' in item) {
      return renderGroup(item as DownloadGroup);
    }
    return renderDownload(item as Download);
  }, [renderGroup, renderDownload]);

  // Calculate dynamic height for virtualized list items based on content
  const getItemHeight = useCallback((_index: number, item: any): number => {
    if ('downloads' in item) {
      // Group height
      return settings.viewMode === 'normal' ? 220 : 180;
    }

    const download = item as Download;
    // Check if download has a displayable game image (not hardcoded to service type)
    const hasDisplayableGameInfo = download.gameAppId &&
                                   download.gameName &&
                                   download.gameName !== 'Unknown Steam Game' &&
                                   !download.gameName.match(/^Steam App \d+$/);

    if (settings.viewMode === 'normal') {
      // Downloads with images/game info have larger height
      if (hasDisplayableGameInfo) {
        return 120; // Height for downloads with header image
      }
      return 95; // Height for downloads without images
    } else {
      // Compact mode heights
      if (hasDisplayableGameInfo) {
        return 80; // Expandable items with game info
      }
      return 72; // Standard compact items
    }
  }, [settings.viewMode]);

  // Loading state
  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader className="w-8 h-8 animate-spin" />
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
    <div className="space-y-4">
      {/* Controls */}
      <Card padding="sm">
        <div className="flex flex-col sm:flex-row gap-4 items-start sm:items-center justify-between">
          <div className="flex flex-col sm:flex-row gap-2 items-start sm:items-center flex-1">
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
                { value: 'latest', label: 'Latest First' },
                { value: 'oldest', label: 'Oldest First' },
                { value: 'largest', label: 'Largest First' },
                { value: 'smallest', label: 'Smallest First' },
                { value: 'service', label: 'By Service' }
              ]}
              value={settings.sortOrder}
              onChange={(value) =>
                setSettings({ ...settings, sortOrder: value as any })
              }
              className="w-full sm:w-40"
            />
          </div>

          <div className="flex gap-2">
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
                <span className="text-xs hidden sm:inline">Compact</span>
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
                <span className="text-xs hidden sm:inline">Normal</span>
              </button>
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

        {settingsOpened && (
          <>
            <div className="border-t border-themed-secondary my-3" />
            <div className="space-y-2">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={settings.groupGames}
                  onChange={(e) =>
                    setSettings({ ...settings, groupGames: e.target.checked })
                  }
                  className="themed-checkbox"
                />
                <span className="text-sm text-themed-secondary">Group by games</span>
              </label>
              
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
      {filteredDownloads.length !== latestDownloads.length && (
        <Alert color="blue" icon={<Database className="w-5 h-5" />}>
          Showing {filteredDownloads.length} of {latestDownloads.length} downloads
          {settings.selectedService !== 'all' && ` for ${settings.selectedService}`}
        </Alert>
      )}

      {/* Downloads list */}
      <div>
        {(settings.itemsPerPage === 'unlimited' || settings.itemsPerPage >= 200) && itemsToDisplay.length >= 200 ? (
          <VirtualizedList
            items={itemsToDisplay}
            height={window.innerHeight - 250}
            itemHeight={getItemHeight}
            renderItem={renderVirtualItem}
            overscan={5}
          />
        ) : (
          <div className="flex flex-col gap-3">
            {itemsToDisplay.map((item) => {
              if ('downloads' in item) {
                return renderGroup(item as DownloadGroup);
              }
              return renderDownload(item as Download);
            })}
          </div>
        )}
      </div>

      {/* Performance warning */}
      {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 500 && (
        <Alert color="yellow" icon={<AlertTriangle className="w-5 h-5" />}>
          Loading {itemsToDisplay.length} items. Virtual scrolling enabled for optimal performance.
        </Alert>
      )}
    </div>
  );
};

export default DownloadsTab;