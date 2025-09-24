import React, { useState, Suspense, lazy } from 'react';
import { DataProvider } from '@contexts/DataContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import Header from '@components/layout/Header';
import Navigation from '@components/layout/Navigation';
import ErrorBoundary from '@components/common/ErrorBoundary';
import LoadingSpinner from '@components/common/LoadingSpinner';
import PicsProgressBar from '@components/common/PicsProgressBar';

// Lazy load heavy components
const Dashboard = lazy(() => import('@components/dashboard/Dashboard'));
const DownloadsTab = lazy(() => import('@components/downloads/DownloadsTab'));
const ClientsTab = lazy(() => import('@components/clients/ClientsTab'));
const ServicesTab = lazy(() => import('@components/services/ServicesTab'));
const ManagementTab = lazy(() => import('@components/management/ManagementTab'));

const App: React.FC = () => {
  const [activeTab, setActiveTab] = useState('dashboard');

  const renderContent = () => {
    const TabComponent = (() => {
      switch (activeTab) {
        case 'dashboard':
          return Dashboard;
        case 'downloads':
          return DownloadsTab;
        case 'clients':
          return ClientsTab;
        case 'services':
          return ServicesTab;
        case 'management':
          return ManagementTab;
        default:
          return Dashboard;
      }
    })();

    return (
      <Suspense fallback={<LoadingSpinner fullScreen={false} message="Loading..." />}>
        <TabComponent />
      </Suspense>
    );
  };

  return (
    <ErrorBoundary>
      <TimeFilterProvider>
        <DataProvider>
          <div
            className="min-h-screen"
            style={{
              backgroundColor: 'var(--theme-bg-primary)',
              color: 'var(--theme-text-primary)'
            }}
          >
            <Header />
            <Navigation activeTab={activeTab} setActiveTab={setActiveTab} />
            <PicsProgressBar />
            <main className="container mx-auto px-4 py-6">{renderContent()}</main>
          </div>
        </DataProvider>
      </TimeFilterProvider>
    </ErrorBoundary>
  );
};

export default App;
