import React, { createContext, useContext, useState, useEffect } from 'react';
import ApiService from '../services/api.service';
import MockDataService from '../services/mockData.service';
import { REFRESH_INTERVAL, MOCK_UPDATE_INTERVAL } from '../utils/constants';

const DataContext = createContext();

export const useData = () => {
  const context = useContext(DataContext);
  if (!context) throw new Error('useData must be used within DataProvider');
  return context;
};

export const DataProvider = ({ children }) => {
  const [mockMode, setMockMode] = useState(false);
  const [cacheInfo, setCacheInfo] = useState(null);
  const [activeDownloads, setActiveDownloads] = useState([]);
  const [latestDownloads, setLatestDownloads] = useState([]);
  const [clientStats, setClientStats] = useState([]);
  const [serviceStats, setServiceStats] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const clearAllData = () => {
    setCacheInfo(null);
    setActiveDownloads([]);
    setLatestDownloads([]);
    setClientStats([]);
    setServiceStats([]);
  };

  const fetchData = async () => {
    try {
      setLoading(true);
      
      if (mockMode) {
        // Use mock data
        const mockData = MockDataService.generateMockData();
        setCacheInfo(mockData.cacheInfo);
        setActiveDownloads(mockData.activeDownloads);
        setLatestDownloads(mockData.latestDownloads);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setError(null);
      } else {
        // Clear existing data first when switching to real mode
        clearAllData();
        
        // Fetch real data
        try {
          const [cache, active, latest, clients, services] = await Promise.allSettled([
            ApiService.getCacheInfo(),
            ApiService.getActiveDownloads(),
            ApiService.getLatestDownloads(),
            ApiService.getClientStats(),
            ApiService.getServiceStats()
          ]);

          // Only update if we got successful responses
          if (cache.status === 'fulfilled') {
            setCacheInfo(cache.value);
          } else {
            setCacheInfo(null);
          }

          if (active.status === 'fulfilled') {
            setActiveDownloads(active.value || []);
          } else {
            setActiveDownloads([]);
          }

          if (latest.status === 'fulfilled') {
            setLatestDownloads(latest.value || []);
          } else {
            setLatestDownloads([]);
          }

          if (clients.status === 'fulfilled') {
            setClientStats(clients.value || []);
          } else {
            setClientStats([]);
          }

          if (services.status === 'fulfilled') {
            setServiceStats(services.value || []);
          } else {
            setServiceStats([]);
          }

          // Check if all requests failed
          const allFailed = [cache, active, latest, clients, services].every(
            result => result.status === 'rejected'
          );

          if (allFailed) {
            setError('Unable to connect to API. Please ensure the backend is running on port 5000.');
          } else {
            setError(null);
          }
        } catch (err) {
          console.error('Error fetching real data:', err);
          setError('Failed to fetch data from API');
          clearAllData();
        }
      }
    } catch (err) {
      console.error('Error in fetchData:', err);
      setError('An unexpected error occurred');
    } finally {
      setLoading(false);
    }
  };

  // Handle mock mode changes
  useEffect(() => {
    // Clear all data when switching modes
    clearAllData();
    setError(null);
    
    // Fetch new data
    fetchData();
    
    // Set up refresh interval
    const interval = setInterval(fetchData, REFRESH_INTERVAL);
    
    return () => clearInterval(interval);
  }, [mockMode]); // Re-run when mockMode changes

  // Simulate real-time updates in mock mode
  useEffect(() => {
    if (mockMode) {
      const interval = setInterval(() => {
        const newDownload = MockDataService.generateRealtimeUpdate();
        setLatestDownloads(prev => [newDownload, ...prev.slice(0, 49)]);
        setActiveDownloads(prev => [newDownload, ...prev.slice(0, 4)]);
      }, MOCK_UPDATE_INTERVAL);
      return () => clearInterval(interval);
    }
  }, [mockMode]);

  const value = {
    mockMode,
    setMockMode,
    cacheInfo,
    activeDownloads,
    latestDownloads,
    clientStats,
    serviceStats,
    loading,
    error,
    fetchData,
    clearAllData
  };

  return <DataContext.Provider value={value}>{children}</DataContext.Provider>;
};