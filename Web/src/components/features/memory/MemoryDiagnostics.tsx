import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { getErrorMessage } from '@utils/error';
import { ErrorBlock } from '@components/ui/ErrorBlock';

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
  committedMB: number;
  committedGB: number;
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
  const { isConnected } = useSignalR();
  const [stats, setStats] = useState<MemoryStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const refreshedAt = useFormattedDateTime(stats?.timestamp);
  // Mount, reconnect and Retry can overlap, so only the newest read may write the page.
  const statsRequestRef = useRef(0);

  const fetchMemoryStats = async () => {
    const request = ++statsRequestRef.current;
    try {
      setError(null);
      const data = await ApiService.getMemoryStats<MemoryStats>();
      if (request !== statsRequestRef.current) return;
      setStats(data);
    } catch (err: unknown) {
      if (request !== statsRequestRef.current) return;
      const detail = getErrorMessage(err);
      console.error('Failed to fetch memory stats:', detail);
      setError(detail);
    }
    setLoading(false);
  };

  useEffect(() => {
    fetchMemoryStats();
  }, []);

  // A page opened during an outage fills itself once the connection returns.
  useReconnectRefetch(isConnected, () => void fetchMemoryStats());

  if (loading) {
    return (
      <div
        className="min-h-screen p-6 bg-themed-primary"
        role="status"
        aria-live="polite"
        aria-busy="true"
      >
        <span className="sr-only">{t('memory.loading')}</span>
        <div className="h-9 w-64 max-w-full rounded skeleton-shimmer mb-6" />
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6" aria-hidden="true">
          {Array.from({ length: 3 }, (_, cardIndex) => (
            <div key={cardIndex} className="rounded-lg p-6 border bg-themed-card border-themed">
              <div className="h-6 w-1/2 rounded skeleton-shimmer mb-6" />
              <div className="space-y-5">
                {Array.from({ length: 3 }, (_, rowIndex) => (
                  <div key={rowIndex} className="space-y-2">
                    <div className="flex items-center justify-between gap-4">
                      <div className="h-4 w-2/5 rounded skeleton-shimmer" />
                      <div className="h-4 w-1/3 rounded skeleton-shimmer" />
                    </div>
                    <div className="h-3 w-3/4 rounded skeleton-shimmer" />
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  if (error && !stats) {
    // Under the connection banner the box renders nothing, and the page frame hides with it
    // instead of standing as a blank screen.
    return (
      <div className="min-h-screen p-6 bg-themed-primary empty:hidden">
        <ErrorBlock
          title={t('memory.failedToLoad')}
          message={error}
          retryLabel={t('common.retry')}
          onRetry={() => void fetchMemoryStats()}
        />
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

      {/* A failed refresh keeps the last figures under the box. */}
      {error && (
        <ErrorBlock
          className="mb-6"
          title={t('memory.failedToLoad')}
          message={error}
          retryLabel={t('common.retry')}
          onRetry={() => void fetchMemoryStats()}
        />
      )}

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
                  <span className="text-themed-primary">{stats.workingSetMB.toFixed(2)} MB</span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">{stats.workingSetGB.toFixed(2)} GB</span>
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
                  <span className="text-themed-primary">{stats.managedMB.toFixed(2)} MB</span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">{stats.managedGB.toFixed(2)} GB</span>
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
                  <span className="text-themed-primary">{stats.unmanagedMB.toFixed(2)} MB</span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">{stats.unmanagedGB.toFixed(2)} GB</span>
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
                  <span className="text-themed-accent">{stats.totalAllocatedGB.toFixed(2)} GB</span>
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
                  <span className="text-themed-primary">{stats.heapSizeMB.toFixed(2)} MB</span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">{stats.heapSizeGB.toFixed(2)} GB</span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.heapSizeDesc')}
              </div>
            </div>
            <div className="py-2 border-b border-themed">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.committed')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">{stats.committedMB.toFixed(2)} MB</span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">{stats.committedGB.toFixed(2)} GB</span>
                  <span className="text-themed-muted">)</span>
                </span>
              </div>
              <div className="text-xs mt-1 text-themed-muted opacity-80">
                {t('memory.committedDesc')}
              </div>
            </div>
            <div className="py-2">
              <div className="flex justify-between items-center">
                <span className="text-themed-muted">{t('memory.fragmented')}</span>
                <span className="font-bold">
                  <span className="text-themed-primary">{stats.fragmentedMB.toFixed(2)} MB</span>
                  <span className="text-themed-muted"> (</span>
                  <span className="text-themed-accent">{stats.fragmentedGB.toFixed(2)} GB</span>
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
                    <span className="font-bold text-themed-primary">{stats.threadCount}</span>
                  </div>
                  <div className="text-xs mt-1 text-themed-muted opacity-80">
                    {t('memory.activeThreadsDesc')}
                  </div>
                </div>
                <div className="py-2">
                  <div className="flex justify-between items-center">
                    <span className="text-themed-muted">{t('memory.openHandles')}</span>
                    <span className="font-bold text-themed-primary">{stats.handleCount}</span>
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
        {t('memory.autoRefresh', { timestamp: refreshedAt })}
      </div>
    </div>
  );
};

export default MemoryDiagnostics;
