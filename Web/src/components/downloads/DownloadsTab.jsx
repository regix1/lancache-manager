import React, { useState, useEffect, useMemo, useCallback, useRef, useLayoutEffect } from 'react';
import ReactDOM from 'react-dom';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { 
  ChevronRight, Gamepad2, ExternalLink, Loader, Database, 
  CloudOff, Filter, CheckCircle, Info, AlertTriangle, Layers, Users, 
  Settings, X 
} from 'lucide-react';
import { CachePerformanceTooltip, TimestampTooltip } from '../common/Tooltip';
import './downloads-animations.css';

// localStorage keys
const STORAGE_KEYS = {
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small'
};

// Settings Dropdown Component - Fixed positioning
const SettingsDropdown = ({ isOpen, onClose, buttonRef, settings, updateSettings }) => {
  const [renderDropdown, setRenderDropdown] = useState(false);
  const [position, setPosition] = useState(null);
  
  useLayoutEffect(() => {
    if (isOpen && buttonRef?.current) {
      // Calculate position immediately
      const rect = buttonRef.current.getBoundingClientRect();
      const dropdownWidth = 256;
      const dropdownHeight = 180;
      
      let top = rect.bottom + 4;
      let left = rect.right - dropdownWidth;
      
      if (left < 10) {
        left = Math.max(10, rect.left);
      }
      
      if (left + dropdownWidth > window.innerWidth - 10) {
        left = window.innerWidth - dropdownWidth - 10;
      }
      
      if (top + dropdownHeight > window.innerHeight - 10) {
        top = rect.top - dropdownHeight - 4;
        if (top < 10) {
          top = 10;
        }
      }
      
      setPosition({ top, left });
      // Only render after position is calculated
      setRenderDropdown(true);
      
      const updatePosition = () => {
        if (!buttonRef.current) return;
        
        const newRect = buttonRef.current.getBoundingClientRect();
        let newTop = newRect.bottom + 4;
        let newLeft = newRect.right - dropdownWidth;
        
        if (newLeft < 10) {
          newLeft = Math.max(10, newRect.left);
        }
        
        if (newLeft + dropdownWidth > window.innerWidth - 10) {
          newLeft = window.innerWidth - dropdownWidth - 10;
        }
        
        if (newTop + dropdownHeight > window.innerHeight - 10) {
          newTop = newRect.top - dropdownHeight - 4;
          if (newTop < 10) {
            newTop = 10;
          }
        }
        
        setPosition({ top: newTop, left: newLeft });
      };
      
      window.addEventListener('scroll', updatePosition, true);
      window.addEventListener('resize', updatePosition);
      
      return () => {
        window.removeEventListener('scroll', updatePosition, true);
        window.removeEventListener('resize', updatePosition);
      };
    } else {
      setRenderDropdown(false);
      setPosition(null);
    }
  }, [isOpen, buttonRef]);
  
  // Don't render until we have a position
  if (!isOpen || !renderDropdown || !position) return null;

  return ReactDOM.createPortal(
    <>
      <div
        className="fixed inset-0"
        style={{ zIndex: 99998 }}
        onClick={onClose}
      />
      
      <div 
        className="fixed bg-gray-700 rounded-lg border border-gray-600 shadow-2xl p-4 animate-fadeIn"
        style={{ 
          zIndex: 99999,
          top: `${position.top}px`,
          left: `${position.left}px`,
          width: '256px'
        }}
      >
        <div className="flex items-center justify-between mb-3">
          <span className="text-sm font-medium text-gray-200">Filter Settings</span>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-200 transition-colors p-0.5"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="space-y-3">
          <label className="flex items-center gap-2.5 text-sm text-gray-300 cursor-pointer hover:text-white transition-colors">
            <input
              type="checkbox"
              checked={settings.groupGames}
              onChange={(e) => updateSettings({ groupGames: e.target.checked })}
              className="rounded border-gray-500 text-purple-500 focus:ring-purple-500 focus:ring-offset-0"
            />
            <Layers className="w-4 h-4 text-purple-400 flex-shrink-0" />
            <span>Group similar items</span>
          </label>

          <label className="flex items-center gap-2.5 text-sm text-gray-300 cursor-pointer hover:text-white transition-colors">
            <input
              type="checkbox"
              checked={settings.showZeroBytes}
              onChange={(e) => updateSettings({ showZeroBytes: e.target.checked })}
              className="rounded border-gray-500 text-blue-500 focus:ring-blue-500 focus:ring-offset-0"
            />
            <Database className="w-4 h-4 text-blue-400 flex-shrink-0" />
            <span>Show 0-byte requests</span>
          </label>

          <label className="flex items-center gap-2.5 text-sm text-gray-300 cursor-pointer hover:text-white transition-colors">
            <input
              type="checkbox"
              checked={settings.showSmallFiles}
              onChange={(e) => updateSettings({ showSmallFiles: e.target.checked })}
              className="rounded border-gray-500 text-amber-500 focus:ring-amber-500 focus:ring-offset-0"
            />
            <Filter className="w-4 h-4 text-amber-400 flex-shrink-0" />
            <span>Show files &lt;1MB</span>
          </label>
        </div>
      </div>
    </>,
    document.body
  );
};

// Main Downloads Tab Component
const DownloadsTab = () => {
  const { latestDownloads, mockMode, updateMockDataCount, updateApiDownloadCount } = useData();
  
  // UI State
  const [expandedDownload, setExpandedDownload] = useState(null);
  const [expandedGroup, setExpandedGroup] = useState(null);
  const [gameInfo, setGameInfo] = useState({});
  const [loadingGame, setLoadingGame] = useState(null);
  const [isLoadingItems, setIsLoadingItems] = useState(false);
  const [renderedItems, setRenderedItems] = useState([]);
  const [showSettingsDropdown, setShowSettingsDropdown] = useState(false);
  
  // Refs
  const settingsButtonRef = useRef(null);
  
  // Filter Settings with localStorage persistence
  const [settings, setSettings] = useState({
    showZeroBytes: localStorage.getItem(STORAGE_KEYS.SHOW_METADATA) === 'true',
    showSmallFiles: localStorage.getItem(STORAGE_KEYS.SHOW_SMALL_FILES) !== 'false',
    selectedService: localStorage.getItem(STORAGE_KEYS.SERVICE_FILTER) || 'all',
    itemsPerPage: (() => {
      const saved = localStorage.getItem(STORAGE_KEYS.ITEMS_PER_PAGE);
      return saved === 'unlimited' ? 'unlimited' : (saved ? parseInt(saved, 10) : 50);
    })(),
    groupGames: localStorage.getItem(STORAGE_KEYS.GROUP_GAMES) === 'true'
  });
  
  // Update settings helper
  const updateSettings = useCallback((updates) => {
    setSettings(prev => ({ ...prev, ...updates }));
  }, []);
  
  // Save settings to localStorage when they change
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, settings.itemsPerPage.toString());
    localStorage.setItem(STORAGE_KEYS.GROUP_GAMES, settings.groupGames.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
  }, [settings]);
  
  // Update data count when pagination changes
  useEffect(() => {
    if (mockMode && updateMockDataCount) {
      updateMockDataCount(settings.itemsPerPage);
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount(settings.itemsPerPage);
    }
  }, [settings.itemsPerPage, mockMode, updateMockDataCount, updateApiDownloadCount]);
  
  // Get unique services for filter dropdown
  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map(d => d.service.toLowerCase()));
    return Array.from(services).sort();
  }, [latestDownloads]);
  
  // Filter downloads based on settings
  const filteredDownloads = useMemo(() => {
    let filtered = latestDownloads;
    
    if (!settings.showZeroBytes) {
      filtered = filtered.filter(d => (d.totalBytes || 0) > 0);
    }
    
    if (!settings.showSmallFiles) {
      filtered = filtered.filter(d => (d.totalBytes || 0) === 0 || (d.totalBytes || 0) >= 1048576);
    }
    
    if (settings.selectedService !== 'all') {
      filtered = filtered.filter(d => d.service.toLowerCase() === settings.selectedService);
    }
    
    return filtered;
  }, [latestDownloads, settings]);
  
  // Group downloads if enabled
  const groupedDownloads = useMemo(() => {
    if (!settings.groupGames) return null;
    
    const groups = {};
    
    filteredDownloads.forEach(download => {
      let groupKey, groupName, groupType;
      
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
          count: 0
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
    
    return Object.values(groups).map(group => ({
      ...group,
      clientCount: group.clientsSet.size
    })).sort((a, b) => b.totalBytes - a.totalBytes);
  }, [filteredDownloads, settings.groupGames]);
  
  // Items to render
  const itemsToRender = useMemo(() => {
    return settings.groupGames ? groupedDownloads : filteredDownloads;
  }, [settings.groupGames, groupedDownloads, filteredDownloads]);
  
  // Progressive rendering
  const loadItemsProgressively = useCallback(async (items, limit) => {
    if (!items) return;
    
    setIsLoadingItems(true);
    setRenderedItems([]);
    
    if (limit === 'unlimited' && items.length > 100) {
      const chunkSize = 50;
      const chunks = [];
      
      for (let i = 0; i < items.length; i += chunkSize) {
        chunks.push(items.slice(i, i + chunkSize));
      }
      
      for (let i = 0; i < chunks.length; i++) {
        await new Promise(resolve => setTimeout(resolve, 0));
        setRenderedItems(prev => [...prev, ...chunks[i]]);
      }
    } else {
      const limitedItems = limit === 'unlimited' ? items : items.slice(0, limit);
      setRenderedItems(limitedItems);
    }
    
    setIsLoadingItems(false);
  }, []);
  
  // Load items when filters change
  useEffect(() => {
    loadItemsProgressively(itemsToRender, settings.itemsPerPage);
  }, [itemsToRender, settings.itemsPerPage, loadItemsProgressively]);
  
  // Helper functions
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
  
  const getDownloadType = (download) => {
    const bytes = download.totalBytes || 0;
    const isLocalhost = download.clientIp === '127.0.0.1';
    const serviceName = download.service.charAt(0).toUpperCase() + download.service.slice(1).toLowerCase();
    
    if (bytes === 0) {
      if (isLocalhost) {
        return { type: 'metadata', label: `${serviceName} Service`, icon: Database };
      }
      return { type: 'metadata', label: 'Metadata', icon: Database };
    }
    
    if (download.service.toLowerCase() === 'steam' && download.gameName && download.gameName !== 'Unknown Steam Game') {
      return { type: 'game', label: download.gameName, icon: Gamepad2 };
    }
    
    if (bytes < 1048576) {
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
  
  // Event Handlers
  const handleDownloadClick = async (download) => {
    if (download.service.toLowerCase() !== 'steam' || (download.totalBytes || 0) === 0) {
      return;
    }
    
    if (expandedDownload === download.id) {
      setExpandedDownload(null);
      return;
    }
    
    setExpandedDownload(download.id);
    
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
    
    if (gameInfo[download.id] || !download.id || download.id > 2147483647) {
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
        setGameInfo(prev => ({
          ...prev,
          [download.id]: { error: 'Unable to identify game' }
        }));
      }
    } catch (err) {
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
  
  const handleItemsPerPageChange = (value) => {
    const newValue = value === 'unlimited' ? 'unlimited' : parseInt(value);
    
    if (newValue === 'unlimited' && itemsToRender && itemsToRender.length > 200) {
      if (!window.confirm(`Loading ${itemsToRender.length} items may affect performance. Continue?`)) {
        return;
      }
    }
    
    updateSettings({ itemsPerPage: newValue });
  };
  
  const handleServiceFilterChange = (value) => {
    updateSettings({ selectedService: value });
    setExpandedDownload(null);
    setExpandedGroup(null);
  };
  
  // Render functions
  const renderGroupedItem = (group) => {
    const isExpanded = expandedGroup === group.id;
    const cacheHitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    
    return (
      <div key={group.id} className="download-item bg-gray-900 rounded-lg border border-gray-700 hover-lift">
        <div className="p-3 md:p-4 cursor-pointer group-header smooth-transition" onClick={() => handleGroupClick(group.id)}>
          <div className="flex flex-col sm:grid sm:grid-cols-2 md:grid-cols-5 gap-2 sm:gap-4">
            <div className="sm:col-span-2 md:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Group / Type</p>
              <div className="flex items-center gap-2">
                <div className={`chevron-icon ${isExpanded ? 'expanded' : ''}`}>
                  <ChevronRight className="w-4 h-4 text-gray-400 flex-shrink-0" />
                </div>
                <Layers className="w-4 h-4 text-purple-400 flex-shrink-0" />
                <p className="text-sm font-medium text-purple-400 truncate">{group.name}</p>
              </div>
              <p className="text-xs text-gray-500 mt-1">
                {group.count} {group.count === 1 ? 'download' : 'downloads'}
              </p>
            </div>
            
            <div className="grid grid-cols-2 gap-2 sm:contents">
              <div>
                <p className="text-xs text-gray-400 mb-1">Clients</p>
                <div className="flex items-center gap-1">
                  <Users className="w-3 h-3 sm:w-4 sm:h-4 text-gray-400" />
                  <p className="text-xs sm:text-sm data-value animated-number">{group.clientCount || 0}</p>
                </div>
              </div>
              
              <div>
                <p className="text-xs text-gray-400 mb-1">Total Size</p>
                <p className={`text-xs sm:text-sm font-medium data-value animated-number ${group.totalBytes > 0 ? 'text-white' : 'text-gray-500'}`}>
                  {group.totalBytes > 0 ? formatBytes(group.totalBytes) : 'Metadata'}
                </p>
              </div>
            </div>
            
            <div className="col-span-2 sm:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Cache Hit Rate</p>
              {group.totalBytes > 0 ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 bg-gray-700 rounded-full h-2">
                    <div
                      className={`cache-hit-bar h-2 rounded-full ${
                        cacheHitPercent > 75 ? 'bg-green-500' :
                        cacheHitPercent > 50 ? 'bg-blue-500' :
                        cacheHitPercent > 25 ? 'bg-yellow-500' :
                        'bg-orange-500'
                      }`}
                      style={{ width: `${cacheHitPercent}%` }}
                    />
                  </div>
                  <span className="text-xs sm:text-sm font-medium animated-number">{formatPercent(cacheHitPercent)}</span>
                </div>
              ) : (
                <span className="text-xs sm:text-sm text-gray-500">N/A</span>
              )}
            </div>
            
            <div className="hidden sm:block">
              <p className="text-xs text-gray-400 mb-1">Time Range</p>
              <p className="text-xs text-gray-400 truncate">{formatDateTime(group.firstSeen)}</p>
              <p className="text-xs text-gray-500 truncate">to {formatDateTime(group.lastSeen)}</p>
            </div>
          </div>
          
          {group.totalBytes > 0 && (
            <div className="mt-3 pt-3 border-t border-gray-800 flex flex-wrap gap-3 sm:gap-6 text-xs">
              <div className="flex items-center gap-1 sm:gap-2">
                <span className="text-gray-400">Saved:</span>
                <span className="text-green-400 font-medium cache-hits animated-number">{formatBytes(group.cacheHitBytes)}</span>
              </div>
              <div className="flex items-center gap-1 sm:gap-2">
                <span className="text-gray-400">Downloaded:</span>
                <span className="text-yellow-400 font-medium cache-misses animated-number">{formatBytes(group.cacheMissBytes)}</span>
              </div>
              <div className="flex items-center gap-1 sm:gap-2">
                <span className="text-gray-400">Service:</span>
                <span className="text-blue-400 service-name">{group.service}</span>
              </div>
            </div>
          )}
        </div>
        
        {isExpanded && (
          <div className="expandable-content border-t border-gray-700 bg-gray-850 p-3 md:p-4">
            <div className="space-y-2 max-h-96 overflow-y-auto">
              {group.downloads.map((download, idx) => {
                const hasData = (download.totalBytes || 0) > 0;
                const duration = getDownloadDuration(download.startTime, download.endTime);
                
                return (
                  <div key={download.id || idx} className="bg-gray-900 rounded p-2 sm:p-3 border border-gray-700 smooth-transition">
                    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-2 sm:gap-3 text-xs sm:text-sm">
                      <div>
                        <p className="text-xs text-gray-500">Client</p>
                        <p className="text-gray-300 truncate">{download.clientIp}</p>
                      </div>
                      <div>
                        <p className="text-xs text-gray-500">Size</p>
                        <p className={hasData ? 'text-gray-300 animated-number' : 'text-gray-500'}>
                          {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
                        </p>
                      </div>
                      <div className="hidden sm:block">
                        <p className="text-xs text-gray-500">Cache Hit</p>
                        <p className="text-gray-300 animated-number">
                          {hasData ? formatPercent(download.cacheHitPercent || 0) : 'N/A'}
                        </p>
                      </div>
                      <div className="col-span-2 sm:col-span-1">
                        <p className="text-xs text-gray-500">Time</p>
                        <p className="text-gray-400 text-xs truncate">{formatDateTime(download.startTime)}</p>
                      </div>
                      <div className="hidden md:block">
                        <p className="text-xs text-gray-500">Duration</p>
                        <p className="text-gray-400 text-xs">{duration || 'N/A'}</p>
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
      <div key={download.id || idx} className={`download-item bg-gray-900 rounded-lg border border-gray-700 ${isSteam && hasData ? 'hover-lift' : ''}`}>
        <div className={`p-3 md:p-4 ${isSteam && hasData ? 'cursor-pointer smooth-transition' : ''}`} onClick={() => handleDownloadClick(download)}>
          <div className="flex flex-col sm:grid sm:grid-cols-2 md:grid-cols-5 gap-2 sm:gap-4">
            <div className="sm:col-span-2 md:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Service / Type</p>
              <div className="flex items-center gap-2">
                {isSteam && hasData && (
                  <div className={`chevron-icon ${isExpanded ? 'expanded' : ''}`}>
                    <ChevronRight className="w-4 h-4 text-gray-400 flex-shrink-0" />
                  </div>
                )}
                <p className="text-xs sm:text-sm font-medium text-blue-400 service-name">{download.service}</p>
                <IconComponent className={`w-4 h-4 flex-shrink-0 ${
                  downloadType.type === 'game' ? 'text-green-400' :
                  downloadType.type === 'metadata' ? 'text-gray-500' :
                  'text-blue-400'
                }`} />
              </div>
              {downloadType.type !== 'metadata' && (
                <p className={`text-xs mt-1 truncate ${downloadType.type === 'game' ? 'text-green-400' : 'text-gray-500'}`}>
                  {downloadType.label}
                </p>
              )}
            </div>
            
            <div className="grid grid-cols-2 gap-2 sm:contents">
              <div>
                <p className="text-xs text-gray-400 mb-1">Client</p>
                <p className="text-xs sm:text-sm truncate">{download.clientIp}</p>
                {download.clientIp === '127.0.0.1' && (
                  <p className="text-xs text-gray-500">Local</p>
                )}
              </div>
              
              <div>
                <p className="text-xs text-gray-400 mb-1">Size</p>
                <p className={`text-xs sm:text-sm data-value animated-number ${hasData ? '' : 'text-gray-500'}`}>
                  {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
                </p>
              </div>
            </div>
            
            <div className="col-span-2 sm:col-span-1">
              <p className="text-xs text-gray-400 mb-1">Cache Hit Rate</p>
              {hasData ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 bg-gray-700 rounded-full h-1.5 sm:h-2">
                    <div
                      className={`cache-hit-bar h-1.5 sm:h-2 rounded-full ${
                        download.cacheHitPercent > 75 ? 'bg-green-500' :
                        download.cacheHitPercent > 50 ? 'bg-blue-500' :
                        download.cacheHitPercent > 25 ? 'bg-yellow-500' :
                        'bg-orange-500'
                      }`}
                      style={{ width: `${download.cacheHitPercent || 0}%` }}
                    />
                  </div>
                  <span className="text-xs sm:text-sm animated-number">{formatPercent(download.cacheHitPercent || 0)}</span>
                </div>
              ) : (
                <span className="text-xs sm:text-sm text-gray-500">N/A</span>
              )}
            </div>
            
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
                      <span className="status-pulse">●</span> Downloading
                    </span>
                    <p className="text-xs text-gray-500 truncate">{formatDateTime(download.startTime)}</p>
                  </div>
                ) : (
                  <div>
                    <span className="text-xs text-gray-400 flex items-center gap-1">
                      <CheckCircle className="w-3 h-3" /> Done
                    </span>
                    <p className="text-xs text-gray-500 truncate">{formatDateTime(download.endTime || download.startTime)}</p>
                    {duration && <p className="text-xs text-gray-600 hidden md:block">{duration}</p>}
                  </div>
                )}
              </div>
            </div>
            
            <div className="col-span-2 sm:hidden">
              <p className="text-xs text-gray-400 mb-1">Status</p>
              {download.isActive ? (
                <span className="text-xs text-green-400 flex items-center gap-1">
                  <span className="status-pulse">●</span> Downloading
                </span>
              ) : (
                <span className="text-xs text-gray-400">Completed</span>
              )}
            </div>
          </div>
        </div>
        
        {isExpanded && isSteam && hasData && (
          <div className="expandable-content border-t border-gray-700 bg-gray-850">
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
                  {game.appId && <p className="text-xs text-gray-400">App ID: {game.appId}</p>}
                </div>
                
                <div className="flex flex-col md:flex-row gap-4 md:gap-6">
                  {game.headerImage && isValidGameInfo(game) ? (
                    <div className="flex-shrink-0 w-full md:w-auto">
                      <img
                        src={game.headerImage}
                        alt={game.gameName}
                        className="rounded-lg shadow-lg w-full md:w-[460px] h-auto md:h-[215px] object-cover smooth-transition"
                        onError={(e) => { e.target.style.display = 'none'; }}
                      />
                    </div>
                  ) : (
                    <div className="flex-shrink-0">
                      <div className="flex items-center justify-center bg-gray-900 rounded-lg shadow-lg h-32 md:h-[215px] w-full md:w-[460px]">
                        <Gamepad2 className="w-16 h-16 md:w-32 md:h-32 text-gray-600" />
                      </div>
                    </div>
                  )}
                  
                  <div className="flex-grow space-y-2 sm:space-y-3">
                    <div className="flex justify-between text-xs sm:text-sm">
                      <span className="text-gray-400">Cache Saved:</span>
                      <span className="text-green-400 cache-hits animated-number">{formatBytes(game.cacheHitBytes || download.cacheHitBytes || 0)}</span>
                    </div>
                    <div className="flex justify-between text-xs sm:text-sm">
                      <span className="text-gray-400">Downloaded:</span>
                      <span className="text-yellow-400 cache-misses animated-number">{formatBytes(game.cacheMissBytes || download.cacheMissBytes || 0)}</span>
                    </div>
                    <div className="flex justify-between text-xs sm:text-sm">
                      <span className="text-gray-400">Total:</span>
                      <span className="text-white data-value animated-number">{formatBytes(game.totalBytes || download.totalBytes || 0)}</span>
                    </div>
                    
                    {game.appId && isValidGameInfo(game) && !mockMode && (
                      <a
                        href={`https://store.steampowered.com/app/${game.appId}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center gap-1 text-xs sm:text-sm text-blue-400 hover:text-blue-300 mt-2 smooth-transition button-press"
                        onClick={(e) => e.stopPropagation()}
                      >
                        View on Steam <ExternalLink className="w-3 h-3" />
                      </a>
                    )}
                    
                    {game.description && (
                      <div className="mt-3 sm:mt-4 pt-3 sm:pt-4 border-t border-gray-700">
                        <p className="text-xs sm:text-sm text-gray-300">{game.description}</p>
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
  
  // Main render
  return (
    <div className="bg-gray-800 rounded-lg p-3 md:p-6 border border-gray-700 downloads-container">
      {/* Header */}
      <div className="mb-4">
        <div className="flex items-center justify-between gap-2 mb-3 downloads-header">
          <h2 className="text-lg md:text-xl font-semibold flex items-center">
            All Downloads
            <span className="ml-2 hidden sm:inline">
              <CachePerformanceTooltip />
            </span>
          </h2>
          
          <div className="text-xs sm:text-sm text-gray-400">
            {mockMode ? (
              <span className="text-yellow-400">Mock</span>
            ) : isLoadingItems ? (
              <span className="text-blue-400 flex items-center gap-1 loading-indicator">
                <Loader className="w-3 h-3 animate-spin" />
                <span className="hidden sm:inline">Loading...</span>
              </span>
            ) : (
              <span>
                {settings.groupGames && groupedDownloads ? 
                  `${renderedItems.length}` : 
                  `${renderedItems.length}/${filteredDownloads.length}`
                }
              </span>
            )}
          </div>
        </div>
        
        {/* Controls */}
        <div className="flex items-center gap-2 downloads-controls">
          <select
            value={settings.selectedService}
            onChange={(e) => handleServiceFilterChange(e.target.value)}
            className="flex-1 sm:flex-initial sm:w-40 bg-gray-700 text-xs sm:text-sm text-gray-300 rounded px-2 py-1.5 border border-gray-600 focus:border-blue-500 focus:outline-none smooth-transition"
            disabled={isLoadingItems}
          >
            <option value="all">All Services</option>
            {availableServices.map(service => (
              <option key={service} value={service}>
                {service.charAt(0).toUpperCase() + service.slice(1)}
              </option>
            ))}
          </select>
          
          <select
            value={settings.itemsPerPage}
            onChange={(e) => handleItemsPerPageChange(e.target.value)}
            className="w-24 sm:w-32 bg-gray-700 text-xs sm:text-sm text-gray-300 rounded px-2 py-1.5 border border-gray-600 focus:border-blue-500 focus:outline-none smooth-transition"
            disabled={isLoadingItems}
          >
            <option value={50}>50 items</option>
            <option value={100}>100 items</option>
            <option value={150}>150 items</option>
            <option value="unlimited">Unlimited</option>
          </select>
          
          <div className="flex-1"></div>
          
          <button
            ref={settingsButtonRef}
            onClick={() => setShowSettingsDropdown(!showSettingsDropdown)}
            className="p-1.5 bg-gray-700 text-gray-300 rounded border border-gray-600 hover:bg-gray-600 smooth-transition button-press"
            disabled={isLoadingItems}
          >
            <Settings className="w-4 h-4" />
          </button>
        </div>
      </div>
      
      {/* Warnings */}
      {settings.itemsPerPage === 'unlimited' && itemsToRender && itemsToRender.length > 200 && !isLoadingItems && (
        <div className="mb-4 p-3 bg-yellow-900/30 border border-yellow-600/50 rounded-lg flex items-start gap-2 warning-banner">
          <AlertTriangle className="w-4 h-4 text-yellow-500 mt-0.5 flex-shrink-0" />
          <div className="text-sm text-yellow-400">
            <p className="font-medium">Large dataset ({itemsToRender.length} items)</p>
            <p className="text-yellow-400/80 text-xs mt-1">
              Rendering this many items may affect scrolling performance.
            </p>
          </div>
        </div>
      )}
      
      {/* Content */}
      <div className="space-y-3 max-h-[calc(100vh-300px)] overflow-y-auto downloads-list">
        {renderedItems.length === 0 && !isLoadingItems ? (
          <div className="empty-state">
            <CloudOff className="w-12 h-12 mx-auto mb-3 text-gray-600" />
            <p className="text-gray-500">No downloads found</p>
          </div>
        ) : (
          renderedItems.map((item, idx) => {
            return settings.groupGames ? renderGroupedItem(item) : renderDownloadItem(item, idx);
          })
        )}
      </div>
      
      {/* Settings Dropdown */}
      <SettingsDropdown
        isOpen={showSettingsDropdown}
        onClose={() => setShowSettingsDropdown(false)}
        buttonRef={settingsButtonRef}
        settings={settings}
        updateSettings={updateSettings}
      />
    </div>
  );
};

export default DownloadsTab;