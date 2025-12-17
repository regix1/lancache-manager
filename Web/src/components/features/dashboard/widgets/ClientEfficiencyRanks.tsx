import React, { useMemo, memo } from 'react';
import { Users, Trophy } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { type ClientStat } from '../../../../types';

interface ClientEfficiencyRanksProps {
  /** Client statistics data */
  clientStats: ClientStat[];
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** Stagger index for entrance animation */
  staggerIndex?: number;
}

interface RankedClient {
  rank: number;
  clientIp: string;
  hitRatio: number;
  totalBytes: number;
  cacheHitBytes: number;
}

/**
 * Widget showing client leaderboard by cache efficiency
 */
const ClientEfficiencyRanks: React.FC<ClientEfficiencyRanksProps> = memo(({
  clientStats,
  glassmorphism = true,
  staggerIndex,
}) => {
  // Calculate and rank clients by efficiency
  const rankedClients = useMemo((): RankedClient[] => {
    const clients = clientStats
      .map((client) => {
        const total = (client.totalCacheHitBytes || 0) + (client.totalCacheMissBytes || 0);
        const hitRatio = total > 0 ? ((client.totalCacheHitBytes || 0) / total) * 100 : 0;
        return {
          rank: 0,
          clientIp: client.clientIp,
          hitRatio,
          totalBytes: total,
          cacheHitBytes: client.totalCacheHitBytes || 0,
        };
      })
      // Filter out clients with no significant data
      .filter((c) => c.totalBytes > 1024 * 1024) // > 1MB
      // Sort by hit ratio (descending), then by total bytes (descending)
      .sort((a, b) => {
        if (Math.abs(a.hitRatio - b.hitRatio) > 0.1) {
          return b.hitRatio - a.hitRatio;
        }
        return b.totalBytes - a.totalBytes;
      });

    // Assign ranks
    clients.forEach((client, index) => {
      client.rank = index + 1;
    });

    return clients.slice(0, 5); // Top 5
  }, [clientStats]);

  // Get rank badge styles - using theme CSS variables
  const getRankBadgeStyle = (rank: number): React.CSSProperties => {
    switch (rank) {
      case 1: // Gold - use warning/yellow theme colors
        return {
          background: 'color-mix(in srgb, var(--theme-icon-yellow) 25%, transparent)',
          color: 'var(--theme-icon-yellow)',
          border: '1px solid color-mix(in srgb, var(--theme-icon-yellow) 50%, transparent)',
        };
      case 2: // Silver - use accent/cyan theme colors
        return {
          background: 'color-mix(in srgb, var(--theme-accent) 20%, transparent)',
          color: 'var(--theme-accent)',
          border: '1px solid color-mix(in srgb, var(--theme-accent) 40%, transparent)',
        };
      case 3: // Bronze - use orange theme colors
        return {
          background: 'color-mix(in srgb, var(--theme-icon-orange) 20%, transparent)',
          color: 'var(--theme-icon-orange)',
          border: '1px solid color-mix(in srgb, var(--theme-icon-orange) 40%, transparent)',
        };
      default:
        return {
          background: 'var(--theme-bg-tertiary)',
          color: 'var(--theme-text-muted)',
          border: 'none',
        };
    }
  };

  // Get rank icon for top 3
  const getRankDisplay = (rank: number): React.ReactNode => {
    const baseClasses = 'flex items-center justify-center w-6 h-6 rounded-full text-xs font-medium';

    if (rank <= 3) {
      return (
        <div className={baseClasses} style={getRankBadgeStyle(rank)}>
          {rank === 1 ? <Trophy className="w-3 h-3" /> : rank}
        </div>
      );
    }
    return (
      <div className={baseClasses} style={getRankBadgeStyle(rank)}>
        {rank}
      </div>
    );
  };

  // Build animation classes
  const animationClasses = staggerIndex !== undefined
    ? `animate-card-entrance stagger-${Math.min(staggerIndex + 1, 12)}`
    : '';

  if (rankedClients.length === 0) {
    return (
      <div
        className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}
      >
        <div className="flex items-center gap-2 mb-3">
          <Users className="w-5 h-5" style={{ color: 'var(--theme-text-muted)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Client Efficiency
          </h3>
        </div>
        <p className="text-sm" style={{ color: 'var(--theme-text-muted)' }}>
          No client data available yet
        </p>
      </div>
    );
  }

  return (
    <div
      className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}
    >
      {/* Header */}
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-2">
          <Users className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Client Efficiency
          </h3>
        </div>
        <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
          Top {rankedClients.length}
        </div>
      </div>

      {/* Leaderboard */}
      <div className="space-y-2">
        {rankedClients.map((client, index) => (
          <div
            key={client.clientIp}
            className="flex items-center gap-3 p-2 rounded-lg transition-colors"
            style={{
              backgroundColor: client.rank <= 3 ? 'color-mix(in srgb, var(--theme-primary) 8%, var(--theme-bg-secondary))' : 'transparent',
              animationDelay: `${index * 50}ms`,
            }}
          >
            {/* Rank badge */}
            {getRankDisplay(client.rank)}

            {/* Client info */}
            <div className="flex-1 min-w-0">
              <div
                className="text-sm font-medium truncate"
                style={{ color: 'var(--theme-text-primary)' }}
                title={client.clientIp}
              >
                {client.clientIp}
              </div>
              <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                {formatBytes(client.cacheHitBytes)} saved
              </div>
            </div>

            {/* Hit ratio */}
            <div className="text-right">
              <div
                className="text-sm font-bold"
                style={{
                  color:
                    client.hitRatio >= 80
                      ? 'var(--theme-success)'
                      : client.hitRatio >= 50
                        ? 'var(--theme-warning)'
                        : 'var(--theme-error)',
                }}
              >
                {client.hitRatio.toFixed(1)}%
              </div>
              <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                hit rate
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
});

ClientEfficiencyRanks.displayName = 'ClientEfficiencyRanks';

export default ClientEfficiencyRanks;
