import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import ReactDOM from 'react-dom';
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
  Loader
} from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import VirtualizedList from '../common/VirtualizedList';
import { Alert } from '../ui/Alert';
import { Card } from '../ui/Card';
import type {
  Download,
  GameInfo,
  DownloadGroup,
  DownloadSettings
} from '../../types';

// Storage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small'
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
  const [imageState, setImageState] = useState<'loading' | 'loaded' | 'error'>('loading');
  const [imageSrc, setImageSrc] = useState<string | null>(null);

  useEffect(() => {
    if (!src) {
      setImageState('error');
      return;
    }

    // Reset state when src changes
    setImageState('loading');
    
    // Create a new image to preload
    const img = new Image();
    
    img.onload = () => {
      setImageSrc(src);
      setImageState('loaded');
      onLoad?.();
    };
    
    img.onerror = () => {
      setImageState('error');
      onError?.();
    };
    
    img.src = src;

    // Cleanup
    return () => {
      img.onload = null;
      img.onerror = null;
    };
  }, [src, onLoad, onError]);

  if (imageState === 'error' || !src) {
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
    <>
      {imageState === 'loading' && (
        <div 
          className={`${className} flex items-center justify-center`}
          style={{
            ...style,
            backgroundColor: 'var(--theme-bg-tertiary)'
          }}
        >
          <Loader className="w-6 h-6 animate-spin" />
        </div>
      )}
      {imageState === 'loaded' && imageSrc && (
        <img
          src={imageSrc}
          alt={alt}
          className={className}
          style={style}
        />
      )}
    </>
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
  const [dropdownPosition, setDropdownPosition] = useState({ top: 0, left: 0, width: 0 });
  
  const selectedOption = options.find((opt) => opt.value === value);

  // Calculate dropdown position
  useEffect(() => {
    if (isOpen && buttonRef.current) {
      const rect = buttonRef.current.getBoundingClientRect();
      const spaceBelow = window.innerHeight - rect.bottom;
      const spaceAbove = rect.top;
      
      // Determine if dropdown should appear above or below
      const shouldFlip = spaceBelow < 200 && spaceAbove > spaceBelow;
      
      setDropdownPosition({
        top: shouldFlip ? rect.top - 8 : rect.bottom + 8,
        left: rect.left,
        width: rect.width
      });
    }
  }, [isOpen]);

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

  // Render dropdown menu in portal
  const dropdownMenu = isOpen && ReactDOM.createPortal(
    <>
      {/* Invisible overlay to catch clicks */}
      <div 
        className="fixed inset-0" 
        style={{ zIndex: 9998 }}
        onClick={() => setIsOpen(false)}
      />
      
      {/* Dropdown menu */}
      <div
        ref={dropdownRef}
        className="fixed themed-card shadow-xl border border-themed-border"
        style={{
          zIndex: 9999,
          top: `${dropdownPosition.top}px`,
          left: `${dropdownPosition.left}px`,
          width: `${dropdownPosition.width}px`,
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
    </>,
    document.body
  );

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        onKeyDown={handleKeyDown}
        className="w-full px-3 py-2 themed-input text-themed-primary text-left focus:outline-none hover:bg-themed-hover transition-colors flex items-center justify-between"
        aria-haspopup="listbox"
        aria-expanded={isOpen}
      >
        <span className="truncate">{selectedOption ? selectedOption.label : placeholder}</span>
        <ChevronDown size={16} className={`transition-transform ${isOpen ? 'rotate-180' : ''}`} />
      </button>
      
      {dropdownMenu}
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

  const [settings, setSettings] = useState<DownloadSettings>(() => ({
    showZeroBytes: localStorage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
    showSmallFiles: localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
    selectedService: localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
    groupGames: localStorage.getItem(STORAGE_KEYS.GROUP_GAMES) === 'true',
    itemsPerPage: 
      localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) === 'unlimited' 
        ? 'unlimited' 
        : parseInt(localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE) || '50')
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

    try {
      const response = await fetch(`/api/steam/game/${encodeURIComponent(download.gameName)}`);
      if (response.ok) {
        const data = await response.json();
        setGameInfo(prev => ({
          ...prev,
          [download.id!]: data
        }));
        
        // Cache the game info
        const cached = localStorage.getItem('steam_game_cache') || '{}';
        const cache = JSON.parse(cached);
        cache[download.gameName] = { data, timestamp: Date.now() };
        localStorage.setItem('steam_game_cache', JSON.stringify(cache));
      }
    } catch (error) {
      console.error('Failed to fetch game info:', error);
    } finally {
      setLoadingGame(null);
    }
  };

  // Load cached game info on mount
  useEffect(() => {
    const loadCachedGameInfo = () => {
      const cached = localStorage.getItem('steam_game_cache');
      if (cached) {
        try {
          const cache = JSON.parse(cached);
          const now = Date.now();
          const validCache: Record<number, GameInfo> = {};
          
          latestDownloads.forEach(download => {
            if (download.id && download.gameName && cache[download.gameName]) {
              const cacheEntry = cache[download.gameName];
              // Cache is valid for 24 hours
              if (now - cacheEntry.timestamp < 86400000) {
                validCache[download.id] = cacheEntry.data;
              }
            }
          });
          
          setGameInfo(validCache);
        } catch (error) {
          console.error('Failed to load cached game info:', error);
        }
      }
    };
    
    loadCachedGameInfo();
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
    let filtered = [...latestDownloads];

    if (!settings.showZeroBytes) {
      filtered = filtered.filter((d) => (d.totalBytes || 0) > 0);
    }

    if (!settings.showSmallFiles) {
      filtered = filtered.filter(
        (d) => (d.totalBytes || 0) === 0 || (d.totalBytes || 0) >= 1048576
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

      if (download.gameName && download.gameName !== 'Unknown Steam Game') {
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
    const items = settings.groupGames ? groupedDownloads || [] : filteredDownloads;
    if (settings.itemsPerPage === 'unlimited') {
      return items;
    }
    const limit = typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 50;
    return items.slice(0, limit);
  }, [settings.groupGames, settings.itemsPerPage, groupedDownloads, filteredDownloads]);

  // Event handlers
  const handleDownloadClick = async (download: Download) => {
    if (download.totalBytes === 0) return;
    
    const newExpanded = expandedDownload === download.id ? null : download.id;
    setExpandedDownload(newExpanded);
    
    if (newExpanded && download.service.toLowerCase() === 'steam' && download.gameName) {
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
      <Card key={group.id} padding="md">
        <div onClick={() => handleGroupClick(group.id)} className="cursor-pointer">
          <div className="flex items-center justify-between">
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
            <div className="max-h-72 overflow-y-auto">
              <div className="space-y-2">
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
    const isExpanded = expandedDownload === download.id;
    const isSteam = download.service.toLowerCase() === 'steam';
    const hasData = (download.totalBytes || 0) > 0;
    const downloadType = getDownloadTypeInfo(download);
    const IconComponent = downloadType.icon;
    const game = download.id ? gameInfo[download.id] : undefined;

    return (
      <Card key={download.id} padding="md">
        <div onClick={() => handleDownloadClick(download)} className={hasData ? 'cursor-pointer' : ''}>
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <div className="flex items-center gap-2">
                {hasData && (
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

          {isExpanded && game && (
            <>
              <div className="border-t border-themed-secondary my-4" />
              {loadingGame === download.id ? (
                <div className="flex justify-center py-4">
                  <Loader className="w-6 h-6 animate-spin" />
                </div>
              ) : (
                <div className="flex gap-6 items-start">
                  <div className="flex-shrink-0">
                    <ImageWithFallback
                      src={game.headerImage || ''}
                      alt={game.gameName || 'Game'}
                      className="rounded w-56 object-cover shadow-lg"
                      style={{ height: '107px' }}
                      fallback={
                        <div 
                          className="rounded w-56 flex items-center justify-center shadow-lg"
                          style={{ 
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
              )}
            </>
          )}
        </div>
      </Card>
    );
  }, [expandedDownload, gameInfo, loadingGame]);

  const renderVirtualItem = useCallback((item: any) => {
    if ('downloads' in item) {
      return renderGroup(item as DownloadGroup);
    }
    return renderDownload(item as Download);
  }, [renderGroup, renderDownload]);

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
          </div>

          <button
            onClick={() => setSettingsOpened(!settingsOpened)}
            className="p-2 rounded hover:bg-themed-hover transition-colors"
            title="Settings"
          >
            <Settings size={18} />
          </button>
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
      <div className="space-y-2">
        {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 100 ? (
          <VirtualizedList
            items={itemsToDisplay}
            height={window.innerHeight - 250}
            itemHeight={120}
            renderItem={renderVirtualItem}
          />
        ) : (
          itemsToDisplay.map((item) => {
            if ('downloads' in item) {
              return renderGroup(item as DownloadGroup);
            }
            return renderDownload(item as Download);
          })
        )}
      </div>

      {/* Performance warning */}
      {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 500 && (
        <Alert color="yellow" icon={<AlertTriangle className="w-5 h-5" />}>
          Loading {itemsToDisplay.length} items. Performance optimized with virtual scrolling.
        </Alert>
      )}
    </div>
  );
};

export default DownloadsTab;