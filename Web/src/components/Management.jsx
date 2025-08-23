import React, { useState, useEffect } from 'react';
import { Server, Database, Trash2, RefreshCw, AlertTriangle, TestTube, Check, X } from 'lucide-react';
import axios from 'axios';

function Management() {
  const [selectedService, setSelectedService] = useState('all');
  const [showConfirmDialog, setShowConfirmDialog] = useState(false);
  const [confirmAction, setConfirmAction] = useState(null);
  const [useMockData, setUseMockData] = useState(true);
  const [showToast, setShowToast] = useState(false);
  const [toastMessage, setToastMessage] = useState('');

  const services = ['all', 'steam', 'epic', 'origin', 'blizzard', 'riot', 'wsus'];

  useEffect(() => {
    // Load mock data setting from localStorage
    const mockDataEnabled = localStorage.getItem('useMockData') !== 'false';
    setUseMockData(mockDataEnabled);
  }, []);

  const handleToggleMockData = () => {
    const newValue = !useMockData;
    setUseMockData(newValue);
    localStorage.setItem('useMockData', newValue.toString());
    
    // Show toast notification
    setToastMessage(newValue ? 'Mock data enabled' : 'Mock data disabled - Using real API');
    setShowToast(true);
    
    // Hide toast after 3 seconds
    setTimeout(() => setShowToast(false), 3000);
    
    // Reload the page to apply changes to all components
    setTimeout(() => window.location.reload(), 500);
  };

  const handleClearCache = () => {
    setConfirmAction('clearCache');
    setShowConfirmDialog(true);
  };

  const handleResetDatabase = () => {
    setConfirmAction('resetDatabase');
    setShowConfirmDialog(true);
  };

  const confirmActionHandler = async () => {
    if (confirmAction === 'clearCache') {
      if (useMockData) {
        setToastMessage(`Mock: Cache cleared for ${selectedService === 'all' ? 'all services' : selectedService.toUpperCase()}`);
        setShowToast(true);
        setTimeout(() => setShowToast(false), 3000);
      } else {
        try {
          await axios.post('/api/management/clear-cache', { service: selectedService });
          setToastMessage(`Cache cleared for ${selectedService === 'all' ? 'all services' : selectedService.toUpperCase()}`);
          setShowToast(true);
          setTimeout(() => setShowToast(false), 3000);
        } catch (error) {
          console.error('Error clearing cache:', error);
        }
      }
    } else if (confirmAction === 'resetDatabase') {
      if (useMockData) {
        setToastMessage('Mock: Database reset successfully');
        setShowToast(true);
        setTimeout(() => setShowToast(false), 3000);
      } else {
        try {
          await axios.post('/api/management/reset-database');
          setToastMessage('Database reset successfully');
          setShowToast(true);
          setTimeout(() => setShowToast(false), 3000);
        } catch (error) {
          console.error('Error resetting database:', error);
        }
      }
    }
    setShowConfirmDialog(false);
    setConfirmAction(null);
  };

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-semibold">System Management</h2>

      {/* Development Settings */}
      <div className="card">
        <div className="flex items-center gap-2 mb-4">
          <TestTube className="h-5 w-5 text-warning" />
          <h3 className="text-lg font-semibold">Development Settings</h3>
        </div>
        
        <div className="space-y-4">
          <div className="flex items-center justify-between p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
            <div>
              <p className="font-medium">Mock Data Mode</p>
              <p className="text-sm text-muted">
                Use simulated data instead of connecting to the real backend API
              </p>
            </div>
            <button
              onClick={handleToggleMockData}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                useMockData ? 'bg-primary' : 'bg-gray-300 dark:bg-gray-600'
              }`}
            >
              <span
                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                  useMockData ? 'translate-x-6' : 'translate-x-1'
                }`}
              />
            </button>
          </div>
          
          {useMockData && (
            <div className="p-3 bg-yellow-50 dark:bg-yellow-900/20 rounded-lg border border-yellow-200 dark:border-yellow-800">
              <div className="flex gap-2">
                <AlertTriangle className="h-5 w-5 text-warning flex-shrink-0 mt-0.5" />
                <div className="text-sm">
                  <p className="font-medium text-yellow-800 dark:text-yellow-200">Mock Data Active</p>
                  <p className="text-yellow-700 dark:text-yellow-300">
                    The application is using simulated data. Toggle off to connect to the real backend API.
                  </p>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>

      {/* Cache Management */}
      <div className="card">
        <div className="flex items-center gap-2 mb-4">
          <Server className="h-5 w-5 text-primary" />
          <h3 className="text-lg font-semibold">Cache Management</h3>
        </div>
        
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium mb-2">Select Service</label>
            <select
              value={selectedService}
              onChange={(e) => setSelectedService(e.target.value)}
              className="select w-full md:w-64"
            >
              {services.map((service) => (
                <option key={service} value={service}>
                  {service === 'all' ? 'All Services' : service.toUpperCase()}
                </option>
              ))}
            </select>
          </div>

          <div className="flex gap-3">
            <button
              onClick={handleClearCache}
              className="btn btn-danger"
            >
              <Trash2 className="h-4 w-4" />
              Clear Cache
            </button>
            
            <button
              className="btn btn-default"
              onClick={() => window.location.reload()}
            >
              <RefreshCw className="h-4 w-4" />
              Refresh Stats
            </button>
          </div>

          <div className="p-3 bg-yellow-50 dark:bg-yellow-900/20 rounded-lg border border-yellow-200 dark:border-yellow-800">
            <div className="flex gap-2">
              <AlertTriangle className="h-5 w-5 text-warning flex-shrink-0 mt-0.5" />
              <div className="text-sm">
                <p className="font-medium text-yellow-800 dark:text-yellow-200">Warning</p>
                <p className="text-yellow-700 dark:text-yellow-300">
                  Clearing cache will remove all cached files for the selected service. This action cannot be undone.
                </p>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Database Management */}
      <div className="card">
        <div className="flex items-center gap-2 mb-4">
          <Database className="h-5 w-5 text-secondary" />
          <h3 className="text-lg font-semibold">Database Management</h3>
        </div>
        
        <div className="space-y-4">
          <p className="text-sm text-muted">
            Reset the database to clear all statistics and download history. This will not affect cached files.
          </p>

          <button
            onClick={handleResetDatabase}
            className="btn btn-danger"
          >
            <Database className="h-4 w-4" />
            Reset Database
          </button>

          <div className="p-3 bg-red-50 dark:bg-red-900/20 rounded-lg border border-red-200 dark:border-red-800">
            <div className="flex gap-2">
              <AlertTriangle className="h-5 w-5 text-danger flex-shrink-0 mt-0.5" />
              <div className="text-sm">
                <p className="font-medium text-red-800 dark:text-red-200">Danger</p>
                <p className="text-red-700 dark:text-red-300">
                  Resetting the database will permanently delete all statistics and download history. This action cannot be undone.
                </p>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* System Info */}
      <div className="card">
        <div className="flex items-center gap-2 mb-4">
          <Server className="h-5 w-5 text-success" />
          <h3 className="text-lg font-semibold">System Information</h3>
        </div>
        
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <p className="text-sm text-muted">Version</p>
            <p className="font-semibold">1.0.0</p>
          </div>
          <div>
            <p className="text-sm text-muted">Uptime</p>
            <p className="font-semibold">{useMockData ? 'Mock Mode' : '14 days, 3 hours'}</p>
          </div>
          <div>
            <p className="text-sm text-muted">Cache Directory</p>
            <p className="font-mono text-sm">{useMockData ? '/mock/lancache' : '/mnt/lancache'}</p>
          </div>
          <div>
            <p className="text-sm text-muted">Database Size</p>
            <p className="font-semibold">{useMockData ? 'Mock Data' : '124 MB'}</p>
          </div>
          <div>
            <p className="text-sm text-muted">API Status</p>
            <p className="font-semibold">
              {useMockData ? (
                <span className="chip chip-warning">Mock Data</span>
              ) : (
                <span className="chip chip-success">Connected</span>
              )}
            </p>
          </div>
          <div>
            <p className="text-sm text-muted">Data Source</p>
            <p className="font-semibold">{useMockData ? 'Local Mock' : 'http://localhost:5000'}</p>
          </div>
        </div>
      </div>

      {/* Confirmation Dialog */}
      {showConfirmDialog && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="card max-w-md w-full mx-4">
            <h3 className="text-lg font-semibold mb-4">Confirm Action</h3>
            <p className="text-sm mb-6">
              {confirmAction === 'clearCache' 
                ? `Are you sure you want to clear the cache for ${selectedService === 'all' ? 'all services' : selectedService.toUpperCase()}?`
                : 'Are you sure you want to reset the database? All statistics will be lost.'}
            </p>
            <div className="flex gap-3 justify-end">
              <button
                onClick={() => setShowConfirmDialog(false)}
                className="btn btn-default"
              >
                Cancel
              </button>
              <button
                onClick={confirmActionHandler}
                className="btn btn-danger"
              >
                Confirm
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Toast Notification */}
      {showToast && (
        <div className="fixed bottom-4 left-4 max-w-sm z-50">
          <div className="bg-gray-800 text-white rounded-lg shadow-lg p-4 flex items-center gap-3">
            <Check className="h-5 w-5 text-success flex-shrink-0" />
            <p className="text-sm">{toastMessage}</p>
          </div>
        </div>
      )}
    </div>
  );
}

export default Management;