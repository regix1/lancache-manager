import React, { useEffect, useState } from 'react';
import ApiService from '@services/api.service';

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
  const [stats, setStats] = useState<MemoryStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchMemoryStats = async () => {
    try {
      setError(null);
      const response = await fetch('/api/memory', {
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      const data = await response.json();
      setStats(data);
    } catch (err: any) {
      console.error('Failed to fetch memory stats:', err);
      setError(err.message || 'Failed to load memory statistics');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchMemoryStats();

    // Auto-refresh every 5 seconds
    const interval = setInterval(fetchMemoryStats, 5000);

    return () => clearInterval(interval);
  }, []);

  if (loading) {
    return (
      <div className="min-h-screen bg-themed-primary flex items-center justify-center">
        <div className="text-themed-primary">Loading memory diagnostics...</div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen bg-themed-primary flex items-center justify-center">
        <div className="text-red-500">Error: {error}</div>
      </div>
    );
  }

  if (!stats) {
    return null;
  }

  return (
    <div className="min-h-screen p-6" style={{ backgroundColor: 'var(--theme-bg-primary)' }}>
      <h1
        className="text-3xl font-bold mb-6 pb-3 border-b-2"
        style={{
          color: 'var(--theme-text-primary)',
          borderColor: 'var(--theme-card-border)'
        }}
      >
        Memory Diagnostics
      </h1>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
        {/* Total Memory */}
        <div
          className="rounded-lg p-6 border shadow-lg"
          style={{
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)'
          }}
        >
          <h2
            className="text-xl font-semibold mb-4 pb-2 border-b"
            style={{
              color: 'var(--theme-text-primary)',
              borderColor: 'var(--theme-card-border)'
            }}
          >
            Total Memory (RAM)
            {stats.totalSystemMemoryGB && (
              <div className="text-sm font-normal mt-1" style={{ color: 'var(--theme-text-muted)' }}>
                System Total: <span style={{ color: 'var(--theme-text-accent)' }}>{stats.totalSystemMemoryGB.toFixed(2)} GB</span>
              </div>
            )}
          </h2>
          <div className="space-y-4">
            <div className="py-2 border-b" style={{ borderColor: 'var(--theme-card-border)' }}>
              <div className="flex justify-between items-center">
                <span style={{ color: 'var(--theme-text-muted)' }}>Working Set (Process RAM):</span>
                <span className="font-bold">
                  <span style={{ color: 'var(--theme-text-primary)' }}>{stats.workingSetMB.toFixed(2)} MB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}> (</span>
                  <span style={{ color: 'var(--theme-text-accent)' }}>{stats.workingSetGB.toFixed(2)} GB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}>)</span>
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                Physical RAM currently used by this process
              </div>
            </div>
            <div className="py-2 border-b" style={{ borderColor: 'var(--theme-card-border)' }}>
              <div className="flex justify-between items-center">
                <span style={{ color: 'var(--theme-text-muted)' }}>Managed (.NET Heap):</span>
                <span className="font-bold">
                  <span style={{ color: 'var(--theme-text-primary)' }}>{stats.managedMB.toFixed(2)} MB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}> (</span>
                  <span style={{ color: 'var(--theme-text-accent)' }}>{stats.managedGB.toFixed(2)} GB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}>)</span>
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                Memory managed by .NET garbage collector
              </div>
            </div>
            <div className="py-2">
              <div className="flex justify-between items-center">
                <span style={{ color: 'var(--theme-text-muted)' }}>Unmanaged (Native):</span>
                <span className="font-bold">
                  <span style={{ color: 'var(--theme-text-primary)' }}>{stats.unmanagedMB.toFixed(2)} MB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}> (</span>
                  <span style={{ color: 'var(--theme-text-accent)' }}>{stats.unmanagedGB.toFixed(2)} GB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}>)</span>
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                Native memory used by system libraries and resources
              </div>
            </div>
          </div>
        </div>

        {/* Managed Memory Details */}
        <div
          className="rounded-lg p-6 border shadow-lg"
          style={{
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)'
          }}
        >
          <h2
            className="text-xl font-semibold mb-4 pb-2 border-b"
            style={{
              color: 'var(--theme-text-primary)',
              borderColor: 'var(--theme-card-border)'
            }}
          >
            Managed Memory Details
          </h2>
          <div className="space-y-4">
            <div className="py-2 border-b" style={{ borderColor: 'var(--theme-card-border)' }}>
              <div className="flex justify-between items-center">
                <span style={{ color: 'var(--theme-text-muted)' }}>Total Allocated:</span>
                <span className="font-bold">
                  <span style={{ color: 'var(--theme-text-primary)' }}>{stats.totalAllocatedMB.toFixed(2)} MB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}> (</span>
                  <span style={{ color: 'var(--theme-text-accent)' }}>{stats.totalAllocatedGB.toFixed(2)} GB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}>)</span>
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                Total memory allocated by .NET runtime
              </div>
            </div>
            <div className="py-2 border-b" style={{ borderColor: 'var(--theme-card-border)' }}>
              <div className="flex justify-between items-center">
                <span style={{ color: 'var(--theme-text-muted)' }}>Heap Size:</span>
                <span className="font-bold">
                  <span style={{ color: 'var(--theme-text-primary)' }}>{stats.heapSizeMB.toFixed(2)} MB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}> (</span>
                  <span style={{ color: 'var(--theme-text-accent)' }}>{stats.heapSizeGB.toFixed(2)} GB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}>)</span>
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                Current size of the managed heap
              </div>
            </div>
            <div className="py-2">
              <div className="flex justify-between items-center">
                <span style={{ color: 'var(--theme-text-muted)' }}>Fragmented Memory:</span>
                <span className="font-bold">
                  <span style={{ color: 'var(--theme-text-primary)' }}>{stats.fragmentedMB.toFixed(2)} MB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}> (</span>
                  <span style={{ color: 'var(--theme-text-accent)' }}>{stats.fragmentedGB.toFixed(2)} GB</span>
                  <span style={{ color: 'var(--theme-text-muted)' }}>)</span>
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                Wasted space due to memory fragmentation
              </div>
            </div>
          </div>
        </div>

        {/* Process Statistics */}
        <div
          className="rounded-lg p-6 border shadow-lg"
          style={{
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)'
          }}
        >
          <h2
            className="text-xl font-semibold mb-4 pb-2 border-b"
            style={{
              color: 'var(--theme-text-primary)',
              borderColor: 'var(--theme-card-border)'
            }}
          >
            Process Statistics
          </h2>
          <div className="space-y-4">
            {/* Resource Usage */}
            <div>
              <div className="text-xs font-semibold mb-2" style={{ color: 'var(--theme-text-secondary)' }}>
                RESOURCE USAGE
              </div>
              <div className="space-y-3">
                <div className="py-2 border-b" style={{ borderColor: 'var(--theme-card-border)' }}>
                  <div className="flex justify-between items-center">
                    <span style={{ color: 'var(--theme-text-muted)' }}>Active Threads:</span>
                    <span className="font-bold" style={{ color: 'var(--theme-text-primary)' }}>{stats.threadCount}</span>
                  </div>
                  <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                    Handling requests and background tasks
                  </div>
                </div>
                <div className="py-2">
                  <div className="flex justify-between items-center">
                    <span style={{ color: 'var(--theme-text-muted)' }}>Open Handles:</span>
                    <span className="font-bold" style={{ color: 'var(--theme-text-primary)' }}>{stats.handleCount}</span>
                  </div>
                  <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                    Database connections, file handles, etc.
                  </div>
                </div>
              </div>
            </div>

            {/* Garbage Collection */}
            <div>
              <div className="text-xs font-semibold mb-2" style={{ color: 'var(--theme-text-secondary)' }}>
                GARBAGE COLLECTION
              </div>
              <div className="py-2">
                <div className="flex justify-between items-center">
                  <span style={{ color: 'var(--theme-text-muted)' }}>Collections (0 / 1 / 2):</span>
                  <span className="font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                    {stats.gen0Collections} / {stats.gen1Collections} / {stats.gen2Collections}
                  </span>
                </div>
                <div className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)', opacity: 0.8 }}>
                  0: Short-lived, 1: Medium-lived, 2: Long-lived objects
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="mt-6 text-center text-sm" style={{ color: 'var(--theme-text-muted)' }}>
        Auto-refreshing every 5 seconds | Last updated: {new Date(stats.timestamp).toLocaleString()}
      </div>
    </div>
  );
};

export default MemoryDiagnostics;
