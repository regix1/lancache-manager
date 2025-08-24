import React, { useState } from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { ChevronDown, ChevronRight, Gamepad2, ExternalLink, Loader } from 'lucide-react';

const DownloadsTab = () => {
  const { latestDownloads, mockMode } = useData();
  const [expandedDownload, setExpandedDownload] = useState(null);
  const [gameInfo, setGameInfo] = useState({});
  const [loadingGame, setLoadingGame] = useState(null);

  const handleDownloadClick = async (download) => {
    // Only expand for Steam downloads
    if (download.service.toLowerCase() !== 'steam') {
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
      setGameInfo(prev => ({ 
        ...prev, 
        [download.id]: { error: 'Invalid download ID' }
      }));
      return;
    }

    // Fetch game info from API
    try {
      setLoadingGame(download.id);
      const apiUrl = import.meta.env.VITE_API_URL || `http://${window.location.hostname}:8080`;
      const response = await fetch(`${apiUrl}/api/gameinfo/download/${download.id}`);
      
      if (response.ok) {
        const data = await response.json();
        setGameInfo(prev => ({ ...prev, [download.id]: data }));
      } else {
        const errorText = await response.text();
        console.error('Failed to load game info:', errorText);
        setGameInfo(prev => ({ 
          ...prev, 
          [download.id]: { error: 'Failed to load game information' }
        }));
      }
    } catch (err) {
      console.error('Error fetching game info:', err);
      setGameInfo(prev => ({ 
        ...prev, 
        [download.id]: { error: 'Failed to load game information' }
      }));
    } finally {
      setLoadingGame(null);
    }
  };

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold">All Downloads</h2>
        <div className="text-sm text-gray-400">
          {mockMode ? (
            <span className="text-yellow-400">Mock Mode - Using demo data</span>
          ) : (
            'Click Steam downloads to see game details'
          )}
        </div>
      </div>
      
      <div className="space-y-3 max-h-[calc(100vh-300px)] overflow-y-auto">
        {latestDownloads.map((download, idx) => {
          const isExpanded = expandedDownload === download.id;
          const isSteam = download.service.toLowerCase() === 'steam';
          const game = gameInfo[download.id];
          
          return (
            <div key={download.id || idx} className="bg-gray-900 rounded-lg border border-gray-700">
              <div 
                className={`p-4 ${isSteam ? 'cursor-pointer hover:bg-gray-850' : ''}`}
                onClick={() => handleDownloadClick(download)}
              >
                <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
                  <div>
                    <p className="text-xs text-gray-400">Service</p>
                    <div className="flex items-center gap-2">
                      {isSteam && (
                        isExpanded ? 
                          <ChevronDown className="w-4 h-4 text-gray-400" /> : 
                          <ChevronRight className="w-4 h-4 text-gray-400" />
                      )}
                      <p className="text-sm font-medium text-blue-400">{download.service}</p>
                      {isSteam && <Gamepad2 className="w-4 h-4 text-blue-400" />}
                    </div>
                    {download.gameName && (
                      <p className="text-xs text-gray-500 mt-1 truncate">{download.gameName}</p>
                    )}
                  </div>
                  <div>
                    <p className="text-xs text-gray-400">Client</p>
                    <p className="text-sm">{download.clientIp}</p>
                  </div>
                  <div>
                    <p className="text-xs text-gray-400">Size</p>
                    <p className="text-sm">{formatBytes(download.totalBytes || 0)}</p>
                  </div>
                  <div>
                    <p className="text-xs text-gray-400">Cache Hit Rate</p>
                    <div className="flex items-center gap-2">
                      <div className="flex-1 bg-gray-700 rounded-full h-2">
                        <div 
                          className={`h-2 rounded-full ${
                            download.cacheHitPercent > 75 ? 'bg-green-500' :
                            download.cacheHitPercent > 50 ? 'bg-yellow-500' :
                            'bg-red-500'
                          }`}
                          style={{ width: `${download.cacheHitPercent || 0}%` }}
                        />
                      </div>
                      <span className="text-sm">{formatPercent(download.cacheHitPercent || 0)}</span>
                    </div>
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
              {isExpanded && isSteam && (
                <div className="border-t border-gray-700 p-4 bg-gray-850">
                  {loadingGame === download.id ? (
                    <div className="flex items-center justify-center py-4">
                      <Loader className="w-5 h-5 animate-spin text-blue-500" />
                      <span className="ml-2 text-gray-400">Loading game information...</span>
                    </div>
                  ) : game?.error ? (
                    <div className="text-center py-4 text-red-400">
                      {game.error}
                    </div>
                  ) : game ? (
                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                      {/* Left side - Game info */}
                      <div>
                        <div className="flex items-start gap-3">
                          {game.headerImage && (
                            <img 
                              src={game.headerImage} 
                              alt={game.gameName}
                              className="w-24 h-12 object-cover rounded"
                            />
                          )}
                          <div className="flex-1">
                            <h3 className="text-lg font-semibold text-white">
                              {game.gameName || 'Unknown Game'}
                            </h3>
                            {game.appId && (
                              <p className="text-xs text-gray-400">
                                App ID: {game.appId}
                              </p>
                            )}
                            {game.description && (
                              <p className="text-sm text-gray-300 mt-2 line-clamp-2">
                                {game.description}
                              </p>
                            )}
                          </div>
                        </div>
                      </div>
                      
                      {/* Right side - Stats */}
                      <div className="space-y-2">
                        <div className="flex justify-between text-sm">
                          <span className="text-gray-400">Cache Saved:</span>
                          <span className="text-green-400">{formatBytes(game.cacheHitBytes || download.cacheHitBytes || 0)}</span>
                        </div>
                        <div className="flex justify-between text-sm">
                          <span className="text-gray-400">Downloaded:</span>
                          <span className="text-red-400">{formatBytes(game.cacheMissBytes || download.cacheMissBytes || 0)}</span>
                        </div>
                        <div className="flex justify-between text-sm">
                          <span className="text-gray-400">Total:</span>
                          <span className="text-white">{formatBytes(game.totalBytes || download.totalBytes || 0)}</span>
                        </div>
                        {game.appId && !mockMode && (
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
                      </div>
                    </div>
                  ) : (
                    <div className="text-center py-4 text-gray-500">
                      No game information available
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};

export default DownloadsTab;