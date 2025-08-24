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
  const [connectionStatus, setConnectionStatus] = useState('checking');

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
        signal: AbortSignal.timeout(5000)
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
      // Don't show loading spinner during processing or if we already have data
      if (!isProcessingLogs && latestDownloads.length === 0) {
        setLoading(true);
      }
      
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
        try {
          // Use longer timeout during processing
          const timeout = isProcessingLogs ? 30000 : 10000; // 30s during processing, 10s normally
          const controller = new AbortController();
          const timeoutId = setTimeout(() => controller.abort(), timeout);
          
          // Fetch all data in parallel
          const [cache, active, latest, clients, services] = await Promise.allSettled([
            ApiService.getCacheInfo(controller.signal),
            ApiService.getActiveDownloads(controller.signal),
            ApiService.getLatestDownloads(controller.signal),
            ApiService.getClientStats(controller.signal),
            ApiService.getServiceStats(controller.signal)
          ]);
          
          clearTimeout(timeoutId);

          // Update only successful responses - keep existing data for failed ones
          if (cache.status === 'fulfilled' && cache.value) {
            setCacheInfo(cache.value);
          }
          // Don't clear cacheInfo if it fails - keep the old value
          
          if (active.status === 'fulfilled' && active.value) {
            setActiveDownloads(active.value);
          } else if (active.status === 'fulfilled' && active.value === null) {
            // Only clear if we got an explicit null/empty response
            setActiveDownloads([]);
          }
          
          if (latest.status === 'fulfilled' && latest.value) {
            setLatestDownloads(latest.value);
            
            // If we're processing and getting data, update the status
            if (isProcessingLogs && latest.value.length > 0) {
              setProcessingStatus(prev => ({
                ...prev,
                message: `Processing logs... Found ${latest.value.length} downloads`,
                downloadCount: latest.value.length
              }));
            }
          } else if (latest.status === 'fulfilled' && latest.value === null) {
            setLatestDownloads([]);
          }
          
          if (clients.status === 'fulfilled' && clients.value) {
            setClientStats(clients.value);
          } else if (clients.status === 'fulfilled' && clients.value === null) {
            setClientStats([]);
          }
          
          if (services.status === 'fulfilled' && services.value) {
            setServiceStats(services.value);
          } else if (services.status === 'fulfilled' && services.value === null) {
            setServiceStats([]);
          }

          // Only show error if ALL requests failed AND we have no data
          const allFailed = [cache, active, latest, clients, services].every(
            result => result.status === 'rejected'
          );
          
          if (allFailed && latestDownloads.length === 0) {
            setError('Unable to fetch data from API');
          } else {
            setError(null);
          }
        } catch (err) {
          // Only show error and clear data if we have no existing data
          if (latestDownloads.length === 0) {
            if (err.name === 'AbortError') {
              setError('Request timeout - the server may be busy');
            } else {
              setError('Failed to fetch data from API');
            }
            clearAllData();
          }
        }
      } else {
        // Only show connection error if we have no data
        if (latestDownloads.length === 0) {
          setError('Cannot connect to API server');
          clearAllData();
        }
      }
    } catch (err) {
      console.error('Error in fetchData:', err);
      if (latestDownloads.length === 0) {
        setError('An unexpected error occurred');
      }
    } finally {
      setLoading(false);
    }
  };

  // Handle mock mode changes
  useEffect(() => {
    // Only clear data when switching modes
    if (mockMode) {
      clearAllData();
    }
    setError(null);
    fetchData();
    
    // Use longer interval during processing
    const interval = setInterval(
      fetchData, 
      isProcessingLogs ? 15000 : REFRESH_INTERVAL // 15s during processing, 5s normally
    );
    
    return () => clearInterval(interval);
  }, [mockMode, isProcessingLogs]);

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