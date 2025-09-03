import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { ChevronDown, ChevronRight, Gamepad2, ExternalLink, Loader, Database, CloudOff, Filter, CheckCircle, Info, AlertTriangle, Layers, Users, Settings, X } from 'lucide-react';
import { CachePerformanceTooltip, TimestampTooltip } from '../common/Tooltip';

// localStorage keys for persistence
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small'
};

const DownloadsTab = () => {
  const { latestDownloads, mockMode, updateMockDataCount, updateApiDownloadCount } = useData();
  const [expandedDownload, setExpandedDownload] = useState(null);
  const [expandedGroup, setExpandedGroup] = useState(null);
  const [gameInfo, setGameInfo] = useState({});
  const [loadingGame, setLoadingGame] = useState(null);
  const [isLoadingItems, setIsLoadingItems] = useState(false);
  const [renderedItems, setRenderedItems] = useState([]);
  const [showMobileFilters, setShowMobileFilters] = useState(false);

  // Load settings from localStorage with defaults
  const [showZeroBytes, setShowZeroBytes] = useState(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.SHOW_METADATA);
    return saved === 'true'; // Default to false
  });

  const [showSmallFiles, setShowSmallFiles] = useState(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES);
    return saved !== null ? saved === 'true' : true; // Default to true
  });

  const [selectedService, setSelectedService] = useState(() => {
    return localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all';
  });

  const [itemsPerPage, setItemsPerPage] = useState(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
    if (saved === 'unlimited') return 'unlimited';
    return saved ? parseInt(saved, 10) : 50;
  });

  const [groupGames, setGroupGames] = useState(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.GROUP_GAMES);
    return saved === 'true'; // Default to false
  });

  // Save settings to localStorage whenever they change
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, selectedService);
  }, [selectedService]);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, itemsPerPage.toString());
  }, [itemsPerPage]);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.GROUP_GAMES, groupGames.toString());
  }, [groupGames]);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, showZeroBytes.toString());
  }, [showZeroBytes]);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, showSmallFiles.toString());
  }, [showSmallFiles]);

  // Update mock data count OR api download count when itemsPerPage changes
  useEffect(() => {
    if (mockMode && updateMockDataCount) {
      updateMockDataCount(itemsPerPage);
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount(itemsPerPage);
    }
  }, [itemsPerPage, mockMode, updateMockDataCount, updateApiDownloadCount]);

  // Get unique services for filter dropdown
  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map(d => d.service.toLowerCase()));
    return Array.from(services).sort();
  }, [latestDownloads]);

  // Filter downloads based on selected criteria
  const filteredDownloadsBase = useMemo(() => {
    let filtered = latestDownloads;

    // Filter by zero bytes
    if (!showZeroBytes) {
      filtered = filtered.filter(d => (d.totalBytes || 0) > 0);
    }

    // Filter by small files (less than 1MB)
    if (!showSmallFiles) {
      filtered = filtered.filter(d => (d.totalBytes || 0) === 0 || (d.totalBytes || 0) >= 1048576);
    }

    // Filter by service
    if (selectedService !== 'all') {
      filtered = filtered.filter(d => d.service.toLowerCase() === selectedService);
    }

    return filtered;
  }, [latestDownloads, showZeroBytes, showSmallFiles, selectedService]);

  // Group downloads by game if grouping is enabled
  const groupedDownloads = useMemo(() => {
    if (!groupGames) return null;

    const groups = {};

    filteredDownloadsBase.forEach(download => {
      // Determine group key
      let groupKey;
      let groupName;
      let groupType;

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
          clientsArray: [],
          firstSeen: download.startTime,
          lastSeen: download.endTime || download.startTime,
          count: 0
        };
      }

      groups[groupKey].downloads.push(download);
      groups[groupKey].totalBytes += download.totalBytes || 0;
      groups[groupKey].cacheHitBytes += download.cacheHitBytes || 0;
      groups[groupKey].cacheMissBytes += download.cacheMissBytes || 0;
      groups[groupKey].clientsSet.add(download.clientIp);
      groups[groupKey].count++;

      // Update time range
      if (new Date(download.startTime) < new Date(groups[groupKey].firstSeen)) {
        groups[groupKey].firstSeen = download.startTime;
      }
      if (download.endTime && new Date(download.endTime) > new Date(groups[groupKey].lastSeen)) {
        groups[groupKey].lastSeen = download.endTime;
      }
    });

    // Convert to array, convert Sets to arrays, and sort by total bytes
    return Object.values(groups).map(group => ({
      ...group,
      clientsArray: Array.from(group.clientsSet),
      clientCount: group.clientsSet.size
    })).sort((a, b) => b.totalBytes - a.totalBytes);
  }, [filteredDownloadsBase, groupGames]);

  // Items to render (either grouped or ungrouped)
  const itemsToRender = useMemo(() => {
    return groupGames ? groupedDownloads : filteredDownloadsBase;
  }, [groupGames, groupedDownloads, filteredDownloadsBase]);

  // Progressive rendering for large datasets
  const loadItemsProgressively = useCallback(async (items, limit) => {
    if (!items) return;

    setIsLoadingItems(true);
    setRenderedItems([]);

    // If unlimited and large dataset, render in chunks
    if (limit === 'unlimited' && items.length > 100) {
      const chunkSize = 50;
      const chunks = [];

      for (let i = 0; i < items.length; i += chunkSize) {
        chunks.push(items.slice(i, i + chunkSize));
      }

      // Render chunks progressively
      for (let i = 0; i < chunks.length; i++) {
        await new Promise(resolve => setTimeout(resolve, 0)); // Allow UI to update
        setRenderedItems(prev => [...prev, ...chunks[i]]);
      }
    } else {
      // For smaller datasets or limited items, render immediately
      const limitedItems = limit === 'unlimited' ? items : items.slice(0, limit);
      setRenderedItems(limitedItems);
    }

    setIsLoadingItems(false);
  }, []);

  // Load items when filters or pagination changes
  useEffect(() => {
    loadItemsProgressively(itemsToRender, itemsPerPage);
  }, [itemsToRender, itemsPerPage, loadItemsProgressively]);

  // Calculate download duration
  const getDownloadDuration = (startTime, endTime) => {
    if (!startTime || !endTime) return null;

    const start = new Date(startTime);
    const end = new Date(endTime);
    const durationMs = end - start;

    if (durationMs < 0) return null;

    const seconds = Math.floor(durationMs / 1000);
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);

    if (hours > 0) {
      return `${hours}h ${minutes % 60}m`;
    } else if (minutes > 0) {
      return `${minutes}m ${seconds % 60}s`;
    } else {
      return `${seconds}s`;
    }
  };

  const handleDownloadClick = async (download) => {
    // Don't expand for non-Steam or zero-byte downloads
    if (download.service.toLowerCase() !== 'steam' || (download.totalBytes || 0) === 0) {
      return;
    }

    // Toggle expansion
    if (expandedDownload === download.id) {
      setExpandedDownload(null);
      return;
    }

    setExpandedDownload(download.id);

    // Don't fetch game info in mock mode
    if (mockMode) {
      setGameInfo(prev => ({
        ...prev,
        [download.id]: {
          downloadId: download.id,
          service: 'steam',
          appId: Math.floor(Math.random() * 100000) + 200000,
          gameName: 'Counter-Strike 2',
          gameType: 'game',
          headerImage: 'https://cdn.akamai.steamstatic.com/steam/apps/730/header.jpg',
          description: 'Counter-Strike 2 is the largest technical leap forward in Counter-Strike\'s history.',
          totalBytes: download.totalBytes,
          cacheHitBytes: download.cacheHitBytes,
          cacheMissBytes: download.cacheMissBytes,
          cacheHitPercent: download.cacheHitPercent,
          startTime: download.startTime,
          endTime: download.endTime,
          clientIp: download.clientIp,
          isActive: download.isActive
        }
      }));
      return;
    }

    if (gameInfo[download.id]) {
      return;
    }

    if (!download.id || download.id > 2147483647) {
      console.warn('Invalid download ID for API call:', download.id);
      return;
    }

    try {
      setLoadingGame(download.id);
      const apiUrl = import.meta.env.VITE_API_URL || '';
      const response = await fetch(`${apiUrl}/api/gameinfo/download/${download.id}`);

      if (response.ok) {
        const data = await response.json();
        setGameInfo(prev => ({ ...prev, [download.id]: data }));
      } else {
        const errorText = await response.text();
        console.error('Failed to load game info:', errorText);
        setGameInfo(prev => ({
          ...prev,
          [download.id]: { error: 'Unable to identify game' }
        }));
      }
    } catch (err) {
      console.error('Error fetching game info:', err);
      setGameInfo(prev => ({
        ...prev,
        [download.id]: { error: 'Unable to identify game' }
      }));
    } finally {
      setLoadingGame(null);
    }
  };

  const handleGroupClick = (groupId) => {
    setExpandedGroup(expandedGroup === groupId ? null : groupId);
  };

  const getDownloadType = (download) => {
    const bytes = download.totalBytes || 0;
    const isLocalhost = download.clientIp === '127.0.0.1';
    // Properly capitalize the service name
    const serviceName = download.service.charAt(0).toUpperCase() + download.service.slice(1).toLowerCase();

    if (bytes === 0) {
      if (isLocalhost) {
        return { type: 'metadata', label: `${serviceName} Service`, icon: Database };
      }
      return { type: 'metadata', label: 'Metadata', icon: Database };
    }

    // Only show game name for Steam games that have been identified
    if (download.service.toLowerCase() === 'steam' && download.gameName && download.gameName !== 'Unknown Steam Game') {
      return { type: 'game', label: download.gameName, icon: Gamepad2 };
    }

    if (bytes < 1048576) { // Less than 1MB
      return { type: 'metadata', label: `${serviceName} Update`, icon: Database };
    }

    return { type: 'content', label: `${serviceName} Content`, icon: CloudOff };
  };

  const isValidGameInfo = (game) => {
    if (!game || !game.gameName) return false;
    return !game.gameName.startsWith('Steam App') &&
      game.gameName !== 'Unknown Steam Game' &&
      game.gameName !== 'Unknown';
  };

  const handleItemsPerPageChange = (value) => {
    const newValue = value === 'unlimited' ? 'unlimited' : parseInt(value);

    // Show warning for large datasets
    if (newValue === 'unlimited' && itemsToRender && itemsToRender.length > 200) {
      if (!window.confirm(`Loading ${itemsToRender.length} items may take a while and could affect performance. Continue?`)) {
        return;
      }
    }

    setItemsPerPage(newValue);
  };

  const handleServiceFilterChange = (value) => {
    setSelectedService(value);
    // Reset expanded states when filter changes
    setExpandedDownload(null);
    setExpandedGroup(null);
  };

  const renderGroupedItem = (group) => {
    const isExpanded = expandedGroup === group.id;
    const cacheHitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;

    return (
      <div key={group.id} className="bg-gray-900 rounded-lg border border-gray-700">
        <div
          className="p-3 md:p-4 cursor-pointer hover:bg-gray-850 transition-colors"
          onClick={() => handleGroupClick(group.id)}
        >
          {/* Mobile: Stack vertically, Desktop: Grid */}
          <div className="flex flex-col sm:grid sm:grid-cols-2 md:grid-cols-5 gap-2 sm:gap-4">
            {/* Group Name */}
            <div className="sm:col-span-2 md:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Group / Type</p>
              <div className="flex items-center gap-2">
                {isExpanded ?
                  <ChevronDown className="w-4 h-4 text-gray-400 flex-shrink-0" /> :
                  <ChevronRight className="w-4 h-4 text-gray-400 flex-shrink-0" />
                }
                <Layers className="w-4 h-4 text-purple-400 flex-shrink-0" />
                <p className="text-sm font-medium text-purple-400 truncate">{group.name}</p>
              </div>
              <p className="text-xs text-gray-500 mt-1">
                {group.count} {group.count === 1 ? 'download' : 'downloads'}
              </p>
            </div>

            {/* Mobile: Two column grid for stats */}
            <div className="grid grid-cols-2 gap-2 sm:contents">
              {/* Clients */}
              <div>
                <p className="text-xs text-gray-400 mb-1">Clients</p>
                <div className="flex items-center gap-1">
                  <Users className="w-3 h-3 sm:w-4 sm:h-4 text-gray-400" />
                  <p className="text-xs sm:text-sm">{group.clientCount || 0}</p>
                </div>
              </div>

              {/* Total Size */}
              <div>
                <p className="text-xs text-gray-400 mb-1">Total Size</p>
                <p className={`text-xs sm:text-sm font-medium ${group.totalBytes > 0 ? 'text-white' : 'text-gray-500'}`}>
                  {group.totalBytes > 0 ? formatBytes(group.totalBytes) : 'Metadata'}
                </p>
              </div>
            </div>

            {/* Cache Hit Rate - Full width on mobile */}
            <div className="col-span-2 sm:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Cache Hit Rate</p>
              {group.totalBytes > 0 ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 bg-gray-700 rounded-full h-2">
                    <div
                      className={`h-2 rounded-full transition-all ${cacheHitPercent > 75 ? 'bg-green-500' :
                          cacheHitPercent > 50 ? 'bg-blue-500' :
                            cacheHitPercent > 25 ? 'bg-yellow-500' :
                              'bg-orange-500'
                        }`}
                      style={{ width: `${cacheHitPercent}%` }}
                    />
                  </div>
                  <span className="text-xs sm:text-sm font-medium">{formatPercent(cacheHitPercent)}</span>
                </div>
              ) : (
                <span className="text-xs sm:text-sm text-gray-500">N/A</span>
              )}
            </div>

            {/* Time Range - Hidden on mobile unless expanded */}
            <div className="hidden sm:block">
              <p className="text-xs text-gray-400 mb-1">Time Range</p>
              <p className="text-xs text-gray-400 truncate">
                {formatDateTime(group.firstSeen)}
              </p>
              <p className="text-xs text-gray-500 truncate">
                to {formatDateTime(group.lastSeen)}
              </p>
            </div>
          </div>

          {/* Summary stats */}
          {group.totalBytes > 0 && (
            <div className="mt-3 pt-3 border-t border-gray-800 flex flex-wrap gap-3 sm:gap-6 text-xs">
              <div className="flex items-center gap-1 sm:gap-2">
                <span className="text-gray-400">Saved:</span>
                <span className="text-green-400 font-medium">{formatBytes(group.cacheHitBytes)}</span>
              </div>
              <div className="flex items-center gap-1 sm:gap-2">
                <span className="text-gray-400">Downloaded:</span>
                <span className="text-yellow-400 font-medium">{formatBytes(group.cacheMissBytes)}</span>
              </div>
              <div className="flex items-center gap-1 sm:gap-2">
                <span className="text-gray-400">Service:</span>
                <span className="text-blue-400">{group.service}</span>
              </div>
            </div>
          )}
        </div>

        {/* Expanded downloads list */}
        {isExpanded && (
          <div className="border-t border-gray-700 bg-gray-850 p-3 md:p-4">
            <div className="space-y-2 max-h-96 overflow-y-auto">
              {group.downloads.map((download, idx) => {
                const hasData = (download.totalBytes || 0) > 0;
                const duration = getDownloadDuration(download.startTime, download.endTime);

                return (
                  <div key={download.id || idx} className="bg-gray-900 rounded p-2 sm:p-3 border border-gray-700">
                    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-2 sm:gap-3 text-xs sm:text-sm">
                      <div>
                        <p className="text-xs text-gray-500">Client</p>
                        <p className="text-gray-300 truncate">{download.clientIp}</p>
                      </div>

                      <div>
                        <p className="text-xs text-gray-500">Size</p>
                        <p className={hasData ? 'text-gray-300' : 'text-gray-500'}>
                          {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
                        </p>
                      </div>

                      <div className="hidden sm:block">
                        <p className="text-xs text-gray-500">Cache Hit</p>
                        <p className="text-gray-300">
                          {hasData ? formatPercent(download.cacheHitPercent || 0) : 'N/A'}
                        </p>
                      </div>

                      <div className="col-span-2 sm:col-span-1">
                        <p className="text-xs text-gray-500">Time</p>
                        <p className="text-gray-400 text-xs truncate">
                          {formatDateTime(download.startTime)}
                        </p>
                      </div>

                      <div className="hidden md:block">
                        <p className="text-xs text-gray-500">Duration</p>
                        <p className="text-gray-400 text-xs">
                          {duration || 'N/A'}
                        </p>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>
    );
  };

  const renderDownloadItem = (download, idx) => {
    const isExpanded = expandedDownload === download.id;
    const isSteam = download.service.toLowerCase() === 'steam';
    const downloadType = getDownloadType(download);
    const game = gameInfo[download.id];
    const hasData = (download.totalBytes || 0) > 0;
    const IconComponent = downloadType.icon;
    const duration = getDownloadDuration(download.startTime, download.endTime);

    return (
      <div key={download.id || idx} className="bg-gray-900 rounded-lg border border-gray-700">
        <div
          className={`p-3 md:p-4 ${isSteam && hasData ? 'cursor-pointer hover:bg-gray-850 transition-colors' : ''}`}
          onClick={() => handleDownloadClick(download)}
        >
          {/* Mobile: Stack layout, Desktop: Grid */}
          <div className="flex flex-col sm:grid sm:grid-cols-2 md:grid-cols-5 gap-2 sm:gap-4">
            {/* Service / Type - Full width on mobile */}
            <div className="sm:col-span-2 md:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Service / Type</p>
              <div className="flex items-center gap-2">
                {isSteam && hasData && (
                  isExpanded ?
                    <ChevronDown className="w-4 h-4 text-gray-400 flex-shrink-0" /> :
                    <ChevronRight className="w-4 h-4 text-gray-400 flex-shrink-0" />
                )}
                <p className="text-xs sm:text-sm font-medium text-blue-400">{download.service}</p>
                <IconComponent className={`w-4 h-4 flex-shrink-0 ${downloadType.type === 'game' ? 'text-green-400' :
                    downloadType.type === 'metadata' ? 'text-gray-500' :
                      'text-blue-400'
                  }`} />
              </div>
              {downloadType.type !== 'metadata' && (
                <p className={`text-xs mt-1 truncate ${downloadType.type === 'game' ? 'text-green-400' : 'text-gray-500'
                  }`}>
                  {downloadType.label}
                </p>
              )}
            </div>

            {/* Mobile: Two column grid for client/size */}
            <div className="grid grid-cols-2 gap-2 sm:contents">
              {/* Client */}
              <div>
                <p className="text-xs text-gray-400 mb-1">Client</p>
                <p className="text-xs sm:text-sm truncate">{download.clientIp}</p>
                {download.clientIp === '127.0.0.1' && (
                  <p className="text-xs text-gray-500">Local</p>
                )}
              </div>

              {/* Size */}
              <div>
                <p className="text-xs text-gray-400 mb-1">Size</p>
                <p className={`text-xs sm:text-sm ${hasData ? '' : 'text-gray-500'}`}>
                  {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
                </p>
              </div>
            </div>

            {/* Cache Hit Rate - Full width on mobile */}
            <div className="col-span-2 sm:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Cache Hit Rate</p>
              {hasData ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 bg-gray-700 rounded-full h-1.5 sm:h-2">
                    <div
                      className={`h-1.5 sm:h-2 rounded-full transition-all ${download.cacheHitPercent > 75 ? 'bg-green-500' :
                          download.cacheHitPercent > 50 ? 'bg-blue-500' :
                            download.cacheHitPercent > 25 ? 'bg-yellow-500' :
                              'bg-orange-500'
                        }`}
                      style={{ width: `${download.cacheHitPercent || 0}%` }}
                    />
                  </div>
                  <span className="text-xs sm:text-sm">{formatPercent(download.cacheHitPercent || 0)}</span>
                </div>
              ) : (
                <span className="text-xs sm:text-sm text-gray-500">N/A</span>
              )}
            </div>

            {/* Status / Time - Hidden on mobile, shown on tablet+ */}
            <div className="hidden sm:block">
              <p className="text-xs text-gray-400 mb-1 flex items-center">
                Status / Time
                <span className="ml-1">
                  <TimestampTooltip
                    startTime={formatDateTime(download.startTime)}
                    endTime={download.endTime ? formatDateTime(download.endTime) : null}
                    isActive={download.isActive}
                  >
                    <Info className="w-3 h-3 text-gray-400 cursor-help" />
                  </TimestampTooltip>
                </span>
              </p>
              <div className="space-y-1">
                {download.isActive ? (
                  <div>
                    <span className="text-xs text-green-400 flex items-center gap-1">
                      <span className="animate-pulse">●</span> Downloading
                    </span>
                    <p className="text-xs text-gray-500 truncate">
                      {formatDateTime(download.startTime)}
                    </p>
                  </div>
                ) : (
                  <div>
                    <span className="text-xs text-gray-400 flex items-center gap-1">
                      <CheckCircle className="w-3 h-3" /> Done
                    </span>
                    <p className="text-xs text-gray-500 truncate">
                      {formatDateTime(download.endTime || download.startTime)}
                    </p>
                    {duration && (
                      <p className="text-xs text-gray-600 hidden md:block">
                        {duration}
                      </p>
                    )}
                  </div>
                )}
              </div>
            </div>

            {/* Mobile status - Simple version */}
            <div className="col-span-2 sm:hidden">
              <p className="text-xs text-gray-400 mb-1">Status</p>
              {download.isActive ? (
                <span className="text-xs text-green-400">● Downloading</span>
              ) : (
                <span className="text-xs text-gray-400">Completed</span>
              )}
            </div>
          </div>
        </div>

        {/* Expandable Game Info Section - Only for Steam */}
        {isExpanded && isSteam && hasData && (
          <div className="border-t border-gray-700 bg-gray-850">
            {loadingGame === download.id ? (
              <div className="flex items-center justify-center py-6 sm:py-8">
                <Loader className="w-4 h-4 sm:w-5 sm:h-5 animate-spin text-blue-500" />
                <span className="ml-2 text-xs sm:text-sm text-gray-400">Loading game info...</span>
              </div>
            ) : game?.error ? (
              <div className="text-center py-6 sm:py-8 text-gray-500">
                <p className="text-xs sm:text-sm">Unable to identify specific game</p>
                <p className="text-xs mt-1">This may be a Steam client update or workshop content</p>
              </div>
            ) : game ? (
              <div className="p-3 md:p-4">
                <div className="mb-3 md:mb-4">
                  <h3 className="text-sm sm:text-lg font-semibold text-white">
                    {isValidGameInfo(game) ? game.gameName : 'Steam Content'}
                  </h3>
                  {game.appId && (
                    <p className="text-xs text-gray-400">
                      App ID: {game.appId}
                    </p>
                  )}
                </div>

                {/* Mobile: Stack, Desktop: Side by side */}
                <div className="flex flex-col md:flex-row gap-4 md:gap-6">
                  {/* Game Image */}
                  {game.headerImage && isValidGameInfo(game) ? (
                    <div className="flex-shrink-0 w-full md:w-auto">
                      <img
                        src={game.headerImage}
                        alt={game.gameName}
                        className="rounded-lg shadow-lg w-full md:w-[460px] h-auto md:h-[215px] object-cover"
                        onError={(e) => {
                          e.target.style.display = 'none';
                        }}
                      />
                    </div>
                  ) : (
                    <div className="flex-shrink-0">
                      <div className="flex items-center justify-center bg-gray-900 rounded-lg shadow-lg h-32 md:h-[215px] w-full md:w-[460px]">
                        <Gamepad2 className="w-16 h-16 md:w-32 md:h-32 text-gray-600" />
                      </div>
                    </div>
                  )}

                  {/* Game Stats */}
                  <div className="flex-grow space-y-2 sm:space-y-3">
                    <div className="flex justify-between text-xs sm:text-sm">
                      <span className="text-gray-400">Cache Saved:</span>
                      <span className="text-green-400">{formatBytes(game.cacheHitBytes || download.cacheHitBytes || 0)}</span>
                    </div>
                    <div className="flex justify-between text-xs sm:text-sm">
                      <span className="text-gray-400">Downloaded:</span>
                      <span className="text-yellow-400">{formatBytes(game.cacheMissBytes || download.cacheMissBytes || 0)}</span>
                    </div>
                    <div className="flex justify-between text-xs sm:text-sm">
                      <span className="text-gray-400">Total:</span>
                      <span className="text-white">{formatBytes(game.totalBytes || download.totalBytes || 0)}</span>
                    </div>

                    {game.appId && isValidGameInfo(game) && !mockMode && (
                      <a
                        href={`https://store.steampowered.com/app/${game.appId}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center gap-1 text-xs sm:text-sm text-blue-400 hover:text-blue-300 mt-2 transition-colors"
                        onClick={(e) => e.stopPropagation()}
                      >
                        View on Steam <ExternalLink className="w-3 h-3" />
                      </a>
                    )}

                    {game.description && (
                      <div className="mt-3 sm:mt-4 pt-3 sm:pt-4 border-t border-gray-700">
                        <p className="text-xs sm:text-sm text-gray-300">
                          {game.description}
                        </p>
                      </div>
                    )}
                  </div>
                </div>
              </div>
            ) : (
              <div className="text-center py-6 sm:py-8 text-gray-500">
                <p className="text-xs sm:text-sm">No additional information available</p>
              </div>
            )}
          </div>
        )}
      </div>
    );
  };

  return (
    <div className="bg-gray-800 rounded-lg p-3 md:p-6 border border-gray-700">
      {/* Header and Controls */}
      <div className="mb-4">
        {/* Title and Status - Always visible */}
        <div className="flex items-center justify-between gap-2 mb-3">
          <h2 className="text-lg md:text-xl font-semibold flex items-center">
            All Downloads
            <span className="ml-2 hidden sm:inline">
              <CachePerformanceTooltip />
            </span>
          </h2>

          {/* Status indicators - Always visible */}
          <div className="text-xs sm:text-sm text-gray-400">
            {mockMode ? (
              <span className="text-yellow-400">Mock</span>
            ) : isLoadingItems ? (
              <span className="text-blue-400 flex items-center gap-1">
                <Loader className="w-3 h-3 animate-spin" />
                <span className="hidden sm:inline">Loading...</span>
              </span>
            ) : (
              <span>
                {groupGames && groupedDownloads ?
                  `${renderedItems.length}` :
                  `${renderedItems.length}/${filteredDownloadsBase.length}`
                }
              </span>
            )}
          </div>
        </div>

        {/* Controls bar */}
        <div className="flex items-center gap-2">
          {/* Service filter */}
          <select
            value={selectedService}
            onChange={(e) => handleServiceFilterChange(e.target.value)}
            className="flex-1 sm:flex-initial sm:w-40 bg-gray-700 text-xs sm:text-sm text-gray-300 rounded px-2 py-1.5 border border-gray-600 focus:border-blue-500 focus:outline-none"
            disabled={isLoadingItems}
          >
            <option value="all">All Services</option>
            {availableServices.map(service => (
              <option key={service} value={service}>
                {service.charAt(0).toUpperCase() + service.slice(1)}
              </option>
            ))}
          </select>

          {/* Items count */}
          <select
            value={itemsPerPage}
            onChange={(e) => handleItemsPerPageChange(e.target.value)}
            className="w-24 sm:w-32 bg-gray-700 text-xs sm:text-sm text-gray-300 rounded px-2 py-1.5 border border-gray-600 focus:border-blue-500 focus:outline-none"
            disabled={isLoadingItems}
          >
            <option value={50}>50 items</option>
            <option value={100}>100 items</option>
            <option value={150}>150 items</option>
            <option value="unlimited">Unlimited</option>
          </select>

          {/* Spacer to push settings to the right */}
          <div className="flex-1"></div>

          {/* Settings dropdown button and menu */}
          <div className="relative">
            <button
              onClick={() => setShowMobileFilters(!showMobileFilters)}
              className="p-1.5 bg-gray-700 text-gray-300 rounded border border-gray-600 hover:bg-gray-600 transition-colors"
              disabled={isLoadingItems}
            >
              <Settings className="w-4 h-4" />
            </button>

            {/* Settings dropdown menu */}
            {showMobileFilters && (
              <>
                {/* Invisible backdrop to catch clicks outside */}
                <div
                  className="fixed inset-0 z-10"
                  onClick={() => setShowMobileFilters(false)}
                />

                {/* Dropdown menu */}
                <div className="absolute right-0 z-20 mt-2 w-64 p-4 bg-gray-700 rounded-lg border border-gray-600 shadow-xl">
                  <div className="flex items-center justify-between mb-3">
                    <span className="text-sm font-medium text-gray-200">Filter Settings</span>
                    <button
                      onClick={() => setShowMobileFilters(false)}
                      className="text-gray-400 hover:text-gray-200 transition-colors p-0.5"
                    >
                      <X className="w-4 h-4" />
                    </button>
                  </div>

                  <div className="space-y-3">
                    <label className="flex items-center gap-2.5 text-sm text-gray-300 cursor-pointer hover:text-white transition-colors">
                      <input
                        type="checkbox"
                        checked={groupGames}
                        onChange={(e) => setGroupGames(e.target.checked)}
                        className="rounded border-gray-500 text-purple-500 focus:ring-purple-500 focus:ring-offset-0"
                      />
                      <Layers className="w-4 h-4 text-purple-400 flex-shrink-0" />
                      <span>Group similar items</span>
                    </label>

                    <label className="flex items-center gap-2.5 text-sm text-gray-300 cursor-pointer hover:text-white transition-colors">
                      <input
                        type="checkbox"
                        checked={showZeroBytes}
                        onChange={(e) => setShowZeroBytes(e.target.checked)}
                        className="rounded border-gray-500 text-blue-500 focus:ring-blue-500 focus:ring-offset-0"
                      />
                      <Database className="w-4 h-4 text-blue-400 flex-shrink-0" />
                      <span>Show 0-byte requests</span>
                    </label>

                    <label className="flex items-center gap-2.5 text-sm text-gray-300 cursor-pointer hover:text-white transition-colors">
                      <input
                        type="checkbox"
                        checked={showSmallFiles}
                        onChange={(e) => setShowSmallFiles(e.target.checked)}
                        className="rounded border-gray-500 text-amber-500 focus:ring-amber-500 focus:ring-offset-0"
                      />
                      <Filter className="w-4 h-4 text-amber-400 flex-shrink-0" />
                      <span>Show files &lt;1MB</span>
                    </label>
                  </div>
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      {/* Performance warning for large datasets */}
      {itemsPerPage === 'unlimited' && itemsToRender && itemsToRender.length > 200 && !isLoadingItems && (
        <div className="mb-4 p-3 bg-yellow-900/30 border border-yellow-600/50 rounded-lg flex items-start gap-2">
          <AlertTriangle className="w-4 h-4 text-yellow-500 mt-0.5 flex-shrink-0" />
          <div className="text-sm text-yellow-400">
            <p className="font-medium">Large dataset ({itemsToRender.length} items)</p>
            <p className="text-yellow-400/80 text-xs mt-1">
              Rendering this many items may affect scrolling performance. Consider using pagination for better performance.
            </p>
          </div>
        </div>
      )}

      {/* Loading overlay for large datasets */}
      {isLoadingItems && itemsToRender && itemsToRender.length > 100 && (
        <div className="mb-4 p-4 bg-blue-900/20 border border-blue-600/30 rounded-lg">
          <div className="flex items-center gap-3">
            <Loader className="w-5 h-5 animate-spin text-blue-500" />
            <div>
              <p className="text-sm text-blue-400">Loading {itemsToRender.length} items...</p>
              <p className="text-xs text-blue-400/70 mt-1">
                Progress: {renderedItems.length} / {itemsToRender.length}
              </p>
            </div>
          </div>
        </div>
      )}

      <div className="space-y-3 max-h-[calc(100vh-300px)] overflow-y-auto">
        {renderedItems.length === 0 && !isLoadingItems ? (
          <div className="text-center py-8 text-gray-500">
            <CloudOff className="w-12 h-12 mx-auto mb-3 text-gray-600" />
            <p>No downloads found</p>
            <p className="text-sm mt-2">
              {selectedService !== 'all'
                ? `No ${selectedService} downloads`
                : (() => {
                  const hiddenZeroBytes = !showZeroBytes ? latestDownloads.filter(d => (d.totalBytes || 0) === 0).length : 0;
                  const hiddenSmallFiles = !showSmallFiles ? latestDownloads.filter(d => (d.totalBytes || 0) > 0 && (d.totalBytes || 0) < 1048576).length : 0;
                  const totalHidden = hiddenZeroBytes + hiddenSmallFiles;

                  if (totalHidden > 0) {
                    const parts = [];
                    if (hiddenZeroBytes > 0) parts.push(`${hiddenZeroBytes} zero-byte`);
                    if (hiddenSmallFiles > 0) parts.push(`${hiddenSmallFiles} small`);
                    return `${parts.join(' and ')} requests hidden`;
                  }
                  return 'Waiting for downloads...';
                })()}
            </p>
          </div>
        ) : (
          renderedItems.map((item, idx) => {
            return groupGames ? renderGroupedItem(item) : renderDownloadItem(item, idx);
          })
        )}
      </div>

      {/* Show more indicator if limited */}
      {itemsPerPage !== 'unlimited' && itemsToRender && itemsToRender.length > itemsPerPage && (
        <div className="text-center mt-4 text-sm text-gray-500">
          Showing {renderedItems.length} of {itemsToRender.length} total {groupGames ? 'groups' : 'downloads'}
        </div>
      )}
    </div>
  );
};

export default DownloadsTab;