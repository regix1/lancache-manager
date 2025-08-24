import React, { createContext, useContext, useState, useEffect, useRef } from 'react';
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
  
  // Use ref to track if this is the initial load
  const isInitialLoad = useRef(true);
  const hasData = useRef(false);

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
      // Only show loading on initial load
      if (isInitialLoad.current) {
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
        hasData.current = true;
      } else if (isConnected) {
        try {
          // Use longer timeout during processing
          const timeout = isProcessingLogs ? 30000 : 10000;
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
          if (cache.status === 'fulfilled' && cache.value !== undefined) {
            setCacheInfo(cache.value);
          }
          
          if (active.status === 'fulfilled' && active.value !== undefined) {
            setActiveDownloads(active.value);
          }
          
          if (latest.status === 'fulfilled' && latest.value !== undefined) {
            setLatestDownloads(latest.value);
            hasData.current = true;
            
            // If we're processing and getting data, update the status
            if (isProcessingLogs && latest.value.length > 0) {
              setProcessingStatus(prev => ({
                ...prev,
                message: `Processing logs... Found ${latest.value.length} downloads`,
                downloadCount: latest.value.length
              }));
            }
          }
          
          if (clients.status === 'fulfilled' && clients.value !== undefined) {
            setClientStats(clients.value);
          }
          
          if (services.status === 'fulfilled' && services.value !== undefined) {
            setServiceStats(services.value);
          }

          // Clear error if we got any successful response
          const anySuccess = [cache, active, latest, clients, services].some(
            result => result.status === 'fulfilled'
          );
          
          if (anySuccess) {
            setError(null);
          } else if (!hasData.current) {
            // Only show error if we have no data at all
            setError('Unable to fetch data from API');
          }
        } catch (err) {
          // Only show error if we have no data
          if (!hasData.current) {
            if (err.name === 'AbortError') {
              setError('Request timeout - the server may be busy');
            } else {
              setError('Failed to fetch data from API');
            }
          }
        }
      } else {
        // Only show connection error if we have no data
        if (!hasData.current) {
          setError('Cannot connect to API server');
        }
      }
    } catch (err) {
      console.error('Error in fetchData:', err);
      if (!hasData.current) {
        setError('An unexpected error occurred');
      }
    } finally {
      if (isInitialLoad.current) {
        setLoading(false);
        isInitialLoad.current = false;
      }
    }
  };

  // Initial load and setup interval
  useEffect(() => {
    // Initial fetch
    fetchData();
    
    // Setup interval based on processing state
    const interval = setInterval(
      fetchData, 
      isProcessingLogs ? 15000 : REFRESH_INTERVAL
    );
    
    return () => clearInterval(interval);
  }, [isProcessingLogs]); // Only recreate interval when processing state changes

  // Handle mock mode changes separately
  useEffect(() => {
    if (mockMode) {
      // Clear data when entering mock mode
      setCacheInfo(null);
      setActiveDownloads([]);
      setLatestDownloads([]);
      setClientStats([]);
      setServiceStats([]);
      hasData.current = false;
      
      // Fetch mock data
      fetchData();
    } else {
      // When leaving mock mode, just fetch real data
      fetchData();
    }
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
    clearAllData: () => {
      setCacheInfo(null);
      setActiveDownloads([]);
      setLatestDownloads([]);
      setClientStats([]);
      setServiceStats([]);
      hasData.current = false;
    },
    isProcessingLogs,
    setIsProcessingLogs,
    processingStatus,
    setProcessingStatus,
    connectionStatus
  };

  return <DataContext.Provider value={value}>{children}</DataContext.Provider>;
};