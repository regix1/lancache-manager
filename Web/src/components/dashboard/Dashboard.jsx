import React from 'react';
import { HardDrive, Download, Users, Database } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import StatCard from '../common/StatCard';
import ServiceDistributionChart from './ServiceDistributionChart';
import RecentDownloadsPanel from './RecentDownloadsPanel';
import TopClientsTable from './TopClientsTable';

const Dashboard = () => {
  const { cacheInfo, activeDownloads, latestDownloads, clientStats } = useData();
  
  const activeClients = [...new Set(activeDownloads.map(d => d.clientIp))].length;
  const totalActiveDownloads = activeDownloads.length;

  return (
    <div className="space-y-6">
      {/* Stats Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        <StatCard
          title="Total Cache"
          value={cacheInfo ? formatBytes(cacheInfo.totalCacheSize) : '0 B'}
          subtitle={`${cacheInfo?.totalFiles || 0} files`}
          icon={Database}
          color="blue"
        />
        <StatCard
          title="Used Space"
          value={cacheInfo ? formatBytes(cacheInfo.usedCacheSize) : '0 B'}
          subtitle={`${cacheInfo ? formatPercent(cacheInfo.usagePercent) : '0%'} utilized`}
          icon={HardDrive}
          color="green"
        />
        <StatCard
          title="Active Downloads"
          value={totalActiveDownloads}
          subtitle={`${latestDownloads.length} total`}
          icon={Download}
          color="purple"
        />
        <StatCard
          title="Active Clients"
          value={activeClients}
          subtitle={`${clientStats.length} services`}
          icon={Users}
          color="yellow"
        />
      </div>

      {/* Charts Row */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <ServiceDistributionChart />
        <RecentDownloadsPanel />
      </div>

      {/* Top Clients */}
      <TopClientsTable />
    </div>
  );
};

export default Dashboard;