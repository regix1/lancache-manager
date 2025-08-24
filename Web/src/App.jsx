import React, { useState } from 'react';
import { DataProvider } from './contexts/DataContext';
import Header from './components/layout/Header';
import Navigation from './components/layout/Navigation';
import Dashboard from './components/dashboard/Dashboard';
import DownloadsTab from './components/downloads/DownloadsTab';
import ClientsTab from './components/clients/ClientsTab';
import ServicesTab from './components/services/ServicesTab';
import ManagementTab from './components/management/ManagementTab';
import ErrorBoundary from './components/common/ErrorBoundary';
import ProcessingStatus from './components/common/ProcessingStatus';

const App = () => {
  const [activeTab, setActiveTab] = useState('dashboard');

  return (
    <DataProvider>
      <div className="min-h-screen bg-gray-900 text-white">
        <Header />
        <Navigation activeTab={activeTab} setActiveTab={setActiveTab} />
        
        <main className="container mx-auto px-4 py-8">
          <ErrorBoundary>
            {activeTab === 'dashboard' && <Dashboard />}
            {activeTab === 'downloads' && <DownloadsTab />}
            {activeTab === 'clients' && <ClientsTab />}
            {activeTab === 'services' && <ServicesTab />}
            {activeTab === 'management' && <ManagementTab />}
          </ErrorBoundary>
        </main>
        
        {/* Global Processing Status */}
        <ProcessingStatus />
      </div>
    </DataProvider>
  );
};

export default App;