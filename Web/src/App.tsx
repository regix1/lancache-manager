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

const App: React.FC = () => {
  const [activeTab, setActiveTab] = useState('dashboard');

  const renderContent = () => {
    switch (activeTab) {
      case 'dashboard':
        return <Dashboard />;
      case 'downloads':
        return <DownloadsTab />;
      case 'clients':
        return <ClientsTab />;
      case 'services':
        return <ServicesTab />;
      case 'management':
        return <ManagementTab />;
      default:
        return <Dashboard />;
    }
  };

  return (
    <ErrorBoundary>
      <DataProvider>
        <div className="min-h-screen bg-gray-900 text-gray-100">
          <Header />
          <Navigation activeTab={activeTab} setActiveTab={setActiveTab} />
          <main className="container mx-auto px-4 py-6">
            {renderContent()}
          </main>
        </div>
      </DataProvider>
    </ErrorBoundary>
  );
};

export default App;