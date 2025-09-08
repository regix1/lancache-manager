import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { CacheInfoTooltip } from '../common/Tooltip';

const ClientsTab: React.FC = () => {
  const { clientStats } = useData();

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-bold text-themed-primary tracking-tight">Clients</h2>
      
      <Card>
        <h3 className="text-lg font-semibold mb-4 flex items-center gap-2 text-themed-primary">
          Client Statistics
          <CacheInfoTooltip />
        </h3>
        
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className="text-left text-xs text-themed-muted uppercase tracking-wider">
                <th className="pb-3">Client IP</th>
                <th className="pb-3">Total Downloads</th>
                <th className="pb-3">Total Data</th>
                <th className="pb-3">Cache Hits</th>
                <th className="pb-3">Cache Misses</th>
                <th className="pb-3">Hit Rate</th>
                <th className="pb-3">Last Activity</th>
              </tr>
            </thead>
            <tbody className="text-sm">
              {clientStats.length > 0 ? (
                clientStats.map((client, idx) => (
                  <tr key={idx} className="hover:bg-themed-hover transition-colors">
                    <td className="py-3 text-themed-primary font-medium">{client.clientIp}</td>
                    <td className="py-3 text-themed-secondary">{client.totalDownloads}</td>
                    <td className="py-3 text-themed-secondary">{formatBytes(client.totalBytes)}</td>
                    <td className="py-3 cache-hit">{formatBytes(client.totalCacheHitBytes)}</td>
                    <td className="py-3 cache-miss">{formatBytes(client.totalCacheMissBytes)}</td>
                    <td className="py-3">
                      <div className="flex items-center space-x-2">
                        <div className="w-24 progress-track rounded-full h-2">
                          <div 
                            className="progress-bar-high h-2 rounded-full"
                            style={{ width: `${client.cacheHitPercent}%` }}
                          />
                        </div>
                        <span className="text-xs text-themed-secondary">{formatPercent(client.cacheHitPercent)}</span>
                      </div>
                    </td>
                    <td className="py-3 text-themed-muted">
                      {formatDateTime(client.lastSeen)}
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td colSpan={7} className="py-8 text-center text-themed-muted">
                    No client data available
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
};

export default ClientsTab;