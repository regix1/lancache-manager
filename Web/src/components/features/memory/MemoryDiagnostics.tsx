import React, { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { formatDateTime } from '@utils/formatters';

interface MemoryStats {
  totalSystemMemoryMB: number;
  totalSystemMemoryGB: number;
  workingSetMB: number;
  workingSetGB: number;
  managedMB: number;
  managedGB: number;
  unmanagedMB: number;
  unmanagedGB: number;
  totalAllocatedMB: number;
  totalAllocatedGB: number;
  heapSizeMB: number;
  heapSizeGB: number;
  fragmentedMB: number;
  fragmentedGB: number;
  threadCount: number;
  handleCount: number;
  gen0Collections: number;
  gen1Collections: number;
  gen2Collections: number;
  timestamp: string;
}

const MemoryDiagnostics: React.FC = () => {
  const { t } = useTranslation();
  const [stats, setStats] = useState<MemoryStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchMemoryStats = async () => {
    try {
      setError(null);
      const response = await fetch('/api/memory', ApiService.getFetchOptions());

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      const data = await response.json();
      setStats(data);
    } catch (err: unknown) {
      console.error('Failed to fetch memory stats:', err);
      setError((err instanceof Error ? err.message : String(err)) || t('memory.failedToLoad'));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchMemoryStats();
  }, []);

  if (loading) {
    return (
      <div className="min-h-screen bg-themed-primary flex items-center justify-center">
        <div className="text-themed-primary">{t('memory.loading')}</div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen bg-themed-primary flex items-center justify-center">
        <div className="text-themed-error">{t('memory.error', { error })}</div>
      </div>
    );
  }

  if (!stats) {
    return null;
  }

  return (
    <div className="min-h-screen p-6 bg-themed-primary animate-fadeIn">
      <h1 className="text-3xl font-bold mb-6 pb-3 border-b-2 text-themed-primary border-themed">
        {t('memory.title')}
      </h1>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
        {/* Total Memory */}
        <div className="rounded-lg p-6 border shadow-lg bg-themed-card border-themed">
          <h2 className="text-xl font-semibold mb-4 pb-2 border-b text-themed-primary border-themed">
            {t('memory.totalMemory')}
            {stats.totalSystemMemoryGB && (
              <div className="text-sm font-normal mt-1 text-themed-muted">
                {t('memory.systemTotal')}{' '}
                <span className="text-themed-accent">
                  {stats.totalSystemMemoryGB.toFixed(2)} GB
                </span>
              </div>
            )}
          </h2>
          <div className="space-y-4">
            <div className="py-2 border-b border-themed">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.workingSet')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">
                    {stats.workingSetMB.toFixed(2)} MB
                  </span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">
                    {stats.workingSetGB.toFixed(2)} GB
                  </span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.workingSetDesc')}
              </div>
            </div>
            <div className="py-2 border-b border-themed">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.managed')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">
                    {stats.managedMB.toFixed(2)} MB
                  </span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">
                    {stats.managedGB.toFixed(2)} GB
                  </span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.managedDesc')}
              </div>
            </div>
            <div className="py-2">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.unmanaged')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">
                    {stats.unmanagedMB.toFixed(2)} MB
                  </span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">
                    {stats.unmanagedGB.toFixed(2)} GB
                  </span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.unmanagedDesc')}
              </div>
            </div>
          </div>
        </div>

        {/* Managed Memory Details */}
        <div className="rounded-lg p-6 border shadow-lg bg-themed-card border-themed">
          <h2 className="text-xl font-semibold mb-4 pb-2 border-b text-themed-primary border-themed">
            {t('memory.managedDetails')}
          </h2>
          <div className="space-y-4">
            <div className="py-2 border-b border-themed">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.totalAllocated')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">
                    {stats.totalAllocatedMB.toFixed(2)} MB
                  </span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">
                    {stats.totalAllocatedGB.toFixed(2)} GB
                  </span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.totalAllocatedDesc')}
              </div>
            </div>
            <div className="py-2 border-b border-themed">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.heapSize')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">
                    {stats.heapSizeMB.toFixed(2)} MB
                  </span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">
                    {stats.heapSizeGB.toFixed(2)} GB
                  </span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.heapSizeDesc')}
              </div>
            </div>
            <div className="py-2">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.fragmented')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">
                    {stats.fragmentedMB.toFixed(2)} MB
                  </span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">
                    {stats.fragmentedGB.toFixed(2)} GB
                  </span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.fragmentedDesc')}
              </div>
            </div>
          </div>
        </div>

        {/* Process Statistics */}
        <div className="rounded-lg p-6 border shadow-lg bg-themed-card border-themed">
          <h2 className="text-xl font-semibold mb-4 pb-2 border-b text-themed-primary border-themed">
            {t('memory.processStats')}
          </h2>
          <div className="space-y-4">
            {/* Resource Usage */}
            <div>
              <div className="text-xs font-semibold mb-2 text-themed-secondary">
                {t('memory.resourceUsage')}
              </div>
              <div className="space-y-3">
                <div className="py-2 border-b border-themed">
                  <div className="flex justify-between items-center">
                    <span className="text-themed-muted">{t('memory.activeThreads')}</span>
                    <span className="font-bold text-themed-primary">
                      {stats.threadCount}
                    </span>
                  </div>
                  <div className="text-xs mt-1 text-themed-muted opacity-80">
                    {t('memory.activeThreadsDesc')}
                  </div>
                </div>
                <div className="py-2">
                  <div className="flex justify-between items-center">
                    <span className="text-themed-muted">{t('memory.openHandles')}</span>
                    <span className="font-bold text-themed-primary">
                      {stats.handleCount}
                    </span>
                  </div>
                  <div className="text-xs mt-1 text-themed-muted opacity-80">
                    {t('memory.openHandlesDesc')}
                  </div>
                </div>
              </div>
            </div>

            {/* Garbage Collection */}
            <div>
              <div className="text-xs font-semibold mb-2 text-themed-secondary">
                {t('memory.garbageCollection')}
              </div>
              <div className="py-2">
                <div className="flex justify-between items-center">
                  <span className="text-themed-muted">{t('memory.collections')}</span>
                  <span className="font-bold text-themed-primary">
                    {stats.gen0Collections} / {stats.gen1Collections} / {stats.gen2Collections}
                  </span>
                </div>
                <div className="text-xs mt-1 text-themed-muted opacity-80">
                  {t('memory.collectionsDesc')}
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="mt-6 text-center text-sm text-themed-muted">
        {t('memory.autoRefresh', { timestamp: formatDateTime(new Date(stats.timestamp)) })}
      </div>
    </div>
  );
};

export default MemoryDiagnostics;
