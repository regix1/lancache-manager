import React, { useState, useEffect } from 'react';
import { Loader } from 'lucide-react';
import axios from 'axios';

function StatusIndicator() {
  const [status, setStatus] = useState({
    isProcessing: false,
    message: '',
    progress: 0
  });
  const [useMockData, setUseMockData] = useState(true);

  useEffect(() => {
    // Check for mock data setting in localStorage
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, []);

  useEffect(() => {
    checkStatus();
    const interval = setInterval(checkStatus, 5000);
    return () => clearInterval(interval);
  }, [useMockData]);

  const checkStatus = async () => {
    if (useMockData) {
      // For demo purposes, show a processing indicator sometimes in mock mode
      const randomShow = Math.random() > 0.8;
      if (randomShow) {
        setStatus({
          isProcessing: true,
          message: 'Processing historical data...',
          progress: Math.floor(Math.random() * 100)
        });
        setTimeout(() => {
          setStatus({
            isProcessing: false,
            message: '',
            progress: 0
          });
        }, 5000);
      }
    } else {
      try {
        const response = await axios.get('/api/stats/processing-status');
        setStatus(response.data);
      } catch (error) {
        // API endpoint doesn't exist yet, that's ok
      }
    }
  };

  if (!status.isProcessing && !status.message) return null;

  return (
    <div className="fixed bottom-4 right-4 max-w-md z-50">
      <div className="bg-primary text-white rounded-lg shadow-lg p-4">
        <div className="flex items-center gap-3">
          <Loader className="h-5 w-5 animate-spin" />
          <div className="flex-1">
            <div className="font-semibold">Processing Log File</div>
            <div className="text-sm opacity-90">
              {status.message || 'Processing historical data...'}
            </div>
            {status.progress > 0 && (
              <div className="mt-2 bg-white/20 rounded-full overflow-hidden">
                <div 
                  className="h-1 bg-white rounded-full transition-all duration-300"
                  style={{ width: `${status.progress}%` }}
                ></div>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

export default StatusIndicator;