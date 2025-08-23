// Create a new file: src/components/SettingsModal.jsx
import React, { useState, useEffect } from 'react';
import { X, Database, Wifi, WifiOff } from 'lucide-react';

function SettingsModal({ isOpen, onClose }) {
  const [useMockData, setUseMockData] = useState(true);

  useEffect(() => {
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, [isOpen]);

  const handleMockDataToggle = () => {
    const newValue = !useMockData;
    setUseMockData(newValue);
    localStorage.setItem('useMockData', newValue ? 'true' : 'false');
    
    // Reload the page to apply changes
    setTimeout(() => {
      window.location.reload();
    }, 300);
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="card max-w-md w-full mx-4">
        <div className="flex justify-between items-center mb-4">
          <h3 className="text-lg font-semibold">Application Settings</h3>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-gray-100 dark:hover:bg-dark-border"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        
        <div className="divider mb-4"></div>
        
        <div className="space-y-4">
          {/* Mock Data Toggle */}
          <div className="flex items-center justify-between p-3 rounded-lg bg-gray-50 dark:bg-dark-border">
            <div className="flex items-center gap-3">
              <Database className="h-5 w-5 text-primary" />
              <div>
                <p className="font-medium">Data Source</p>
                <p className="text-sm text-muted">
                  {useMockData ? 'Using mock data for demo' : 'Connected to live API'}
                </p>
              </div>
            </div>
            <button
              onClick={handleMockDataToggle}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                useMockData ? 'bg-warning' : 'bg-success'
              }`}
            >
              <span
                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                  useMockData ? 'translate-x-1' : 'translate-x-6'
                }`}
              />
            </button>
          </div>
          
          {/* Status Indicator */}
          <div className="p-3 rounded-lg border border-gray-200 dark:border-dark-border">
            <div className="flex items-center gap-2 mb-2">
              {useMockData ? (
                <>
                  <WifiOff className="h-4 w-4 text-warning" />
                  <span className="chip chip-warning">Mock Mode</span>
                </>
              ) : (
                <>
                  <Wifi className="h-4 w-4 text-success" />
                  <span className="chip chip-success">Live Mode</span>
                </>
              )}
            </div>
            <p className="text-sm text-muted">
              {useMockData 
                ? 'Application is using simulated data. Toggle off to connect to real API endpoints.'
                : 'Application is connected to live API endpoints. Toggle on to use mock data for testing.'}
            </p>
          </div>
          
          {/* Info Box */}
          <div className="p-3 bg-blue-50 dark:bg-blue-900/20 rounded-lg border border-blue-200 dark:border-blue-800">
            <p className="text-sm text-blue-800 dark:text-blue-200">
              <strong>Note:</strong> Changing the data source will refresh the page to apply the new settings.
            </p>
          </div>
        </div>
        
        <div className="divider mt-6 mb-4"></div>
        
        <div className="flex justify-end">
          <button
            onClick={onClose}
            className="btn btn-primary"
          >
            Done
          </button>
        </div>
      </div>
    </div>
  );
}

export default SettingsModal;