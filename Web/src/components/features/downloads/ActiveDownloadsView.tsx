import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, HardDrive, Users, RefreshCw } from 'lucide-react';
import { LoadingState } from '@components/ui/ManagerCard';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { formatBytes, formatSpeed } from '@utils/formatters';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { Tooltip } from '@components/ui/Tooltip';
import { Button } from '@components/ui/Button';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import Badge from '@components/ui/Badge';
import BadgesRow from './BadgesRow';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { buildTrafficKey } from './liveDownloadPreviews';
import { efficiencyTier, HIT_TIER_CLASS } from '@utils/efficiencyTier';
import type { GameSpeedInfo, ClientSpeedInfo } from '../../../types';

const ActiveDownloadsView: React.FC = () => {
  const { t } = useTranslation();
  const { speedSnapshot, gameSpeeds, clientSpeeds, isLoading, refreshSpeed } = useSpeed();
  // The per-row live dot reads the unified activity registry, which is authoritative once ready. NOT
  // an `||`: hasActiveDownloads is a GLOBAL "is anything downloading" flag, not per-row - falling back
  // to it after ready would make every row's dot light up whenever anything else is downloading.
  const activity = useActivityStatus();

  const [viewMode, setViewMode] = useState<'games' | 'clients'>('games');

  // Use data from context
  const hasActiveDownloads = speedSnapshot?.hasActiveDownloads || false;
  const games = gameSpeeds;
  const clients = clientSpeeds;

  const gameDownloading = (game: GameSpeedInfo): boolean =>
    activity.isActiveOrFallback(
      'download',
      buildTrafficKey(game),
      'downloading',
      hasActiveDownloads
    );
  const clientDownloading = (client: ClientSpeedInfo): boolean =>
    activity.isActiveOrFallback('download', client.clientIp, 'downloading', hasActiveDownloads);

  if (isLoading) {
    return (
      <div className="active-downloads-view">
        <div className="w-full">
          <LoadingState shape="downloads" rows={3} />
        </div>
      </div>
    );
  }

  if (!hasActiveDownloads) {
    return (
      <div className="active-empty-state">
        <div className="empty-icon-container">
          <div className="empty-icon-ring" />
          <div className="empty-icon">
            <Activity className="empty-state-icon" />
          </div>
        </div>
        <div className="empty-title">{t('downloads.active.empty.title')}</div>
        <div className="empty-description">{t('downloads.active.empty.description')}</div>
      </div>
    );
  }

  return (
    <div className="active-downloads-view">
      {/* View Toggle */}
      <div className="view-toggle-row">
        <SegmentedControl
          value={viewMode}
          onChange={(next) => setViewMode(next as 'games' | 'clients')}
          showLabels
          options={[
            {
              value: 'games',
              icon: <HardDrive />,
              label: (
                <>
                  {t('downloads.active.tabs.games')}
                  {games.length > 0 && (
                    <Badge variant="neutral" className="badge-count">
                      {games.length}
                    </Badge>
                  )}
                </>
              )
            },
            {
              value: 'clients',
              icon: <Users />,
              label: (
                <>
                  {t('downloads.active.tabs.clients')}
                  {clients.length > 0 && (
                    <Badge variant="neutral" className="badge-count">
                      {clients.length}
                    </Badge>
                  )}
                </>
              )
            }
          ]}
        />

        <Button
          type="button"
          variant="transparent"
          size="xs"
          className="refresh-btn"
          onClick={refreshSpeed}
        >
          <RefreshCw />
          {t('downloads.active.refresh')}
        </Button>
      </div>

      {/* Downloads List */}
      <div className="downloads-list">
        {viewMode === 'games'
          ? games.map((game: GameSpeedInfo, index: number) => (
              <div
                key={`${game.service}-${game.gameAppId || game.gameName || game.depotId}-${game.clientIp ?? 'unknown'}`}
                className={`download-item ${index === 0 ? 'top' : ''}`}
              >
                <div className="download-avatar">
                  <HardDrive className="fallback-icon" size={20} />
                  {gameDownloading(game) && <div className="active-indicator" />}
                </div>

                <div className="download-info">
                  <div className="download-name-row">
                    <BadgesRow service={game.service} showDatasource={false} />
                    <Tooltip
                      content={
                        game.gameName || t('downloads.active.depotLabel', { depotId: game.depotId })
                      }
                      className="download-name"
                    >
                      {game.gameName || t('downloads.active.depotLabel', { depotId: game.depotId })}
                    </Tooltip>
                  </div>
                  <div className="download-meta">
                    <span className="meta-item">{formatBytes(game.totalBytes)}</span>
                    <span className="meta-divider">•</span>
                    <span
                      className={`meta-item cache-hit ${HIT_TIER_CLASS[efficiencyTier(game.cacheHitPercent)]}`}
                    >
                      {t('downloads.active.hitRate', {
                        percent: Math.round(game.cacheHitPercent)
                      })}
                    </span>
                    <span className="meta-divider">•</span>
                    <span className="meta-item">
                      {t('downloads.active.requests', { count: game.requestCount })}
                    </span>
                    {game.clientIp && (
                      <>
                        <span className="meta-divider">•</span>
                        <span className="meta-item">
                          <ClientIpDisplay clientIp={game.clientIp} />
                        </span>
                      </>
                    )}
                  </div>
                </div>

                <div className="download-speed">
                  <span className="speed-value">{formatSpeed(game.bytesPerSecond)}</span>
                  <span className="speed-label caps-label">{t('downloads.active.speed')}</span>
                </div>
              </div>
            ))
          : clients.map((client: ClientSpeedInfo, index: number) => (
              <div key={client.clientIp} className={`download-item ${index === 0 ? 'top' : ''}`}>
                <div className="download-avatar">
                  <Users className="fallback-icon" size={20} />
                  {clientDownloading(client) && <div className="active-indicator" />}
                </div>

                <div className="download-info">
                  <div className="download-name">
                    <ClientIpDisplay clientIp={client.clientIp} />
                  </div>
                  <div className="download-meta">
                    <span className="meta-item">{formatBytes(client.totalBytes)}</span>
                    <span className="meta-divider">•</span>
                    <span className="meta-item">
                      {t('downloads.active.gamesCount', { count: client.activeGames })}
                    </span>
                  </div>
                </div>

                <div className="download-speed">
                  <span className="speed-value">{formatSpeed(client.bytesPerSecond)}</span>
                  <span className="speed-label caps-label">{t('downloads.active.speed')}</span>
                </div>
              </div>
            ))}
      </div>

      {/* Summary Footer */}
      <div className="summary-footer">
        <div className="summary-stat">
          <strong>{games.length}</strong>{' '}
          {t('downloads.active.summary.gamesLabel', { count: games.length })}
        </div>
        <div className="summary-stat">
          <strong>{clients.length}</strong>{' '}
          {t('downloads.active.summary.clientsLabel', { count: clients.length })}
        </div>
        <div className="summary-stat">
          <strong>{formatSpeed(speedSnapshot?.totalBytesPerSecond || 0)}</strong>{' '}
          {t('downloads.active.summary.totalLabel')}
        </div>
        <div className="summary-stat">
          <strong>{speedSnapshot?.entriesInWindow || 0}</strong>{' '}
          {t('downloads.active.summary.requestsWindowLabel', {
            seconds: Math.round(speedSnapshot?.windowSeconds ?? 2)
          })}
        </div>
      </div>
    </div>
  );
};

export default ActiveDownloadsView;
