import React, { useState, useEffect, useMemo, useCallback } from 'react';
import { 
  ChevronRight, 
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
      return saved === 'unlimited' ? 'unlimited' as const : (saved ? parseInt(saved, 10) : 20);
    })(),
    groupGames: localStorage.getItem(STORAGE_KEYS.GROUP_GAMES) === 'true'
  }));
  
  const updateSettings = useCallback((updates: Partial<DownloadSettings>) => {
    setSettings(prev => ({ ...prev, ...updates }));
  }, []);
  
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.SERVICE_FILTER, settings.selectedService);
    localStorage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE, settings.itemsPerPage.toString());
    localStorage.setItem(STORAGE_KEYS.GROUP_GAMES, settings.groupGames.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_METADATA, settings.showZeroBytes.toString());
    localStorage.setItem(STORAGE_KEYS.SHOW_SMALL_FILES, settings.showSmallFiles.toString());
  }, [settings]);
  
  useEffect(() => {
    const count = settings.itemsPerPage === 'unlimited' ? 100 : settings.itemsPerPage;
    if (mockMode && updateMockDataCount) {
      updateMockDataCount(count);
    } else if (!mockMode && updateApiDownloadCount) {
      updateApiDownloadCount(count);
    }
  }, [settings.itemsPerPage, mockMode, updateMockDataCount, updateApiDownloadCount]);
  
  const availableServices = useMemo(() => {
    const services = new Set(latestDownloads.map(d => d.service.toLowerCase()));
    return Array.from(services).sort();
  }, [latestDownloads]);
  
  const filteredDownloads = useMemo(() => {
    let filtered = [...latestDownloads];
    
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
  
  const groupedDownloads = useMemo((): DownloadGroup[] | null => {
    if (!settings.groupGames) return null;
    
    const groups: Record<string, DownloadGroup> = {};
    
    filteredDownloads.forEach(download => {
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
    
    return Object.values(groups).map(group => ({
      ...group,
      clientCount: group.clientsSet.size
    })).sort((a, b) => b.totalBytes - a.totalBytes);
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
    
    if (download.service.toLowerCase() === 'steam' && 
        download.gameName && 
        download.gameName !== 'Unknown Steam Game') {
      return { type: 'game', label: download.gameName, icon: Gamepad2 };
    }
    
    if (bytes < 1048576) {
      return { type: 'metadata', label: `${serviceName} Update`, icon: Database };
    }
    
    return { type: 'content', label: `${serviceName} Content`, icon: CloudOff };
  };
  
  const getHitRateColor = (percent: number): string => {
    if (percent >= 75) return 'bg-green-500';
    if (percent >= 50) return 'bg-blue-500';
    if (percent >= 25) return 'bg-yellow-500';
    return 'bg-orange-500';
  };
  
  const isDownloadGroup = (item: Download | DownloadGroup): item is DownloadGroup => {
    return 'downloads' in item;
  };
  
  const handleDownloadClick = async (download: Download) => {
    if (download.service.toLowerCase() !== 'steam' || 
        (download.totalBytes || 0) === 0 || 
        !download.id) {
      return;
    }
    
    setExpandedDownload(expandedDownload === download.id ? null : download.id);
    
    if (expandedDownload !== download.id && !gameInfo[download.id]) {
      if (mockMode) {
        setGameInfo(prev => ({
          ...prev,
          [download.id]: {
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
          }
        }));
      } else {
        try {
          setLoadingGame(download.id);
          const response = await fetch(`/api/gameinfo/download/${download.id}`);
          if (response.ok) {
            const data = await response.json();
            setGameInfo(prev => ({ ...prev, [download.id]: data }));
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
  
  const renderGroup = useCallback((group: DownloadGroup) => {
    const isExpanded = expandedGroup === group.id;
    const hitPercent = group.totalBytes > 0 ? (group.cacheHitBytes / group.totalBytes) * 100 : 0;
    
    return (
      <Card key={group.id} padding="md">
        <div onClick={() => handleGroupClick(group.id)} className="cursor-pointer">
          <div className="grid grid-cols-12 gap-4">
            <div className="col-span-12 sm:col-span-4 md:col-span-3">
              <p className="text-xs text-gray-400">Group</p>
              <div className="flex items-center gap-2">
                <ChevronRight 
                  size={16} 
                  className={`transition-transform ${isExpanded ? 'rotate-90' : ''}`}
                />
                <div className="w-6 h-6 rounded bg-purple-500 bg-opacity-20 flex items-center justify-center">
                  <Layers size={14} className="text-purple-400" />
                </div>
                <span className="text-sm font-medium text-purple-400">{group.name}</span>
              </div>
              <p className="text-xs text-gray-400">{group.count} items</p>
            </div>
            
            <div className="col-span-6 sm:col-span-4 md:col-span-3">
              <p className="text-xs text-gray-400">Size</p>
              <p className="text-sm font-medium">
                {group.totalBytes > 0 ? formatBytes(group.totalBytes) : 'Metadata'}
              </p>
            </div>
            
            <div className="col-span-6 sm:col-span-4 md:col-span-3">
              <p className="text-xs text-gray-400">Clients</p>
              <div className="flex items-center gap-1">
                <Users size={14} />
                <span className="text-sm">{group.clientCount || 0}</span>
              </div>
            </div>
            
            <div className="col-span-12 md:col-span-3">
              <p className="text-xs text-gray-400">Cache Hit</p>
              {group.totalBytes > 0 ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 bg-gray-700 rounded-full h-2 overflow-hidden">
                    <div 
                      className={`h-full ${getHitRateColor(hitPercent)}`}
                      style={{ width: `${hitPercent}%` }}
                    />
                  </div>
                  <span className="text-sm">{formatPercent(hitPercent)}</span>
                </div>
              ) : (
                <p className="text-sm text-gray-400">N/A</p>
              )}
            </div>
          </div>
        </div>
        
        {isExpanded && (
          <>
            <div className="border-t border-gray-700 my-4" />
            <div className="max-h-72 overflow-y-auto">
              <div className="space-y-2">
                {group.downloads.map(d => (
                  <div key={d.id} className="p-2 bg-gray-700 rounded">
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
  }, [expandedGroup]);
  
  const renderDownload = useCallback((download: Download) => {
    const isExpanded = expandedDownload === download.id;
    const isSteam = download.service.toLowerCase() === 'steam';
    const hasData = (download.totalBytes || 0) > 0;
    const downloadType = getDownloadTypeInfo(download);
    const IconComponent = downloadType.icon;
    const game = download.id ? gameInfo[download.id] : undefined;
    
    return (
      <Card 
        key={download.id} 
        padding="md"
        className={isSteam && hasData ? 'cursor-pointer' : ''}
      >
        <div onClick={() => isSteam && hasData ? handleDownloadClick(download) : undefined}>
          <div className="grid grid-cols-12 gap-4">
            <div className="col-span-12 sm:col-span-4 md:col-span-3">
              <p className="text-xs text-gray-400">Service</p>
              <div className="flex items-center gap-2">
                {isSteam && hasData && (
                  <ChevronRight 
                    size={16} 
                    className={`transition-transform ${isExpanded ? 'rotate-90' : ''}`}
                  />
                )}
                <span className="text-sm text-blue-400 font-medium">{download.service}</span>
                <div className={`w-6 h-6 rounded flex items-center justify-center ${
                  downloadType.type === 'game' ? 'bg-green-500 bg-opacity-20' : 
                  downloadType.type === 'metadata' ? 'bg-gray-500 bg-opacity-20' : 'bg-blue-500 bg-opacity-20'
                }`}>
                  <IconComponent size={14} className={
                    downloadType.type === 'game' ? 'text-green-400' : 
                    downloadType.type === 'metadata' ? 'text-gray-400' : 'text-blue-400'
                  } />
                </div>
              </div>
              {downloadType.label && (
                <p className="text-xs text-gray-400 truncate">{downloadType.label}</p>
              )}
            </div>
            
            <div className="col-span-6 sm:col-span-4 md:col-span-2">
              <p className="text-xs text-gray-400">Client</p>
              <p className="text-sm">{download.clientIp}</p>
            </div>
            
            <div className="col-span-6 sm:col-span-4 md:col-span-2">
              <p className="text-xs text-gray-400">Size</p>
              <p className={`text-sm ${hasData ? 'font-medium' : ''}`}>
                {hasData ? formatBytes(download.totalBytes) : 'Metadata'}
              </p>
            </div>
            
            <div className="col-span-12 sm:col-span-6 md:col-span-3">
              <p className="text-xs text-gray-400">Cache Hit</p>
              {hasData ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 bg-gray-700 rounded-full h-2 overflow-hidden">
                    <div 
                      className={`h-full ${getHitRateColor(download.cacheHitPercent || 0)}`}
                      style={{ width: `${download.cacheHitPercent || 0}%` }}
                    />
                  </div>
                  <span className="text-sm">{formatPercent(download.cacheHitPercent || 0)}</span>
                </div>
              ) : (
                <p className="text-sm text-gray-400">N/A</p>
              )}
            </div>
            
            <div className="col-span-12 sm:col-span-6 md:col-span-2">
              <p className="text-xs text-gray-400">Status</p>
              {download.isActive ? (
                <span className="inline-flex items-center gap-1 px-2 py-1 bg-green-500 bg-opacity-20 text-green-400 text-xs rounded">
                  <DownloadIcon size={12} />
                  Active
                </span>
              ) : (
                <span className="inline-flex items-center gap-1 px-2 py-1 bg-gray-500 bg-opacity-20 text-gray-400 text-xs rounded">
                  <Check size={12} />
                  Done
                </span>
              )}
            </div>
          </div>
          
          {isExpanded && game && (
            <>
              <div className="border-t border-gray-700 my-4" />
              {loadingGame === download.id ? (
                <div className="flex justify-center py-4">
                  <Loader className="w-6 h-6 animate-spin" />
                </div>
              ) : (
                <div className="grid grid-cols-12 gap-4">
                  {game.headerImage && (
                    <div className="col-span-12 md:col-span-6">
                      <img src={game.headerImage} alt={game.gameName} className="rounded-lg w-full" />
                    </div>
                  )}
                  <div className={`col-span-12 ${game.headerImage ? 'md:col-span-6' : ''}`}>
                    <div className="space-y-3">
                      <h3 className="text-lg font-semibold">{game.gameName}</h3>
                      {game.description && (
                        <p className="text-sm text-gray-400">{game.description}</p>
                      )}
                      {game.appId && (
                        <a 
                          href={`https://store.steampowered.com/app/${game.appId}`} 
                          target="_blank" 
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-1 text-sm text-blue-400 hover:text-blue-300"
                        >
                          View on Steam <ExternalLink size={14} />
                        </a>
                      )}
                    </div>
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </Card>
    );
  }, [expandedDownload, gameInfo, loadingGame]);
  
  const renderVirtualItem = useCallback((item: Download | DownloadGroup) => {
    return isDownloadGroup(item) ? renderGroup(item) : renderDownload(item);
  }, [renderGroup, renderDownload]);
  
  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h2 className="text-2xl font-semibold">Downloads</h2>
        <div className="flex items-center gap-3">
          <select
            value={settings.selectedService}
            onChange={(e) => updateSettings({ selectedService: e.target.value })}
            className="px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white focus:outline-none focus:border-blue-500"
          >
            <option value="all">All Services</option>
            {availableServices.map(s => (
              <option key={s} value={s}>
                {s.charAt(0).toUpperCase() + s.slice(1)}
              </option>
            ))}
          </select>
          
          <select
            value={settings.itemsPerPage.toString()}
            onChange={(e) => updateSettings({ 
              itemsPerPage: e.target.value === 'unlimited' ? 'unlimited' : parseInt(e.target.value) 
            })}
            className="px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white focus:outline-none focus:border-blue-500"
          >
            <option value="20">20 items</option>
            <option value="50">50 items</option>
            <option value="100">100 items</option>
            <option value="200">200 items</option>
            <option value="unlimited">Load All</option>
          </select>
          
          <div className="relative">
            <button
              onClick={() => setSettingsOpened(!settingsOpened)}
              className="p-2.5 bg-gray-700 hover:bg-gray-600 rounded transition-colors"
            >
              <Settings size={20} />
            </button>
            
            {settingsOpened && (
              <>
                <div 
                  className="fixed inset-0 z-10" 
                  onClick={() => setSettingsOpened(false)}
                />
                <div className="absolute right-0 top-12 z-20 bg-gray-800 border border-gray-700 rounded-lg p-4 shadow-lg min-w-[200px]">
                  <div className="space-y-3">
                    <p className="text-sm font-medium">Settings</p>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={settings.groupGames}
                        onChange={(e) => updateSettings({ groupGames: e.target.checked })}
                        className="rounded border-gray-600"
                      />
                      <span className="text-sm">Group similar items</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={settings.showZeroBytes}
                        onChange={(e) => updateSettings({ showZeroBytes: e.target.checked })}
                        className="rounded border-gray-600"
                      />
                      <span className="text-sm">Show 0-byte requests</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={settings.showSmallFiles}
                        onChange={(e) => updateSettings({ showSmallFiles: e.target.checked })}
                        className="rounded border-gray-600"
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
          <div className="w-16 h-16 rounded-full bg-gray-700 flex items-center justify-center mb-4">
            <CloudOff size={32} className="text-gray-500" />
          </div>
          <p className="text-gray-400">No downloads found</p>
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
            <div className="space-y-3 max-h-[calc(100vh-250px)] overflow-y-auto">
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