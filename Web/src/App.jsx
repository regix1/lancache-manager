import React, { useState, useEffect } from 'react';
import { DataProvider, useData } from './contexts/DataContext';
import Header from './components/layout/Header';
import Navigation from './components/layout/Navigation';
import Dashboard from './components/dashboard/Dashboard';
import DownloadsTab from './components/downloads/DownloadsTab';
import ClientsTab from './components/clients/ClientsTab';
import ServicesTab from './components/services/ServicesTab';
import ManagementTab from './components/management/ManagementTab';
import ErrorBoundary from './components/common/ErrorBoundary';
import ProcessingStatus from './components/common/ProcessingStatus';
import themeService from './services/theme.service';

const AppContent = () => {
  const [activeTab, setActiveTab] = useState('dashboard');
  const { loading, error } = useData();

  // Load saved theme on app start
  useEffect(() => {
    themeService.loadSavedTheme();
  }, []);

  return (
    <div className="min-h-screen bg-gray-900 text-white">
      <Header />
      <Navigation activeTab={activeTab} setActiveTab={setActiveTab} />
      
      <main className="container mx-auto px-4 py-8">
        <ErrorBoundary>
          {/* Only show loading spinner on initial load */}
          {loading ? (
            <div className="flex items-center justify-center h-64">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-500"></div>
            </div>
          ) : error ? (
            <div className="bg-red-900 bg-opacity-30 rounded-lg p-6 border border-red-700">
              <h2 className="text-xl font-semibold text-red-400 mb-2">Connection Error</h2>
              <p className="text-gray-300">{error}</p>
            </div>
          ) : (
            <>
              {activeTab === 'dashboard' && <Dashboard />}
              {activeTab === 'downloads' && <DownloadsTab />}
              {activeTab === 'clients' && <ClientsTab />}
              {activeTab === 'services' && <ServicesTab />}
              {activeTab === 'management' && <ManagementTab />}
            </>
          )}
        </ErrorBoundary>
      </main>
      
      {/* Global Processing Status */}
      <ProcessingStatus />
    </div>
  );
};

const App = () => {
  return (
    <DataProvider>
      <AppContent />
    </DataProvider>
  );
};

export default App;