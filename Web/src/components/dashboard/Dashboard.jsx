import React, { useState, useEffect, useRef } from 'react';
import { HardDrive, Download, Users, Database, TrendingUp, Zap, Server, Activity, Eye, EyeOff, ChevronDown, Search, Clock } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { STORAGE_KEYS, API_BASE } from '../../utils/constants';
import StatCard from '../common/StatCard';
import EnhancedServiceChart from './EnhancedServiceChart';
import RecentDownloadsPanel from './RecentDownloadsPanel';
import TopClientsTable from './TopClientsTable';
import ApiService from '../../services/api.service';

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
  const { cacheInfo, activeDownloads, mockMode } = useData();
  const [dashboardStats, setDashboardStats] = useState(null);
  const [filteredLatestDownloads, setFilteredLatestDownloads] = useState([]);
  const [filteredClientStats, setFilteredClientStats] = useState([]);
  const [filteredServiceStats, setFilteredServiceStats] = useState([]);
  const [loading, setLoading] = useState(true);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [timeFilterOpen, setTimeFilterOpen] = useState(false);
  const [selectedTimeRange, setSelectedTimeRange] = useState('24h');
  const dropdownRef = useRef(null);
  const timeFilterRef = useRef(null);

  // Define time range options
  const timeRanges = [
    { label: 'Last 15 minutes', value: '15m' },
    { label: 'Last 30 minutes', value: '30m' },
    { label: 'Last 1 hour', value: '1h' },
    { label: 'Last 6 hours', value: '6h' },
    { label: 'Last 12 hours', value: '12h' },
    { label: 'Last 24 hours', value: '24h' },
    { label: 'Last 7 days', value: '7d' },
    { label: 'Last 30 days', value: '30d' },
    { label: 'Last 90 days', value: '90d' },
    { label: 'All time', value: 'all' }
  ];

  // Get label for selected time range
  const getTimeRangeLabel = (value) => {
    const range = timeRanges.find(r => r.value === value);
    return range ? range.label : 'Last 24 hours';
  };

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
      if (timeFilterRef.current && !timeFilterRef.current.contains(event.target)) {
        setTimeFilterOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);
  
  // Fetch all data when time range changes
  useEffect(() => {
    if (!mockMode) {
      fetchAllData();
      const interval = setInterval(fetchAllData, 30000); // Refresh every 30 seconds
      return () => clearInterval(interval);
    } else {
      // In mock mode, use the mock data from context
      const { latestDownloads, clientStats, serviceStats } = useData();
      setFilteredLatestDownloads(latestDownloads || []);
      setFilteredClientStats(clientStats || []);
      setFilteredServiceStats(serviceStats || []);
      setLoading(false);
    }
  }, [mockMode, selectedTimeRange]);

  const fetchAllData = async () => {
    try {
      setLoading(true);
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 10000);

      // Fetch all data in parallel with time filter
      const promises = [
        // Dashboard stats
        ApiService.getDashboardStats(selectedTimeRange, controller.signal)
          .catch(err => {
            console.error('Dashboard stats error:', err);
            return null;
          }),
        
        // Latest downloads - fetch more for all time ranges
        fetchFilteredDownloads(selectedTimeRange, controller.signal)
          .catch(err => {
            console.error('Downloads error:', err);
            return [];
          }),
        
        // Client stats with time filter
        fetchFilteredClients(selectedTimeRange, controller.signal)
          .catch(err => {
            console.error('Client stats error:', err);
            return [];
          }),
        
        // Service stats with time filter - pass the period correctly
        ApiService.getServiceStats(controller.signal, selectedTimeRange)
          .catch(err => {
            console.error('Service stats error:', err);
            return [];
          })
      ];

      const [dashboardData, downloadsData, clientsData, servicesData] = await Promise.all(promises);

      clearTimeout(timeout);

      setDashboardStats(dashboardData);
      setFilteredLatestDownloads(downloadsData || []);
      setFilteredClientStats(clientsData || []);
      setFilteredServiceStats(servicesData || []);
    } catch (error) {
      if (error.name !== 'AbortError') {
        console.error('Failed to fetch dashboard data:', error);
      }
    } finally {
      setLoading(false);
    }
  };

  // Fetch downloads filtered by time range
  const fetchFilteredDownloads = async (period, signal) => {
    try {
      // Fetch more downloads for "all" time range
      const count = period === 'all' ? 500 : 100;
      
      const downloads = await ApiService.getLatestDownloads(signal, count);
      
      // Filter by time on client side if needed (except for "all")
      if (period !== 'all') {
        const cutoffTime = getCutoffTime(period);
        return downloads.filter(d => new Date(d.startTime) >= cutoffTime);
      }
      
      return downloads;
    } catch (error) {
      console.error('Failed to fetch downloads:', error);
      return [];
    }
  };

  // Fetch clients filtered by time range
  const fetchFilteredClients = async (period, signal) => {
    try {
      const clients = await ApiService.getClientStats(signal);
      
      // Filter by last seen time if needed
      if (period !== 'all') {
        const cutoffTime = getCutoffTime(period);
        return clients.filter(c => new Date(c.lastSeen) >= cutoffTime);
      }
      
      return clients;
    } catch (error) {
      console.error('Failed to fetch clients:', error);
      return [];
    }
  };

  // Helper to get cutoff time for filtering
  const getCutoffTime = (period) => {
    const now = new Date();
    const timeRangeMs = {
      '15m': 15 * 60 * 1000,
      '30m': 30 * 60 * 1000,
      '1h': 60 * 60 * 1000,
      '6h': 6 * 60 * 60 * 1000,
      '12h': 12 * 60 * 60 * 1000,
      '24h': 24 * 60 * 60 * 1000,
      '7d': 7 * 24 * 60 * 60 * 1000,
      '30d': 30 * 24 * 60 * 60 * 1000,
      '90d': 90 * 24 * 60 * 60 * 1000
    };
    
    return new Date(now - (timeRangeMs[period] || 24 * 60 * 60 * 1000));
  };
  
  const toggleCardVisibility = (cardKey) => {
    setCardVisibility(prev => ({
      ...prev,
      [cardKey]: !prev[cardKey]
    }));
  };

  const activeClients = [...new Set(activeDownloads.map(d => d.clientIp))].length;
  const totalActiveDownloads = activeDownloads.length;
  
  // Calculate total downloads from filtered service stats
  const totalDownloads = filteredServiceStats.reduce((sum, service) => sum + (service.totalDownloads || 0), 0);
  
  // Use dashboard stats if available, otherwise calculate from filtered data
  const bandwidthSaved = dashboardStats?.period?.bandwidthSaved || 
    dashboardStats?.totalBandwidthSaved ||
    filteredServiceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
  const addedToCache = dashboardStats?.period?.addedToCache || 
    dashboardStats?.totalAddedToCache ||
    filteredServiceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
  const totalServed = dashboardStats?.period?.totalServed || 
    dashboardStats?.totalServed ||
    (bandwidthSaved + addedToCache);
  const cacheHitRatio = dashboardStats?.period?.hitRatio || 
    dashboardStats?.cacheHitRatio ||
    (totalServed > 0 ? bandwidthSaved / totalServed : 0);

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
      subtitle: selectedTimeRange === 'all' ? 'All-time saved' : `${getTimeRangeLabel(selectedTimeRange).toLowerCase()}`,
      icon: TrendingUp,
      color: 'emerald',
      visible: cardVisibility.bandwidthSaved
    },
    {
      key: 'addedToCache',
      title: 'Added to Cache',
      value: formatBytes(addedToCache),
      subtitle: selectedTimeRange === 'all' ? 'All-time cached' : `${getTimeRangeLabel(selectedTimeRange).toLowerCase()}`,
      icon: Zap,
      color: 'purple',
      visible: cardVisibility.addedToCache
    },
    {
      key: 'totalServed',
      title: 'Total Served',
      value: formatBytes(totalServed),
      subtitle: selectedTimeRange === 'all' ? 'All-time served' : `${getTimeRangeLabel(selectedTimeRange).toLowerCase()}`,
      icon: Server,
      color: 'indigo',
      visible: cardVisibility.totalServed
    },
    {
      key: 'activeDownloads',
      title: 'Active Downloads',
      value: totalActiveDownloads,
      subtitle: `${filteredLatestDownloads.length} in period`,
      icon: Download,
      color: 'orange',
      visible: cardVisibility.activeDownloads
    },
    {
      key: 'activeClients',
      title: 'Active Clients',
      value: dashboardStats?.uniqueClients || filteredClientStats.length || activeClients,
      subtitle: `${totalDownloads} downloads`,
      icon: Users,
      color: 'yellow',
      visible: cardVisibility.activeClients
    },
    {
      key: 'cacheHitRatio',
      title: 'Cache Hit Ratio',
      value: formatPercent(cacheHitRatio * 100),
      subtitle: selectedTimeRange === 'all' ? 'Overall' : getTimeRangeLabel(selectedTimeRange).toLowerCase(),
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
      {/* Time Range Filter */}
      <div className="flex justify-between items-center">
        <h2 className="text-xl font-semibold text-white">Dashboard</h2>
        <div className="relative" ref={timeFilterRef}>
          <button
            onClick={() => setTimeFilterOpen(!timeFilterOpen)}
            className="flex items-center gap-2 px-4 py-2 bg-gray-800 rounded-lg border border-gray-700 hover:bg-gray-700 transition-colors"
          >
            <Clock className="w-4 h-4 text-gray-400" />
            <span className="text-sm text-gray-200">{getTimeRangeLabel(selectedTimeRange)}</span>
            <ChevronDown className={`w-4 h-4 text-gray-400 transition-transform ${timeFilterOpen ? 'rotate-180' : ''}`} />
          </button>

          {/* Time Range Dropdown */}
          {timeFilterOpen && (
            <div className="absolute right-0 mt-2 w-56 bg-gray-800 rounded-lg border border-gray-700 shadow-xl z-50">
              <div className="p-2">
                <div className="text-xs text-gray-500 font-semibold px-2 py-1.5">Time Range</div>
                {timeRanges.map(range => (
                  <button
                    key={range.value}
                    onClick={() => {
                      setSelectedTimeRange(range.value);
                      setTimeFilterOpen(false);
                    }}
                    className={`w-full text-left px-3 py-2 rounded text-sm hover:bg-gray-700 transition-colors ${
                      selectedTimeRange === range.value 
                        ? 'bg-gray-700 text-blue-400' 
                        : 'text-gray-300'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span>{range.label}</span>
                      {selectedTimeRange === range.value && (
                        <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
                          <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                        </svg>
                      )}
                    </div>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

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

      {/* Loading overlay */}
      {loading && (
        <div className="text-center py-4">
          <div className="inline-flex items-center gap-2 text-gray-400">
            <svg className="animate-spin h-5 w-5" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
            </svg>
            <span>Loading {getTimeRangeLabel(selectedTimeRange).toLowerCase()} data...</span>
          </div>
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
        <EnhancedServiceChart 
          serviceStats={filteredServiceStats}
          timeRange={selectedTimeRange} 
        />
        <RecentDownloadsPanel 
          downloads={filteredLatestDownloads}
          timeRange={selectedTimeRange} 
        />
      </div>

      {/* Top Clients */}
      <TopClientsTable 
        clientStats={filteredClientStats}
        downloads={filteredLatestDownloads}
        timeRange={selectedTimeRange} 
      />
    </div>
  );
};

export default Dashboard;