import React, { useMemo, memo } from 'react';
import { Gamepad2 } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { type Download } from '../../../../types';

interface GameCachePerformanceProps {
  /** Latest downloads data to aggregate */
  downloads: Download[];
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** Stagger index for entrance animation */
  staggerIndex?: number;
}

interface GameStats {
  appId: number | null;
  name: string;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  hitRatio: number;
  downloadCount: number;
}

/**
 * Widget showing top games by cache performance
 */
const GameCachePerformance: React.FC<GameCachePerformanceProps> = memo(({
  downloads,
  glassmorphism = true,
  staggerIndex,
}) => {
  // Aggregate downloads by game
  const gameStats = useMemo((): GameStats[] => {
    const gameMap = new Map<string, GameStats>();

    downloads.forEach((download) => {
      const gameKey = download.gameName || download.service || 'Unknown';
      const existing = gameMap.get(gameKey);

      if (existing) {
        existing.cacheHitBytes += download.cacheHitBytes || 0;
        existing.cacheMissBytes += download.cacheMissBytes || 0;
        existing.totalBytes += download.totalBytes || 0;
        existing.downloadCount += 1;
      } else {
        gameMap.set(gameKey, {
          appId: download.gameAppId || null,
          name: gameKey,
          cacheHitBytes: download.cacheHitBytes || 0,
          cacheMissBytes: download.cacheMissBytes || 0,
          totalBytes: download.totalBytes || 0,
          hitRatio: 0,
          downloadCount: 1,
        });
      }
    });

    // Calculate hit ratios and sort by cache hit bytes
    const stats = Array.from(gameMap.values());
    stats.forEach((stat) => {
      const total = stat.cacheHitBytes + stat.cacheMissBytes;
      stat.hitRatio = total > 0 ? (stat.cacheHitBytes / total) * 100 : 0;
    });

    // Sort by total bytes cached (hits)
    stats.sort((a, b) => b.cacheHitBytes - a.cacheHitBytes);

    return stats.slice(0, 5); // Top 5
  }, [downloads]);

  // Calculate total bandwidth saved
  const totalBandwidthSaved = useMemo(() => {
    return gameStats.reduce((acc, stat) => acc + stat.cacheHitBytes, 0);
  }, [gameStats]);

  // Get hit ratio color class
  const getHitRatioColor = (ratio: number): string => {
    if (ratio >= 80) return 'var(--theme-success)';
    if (ratio >= 50) return 'var(--theme-warning)';
    return 'var(--theme-error)';
  };

  // Build animation classes
  const animationClasses = staggerIndex !== undefined
    ? `animate-card-entrance stagger-${Math.min(staggerIndex + 1, 12)}`
    : '';

  if (gameStats.length === 0) {
    return (
      <div
        className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}
      >
        <div className="flex items-center gap-2 mb-3">
          <Gamepad2 className="w-5 h-5" style={{ color: 'var(--theme-text-muted)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Game Cache Performance
          </h3>
        </div>
        <p className="text-sm" style={{ color: 'var(--theme-text-muted)' }}>
          No game data available yet
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
          <Gamepad2 className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Game Cache Performance
          </h3>
        </div>
        <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
          {formatBytes(totalBandwidthSaved)} saved
        </div>
      </div>

      {/* Game list */}
      <div className="space-y-3">
        {gameStats.map((game, index) => (
          <div key={game.name} className="space-y-1.5">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2 min-w-0 flex-1">
                <span
                  className="text-xs font-medium truncate"
                  style={{ color: 'var(--theme-text-primary)' }}
                  title={game.name}
                >
                  {game.name}
                </span>
                <span
                  className="text-xs flex-shrink-0"
                  style={{ color: 'var(--theme-text-muted)' }}
                >
                  ({game.downloadCount})
                </span>
              </div>
              <div className="flex items-center gap-2 flex-shrink-0">
                <span className="text-xs" style={{ color: 'var(--theme-text-secondary)' }}>
                  {formatBytes(game.cacheHitBytes)}
                </span>
                <span
                  className="text-xs font-medium px-1.5 py-0.5 rounded"
                  style={{
                    color: getHitRatioColor(game.hitRatio),
                    backgroundColor: `${getHitRatioColor(game.hitRatio)}20`,
                  }}
                >
                  {game.hitRatio.toFixed(0)}%
                </span>
              </div>
            </div>
            {/* Progress bar */}
            <div className="widget-progress">
              <div
                className="widget-progress-fill"
                style={{
                  width: `${game.hitRatio}%`,
                  backgroundColor: getHitRatioColor(game.hitRatio),
                  animationDelay: `${index * 100}ms`,
                }}
              />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
});

GameCachePerformance.displayName = 'GameCachePerformance';

export default GameCachePerformance;
