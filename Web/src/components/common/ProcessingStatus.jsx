import React, { useEffect, useState } from 'react';
import { Activity, AlertCircle, CheckCircle, Loader } from 'lucide-react';
import { useData } from '../../contexts/DataContext';

const ProcessingStatus = () => {
  const { isProcessingLogs, processingStatus, connectionStatus } = useData();
  const [dots, setDots] = useState('');

  useEffect(() => {
    if (isProcessingLogs) {
      const interval = setInterval(() => {
        setDots(prev => prev.length >= 3 ? '' : prev + '.');
      }, 500);
      return () => clearInterval(interval);
    }
  }, [isProcessingLogs]);

  if (!isProcessingLogs && !processingStatus && connectionStatus === 'connected') {
    return null;
  }

  return (
    <div className="fixed bottom-4 right-4 z-50 max-w-md">
      {/* Connection Status */}
      {connectionStatus !== 'connected' && (
        <div className="mb-2 bg-gray-800 rounded-lg p-4 border border-gray-700 shadow-lg">
          <div className="flex items-center space-x-3">
            <AlertCircle className="w-5 h-5 text-yellow-500" />
            <div>
              <p className="text-sm font-medium text-white">Connection Issue</p>
              <p className="text-xs text-gray-400">
                {connectionStatus === 'disconnected' ? 'Cannot reach API server' : 'Checking connection...'}
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Processing Status */}
      {isProcessingLogs && (
        <div className="bg-gray-800 rounded-lg p-4 border border-gray-700 shadow-lg">
          <div className="flex items-center space-x-3">
            <Loader className="w-5 h-5 text-blue-500 animate-spin" />
            <div className="flex-1">
              <p className="text-sm font-medium text-white">Processing Logs{dots}</p>
              {processingStatus && (
                <div className="mt-2">
                  <p className="text-xs text-gray-400">{processingStatus.message}</p>
                  {processingStatus.progress !== undefined && processingStatus.progress > 0 && (
                    <div className="mt-2">
                      <div className="w-full bg-gray-700 rounded-full h-2">
                        <div 
                          className="bg-blue-500 h-2 rounded-full transition-all duration-300"
                          style={{ width: `${Math.min(processingStatus.progress || 0, 100)}%` }}
                        />
                      </div>
                      <p className="text-xs text-gray-500 mt-1">
                        {(processingStatus.progress || 0).toFixed(1)}% complete
                      </p>
                    </div>
                  )}
                  {processingStatus.estimatedTime && (
                    <p className="text-xs text-gray-500 mt-1">
                      Estimated time: {processingStatus.estimatedTime}
                    </p>
                  )}
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Success Status */}
      {!isProcessingLogs && processingStatus?.type === 'success' && (
        <div className="bg-gray-800 rounded-lg p-4 border border-green-700 shadow-lg">
          <div className="flex items-center space-x-3">
            <CheckCircle className="w-5 h-5 text-green-500" />
            <div>
              <p className="text-sm font-medium text-white">Processing Complete</p>
              <p className="text-xs text-gray-400">{processingStatus.message}</p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default ProcessingStatus;