import React, { useState, useEffect } from 'react';
import { Activity, CheckCircle, AlertCircle, Clock, Loader } from 'lucide-react';
import axios from 'axios';

function StatusIndicator() {
  const [status, setStatus] = useState({
    isProcessing: false,
    message: '',
    progress: 0
  });

  useEffect(() => {
    checkStatus();
    const interval = setInterval(checkStatus, 5000);
    return () => clearInterval(interval);
  }, []);

  const checkStatus = async () => {
    try {
      const response = await axios.get('/api/stats/processing-status');
      setStatus(response.data);
    } catch (error) {
      // API endpoint doesn't exist yet, that's ok
    }
  };

  if (!status.isProcessing && !status.message) return null;

  return (
    <div className="fixed bottom-4 right-4 bg-blue-500 text-white px-4 py-3 rounded-lg shadow-lg flex items-center gap-3 max-w-md z-50">
      <Loader className="w-5 h-5 animate-spin" />
      <div>
        <div className="font-semibold">Processing Log File</div>
        <div className="text-sm opacity-90">{status.message || 'Processing historical data...'}</div>
        {status.progress > 0 && (
          <div className="mt-2 w-full bg-blue-600 rounded-full h-2">
            <div 
              className="bg-white rounded-full h-2 transition-all duration-500"
              style={{ width: `${status.progress}%` }}
            />
          </div>
        )}
      </div>
    </div>
  );
}

export default StatusIndicator;