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
  
  // New states for processing status
  const [isProcessingLogs, setIsProcessingLogs] = useState(false);
  const [processingStatus, setProcessingStatus] = useState(null);
  const [connectionStatus, setConnectionStatus] = useState('checking'); // 'checking', 'connected', 'disconnected', 'error'

  const clearAllData = () => {
    setCacheInfo(null);
    setActiveDownloads([]);
    setLatestDownloads([]);
    setClientStats([]);
    setServiceStats([]);
  };

  const checkConnectionStatus = async () => {
    try {
      const response = await fetch(`${import.meta.env.VITE_API_URL || `http://${window.location.hostname}:8080`}/health`, {
        signal: AbortSignal.timeout(5000) // 5 second timeout
      });
      if (response.ok) {
        setConnectionStatus('connected');
        return true;
      }
      setConnectionStatus('error');
      return false;
    } catch (err) {
      setConnectionStatus('disconnected');
      return false;
    }
  };

  const fetchData = async () => {
    try {
      setLoading(true);
      
      // Check connection first
      const isConnected = await checkConnectionStatus();
      
      if (mockMode) {
        // Use mock data
        const mockData = MockDataService.generateMockData();
        setCacheInfo(mockData.cacheInfo);
        setActiveDownloads(mockData.activeDownloads);
        setLatestDownloads(mockData.latestDownloads);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setError(null);
      } else if (isConnected) {
        // Clear existing data first when switching to real mode
        clearAllData();
        
        // Fetch real data with timeout
        try {
          const controller = new AbortController();
          const timeoutId = setTimeout(() => controller.abort(), 10000); // 10 second timeout
          
          const [cache, active, latest, clients, services] = await Promise.allSettled([
            ApiService.getCacheInfo(controller.signal),
            ApiService.getActiveDownloads(controller.signal),
            ApiService.getLatestDownloads(controller.signal),
            ApiService.getClientStats(controller.signal),
            ApiService.getServiceStats(controller.signal)
          ]);
          
          clearTimeout(timeoutId);

          // Only update if we got successful responses
          if (cache.status === 'fulfilled') setCacheInfo(cache.value);
          if (active.status === 'fulfilled') setActiveDownloads(active.value || []);
          if (latest.status === 'fulfilled') setLatestDownloads(latest.value || []);
          if (clients.status === 'fulfilled') setClientStats(clients.value || []);
          if (services.status === 'fulfilled') setServiceStats(services.value || []);

          // Check if all requests failed
          const allFailed = [cache, active, latest, clients, services].every(
            result => result.status === 'rejected'
          );

          if (allFailed) {
            setError('Unable to fetch data from API. The server may be processing logs.');
          } else {
            setError(null);
          }
        } catch (err) {
          if (err.name === 'AbortError') {
            setError('Request timeout - the server may be busy processing logs');
          } else {
            setError('Failed to fetch data from API');
          }
          clearAllData();
        }
      } else {
        setError('Cannot connect to API server');
        clearAllData();
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
    clearAllData();
    setError(null);
    fetchData();
    
    const interval = setInterval(fetchData, REFRESH_INTERVAL);
    return () => clearInterval(interval);
  }, [mockMode]);

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
    clearAllData,
    isProcessingLogs,
    setIsProcessingLogs,
    processingStatus,
    setProcessingStatus,
    connectionStatus
  };

  return <DataContext.Provider value={value}>{children}</DataContext.Provider>;
};