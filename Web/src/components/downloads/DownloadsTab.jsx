import React, { useState, useEffect, useMemo, useCallback } from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { ChevronDown, ChevronRight, Gamepad2, ExternalLink, Loader, Database, CloudOff, Filter, CheckCircle, Info, AlertTriangle } from 'lucide-react';
import { CachePerformanceTooltip, TimestampTooltip } from '../common/Tooltip';

const DownloadsTab = () => {
  const { latestDownloads, mockMode, updateMockDataCount, updateApiDownloadCount } = useData();
  const [expandedDownload, setExpandedDownload] = useState(null);
  const [gameInfo, setGameInfo] = useState({});
  const [loadingGame, setLoadingGame] = useState(null);
  const [showZeroBytes, setShowZeroBytes] = useState(false);
  const [selectedService, setSelectedService] = useState('all');
  const [itemsPerPage, setItemsPerPage] = useState(50);
  const [isLoadingItems, setIsLoadingItems] = useState(false);
  const [renderedItems, setRenderedItems] = useState([]);
  
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
  
  // Filter downloads based on selected criteria (but don't slice yet)
  const filteredDownloadsBase = useMemo(() => {
    let filtered = latestDownloads;
    
    // Filter by zero bytes
    if (!showZeroBytes) {
      filtered = filtered.filter(d => (d.totalBytes || 0) > 0);
    }
    
    // Filter by service
    if (selectedService !== 'all') {
      filtered = filtered.filter(d => d.service.toLowerCase() === selectedService);
    }
    
    return filtered;
  }, [latestDownloads, showZeroBytes, selectedService]);

  // Progressive rendering for large datasets
  const loadItemsProgressively = useCallback(async (items, limit) => {
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
    loadItemsProgressively(filteredDownloadsBase, itemsPerPage);
  }, [filteredDownloadsBase, itemsPerPage, loadItemsProgressively]);

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

  const getDownloadType = (download) => {
    const bytes = download.totalBytes || 0;
    const isLocalhost = download.clientIp === '127.0.0.1';
    
    if (bytes === 0) {
      if (isLocalhost) {
        return { type: 'metadata', label: 'Steam Service', icon: Database };
      }
      return { type: 'metadata', label: 'Metadata', icon: Database };
    }
    
    if (download.gameName && download.gameName !== 'Unknown Steam Game') {
      return { type: 'game', label: download.gameName, icon: Gamepad2 };
    }
    
    if (bytes < 1048576) {
      return { type: 'metadata', label: 'Steam Update', icon: Database };
    }
    
    return { type: 'content', label: 'Steam Content', icon: CloudOff };
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
    if (newValue === 'unlimited' && filteredDownloadsBase.length > 200) {
      if (!window.confirm(`Loading ${filteredDownloadsBase.length} items may take a while and could affect performance. Continue?`)) {
        return;
      }
    }
    
    setItemsPerPage(newValue);
  };

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold flex items-center">
          All Downloads
          <span className="ml-2">
            <CachePerformanceTooltip />
          </span>
        </h2>
        <div className="flex items-center gap-4">
          {/* Service Filter */}
          <div className="flex items-center gap-2">
            <Filter className="w-4 h-4 text-gray-400" />
            <select
              value={selectedService}
              onChange={(e) => setSelectedService(e.target.value)}
              className="bg-gray-700 text-sm text-gray-300 rounded px-3 py-1 border border-gray-600 focus:border-blue-500 focus:outline-none"
              disabled={isLoadingItems}
            >
              <option value="all">All Services</option>
              {availableServices.map(service => (
                <option key={service} value={service}>
                  {service.charAt(0).toUpperCase() + service.slice(1)}
                </option>
              ))}
            </select>
          </div>
          
          {/* Items per page */}
          <select
            value={itemsPerPage}
            onChange={(e) => handleItemsPerPageChange(e.target.value)}
            className="bg-gray-700 text-sm text-gray-300 rounded px-3 py-1 border border-gray-600 focus:border-blue-500 focus:outline-none"
            disabled={isLoadingItems}
          >
            <option value={50}>50 items</option>
            <option value={100}>100 items</option>
            <option value={150}>150 items</option>
            <option value="unlimited">Unlimited</option>
          </select>
          
          {/* Show metadata checkbox */}
          <label className="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
            <input
              type="checkbox"
              checked={showZeroBytes}
              onChange={(e) => setShowZeroBytes(e.target.checked)}
              className="rounded border-gray-600 text-blue-500 focus:ring-blue-500"
              disabled={isLoadingItems}
            />
            Show metadata
          </label>
          
          {/* Status indicators */}
          <div className="text-sm text-gray-400">
            {mockMode ? (
              <div className="flex items-center gap-3">
                <span className="text-yellow-400">Mock Mode</span>
                {itemsPerPage === 'unlimited' && (
                  <span className="text-xs text-gray-500">
                    (30s refresh rate)
                  </span>
                )}
              </div>
            ) : isLoadingItems ? (
              <span className="text-blue-400 flex items-center gap-1">
                <Loader className="w-3 h-3 animate-spin" />
                Loading...
              </span>
            ) : (
              <div className="flex items-center gap-3">
                <span>{renderedItems.length} of {filteredDownloadsBase.length} shown</span>
                {itemsPerPage === 'unlimited' && (
                  <span className="text-xs text-gray-500">
                    (30s refresh rate)
                  </span>
                )}
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Performance warning for large datasets */}
      {itemsPerPage === 'unlimited' && filteredDownloadsBase.length > 200 && !isLoadingItems && (
        <div className="mb-4 p-3 bg-yellow-900/30 border border-yellow-600/50 rounded-lg flex items-start gap-2">
          <AlertTriangle className="w-4 h-4 text-yellow-500 mt-0.5 flex-shrink-0" />
          <div className="text-sm text-yellow-400">
            <p className="font-medium">Large dataset ({filteredDownloadsBase.length} items)</p>
            <p className="text-yellow-400/80 text-xs mt-1">
              Rendering this many items may affect scrolling performance. Consider using pagination for better performance.
            </p>
          </div>
        </div>
      )}
      
      {/* Loading overlay for large datasets */}
      {isLoadingItems && filteredDownloadsBase.length > 100 && (
        <div className="mb-4 p-4 bg-blue-900/20 border border-blue-600/30 rounded-lg">
          <div className="flex items-center gap-3">
            <Loader className="w-5 h-5 animate-spin text-blue-500" />
            <div>
              <p className="text-sm text-blue-400">Loading {filteredDownloadsBase.length} items...</p>
              <p className="text-xs text-blue-400/70 mt-1">
                Progress: {renderedItems.length} / {filteredDownloadsBase.length}
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
                : !showZeroBytes && latestDownloads.length > 0 
                  ? `${latestDownloads.length} metadata requests hidden` 
                  : 'Waiting for downloads...'}
            </p>
          </div>
        ) : (
          renderedItems.map((download, idx) => {
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
                  className={`p-4 ${isSteam && hasData ? 'cursor-pointer hover:bg-gray-850' : ''}`}
                  onClick={() => handleDownloadClick(download)}
                >
                  <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
                    <div>
                      <p className="text-xs text-gray-400">Service / Type</p>
                      <div className="flex items-center gap-2">
                        {isSteam && hasData && (
                          isExpanded ? 
                            <ChevronDown className="w-4 h-4 text-gray-400" /> : 
                            <ChevronRight className="w-4 h-4 text-gray-400" />
                        )}
                        <p className="text-sm font-medium text-blue-400">{download.service}</p>
                        <IconComponent className={`w-4 h-4 ${
                          downloadType.type === 'game' ? 'text-green-400' :
                          downloadType.type === 'metadata' ? 'text-gray-500' :
                          'text-blue-400'
                        }`} />
                      </div>
                      {downloadType.type !== 'metadata' && (
                        <p className={`text-xs mt-1 truncate ${
                          downloadType.type === 'game' ? 'text-green-400' : 'text-gray-500'
                        }`}>
                          {downloadType.label}
                        </p>
                      )}
                    </div>
                    
                    <div>
                      <p className="text-xs text-gray-400">Client</p>
                      <p className="text-sm">{download.clientIp}</p>
                      {download.clientIp === '127.0.0.1' && (
                        <p className="text-xs text-gray-500">Local</p>
                      )}
                    </div>
                    
                    <div>
                      <p className="text-xs text-gray-400">Size</p>
                      <p className={`text-sm ${hasData ? '' : 'text-gray-500'}`}>
                        {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
                      </p>
                    </div>
                    
                    <div>
                      <p className="text-xs text-gray-400">Cache Hit Rate</p>
                      {hasData ? (
                        <div className="flex items-center gap-2">
                          <div className="flex-1 bg-gray-700 rounded-full h-2">
                            <div 
                              className={`h-2 rounded-full ${
                                download.cacheHitPercent > 75 ? 'bg-green-500' :
                                download.cacheHitPercent > 50 ? 'bg-blue-500' :
                                download.cacheHitPercent > 25 ? 'bg-yellow-500' :
                                'bg-orange-500'
                              }`}
                              style={{ width: `${download.cacheHitPercent || 0}%` }}
                            />
                          </div>
                          <span className="text-sm">{formatPercent(download.cacheHitPercent || 0)}</span>
                        </div>
                      ) : (
                        <span className="text-sm text-gray-500">N/A</span>
                      )}
                    </div>
                    
                    <div>
                      <p className="text-xs text-gray-400 flex items-center">
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
                            <p className="text-xs text-gray-500">
                              Started: {formatDateTime(download.startTime)}
                            </p>
                          </div>
                        ) : (
                          <div>
                            <span className="text-xs text-gray-400 flex items-center gap-1">
                              <CheckCircle className="w-3 h-3" /> Completed
                            </span>
                            <p className="text-xs text-gray-500">
                              {formatDateTime(download.endTime || download.startTime)}
                            </p>
                            {duration && (
                              <p className="text-xs text-gray-600">
                                Duration: {duration}
                              </p>
                            )}
                          </div>
                        )}
                      </div>
                    </div>
                  </div>
                </div>
                
                {/* Expandable Game Info Section */}
                {isExpanded && isSteam && hasData && (
                  <div className="border-t border-gray-700 bg-gray-850">
                    {loadingGame === download.id ? (
                      <div className="flex items-center justify-center py-8">
                        <Loader className="w-5 h-5 animate-spin text-blue-500" />
                        <span className="ml-2 text-gray-400">Loading game information...</span>
                      </div>
                    ) : game?.error ? (
                      <div className="text-center py-8 text-gray-500">
                        <p>Unable to identify specific game</p>
                        <p className="text-xs mt-1">This may be a Steam client update or workshop content</p>
                      </div>
                    ) : game ? (
                      <div className="p-4">
                        <div className="mb-4">
                          <h3 className="text-lg font-semibold text-white">
                            {isValidGameInfo(game) ? game.gameName : 'Steam Content'}
                          </h3>
                          {game.appId && (
                            <p className="text-xs text-gray-400">
                              App ID: {game.appId}
                            </p>
                          )}
                        </div>
                        
                        <div className="flex gap-6">
                          {game.headerImage && isValidGameInfo(game) ? (
                            <div className="flex-shrink-0">
                              <img 
                                src={game.headerImage} 
                                alt={game.gameName}
                                className="rounded-lg shadow-lg"
                                style={{ width: '460px', height: '215px', objectFit: 'cover' }}
                                onError={(e) => {
                                  e.target.style.display = 'none';
                                  e.target.parentElement.innerHTML = `
                                    <div class="flex items-center justify-center bg-gray-900 rounded-lg shadow-lg" style="width: 460px; height: 215px;">
                                      <svg class="w-32 h-32 text-gray-600" viewBox="0 0 24 24" fill="currentColor">
                                        <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 15.5v-5l3 3-3 2.5zm1-6.5V6l5 5-5 3z"/>
                                      </svg>
                                    </div>
                                  `;
                                }}
                              />
                            </div>
                          ) : (
                            <div className="flex-shrink-0">
                              <div className="flex items-center justify-center bg-gray-900 rounded-lg shadow-lg" style={{ width: '460px', height: '215px' }}>
                                <Gamepad2 className="w-32 h-32 text-gray-600" />
                              </div>
                            </div>
                          )}
                          
                          <div className="flex-grow space-y-3">
                            <div className="flex justify-between text-sm">
                              <span className="text-gray-400">Cache Saved:</span>
                              <span className="text-green-400">{formatBytes(game.cacheHitBytes || download.cacheHitBytes || 0)}</span>
                            </div>
                            <div className="flex justify-between text-sm">
                              <span className="text-gray-400">Downloaded:</span>
                              <span className="text-yellow-400">{formatBytes(game.cacheMissBytes || download.cacheMissBytes || 0)}</span>
                            </div>
                            <div className="flex justify-between text-sm">
                              <span className="text-gray-400">Total:</span>
                              <span className="text-white">{formatBytes(game.totalBytes || download.totalBytes || 0)}</span>
                            </div>
                            
                            {game.appId && isValidGameInfo(game) && !mockMode && (
                              <a
                                href={`https://store.steampowered.com/app/${game.appId}`}
                                target="_blank"
                                rel="noopener noreferrer"
                                className="inline-flex items-center gap-1 text-sm text-blue-400 hover:text-blue-300 mt-2"
                                onClick={(e) => e.stopPropagation()}
                              >
                                View on Steam <ExternalLink className="w-3 h-3" />
                              </a>
                            )}
                            
                            {game.description && (
                              <div className="mt-4 pt-4 border-t border-gray-700">
                                <p className="text-sm text-gray-300">
                                  {game.description}
                                </p>
                              </div>
                            )}
                          </div>
                        </div>
                      </div>
                    ) : (
                      <div className="text-center py-8 text-gray-500">
                        No additional information available
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>
      
      {/* Show more indicator if limited */}
      {itemsPerPage !== 'unlimited' && filteredDownloadsBase.length > itemsPerPage && (
        <div className="text-center mt-4 text-sm text-gray-500">
          Showing {renderedItems.length} of {filteredDownloadsBase.length} total downloads
        </div>
      )}
    </div>
  );
};

export default DownloadsTab;