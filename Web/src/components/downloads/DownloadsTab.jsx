import React, { useState } from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { ChevronDown, ChevronRight, Gamepad2, ExternalLink, Loader, Database, CloudOff } from 'lucide-react';
import { CachePerformanceTooltip } from '../common/Tooltip';

const DownloadsTab = () => {
  const { latestDownloads, mockMode } = useData();
  const [expandedDownload, setExpandedDownload] = useState(null);
  const [gameInfo, setGameInfo] = useState({});
  const [loadingGame, setLoadingGame] = useState(null);
  const [showZeroBytes, setShowZeroBytes] = useState(false);

  // Filter out zero-byte entries unless explicitly showing them
  const filteredDownloads = showZeroBytes 
    ? latestDownloads 
    : latestDownloads.filter(d => (d.totalBytes || 0) > 0);

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
      // Create mock game info
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

    // Check if we already have game info
    if (gameInfo[download.id]) {
      return;
    }

    // Only fetch if we have a valid database ID (not a timestamp from mock data)
    if (!download.id || download.id > 2147483647) {
      console.warn('Invalid download ID for API call:', download.id);
      return;
    }

    // Fetch game info from API - use relative URL
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

  // Determine download type based on characteristics
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
    
    if (bytes < 1048576) { // Less than 1MB
      return { type: 'metadata', label: 'Steam Update', icon: Database };
    }
    
    return { type: 'content', label: 'Steam Content', icon: CloudOff };
  };

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold">All Downloads</h2>
        <div className="flex items-center gap-4">
          <label className="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
            <input
              type="checkbox"
              checked={showZeroBytes}
              onChange={(e) => setShowZeroBytes(e.target.checked)}
              className="rounded border-gray-600 text-blue-500 focus:ring-blue-500"
            />
            Show metadata requests
          </label>
          <div className="text-sm text-gray-400">
            {mockMode ? (
              <span className="text-yellow-400">Mock Mode - Using demo data</span>
            ) : (
              `${filteredDownloads.length} downloads`
            )}
          </div>
        </div>
      </div>
      
      <div className="space-y-3 max-h-[calc(100vh-300px)] overflow-y-auto">
        {filteredDownloads.length === 0 ? (
          <div className="text-center py-8 text-gray-500">
            <CloudOff className="w-12 h-12 mx-auto mb-3 text-gray-600" />
            <p>No game downloads found</p>
            <p className="text-sm mt-2">
              {!showZeroBytes && latestDownloads.length > 0 
                ? `${latestDownloads.length} metadata requests hidden` 
                : 'Waiting for downloads...'}
            </p>
          </div>
        ) : (
          filteredDownloads.map((download, idx) => {
            const isExpanded = expandedDownload === download.id;
            const isSteam = download.service.toLowerCase() === 'steam';
            const downloadType = getDownloadType(download);
            const game = gameInfo[download.id];
            const hasData = (download.totalBytes || 0) > 0;
            const IconComponent = downloadType.icon;
            
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
                      <p className="text-xs text-gray-400 flex items-center">
                        Cache Hit Rate
                        <span className="ml-2">
                          <CachePerformanceTooltip />
                        </span>
                      </p>
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
                      <p className="text-xs text-gray-400">Time</p>
                      <p className="text-sm">{formatDateTime(download.startTime)}</p>
                      {download.isActive && (
                        <span className="text-xs text-green-400">● Active</span>
                      )}
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
                            {game.gameName === 'Unknown Steam Game' ? 'Steam Content' : game.gameName}
                          </h3>
                          {game.appId && (
                            <p className="text-xs text-gray-400">
                              App ID: {game.appId}
                            </p>
                          )}
                        </div>
                        <div className="flex gap-6">
                          {game.headerImage && (
                            <div className="flex-shrink-0">
                              <img 
                                src={game.headerImage} 
                                alt={game.gameName}
                                className="rounded-lg shadow-lg"
                                style={{ width: '460px', height: '215px', objectFit: 'cover' }}
                              />
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
                            {game.appId && game.gameName !== 'Unknown Steam Game' && !mockMode && (
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
    </div>
  );
};

export default DownloadsTab;