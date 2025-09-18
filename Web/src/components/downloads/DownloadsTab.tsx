import React, { useState, useEffect, useMemo, useRef } from 'react';
import {
  ChevronRight,
  ChevronDown,
  Gamepad2,
  ExternalLink,
  Database,
  CloudOff,
  Check,
  AlertTriangle,
  Users,
  Settings,
  Download as DownloadIcon,
  Loader,
  List,
  Grid3x3,
  Clock,
  Layers
} from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { API_BASE } from '../../utils/constants';
import { getServiceBadgeClasses } from '../../utils/serviceColors';
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
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small',
  HIDE_LOCALHOST: 'lancache_downloads_hide_localhost',
  HIDE_UNKNOWN_GAMES: 'lancache_downloads_hide_unknown',
  VIEW_MODE: 'lancache_downloads_view_mode',
  SORT_ORDER: 'lancache_downloads_sort_order'
};

// View modes
type ViewMode = 'compact' | 'normal' | 'grouped';

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

// Helper to get Steam CDN fallback URL
const getSteamCdnUrl = (originalUrl: string): string | null => {
  const appIdMatch = originalUrl.match(/\/gameimages\/(\d+)\//);
  if (appIdMatch) {
    const appId = appIdMatch[1];
    return `https://cdn.akamai.steamstatic.com/steam/apps/${appId}/header.jpg`;
  }
  return null;
};

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
  const [currentSrc, setCurrentSrc] = useState(src);
  const [triedFallback, setTriedFallback] = useState(false);
  const timeoutRef = useRef<NodeJS.Timeout | null>(null);

  useEffect(() => {
    setHasError(false);
    setIsLoading(true);
    setCurrentSrc(src);
    setTriedFallback(false);

    // Clear any existing timeout
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
    }

    // Set a timeout for image loading (10 seconds)
    timeoutRef.current = setTimeout(() => {
      if (isLoading) {
        const timestamp = new Date().toISOString();
        if (currentSrc.includes('/gameimages/')) {
          const appIdMatch = currentSrc.match(/\/gameimages\/(\d+)\//);
          const appId = appIdMatch ? appIdMatch[1] : 'unknown';
          const source = triedFallback ? 'Steam CDN' : 'local API';
          console.error(`[${timestamp}] TIMEOUT: Steam header for "${alt}" (AppID: ${appId}) timed out after 10 seconds from ${source}`, {
            url: currentSrc,
            gameName: alt,
            appId,
            source,
            triedFallback
          });
        }
        setHasError(true);
        setIsLoading(false);
        onError?.();
      }
    }, 10000);

    return () => {
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
      }
    };
  }, [src]);

  const handleLoad = () => {
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
    }

    // Enhanced success logging to compare with failures
    const timestamp = new Date().toISOString();
    if (currentSrc.includes('/gameimages/')) {
      const appIdMatch = currentSrc.match(/\/gameimages\/(\d+)\//);
      const appId = appIdMatch ? appIdMatch[1] : 'unknown';
      const source = triedFallback ? 'Steam CDN' : 'local API';
      console.log(`[${timestamp}] SUCCESS: Loaded Steam header for "${alt}" (AppID: ${appId}) from ${source}`);
    }

    setIsLoading(false);
    onLoad?.();
  };

  const handleError = (error?: React.SyntheticEvent<HTMLImageElement>) => {
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
    }

    // Enhanced debugging for identical game failures
    const timestamp = new Date().toISOString();
    const loadTime = Date.now() - (timeoutRef.current ? 0 : Date.now()); // Approximate

    // Try Steam CDN fallback for Steam game images
    if (!triedFallback && currentSrc.includes('/gameimages/')) {
      const steamCdnUrl = getSteamCdnUrl(currentSrc);
      if (steamCdnUrl) {
        const appIdMatch = currentSrc.match(/\/gameimages\/(\d+)\//);
        const appId = appIdMatch ? appIdMatch[1] : 'unknown';
        console.warn(`[${timestamp}] Failed to load Steam game header from local API. AppID: ${appId}, Game: ${alt}, trying Steam CDN fallback...`);

        setTriedFallback(true);
        setCurrentSrc(steamCdnUrl);
        setIsLoading(true);
        return; // Don't show error yet, try fallback first
      }
    }

    // Enhanced logging for final failures with more context
    if (currentSrc.includes('/gameimages/')) {
      const appIdMatch = currentSrc.match(/\/gameimages\/(\d+)\//);
      const appId = appIdMatch ? appIdMatch[1] : 'unknown';
      const source = triedFallback ? 'Steam CDN' : 'local API';
      console.error(`[${timestamp}] CRITICAL: Failed to load Steam header for "${alt}" (AppID: ${appId}) from ${source}`, {
        url: currentSrc,
        gameName: alt,
        appId,
        source,
        triedFallback,
        error: error?.type || 'unknown',
        loadTime: `${loadTime}ms`
      });
    } else {
      console.warn(`[${timestamp}] Failed to load image: ${currentSrc} for ${alt}`, error);
    }

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
              background: 'linear-gradient(135deg, var(--theme-bg-tertiary), var(--theme-bg-secondary))'
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
        key={`${src}-${triedFallback ? 'fallback' : 'original'}`}
        src={currentSrc}
        alt={alt}
        className={className}
        style={{
          ...style,
          opacity: isLoading ? 0 : 1,
          transition: 'opacity 0.3s'
        }}
        onLoad={handleLoad}
        onError={handleError}
        loading="lazy"
        crossOrigin="anonymous"
      />
    </div>
  );
};

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
        className="w-full px-3 py-2 rounded-lg border text-themed-primary text-left focus:outline-none transition-colors flex items-center justify-between"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: 'var(--theme-border-primary)'
        }}
        onMouseEnter={(e) =>
          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
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
  const [filterLoading, setFilterLoading] = useState(false);

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
    if (!download.id || !download.gameName || download.service.toLowerCase() !== 'steam') {
      return;
    }

    if (gameInfo[download.id]) {
      return;
    }

    setLoadingGame(download.id);
    await new Promise(resolve => setTimeout(resolve, 300));

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

  // Persist settings
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(
      STORAGE_KEYS.ITEMS_PER_PAGE,
      settings.itemsPerPage === 'unlimited' ? 'unlimited' : settings.itemsPerPage.toString()
    );
    localStorage.setItem(STORAGE_KEYS.VIEW_MODE, settings.viewMode);
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_LOCALHOST, settings.hideLocalhost.toString());
    localStorage.setItem(STORAGE_KEYS.HIDE_UNKNOWN_GAMES, settings.hideUnknownGames.toString());
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

  // Calculate service-only filtered downloads for correct count display
  const serviceFilteredDownloads = useMemo(() => {
    if (!Array.isArray(latestDownloads)) {
      return [];
    }

    if (settings.selectedService === 'all') {
      return latestDownloads;
    }

    return latestDownloads.filter((d) => d.service.toLowerCase() === settings.selectedService);
  }, [latestDownloads, settings.selectedService]);

  const groupedDownloads = useMemo((): DownloadGroup[] => {
    if (settings.viewMode !== 'grouped') return [];

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
  }, [filteredDownloads, settings.viewMode]);

  const itemsToDisplay = useMemo(() => {
    let items = settings.viewMode === 'grouped' ? groupedDownloads : filteredDownloads;

    // Apply sorting
    if (settings.viewMode !== 'grouped') {
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
    }

    if (settings.itemsPerPage === 'unlimited') {
      return items;
    }
    const limit = typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 50;
    return items.slice(0, limit);
  }, [settings.viewMode, settings.itemsPerPage, settings.sortOrder, groupedDownloads, filteredDownloads]);

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

  // Render function for compact view
  const renderCompactView = (download: Download) => {
    const isExpanded = expandedDownload === download.id;
    const isSteam = download.service.toLowerCase() === 'steam';
    const hasData = (download.totalBytes || 0) > 0;
    const canExpand = isSteam && hasData && download.gameName &&
                       download.gameName !== 'Unknown Steam Game' &&
                       !download.gameName.match(/^Steam App \d+$/);
    const downloadType = getDownloadTypeInfo(download);
    const IconComponent = downloadType.icon;
    const game = download.id ? gameInfo[download.id] : undefined;

    return (
      <div
        key={download.id}
        className="border-b transition-all duration-200"
        style={{
          borderColor: 'var(--theme-border-primary)',
          marginBottom: isExpanded ? '24px' : '0' // Add space when expanded
        }}
      >
        {/* Desktop Compact View */}
        <div
          className={`hidden md:block px-4 py-2 transition-colors ${canExpand ? 'cursor-pointer hover:bg-[var(--theme-bg-tertiary)]/50' : ''}`}
          onClick={() => canExpand ? handleDownloadClick(download) : undefined}
        >
          <div className="flex items-center">
            {/* Service with expansion arrow */}
            <div className="w-24 flex items-center gap-2">
              {canExpand && (
                <ChevronRight
                  size={16}
                  className={`transition-transform duration-200 text-themed-muted ${isExpanded ? 'rotate-90' : ''}`}
                />
              )}
              <span className={`text-xs font-bold px-2 py-0.5 rounded-md shadow-sm ${getServiceBadgeClasses(download.service)}`}>
                {download.service.toUpperCase()}
              </span>
            </div>

            {/* Status */}
            <div className="w-28 flex items-center">
              <div className={`flex items-center gap-1.5 px-2.5 py-1 rounded-lg text-xs font-medium shadow-sm transition-all duration-200 ${
                downloadType.label === 'Cached' ? 'bg-gradient-to-r from-green-500/15 to-green-400/10 text-green-500 border border-green-500/20' :
                downloadType.label === 'Partial' ? 'bg-gradient-to-r from-yellow-500/15 to-yellow-400/10 text-yellow-500 border border-yellow-500/20' :
                'bg-gradient-to-r from-gray-500/10 to-gray-400/5 text-gray-400 border border-gray-500/10'
              }`}>
                <IconComponent size={12} className="drop-shadow-sm" />
                <span>{downloadType.label}</span>
              </div>
            </div>

            {/* Game Name */}
            <div className="flex-1 min-w-0 px-2">
              {download.gameName && download.gameName !== 'Unknown Steam Game' && (
                <span className="text-sm text-themed-primary truncate block">
                  {download.gameName}
                </span>
              )}
            </div>

            {/* Client */}
            <div className="w-32">
              <div className="flex items-center gap-1.5">
                <Users size={14} className="text-themed-muted" />
                <span className="text-sm text-themed-secondary">{download.clientIp}</span>
              </div>
            </div>

            {/* Time */}
            <div className="w-24">
              <div className="flex items-center gap-1.5">
                <Clock size={14} className="text-themed-muted" />
                <span className="text-sm text-themed-secondary">{formatRelativeTime(download.startTime)}</span>
              </div>
            </div>

            {/* Size */}
            <div className="w-24 text-right">
              <div>
                <div className="text-sm font-semibold text-themed-primary">
                  {formatBytes(download.totalBytes || 0)}
                </div>
                {download.cacheHitBytes > 0 && (
                  <div className="text-xs text-green-500 font-medium">
                    {formatPercent((download.cacheHitBytes || 0) / (download.totalBytes || 1) * 100)}
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>

        {/* Mobile Compact View */}
        <div
          className={`md:hidden px-3 py-2 transition-colors ${canExpand ? 'cursor-pointer hover:bg-[var(--theme-bg-tertiary)]/50' : ''}`}
          onClick={() => canExpand ? handleDownloadClick(download) : undefined}
        >
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 flex-1 min-w-0">
              {canExpand && (
                <ChevronRight
                  size={14}
                  className={`transition-transform duration-200 text-themed-muted ${isExpanded ? 'rotate-90' : ''} flex-shrink-0`}
                />
              )}
              <span className={`text-xs font-bold px-2 py-0.5 rounded shadow-sm flex-shrink-0 ${getServiceBadgeClasses(download.service)}`}>
                {download.service.toUpperCase()}
              </span>
              {download.gameName && download.gameName !== 'Unknown Steam Game' && (
                <span className="text-sm text-themed-primary truncate">
                  {download.gameName}
                </span>
              )}
            </div>
            <div className="text-right flex-shrink-0">
              <div className="text-sm font-semibold text-themed-primary">
                {formatBytes(download.totalBytes || 0)}
              </div>
              {download.cacheHitBytes > 0 && (
                <div className="text-xs text-green-500 font-medium">
                  {formatPercent((download.cacheHitBytes || 0) / (download.totalBytes || 1) * 100)}
                </div>
              )}
            </div>
          </div>

          <div className="flex items-center justify-between mt-1 text-xs text-[var(--theme-text-muted)]">
            <div className="flex items-center gap-3">
              <div className={`flex items-center gap-1 px-2 py-0.5 rounded text-xs ${
                downloadType.label === 'Cached' ? 'bg-green-500/10 text-green-500' :
                downloadType.label === 'Partial' ? 'bg-yellow-500/10 text-yellow-500' :
                'bg-gray-500/10 text-gray-400'
              }`}>
                <IconComponent size={10} />
                <span>{downloadType.label}</span>
              </div>
              <div className="flex items-center gap-1">
                <Users size={10} />
                <span>{download.clientIp}</span>
              </div>
            </div>
            <div className="flex items-center gap-1">
              <Clock size={10} />
              <span>{formatRelativeTime(download.startTime)}</span>
            </div>
          </div>
        </div>

        {/* Expanded content */}
        {isExpanded && canExpand && (
          <div className="px-4 pb-4 bg-[var(--theme-bg-secondary)]/50">
            <div className="border-t border-[var(--theme-border-primary)]/20 my-3" />
            {loadingGame === download.id ? (
              <div className="flex justify-center py-4">
                <Loader className="w-6 h-6 animate-spin" />
              </div>
            ) : game ? (
              <>
                {/* Desktop Expanded View */}
                <div className="hidden md:flex gap-6 items-start">
                  <div className="flex-shrink-0">
                    <ImageWithFallback
                      src={game.headerImage || ''}
                      alt={game.gameName || 'Game'}
                      className="rounded shadow-lg"
                      style={{ width: '224px', height: '107px', objectFit: 'cover' }}
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

                {/* Mobile Expanded View */}
                <div className="md:hidden">
                  <div className="flex gap-3 mb-3">
                    <div className="flex-shrink-0">
                      <ImageWithFallback
                        src={game.headerImage || ''}
                        alt={game.gameName || 'Game'}
                        className="rounded shadow-lg"
                        style={{ width: '120px', height: '56px', objectFit: 'cover' }}
                      />
                    </div>
                    <div className="flex-1 min-w-0">
                      <h3 className="text-sm font-semibold text-themed-primary mb-2 line-clamp-2">
                        {game.gameName || 'Unknown Game'}
                      </h3>
                      {isSteam && (
                        <a
                          href={`https://store.steampowered.com/app/${game.appId}`}
                          target="_blank"
                          rel="noopener noreferrer"
                          onClick={(e) => e.stopPropagation()}
                          className="inline-flex items-center gap-1 px-2 py-1 rounded bg-themed-secondary hover:bg-themed-hover transition-colors text-xs text-themed-accent"
                        >
                          View on Steam
                          <ExternalLink size={10} />
                        </a>
                      )}
                    </div>
                  </div>
                  {game.description && (
                    <p className="text-xs text-themed-secondary line-clamp-3">
                      {game.description}
                    </p>
                  )}
                </div>
              </>
            ) : null}
          </div>
        )}
      </div>
    );
  };

  // Render function for normal view
  const renderNormalView = (download: Download) => {
    const hitPercent = download.totalBytes > 0 ? ((download.cacheHitBytes || 0) / download.totalBytes) * 100 : 0;
    const showGameImage = download.service.toLowerCase() === 'steam' &&
                          download.gameName &&
                          download.gameName !== 'Unknown Steam Game' &&
                          !download.gameName.match(/^Steam App \d+$/);

    // Steam games with headers
    if (showGameImage) {
      return (
        <div
          key={download.id}
          className="rounded-xl bg-[var(--theme-bg-secondary)] border transition-all duration-300 hover:shadow-xl hover:-translate-y-0.5"
          style={{
            borderColor: 'var(--theme-border-primary)',
            marginBottom: '20px', // Proper spacing between cards
            boxShadow: '0 2px 8px rgba(0, 0, 0, 0.06), 0 4px 12px rgba(0, 0, 0, 0.04)'
          }}
        >
          {/* Desktop Layout */}
          <div className="hidden sm:flex">
            {/* Game header on the left */}
            <div className="flex-shrink-0">
              <ImageWithFallback
                src={`${API_BASE}/gameimages/${download.gameAppId}/header/`}
                alt={download.gameName || 'Game'}
                className="w-[230px] h-[108px] rounded-l-xl object-cover"
              />
            </div>

            {/* Content on the right */}
            <div className="flex-1 p-3 min-w-0 h-[108px] flex flex-col justify-between">
              <div className="flex items-start justify-between">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-1">
                    <span className={`px-2.5 py-1 text-xs font-bold rounded-lg shadow-sm ${getServiceBadgeClasses(download.service)}`}>
                      {download.service.toUpperCase()}
                    </span>
                    <h3 className="text-sm font-bold text-[var(--theme-text-primary)] truncate">
                      {download.gameName}
                    </h3>
                  </div>
                  <div className="flex items-center gap-3 text-xs text-[var(--theme-text-muted)]">
                    <div className="flex items-center gap-1">
                      <Users size={11} />
                      <span>{download.clientIp}</span>
                    </div>
                    <div className="flex items-center gap-1">
                      <Clock size={11} />
                      <span>{formatRelativeTime(download.startTime)}</span>
                    </div>
                  </div>
                </div>
                <a
                  href={`https://store.steampowered.com/app/${download.gameAppId}`}
                  target="_blank"
                  rel="noopener noreferrer"
                  onClick={(e) => e.stopPropagation()}
                  className="p-1 rounded hover:bg-[var(--theme-bg-tertiary)] transition-colors text-[var(--theme-text-muted)] hover:text-[var(--theme-primary)]"
                  title="View in Steam Store"
                >
                  <ExternalLink size={13} />
                </a>
              </div>

              <div className="flex items-center justify-between">
                <div className="flex items-center gap-4">
                  <div>
                    <div className="text-xs text-[var(--theme-text-muted)]">Size</div>
                    <div className="text-sm font-bold text-[var(--theme-text-primary)]">
                      {formatBytes(download.totalBytes || 0)}
                    </div>
                  </div>
                  <div>
                    <div className="text-xs text-[var(--theme-text-muted)]">Cache Hit</div>
                    <div className="text-sm font-bold text-green-500">
                      {formatPercent(hitPercent)}
                    </div>
                  </div>
                  <div>
                    <div className="text-xs text-[var(--theme-text-muted)]">Saved</div>
                    <div className="text-sm font-bold text-blue-500">
                      {formatBytes(download.cacheHitBytes || 0)}
                    </div>
                  </div>
                </div>

                <div className="flex-1 max-w-[180px]">
                  <div className="w-full h-2 rounded-full overflow-hidden"
                       style={{
                         backgroundColor: 'rgba(0, 0, 0, 0.15)',
                         boxShadow: 'inset 0 1px 2px rgba(0, 0, 0, 0.15)'
                       }}>
                    <div
                      className="h-full bg-gradient-to-r from-green-500 to-green-400 transition-all duration-300 rounded-full"
                      style={{
                        width: `${Math.min(hitPercent, 100)}%`,
                        boxShadow: '0 1px 2px rgba(0, 0, 0, 0.1)'
                      }}
                    />
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Mobile Layout */}
          <div className="sm:hidden p-4">
            <div className="flex items-start justify-between mb-3">
              <div className="flex items-center gap-2">
                <span className={`px-2 py-1 text-xs font-bold rounded shadow-sm ${getServiceBadgeClasses(download.service)}`}>
                  {download.service.toUpperCase()}
                </span>
                <a
                  href={`https://store.steampowered.com/app/${download.gameAppId}`}
                  target="_blank"
                  rel="noopener noreferrer"
                  onClick={(e) => e.stopPropagation()}
                  className="p-1 rounded hover:bg-[var(--theme-bg-tertiary)] transition-colors text-[var(--theme-text-muted)]"
                  title="View in Steam Store"
                >
                  <ExternalLink size={12} />
                </a>
              </div>
              <div className="text-right">
                <div className="text-sm font-bold text-[var(--theme-text-primary)]">
                  {formatBytes(download.totalBytes || 0)}
                </div>
                {download.cacheHitBytes > 0 && (
                  <div className="text-xs text-green-500 font-medium">
                    {formatPercent(hitPercent)}
                  </div>
                )}
              </div>
            </div>

            <div className="flex gap-3 mb-3">
              <div className="flex-shrink-0">
                <ImageWithFallback
                  src={`${API_BASE}/gameimages/${download.gameAppId}/header/`}
                  alt={download.gameName || 'Game'}
                  className="w-[120px] h-[56px] rounded object-cover"
                />
              </div>
              <div className="flex-1 min-w-0">
                <h3 className="text-sm font-bold text-[var(--theme-text-primary)] mb-1 line-clamp-2">
                  {download.gameName}
                </h3>
                <div className="flex items-center gap-3 text-xs text-[var(--theme-text-muted)]">
                  <div className="flex items-center gap-1">
                    <Users size={10} />
                    <span>{download.clientIp}</span>
                  </div>
                  <div className="flex items-center gap-1">
                    <Clock size={10} />
                    <span>{formatRelativeTime(download.startTime)}</span>
                  </div>
                </div>
              </div>
            </div>

            {download.totalBytes > 0 && (
              <div className="mt-3">
                <div className="flex items-center justify-between text-xs text-[var(--theme-text-muted)] mb-1">
                  <span>Cache Efficiency</span>
                  <span className="font-semibold">{formatPercent(hitPercent)}</span>
                </div>
                <div className="w-full h-2 rounded-full overflow-hidden"
                     style={{
                       backgroundColor: 'rgba(0, 0, 0, 0.15)',
                       boxShadow: 'inset 0 1px 2px rgba(0, 0, 0, 0.15)'
                     }}>
                  <div
                    className="h-full bg-gradient-to-r from-green-500 to-green-400 transition-all duration-300 rounded-full"
                    style={{
                      width: `${Math.min(hitPercent, 100)}%`,
                      boxShadow: '0 1px 2px rgba(0, 0, 0, 0.1)'
                    }}
                  />
                </div>
              </div>
            )}
          </div>
        </div>
      );
    }

    // Non-Steam or downloads without game headers
    return (
      <div
        key={download.id}
        className="rounded-xl bg-[var(--theme-bg-secondary)] border transition-all duration-300 hover:shadow-xl hover:-translate-y-0.5"
        style={{
          borderColor: 'var(--theme-border-primary)',
          marginBottom: '20px', // Proper spacing between cards
          boxShadow: '0 1px 4px rgba(0, 0, 0, 0.05), 0 2px 8px rgba(0, 0, 0, 0.03)'
        }}
      >
        {/* Desktop Layout */}
        <div className="hidden sm:block p-4">
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-3">
              <span className={`px-3 py-1.5 text-xs font-bold rounded-lg shadow-sm ${getServiceBadgeClasses(download.service)}`}>
                {download.service.toUpperCase()}
              </span>
              {download.gameName && download.gameName !== 'Unknown Steam Game' && (
                <h3 className="text-base font-semibold text-[var(--theme-text-primary)]">
                  {download.gameName}
                </h3>
              )}
            </div>
            <div className="text-right">
              <div className="text-xl font-bold text-[var(--theme-text-primary)]">
                {formatBytes(download.totalBytes || 0)}
              </div>
            </div>
          </div>

          <div className="flex items-center justify-between text-sm">
            <div className="flex items-center gap-4">
              <div className="flex items-center gap-2">
                <Users size={14} className="text-[var(--theme-text-muted)]" />
                <span className="text-[var(--theme-text-secondary)]">Client:</span>
                <span className="font-medium text-[var(--theme-text-primary)]">{download.clientIp}</span>
              </div>
              <div className="flex items-center gap-2">
                <Clock size={14} className="text-[var(--theme-text-muted)]" />
                <span className="font-medium text-[var(--theme-text-primary)]">
                  {formatRelativeTime(download.startTime)}
                </span>
              </div>
            </div>

            <div className="flex items-center gap-4">
              {download.cacheHitBytes > 0 && (
                <>
                  <div className="text-sm">
                    <span className="text-[var(--theme-text-muted)]">Cache: </span>
                    <span className="font-bold text-green-500">{formatPercent(hitPercent)}</span>
                  </div>
                  <div className="text-sm">
                    <span className="text-[var(--theme-text-muted)]">Saved: </span>
                    <span className="font-bold text-blue-500">{formatBytes(download.cacheHitBytes || 0)}</span>
                  </div>
                </>
              )}
            </div>
          </div>

          {download.totalBytes > 0 && download.cacheHitBytes > 0 && (
            <div className="mt-3">
              <div className="w-full h-2 rounded-full overflow-hidden"
                   style={{
                     backgroundColor: 'rgba(0, 0, 0, 0.1)',
                     boxShadow: 'inset 0 1px 2px rgba(0, 0, 0, 0.15)'
                   }}>
                <div
                  className="h-full bg-gradient-to-r from-green-500 to-green-400 transition-all duration-300 rounded-full"
                  style={{
                    width: `${Math.min(hitPercent, 100)}%`,
                    boxShadow: '0 1px 2px rgba(0, 0, 0, 0.1)'
                  }}
                />
              </div>
            </div>
          )}
        </div>

        {/* Mobile Layout */}
        <div className="sm:hidden p-3">
          <div className="flex items-center justify-between mb-2">
            <span className={`px-2 py-1 text-xs font-bold rounded shadow-sm ${getServiceBadgeClasses(download.service)}`}>
              {download.service.toUpperCase()}
            </span>
            <div className="text-right">
              <div className="text-lg font-bold text-[var(--theme-text-primary)]">
                {formatBytes(download.totalBytes || 0)}
              </div>
              {download.cacheHitBytes > 0 && (
                <div className="text-xs text-green-500 font-medium">
                  {formatPercent(hitPercent)}
                </div>
              )}
            </div>
          </div>

          {download.gameName && download.gameName !== 'Unknown Steam Game' && (
            <h3 className="text-sm font-semibold text-[var(--theme-text-primary)] mb-2">
              {download.gameName}
            </h3>
          )}

          <div className="flex items-center justify-between text-xs text-[var(--theme-text-muted)] mb-2">
            <div className="flex items-center gap-1">
              <Users size={10} />
              <span>{download.clientIp}</span>
            </div>
            <div className="flex items-center gap-1">
              <Clock size={10} />
              <span>{formatRelativeTime(download.startTime)}</span>
            </div>
          </div>

          {download.totalBytes > 0 && download.cacheHitBytes > 0 && (
            <div className="mt-2">
              <div className="flex items-center justify-between text-xs text-[var(--theme-text-muted)] mb-1">
                <span>Cache: {formatPercent(hitPercent)}</span>
                <span>Saved: {formatBytes(download.cacheHitBytes || 0)}</span>
              </div>
              <div className="w-full h-2 rounded-full overflow-hidden"
                   style={{
                     backgroundColor: 'rgba(0, 0, 0, 0.1)',
                     boxShadow: 'inset 0 1px 2px rgba(0, 0, 0, 0.15)'
                   }}>
                <div
                  className="h-full bg-gradient-to-r from-green-500 to-green-400 transition-all duration-300 rounded-full"
                  style={{
                    width: `${Math.min(hitPercent, 100)}%`,
                    boxShadow: '0 1px 2px rgba(0, 0, 0, 0.1)'
                  }}
                />
              </div>
            </div>
          )}
        </div>
      </div>
    );
  };

  // Render function for grouped view
  const renderGroupedView = (group: DownloadGroup) => {
    const isExpanded = expandedGroup === group.id;
    const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    const savedAmount = group.cacheHitBytes || 0;
    const totalClients = group.clientsSet.size;

    return (
      <div
        key={group.id}
        className="bg-[var(--theme-bg-secondary)] rounded-xl overflow-hidden transition-all duration-300 hover:shadow-xl hover:-translate-y-0.5"
        style={{
          border: '1px solid var(--theme-border-primary)',
          boxShadow: '0 2px 6px rgba(0, 0, 0, 0.06), 0 4px 12px rgba(0, 0, 0, 0.04)',
          marginBottom: '20px' // Proper spacing between groups
        }}
      >
        {/* Desktop Layout */}
        <div
          onClick={() => handleGroupClick(group.id)}
          className="hidden sm:block p-4 cursor-pointer hover:bg-[var(--theme-bg-tertiary)] transition-colors"
        >
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3 flex-1 min-w-0">
              <ChevronRight
                size={16}
                className={`transition-transform text-[var(--theme-text-secondary)] ${isExpanded ? 'rotate-90' : ''}`}
              />
              <div className="flex items-center gap-2">
                <span className={`px-3 py-1.5 text-xs font-bold rounded-lg shadow-sm ${getServiceBadgeClasses(group.service)}`}>
                  {group.service.toUpperCase()}
                </span>
              </div>
              <div className="flex-1 min-w-0">
                <h3 className="text-sm font-semibold text-[var(--theme-text-primary)] truncate">
                  {group.name}
                </h3>
                <div className="flex items-center gap-4 mt-1">
                  <span className="text-xs text-[var(--theme-text-secondary)]">
                    {group.count} {group.count === 1 ? 'download' : 'downloads'}
                  </span>
                  <span className="text-xs text-[var(--theme-text-secondary)]">
                    {totalClients} {totalClients === 1 ? 'client' : 'clients'}
                  </span>
                </div>
              </div>
            </div>

            <div className="flex items-center gap-4">
              <div className="flex flex-col items-end">
                <span className="text-sm font-semibold text-[var(--theme-text-primary)]">
                  {formatBytes(group.totalBytes)}
                </span>
                {savedAmount > 0 && (
                  <span className="text-xs text-green-500">
                    Saved: {formatBytes(savedAmount)}
                  </span>
                )}
              </div>
              {group.totalBytes > 0 && (
                <div className="w-24">
                  <div className="flex items-center justify-between text-xs text-[var(--theme-text-secondary)] mb-1">
                    <span>Cache</span>
                    <span>{formatPercent(hitPercent)}</span>
                  </div>
                  <div className="w-full h-2 rounded-full overflow-hidden"
                       style={{
                         backgroundColor: 'rgba(0, 0, 0, 0.12)',
                         boxShadow: 'inset 0 1px 2px rgba(0, 0, 0, 0.15)'
                       }}>
                    <div
                      className="h-full bg-gradient-to-r from-green-500 to-green-400 transition-all duration-300 rounded-full"
                      style={{
                        width: `${Math.min(hitPercent, 100)}%`,
                        boxShadow: '0 1px 2px rgba(0, 0, 0, 0.1)'
                      }}
                    />
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Mobile Layout */}
        <div
          onClick={() => handleGroupClick(group.id)}
          className="sm:hidden p-3 cursor-pointer hover:bg-[var(--theme-bg-tertiary)] transition-colors"
        >
          <div className="flex items-center justify-between mb-2">
            <div className="flex items-center gap-2 flex-1 min-w-0">
              <ChevronRight
                size={14}
                className={`transition-transform text-[var(--theme-text-secondary)] ${isExpanded ? 'rotate-90' : ''} flex-shrink-0`}
              />
              <span className={`px-2 py-1 text-xs font-bold rounded shadow-sm flex-shrink-0 ${getServiceBadgeClasses(group.service)}`}>
                {group.service.toUpperCase()}
              </span>
              <h3 className="text-sm font-semibold text-[var(--theme-text-primary)] truncate">
                {group.name}
              </h3>
            </div>
            <div className="text-right flex-shrink-0">
              <div className="text-sm font-semibold text-[var(--theme-text-primary)]">
                {formatBytes(group.totalBytes)}
              </div>
              {savedAmount > 0 && (
                <div className="text-xs text-green-500">
                  {formatBytes(savedAmount)}
                </div>
              )}
            </div>
          </div>

          <div className="flex items-center justify-between text-xs text-[var(--theme-text-secondary)]">
            <div className="flex items-center gap-3">
              <span>{group.count} {group.count === 1 ? 'download' : 'downloads'}</span>
              <span>{totalClients} {totalClients === 1 ? 'client' : 'clients'}</span>
            </div>
            {group.totalBytes > 0 && (
              <span className="font-semibold">{formatPercent(hitPercent)} cached</span>
            )}
          </div>

          {group.totalBytes > 0 && (
            <div className="mt-2">
              <div className="w-full h-2 rounded-full overflow-hidden"
                   style={{
                     backgroundColor: 'rgba(0, 0, 0, 0.12)',
                     boxShadow: 'inset 0 1px 2px rgba(0, 0, 0, 0.15)'
                   }}>
                <div
                  className="h-full bg-gradient-to-r from-green-500 to-green-400 transition-all duration-300 rounded-full"
                  style={{
                    width: `${Math.min(hitPercent, 100)}%`,
                    boxShadow: '0 1px 2px rgba(0, 0, 0, 0.1)'
                  }}
                />
              </div>
            </div>
          )}
        </div>

        {isExpanded && (
          <div className="border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
            <div className="max-h-96 overflow-y-auto">
              <div className="divide-y divide-[var(--theme-border-primary)]">
                {group.downloads.map((d, index) => {
                  const downloadHitPercent = d.totalBytes && d.totalBytes > 0
                    ? ((d.cacheHitBytes || 0) / d.totalBytes) * 100
                    : 0;

                  return (
                    <div
                      key={d.id}
                      className={`p-3 transition-all duration-200 ${
                        index % 2 === 0 ? 'bg-[var(--theme-bg-secondary)]' : 'bg-[var(--theme-bg-tertiary)]'
                      } hover:bg-[var(--theme-bg-primary)] hover:shadow-sm`}
                    >
                      <div className="flex items-center justify-between">
                        <div className="flex items-center gap-3 flex-1">
                          <span className={`px-2.5 py-1 text-xs font-semibold rounded-lg shadow-sm ${
                            d.endTime
                              ? 'bg-gradient-to-r from-green-500/20 to-green-400/15 text-green-400 border border-green-500/25'
                              : 'bg-gradient-to-r from-yellow-500/20 to-yellow-400/15 text-yellow-400 border border-yellow-500/25'
                          }`}>
                            {d.endTime ? 'complete' : 'in-progress'}
                          </span>
                          <div className="flex-1 min-w-0">
                            <div className="flex items-center gap-2">
                              <span className="text-xs text-[var(--theme-text-secondary)]">
                                {d.clientIp}
                              </span>
                              <span className="text-xs text-[var(--theme-text-muted)]">
                                {formatRelativeTime(d.startTime)}
                              </span>
                            </div>
                          </div>
                        </div>
                        <div className="flex items-center gap-4">
                          <span className="text-xs font-medium text-[var(--theme-text-primary)]">
                            {formatBytes(d.totalBytes || 0)}
                          </span>
                          {d.totalBytes && d.totalBytes > 0 && (
                            <span className={`text-xs font-bold px-2 py-0.5 rounded-md ${
                              downloadHitPercent > 75
                                ? 'bg-green-500/10 text-green-500'
                                : downloadHitPercent > 25
                                ? 'bg-yellow-500/10 text-yellow-500'
                                : 'bg-red-500/10 text-red-500'
                            }`}>
                              {formatPercent(downloadHitPercent)}
                            </span>
                          )}
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        )}
      </div>
    );
  };

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
              {/* View Mode Toggle - Three options */}
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
                <button
                  onClick={() => setSettings({ ...settings, viewMode: 'grouped' })}
                  className={`px-3 py-1.5 rounded-md transition-colors flex items-center gap-1 ${
                    settings.viewMode === 'grouped'
                      ? 'bg-primary'
                      : 'text-themed-secondary hover:text-themed-primary'
                  }`}
                  style={{
                    color: settings.viewMode === 'grouped' ? 'var(--theme-button-text)' : undefined
                  }}
                  title="Grouped View"
                >
                  <Layers size={16} />
                  <span className="text-xs">Grouped</span>
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
            <button
              onClick={() => setSettings({ ...settings, viewMode: 'grouped' })}
              className={`flex-1 px-2 py-1.5 rounded-md transition-colors ${
                settings.viewMode === 'grouped'
                  ? 'bg-primary'
                  : 'text-themed-secondary'
              }`}
              style={{
                color: settings.viewMode === 'grouped' ? 'var(--theme-button-text)' : undefined
              }}
            >
              <Layers size={16} className="mx-auto" />
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
          <div className="absolute inset-0 bg-[var(--theme-bg-primary)]/80 backdrop-blur-sm flex items-center justify-center z-10 rounded-lg">
            <div className="flex items-center gap-3 px-4 py-2 rounded-lg bg-[var(--theme-bg-secondary)] border shadow-lg" style={{ borderColor: 'var(--theme-border-primary)' }}>
              <Loader className="w-5 h-5 animate-spin text-[var(--theme-primary)]" />
              <span className="text-sm font-medium text-[var(--theme-text-primary)]">Applying filters...</span>
            </div>
          </div>
        )}

        {/* Table Header for Compact View - Desktop Only */}
        {settings.viewMode === 'compact' && (
          <div className="hidden md:flex items-center px-4 py-2 text-xs font-medium uppercase tracking-wider text-themed-muted border-b bg-[var(--theme-bg-secondary)]/50" style={{ borderColor: 'var(--theme-border-primary)' }}>
            <div className="w-24">Service</div>
            <div className="w-28">Status</div>
            <div className="flex-1 px-2">Game</div>
            <div className="w-32">Client</div>
            <div className="w-24">Time</div>
            <div className="w-24 text-right">Size</div>
          </div>
        )}

        {/* Mobile Header for Compact View */}
        {settings.viewMode === 'compact' && (
          <div className="md:hidden px-3 py-2 text-xs font-medium uppercase tracking-wider text-themed-muted border-b bg-[var(--theme-bg-secondary)]/50" style={{ borderColor: 'var(--theme-border-primary)' }}>
            Downloads
          </div>
        )}

        {/* Content based on view mode */}
        <div>
          {settings.viewMode === 'compact' && (
            <div>
              {itemsToDisplay.map((item) => renderCompactView(item as Download))}
            </div>
          )}

          {settings.viewMode === 'normal' && (
            <div>
              {itemsToDisplay.map((item) => renderNormalView(item as Download))}
            </div>
          )}

          {settings.viewMode === 'grouped' && (
            <div>
              {itemsToDisplay.map((item) => renderGroupedView(item as DownloadGroup))}
            </div>
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