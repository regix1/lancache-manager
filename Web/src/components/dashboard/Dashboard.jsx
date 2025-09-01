import React, { useState, useEffect, useRef } from 'react';
import { HardDrive, Download, Users, Database, TrendingUp, Zap, Server, Activity, Eye, EyeOff, ChevronDown, Search } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { STORAGE_KEYS } from '../../utils/constants';
import StatCard from '../common/StatCard';
import EnhancedServiceChart from './EnhancedServiceChart';
import RecentDownloadsPanel from './RecentDownloadsPanel';
import TopClientsTable from './TopClientsTable';

// Default visibility state for all cards
const DEFAULT_CARD_VISIBILITY = {
  totalCache: true,
  usedSpace: true,
  bandwidthSaved: true,
  addedToCache: true,
  totalServed: true,
  activeDownloads: true,
  activeClients: true,
  cacheHitRatio: true
};

const Dashboard = () => {
  const { cacheInfo, activeDownloads, latestDownloads, clientStats, serviceStats, mockMode } = useData();
  const [dashboardStats, setDashboardStats] = useState(null);
  const [loading, setLoading] = useState(true);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const dropdownRef = useRef(null);
  
  // Load card visibility from localStorage
  const [cardVisibility, setCardVisibility] = useState(() => {
    const saved = localStorage.getItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY);
    if (saved) {
      try {
        return { ...DEFAULT_CARD_VISIBILITY, ...JSON.parse(saved) };
      } catch (e) {
        console.error('Failed to parse card visibility settings:', e);
        return DEFAULT_CARD_VISIBILITY;
      }
    }
    return DEFAULT_CARD_VISIBILITY;
  });

  // Save card visibility to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem(STORAGE_KEYS.DASHBOARD_CARD_VISIBILITY, JSON.stringify(cardVisibility));
  }, [cardVisibility]);
  
  // Close dropdown when clicking outside
  useEffect(() => {
    const handleClickOutside = (event) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target)) {
        setDropdownOpen(false);
        setSearchQuery('');
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);
  
  useEffect(() => {
    // Only fetch dashboard stats if not in mock mode
    if (!mockMode) {
      fetchDashboardStats();
      const interval = setInterval(fetchDashboardStats, 10000); // Refresh every 10 seconds
      return () => clearInterval(interval);
    } else {
      // In mock mode, set mock dashboard stats
      setDashboardStats({
        totalBandwidthSaved: 875000000000,
        totalAddedToCache: 125000000000,
        totalServed: 1000000000000,
        uniqueClients: 12,
        cacheHitRatio: 0.875,
        serviceBreakdown: [
          { service: 'steam', bytes: 450000000000, percentage: 45 },
          { service: 'epic', bytes: 250000000000, percentage: 25 },
          { service: 'blizzard', bytes: 150000000000, percentage: 15 },
          { service: 'origin', bytes: 100000000000, percentage: 10 },
          { service: 'riot', bytes: 50000000000, percentage: 5 }
        ]
      });
      setLoading(false);
    }
  }, [mockMode]);

  const fetchDashboardStats = async () => {
    try {
      const apiUrl = import.meta.env.VITE_API_URL || '';
      const response = await fetch(`${apiUrl}/api/stats/dashboard?period=24h`);
      if (response.ok) {
        const data = await response.json();
        setDashboardStats(data);
      }
    } catch (error) {
      console.error('Failed to fetch dashboard stats:', error);
    } finally {
      setLoading(false);
    }
  };
  
  const toggleCardVisibility = (cardKey) => {
    setCardVisibility(prev => ({
      ...prev,
      [cardKey]: !prev[cardKey]
    }));
  };
  
  const activeClients = [...new Set(activeDownloads.map(d => d.clientIp))].length;
  const totalActiveDownloads = activeDownloads.length;
  
  // Calculate total downloads from service stats
  const totalDownloads = serviceStats.reduce((sum, service) => sum + (service.totalDownloads || 0), 0);
  
  // Use dashboard stats if available, otherwise fall back to calculated values
  const bandwidthSaved = dashboardStats?.totalBandwidthSaved || 
    serviceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
  const addedToCache = dashboardStats?.totalAddedToCache || 
    serviceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
  const totalServed = dashboardStats?.totalServed || 
    (bandwidthSaved + addedToCache);

  // Define all stat cards with their data and metadata
  const statCards = [
    {
      key: 'totalCache',
      title: 'Total Cache',
      value: cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '0 B',
      subtitle: 'Drive capacity',
      icon: Database,
      color: 'blue',
      visible: cardVisibility.totalCache
    },
    {
      key: 'usedSpace',
      title: 'Used Space',
      value: cacheInfo ? formatBytes(cacheInfo.usedCacheSize) : '0 B',
      subtitle: cacheInfo ? formatPercent(cacheInfo.usagePercent) : '0%',
      icon: HardDrive,
      color: 'green',
      visible: cardVisibility.usedSpace
    },
    {
      key: 'bandwidthSaved',
      title: 'Bandwidth Saved',
      value: formatBytes(bandwidthSaved),
      subtitle: 'Internet bandwidth saved',
      icon: TrendingUp,
      color: 'emerald',
      visible: cardVisibility.bandwidthSaved
    },
    {
      key: 'addedToCache',
      title: 'Added to Cache',
      value: formatBytes(addedToCache),
      subtitle: 'New content cached',
      icon: Zap,
      color: 'purple',
      visible: cardVisibility.addedToCache
    },
    {
      key: 'totalServed',
      title: 'Total Served',
      value: formatBytes(totalServed),
      subtitle: 'All-time data served',
      icon: Server,
      color: 'indigo',
      visible: cardVisibility.totalServed
    },
    {
      key: 'activeDownloads',
      title: 'Active Downloads',
      value: totalActiveDownloads,
      subtitle: `${latestDownloads.length} recent`,
      icon: Download,
      color: 'orange',
      visible: cardVisibility.activeDownloads
    },
    {
      key: 'activeClients',
      title: 'Active Clients',
      value: dashboardStats?.uniqueClients || activeClients,
      subtitle: `${totalDownloads} total downloads`,
      icon: Users,
      color: 'yellow',
      visible: cardVisibility.activeClients
    },
    {
      key: 'cacheHitRatio',
      title: 'Cache Hit Ratio',
      value: formatPercent((dashboardStats?.cacheHitRatio || 0) * 100),
      subtitle: 'Overall effectiveness',
      icon: Activity,
      color: 'cyan',
      visible: cardVisibility.cacheHitRatio
    }
  ];

  // Filter visible cards and hidden cards
  const visibleCards = statCards.filter(card => card.visible);
  const hiddenCards = statCards.filter(card => !card.visible);
  const hiddenCardsCount = hiddenCards.length;

  // Filter hidden cards based on search query
  const filteredHiddenCards = hiddenCards.filter(card =>
    card.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
    card.subtitle.toLowerCase().includes(searchQuery.toLowerCase())
  );

  // Helper function to get gradient colors for icons
  const getIconGradient = (color) => {
    const gradients = {
      blue: 'from-blue-500 to-blue-600',
      green: 'from-green-500 to-green-600',
      emerald: 'from-emerald-500 to-emerald-600',
      purple: 'from-purple-500 to-purple-600',
      indigo: 'from-indigo-500 to-indigo-600',
      orange: 'from-orange-500 to-orange-600',
      yellow: 'from-yellow-500 to-yellow-600',
      cyan: 'from-cyan-500 to-cyan-600'
    };
    return gradients[color] || 'from-gray-500 to-gray-600';
  };

  return (
    <div className="space-y-6">
      {/* Stats Grid Header with enhanced dropdown */}
      {hiddenCardsCount > 0 && (
        <div className="relative" ref={dropdownRef}>
          <div className="bg-gray-800 rounded-lg px-4 py-2 border border-gray-700 flex items-center justify-between">
            <span className="text-sm text-gray-400">
              {hiddenCardsCount} stat card{hiddenCardsCount !== 1 ? 's' : ''} hidden
            </span>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setDropdownOpen(!dropdownOpen)}
                className="flex items-center gap-1 text-xs text-blue-400 hover:text-blue-300 transition-colors px-2 py-1 rounded hover:bg-gray-700/50"
              >
                Add cards
                <ChevronDown className={`w-3 h-3 transition-transform ${dropdownOpen ? 'rotate-180' : ''}`} />
              </button>
              <button
                onClick={() => setCardVisibility(DEFAULT_CARD_VISIBILITY)}
                className="text-xs text-blue-400 hover:text-blue-300 transition-colors px-2 py-1 rounded hover:bg-gray-700/50"
              >
                Show all
              </button>
            </div>
          </div>

          {/* Enhanced Dropdown */}
          {dropdownOpen && (
            <div className="absolute right-0 mt-2 w-80 bg-gray-800 rounded-lg border border-gray-700 shadow-xl z-50">
              {/* Search input */}
              <div className="p-3 border-b border-gray-700">
                <div className="relative">
                  <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-gray-400" />
                  <input
                    type="text"
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    placeholder="Search hidden cards..."
                    className="w-full pl-10 pr-3 py-2 bg-gray-700 text-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 text-sm"
                    autoFocus
                  />
                </div>
              </div>

              {/* Hidden cards list */}
              <div className="max-h-96 overflow-y-auto p-2">
                {filteredHiddenCards.length > 0 ? (
                  filteredHiddenCards.map((card) => {
                    const Icon = card.icon;
                    return (
                      <button
                        key={card.key}
                        onClick={() => {
                          toggleCardVisibility(card.key);
                          if (hiddenCardsCount === 1) {
                            setDropdownOpen(false);
                            setSearchQuery('');
                          }
                        }}
                        className="w-full p-3 rounded-lg hover:bg-gray-700/50 transition-colors flex items-center gap-3 group"
                      >
                        <div className={`p-2 rounded-lg bg-gradient-to-br ${getIconGradient(card.color)} group-hover:scale-110 transition-transform`}>
                          <Icon className="w-5 h-5 text-white" />
                        </div>
                        <div className="flex-1 text-left">
                          <div className="text-sm text-gray-200 font-medium">{card.title}</div>
                          <div className="text-xs text-gray-400">{card.subtitle}</div>
                        </div>
                        <Eye className="w-4 h-4 text-gray-500 group-hover:text-blue-400 transition-colors" />
                      </button>
                    );
                  })
                ) : (
                  <div className="px-3 py-6 text-center text-gray-500 text-sm">
                    No hidden cards match "{searchQuery}"
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}

      {/* Enhanced Stats Grid - Always 4 columns on large screens */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        {visibleCards.map((card) => (
          <div key={card.key} className="relative group">
            <StatCard
              title={card.title}
              value={card.value}
              subtitle={card.subtitle}
              icon={card.icon}
              color={card.color}
            />
            {/* Visibility toggle button */}
            <button
              onClick={() => toggleCardVisibility(card.key)}
              className="absolute top-2 right-2 p-1.5 rounded-lg bg-gray-700/50 opacity-0 group-hover:opacity-100 transition-opacity hover:bg-gray-600/50"
              title="Hide this card"
            >
              <EyeOff className="w-3.5 h-3.5 text-gray-400" />
            </button>
          </div>
        ))}
      </div>

      {/* Enhanced Charts Row with tabs */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <EnhancedServiceChart dashboardStats={dashboardStats} />
        <RecentDownloadsPanel />
      </div>

      {/* Top Clients */}
      <TopClientsTable />
    </div>
  );
};

export default Dashboard;