import React, { useState, useEffect, useMemo, useCallback } from 'react';
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
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import VirtualizedList from '../common/VirtualizedList';
import { Alert } from '../ui/Alert';
import { Card } from '../ui/Card';
import type {
  Download,
  GameInfo,
  DownloadGroup,
  DownloadSettings,
  DownloadType
} from '../../types';

const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small'
};

interface DropdownOption {
  value: string;
  label: string;
}

interface CustomDropdownProps {
  options: DropdownOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
}

const CustomDropdown: React.FC<CustomDropdownProps> = ({
  options,
  value,
  onChange,
  placeholder = 'Select option',
  className = ''
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const selectedOption = options.find((opt) => opt.value === value);

  const handleSelect = (optionValue: string) => {
    onChange(optionValue);
    setIsOpen(false);
  };

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Element;
      if (!target.closest('.custom-dropdown')) {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [isOpen]);

  const handleKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      setIsOpen(!isOpen);
    } else if (event.key === 'Escape') {
      setIsOpen(false);
    }
  };

  return (
    <div className={`relative custom-dropdown ${className}`}>
      <button
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

      {isOpen && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setIsOpen(false)} />
          <div className="mobile-dropdown sm:right-0 themed-card">
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
        </>
      )}
    </div>
  );
};

// Game info cache utilities
const GAME_INFO_CACHE_KEY = 'lancache_game_info_cache';
const CACHE_EXPIRY_HOURS = 24;

interface CachedGameInfo {
  data: GameInfo;
  timestamp: number;
  expiresAt: number;
}

const gameInfoCache = {
  get: (downloadId: number): GameInfo | null => {
    try {
      const cacheStr = localStorage.getItem(GAME_INFO_CACHE_KEY);
      if (!cacheStr) return null;
      
      const cache: Record<string, CachedGameInfo> = JSON.parse(cacheStr);
      const cached = cache[downloadId.toString()];
      
      if (!cached) return null;
      
      // Check if expired
      if (Date.now() > cached.expiresAt) {
        delete cache[downloadId.toString()];
        localStorage.setItem(GAME_INFO_CACHE_KEY, JSON.stringify(cache));
        return null;
      }
      
      return cached.data;
    } catch (err) {
      console.error('Error reading game info cache:', err);
      return null;
    }
  },
  
  set: (downloadId: number, gameInfo: GameInfo): void => {
    try {
      const cacheStr = localStorage.getItem(GAME_INFO_CACHE_KEY);
      const cache: Record<string, CachedGameInfo> = cacheStr ? JSON.parse(cacheStr) : {};
      
      const expiresAt = Date.now() + (CACHE_EXPIRY_HOURS * 60 * 60 * 1000);
      
      cache[downloadId.toString()] = {
        data: gameInfo,
        timestamp: Date.now(),
        expiresAt
      };
      
      localStorage.setItem(GAME_INFO_CACHE_KEY, JSON.stringify(cache));
    } catch (err) {
      console.error('Error saving game info to cache:', err);
    }
  },
  
  clear: (): void => {
    try {
      localStorage.removeItem(GAME_INFO_CACHE_KEY);
    } catch (err) {
      console.error('Error clearing game info cache:', err);
    }
  }
};

const DownloadsTab: React.FC = () => {
  const { latestDownloads, mockMode, updateMockDataCount, updateApiDownloadCount } = useData();

  const [expandedDownload, setExpandedDownload] = useState<number | null>(null);
  const [expandedGroup, setExpandedGroup] = useState<string | null>(null);
  const [gameInfo, setGameInfo] = useState<Record<number, GameInfo>>({});
  const [loadingGame, setLoadingGame] = useState<number | null>(null);
  const [settingsOpened, setSettingsOpened] = useState(false);

  const [settings, setSettings] = useState<DownloadSettings>(() => ({
    showZeroBytes: localStorage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
    showSmallFiles: localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
    selectedService: localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
    itemsPerPage: (() => {
      const saved = localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
      return saved === 'unlimited' ? ('unlimited' as const) : saved ? parseInt(saved, 10) : 20;
    })(),
    groupGames: localStorage.getItem(STORAGE_KEYS.GROUP_GAMES) === 'true'
  }));

  const updateSettings = useCallback((updates: Partial<DownloadSettings>) => {
    setSettings((prev) => ({ ...prev, ...updates }));
  }, []);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, settings.itemsPerPage.toString());
    localStorage.setItem(STORAGE_KEYS.GROUP_GAMES, settings.groupGames.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
  }, [settings]);

  // Preload cached game info when component mounts
  useEffect(() => {
    const loadCachedGameInfo = () => {
      try {
        const cacheStr = localStorage.getItem(GAME_INFO_CACHE_KEY);
        if (!cacheStr) return;

        const cache: Record<string, CachedGameInfo> = JSON.parse(cacheStr);
        const cachedGameInfo: Record<number, GameInfo> = {};
        let hasValidCache = false;

        Object.entries(cache).forEach(([downloadId, cached]) => {
          // Only load non-expired cache entries
          if (Date.now() <= cached.expiresAt) {
            cachedGameInfo[parseInt(downloadId)] = cached.data;
            hasValidCache = true;
          }
        });

        if (hasValidCache) {
          setGameInfo(cachedGameInfo);
        }
      } catch (err) {
        console.error('Error loading cached game info:', err);
      }
    };

    loadCachedGameInfo();
  }, []);

  useEffect(() => {
    const count = settings.itemsPerPage === 'unlimited' ? 100 : settings.itemsPerPage;
    if (mockMode && updateMockDataCount) {
      updateMockDataCount(count);
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount(count);
    }
  }, [settings.itemsPerPage, mockMode, updateMockDataCount, updateApiDownloadCount]);

  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map((d) => d.service.toLowerCase()));
    return Array.from(services).sort();
  }, [latestDownloads]);

  const serviceOptions = useMemo(
    () => [
      { value: 'all', label: 'All Services' },
      ...availableServices.map((s) => ({
        value: s,
        label: s.charAt(0).toUpperCase() + s.slice(1)
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
          clientsSet: new Set(),
          firstSeen: download.startTime,
          lastSeen: download.endTime || download.startTime,
          count: 0,
          clientCount: 0
        };
      }

      groups[groupKey].downloads.push(download);
      groups[groupKey].totalBytes += download.totalBytes || 0;
      groups[groupKey].cacheHitBytes += download.cacheHitBytes || 0;
      groups[groupKey].cacheMissBytes += download.cacheMissBytes || 0;
      groups[groupKey].clientsSet.add(download.clientIp);
      groups[groupKey].count++;

      if (new Date(download.startTime) < new Date(groups[groupKey].firstSeen)) {
        groups[groupKey].firstSeen = download.startTime;
      }
      if (download.endTime && new Date(download.endTime) > new Date(groups[groupKey].lastSeen)) {
        groups[groupKey].lastSeen = download.endTime;
      }
    });

    return Object.values(groups)
      .map((group) => ({
        ...group,
        clientCount: group.clientsSet.size
      }))
      .sort((a, b) => b.totalBytes - a.totalBytes);
  }, [filteredDownloads, settings.groupGames]);

  const itemsToDisplay = useMemo(() => {
    const items = settings.groupGames ? groupedDownloads : filteredDownloads;
    if (!items) return [];

    if (settings.itemsPerPage === 'unlimited') {
      return items;
    }
    const limit = typeof settings.itemsPerPage === 'number' ? settings.itemsPerPage : 50;
    return items.slice(0, limit);
  }, [settings.groupGames, settings.itemsPerPage, groupedDownloads, filteredDownloads]);

  const getDownloadTypeInfo = (download: Download): DownloadType => {
    const bytes = download.totalBytes || 0;
    const serviceName = download.service.charAt(0).toUpperCase() + download.service.slice(1);

    if (bytes === 0) {
      return {
        type: 'metadata',
        label: download.clientIp === '127.0.0.1' ? `${serviceName} Service` : 'Metadata',
        icon: Database
      };
    }

    if (
      download.service.toLowerCase() === 'steam' &&
      download.gameName &&
      download.gameName !== 'Unknown Steam Game'
    ) {
      return { type: 'game', label: download.gameName, icon: Gamepad2 };
    }

    if (bytes < 1048576) {
      return { type: 'metadata', label: `${serviceName} Update`, icon: Database };
    }

    return { type: 'content', label: `${serviceName} Content`, icon: CloudOff };
  };

  const getHitRateColor = (percent: number): string => {
    if (percent >= 75) return 'progress-bar-high';
    if (percent >= 50) return 'progress-bar-medium';
    if (percent >= 25) return 'progress-bar-low';
    return 'progress-bar-critical';
  };

  const isDownloadGroup = (item: Download | DownloadGroup): item is DownloadGroup => {
    return 'downloads' in item;
  };

  const handleDownloadClick = async (download: Download) => {
    if (
      download.service.toLowerCase() !== 'steam' ||
      (download.totalBytes || 0) === 0 ||
      !download.id
    ) {
      return;
    }

    setExpandedDownload(expandedDownload === download.id ? null : download.id);

    if (expandedDownload !== download.id && !gameInfo[download.id]) {
      // Check cache first
      const cachedGameInfo = gameInfoCache.get(download.id);
      if (cachedGameInfo) {
        setGameInfo((prev) => ({ ...prev, [download.id]: cachedGameInfo }));
        return;
      }

      if (mockMode) {
        const mockGameInfo = {
          downloadId: download.id,
          service: 'steam',
          appId: 730,
          gameName: 'Counter-Strike 2',
          gameType: 'game',
          headerImage: 'https://cdn.akamai.steamstatic.com/steam/apps/730/header.jpg',
          description: 'Counter-Strike 2 is a tactical shooter.',
          totalBytes: download.totalBytes,
          cacheHitBytes: download.cacheHitBytes,
          cacheMissBytes: download.cacheMissBytes,
          cacheHitPercent: download.cacheHitPercent
        };
        setGameInfo((prev) => ({ ...prev, [download.id]: mockGameInfo }));
        // Cache mock data too
        gameInfoCache.set(download.id, mockGameInfo);
      } else {
        try {
          setLoadingGame(download.id);
          const response = await fetch(`/api/gameinfo/download/${download.id}`);
          if (response.ok) {
            const data = await response.json();
            setGameInfo((prev) => ({ ...prev, [download.id]: data }));
            // Cache the fetched data
            gameInfoCache.set(download.id, data);
          }
        } catch (err) {
          console.error('Error fetching game info:', err);
        } finally {
          setLoadingGame(null);
        }
      }
    }
  };

  const handleGroupClick = (groupId: string) => {
    setExpandedGroup(expandedGroup === groupId ? null : groupId);
  };

  const renderGroup = useCallback(
    (group: DownloadGroup) => {
      const isExpanded = expandedGroup === group.id;
      const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;

      return (
        <Card key={group.id} padding="md">
          <div onClick={() => handleGroupClick(group.id)} className="cursor-pointer">
            <div className="grid grid-cols-12 gap-4">
              <div className="col-span-12 sm:col-span-4 md:col-span-3">
                <p className="text-xs text-themed-muted">Group</p>
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
                <p className="text-xs text-themed-muted">{group.count} items</p>
              </div>

              <div className="col-span-6 sm:col-span-4 md:col-span-3">
                <p className="text-xs text-themed-muted">Size</p>
                <p className="text-sm font-medium">
                  {group.totalBytes > 0 ? formatBytes(group.totalBytes) : 'Metadata'}
                </p>
              </div>

              <div className="col-span-6 sm:col-span-4 md:col-span-3">
                <p className="text-xs text-themed-muted">Clients</p>
                <div className="flex items-center gap-1">
                  <Users size={14} />
                  <span className="text-sm">{group.clientCount || 0}</span>
                </div>
              </div>

              <div className="col-span-12 md:col-span-3">
                <p className="text-xs text-themed-muted">Cache Hit</p>
                {group.totalBytes > 0 ? (
                  <div className="flex items-center gap-2">
                    <div className="flex-1 progress-track rounded-full h-2 overflow-hidden">
                      <div
                        className={`h-full ${getHitRateColor(hitPercent)}`}
                        style={{ width: `${hitPercent}%` }}
                      />
                    </div>
                    <span className="text-sm">{formatPercent(hitPercent)}</span>
                  </div>
                ) : (
                  <p className="text-sm text-themed-muted">N/A</p>
                )}
              </div>
            </div>
          </div>

          {isExpanded && (
            <>
              <div className="border-t border-themed-secondary my-4" />
              <div className="max-h-72 overflow-y-auto">
                <div className="space-y-2">
                  {group.downloads.map((d) => (
                    <div key={d.id} className="p-2 bg-themed-tertiary rounded">
                      <div className="grid grid-cols-4 gap-2">
                        <span className="text-xs">{d.clientIp}</span>
                        <span className="text-xs">{formatBytes(d.totalBytes)}</span>
                        <span className="text-xs">{formatPercent(d.cacheHitPercent || 0)}</span>
                        <span className="text-xs">{formatDateTime(d.startTime)}</span>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </>
          )}
        </Card>
      );
    },
    [expandedGroup]
  );

  const renderDownload = useCallback(
    (download: Download) => {
      const isExpanded = expandedDownload === download.id;
      const isSteam = download.service.toLowerCase() === 'steam';
      const hasData = (download.totalBytes || 0) > 0;
      const downloadType = getDownloadTypeInfo(download);
      const IconComponent = downloadType.icon;
      const game = download.id ? gameInfo[download.id] : undefined;

      return (
        <Card key={download.id} padding="md" className={isSteam && hasData ? 'cursor-pointer' : ''}>
          <div onClick={() => (isSteam && hasData ? handleDownloadClick(download) : undefined)}>
            <div className="grid grid-cols-12 gap-4">
              <div className="col-span-12 sm:col-span-4 md:col-span-3">
                <p className="text-xs text-themed-muted">Service</p>
                <div className="flex items-center gap-2">
                  {isSteam && hasData && (
                    <ChevronRight
                      size={16}
                      className={`transition-transform ${isExpanded ? 'rotate-90' : ''}`}
                    />
                  )}
                  <span className={`text-sm font-medium service-${download.service.toLowerCase()}`}>
                    {download.service}
                  </span>
                  <div
                    className={`w-6 h-6 rounded flex items-center justify-center ${
                      downloadType.type === 'game'
                        ? 'download-game'
                        : downloadType.type === 'metadata'
                          ? 'download-metadata'
                          : 'download-content'
                    }`}
                  >
                    <IconComponent
                      size={14}
                      className={
                        downloadType.type === 'game'
                          ? 'text-themed-primary'
                          : downloadType.type === 'metadata'
                            ? 'text-themed-muted'
                            : 'text-themed-primary'
                      }
                    />
                  </div>
                </div>
                {downloadType.label && (
                  <p className="text-xs text-themed-muted truncate">{downloadType.label}</p>
                )}
              </div>

              <div className="col-span-6 sm:col-span-4 md:col-span-2">
                <p className="text-xs text-themed-muted">Client</p>
                <p className="text-sm">{download.clientIp}</p>
              </div>

              <div className="col-span-6 sm:col-span-4 md:col-span-2">
                <p className="text-xs text-themed-muted">Size</p>
                <p className={`text-sm ${hasData ? 'font-medium' : ''}`}>
                  {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
                </p>
              </div>

              <div className="col-span-12 sm:col-span-6 md:col-span-3">
                <p className="text-xs text-themed-muted">Cache Hit</p>
                {hasData ? (
                  <div className="flex items-center gap-2">
                    <div className="flex-1 progress-track rounded-full h-2 overflow-hidden">
                      <div
                        className={`h-full ${getHitRateColor(download.cacheHitPercent || 0)}`}
                        style={{ width: `${download.cacheHitPercent || 0}%` }}
                      />
                    </div>
                    <span className="text-sm">{formatPercent(download.cacheHitPercent || 0)}</span>
                  </div>
                ) : (
                  <p className="text-sm text-themed-muted">N/A</p>
                )}
              </div>

              <div className="col-span-12 sm:col-span-6 md:col-span-2">
                <p className="text-xs text-themed-muted">Status</p>
                {download.isActive ? (
                  <span className="status-active inline-flex items-center gap-1 px-2 py-1 text-xs rounded">
                    <DownloadIcon size={12} />
                    Active
                  </span>
                ) : (
                  <span className="status-completed inline-flex items-center gap-1 px-2 py-1 text-xs rounded">
                    <Check size={12} />
                    Done
                  </span>
                )}
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
                      {game.headerImage ? (
                        <img
                          src={game.headerImage}
                          alt={game.gameName}
                          className="rounded w-56 object-cover shadow-lg"
                          style={{ height: '107px' }}
                        />
                      ) : (
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
                      )}
                    </div>
                    <div className="flex-1 min-w-0">
                      <h3 className="text-lg font-semibold text-themed-primary mb-3 truncate">
                        {game.gameName}
                      </h3>
                      {game.description && (
                        <p className="text-sm text-themed-muted mb-4 line-clamp-3">
                          {game.description.length > 200
                            ? `${game.description.substring(0, 200)}...`
                            : game.description}
                        </p>
                      )}
                      {game.appId && (
                        <a
                          href={`https://store.steampowered.com/app/${game.appId}`}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-2 text-sm text-themed-accent hover:text-themed-primary transition-colors font-medium"
                        >
                          View on Steam <ExternalLink size={16} />
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
    },
    [expandedDownload, gameInfo, loadingGame]
  );

  const renderVirtualItem = useCallback(
    (item: Download | DownloadGroup) => {
      return isDownloadGroup(item) ? renderGroup(item) : renderDownload(item);
    },
    [renderGroup, renderDownload]
  );

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 sm:gap-0">
        <h2 className="text-xl sm:text-2xl font-semibold text-themed-primary">Downloads</h2>
        <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-2 sm:gap-3">
          <div className="flex items-center gap-2">
            <CustomDropdown
              options={serviceOptions}
              value={settings.selectedService}
              onChange={(value) => updateSettings({ selectedService: value })}
              className="flex-1 sm:min-w-[140px]"
            />

            <CustomDropdown
              options={itemsPerPageOptions}
              value={settings.itemsPerPage.toString()}
              onChange={(value) =>
                updateSettings({
                  itemsPerPage: value === 'unlimited' ? 'unlimited' : parseInt(value)
                })
              }
              className="flex-1 sm:min-w-[120px]"
            />
          </div>

          <div className="relative flex-shrink-0 self-end sm:self-auto">
            <button
              onClick={() => setSettingsOpened(!settingsOpened)}
              className="p-2.5 themed-button-primary transition-colors w-full sm:w-auto"
            >
              <Settings size={20} />
            </button>

            {settingsOpened && (
              <>
                <div className="fixed inset-0 z-10" onClick={() => setSettingsOpened(false)} />
                <div className="mobile-dropdown sm:right-0 themed-card p-4">
                  <div className="space-y-3">
                    <p className="text-sm font-medium">Settings</p>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={settings.groupGames}
                        onChange={(e) => updateSettings({ groupGames: e.target.checked })}
                        className="rounded border-themed-secondary"
                      />
                      <span className="text-sm">Group similar items</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={settings.showZeroBytes}
                        onChange={(e) => updateSettings({ showZeroBytes: e.target.checked })}
                        className="rounded border-themed-secondary"
                      />
                      <span className="text-sm">Show 0-byte requests</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={settings.showSmallFiles}
                        onChange={(e) => updateSettings({ showSmallFiles: e.target.checked })}
                        className="rounded border-themed-secondary"
                      />
                      <span className="text-sm">Show small files</span>
                    </label>
                  </div>
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      {itemsToDisplay.length === 0 && (
        <div className="flex flex-col items-center justify-center py-12">
          <div className="w-16 h-16 rounded-full bg-themed-tertiary flex items-center justify-center mb-4">
            <CloudOff size={32} className="text-themed-muted" />
          </div>
          <p className="text-themed-muted">No downloads found</p>
        </div>
      )}

      {itemsToDisplay.length > 0 && (
        <>
          {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 100 ? (
            <VirtualizedList
              items={itemsToDisplay}
              height={window.innerHeight - 250}
              itemHeight={120}
              renderItem={renderVirtualItem}
              overscan={3}
            />
          ) : (
            <div className="space-y-3 max-h-[calc(100vh-250px)] overflow-y-auto custom-scrollbar">
              {itemsToDisplay.map((item) =>
                isDownloadGroup(item) ? renderGroup(item) : renderDownload(item)
              )}
            </div>
          )}
        </>
      )}

      {settings.itemsPerPage === 'unlimited' && itemsToDisplay.length > 500 && (
        <Alert color="yellow" icon={<AlertTriangle className="w-5 h-5" />}>
          Loading {itemsToDisplay.length} items. Performance optimized with virtual scrolling.
        </Alert>
      )}
    </div>
  );
};

export default DownloadsTab;
