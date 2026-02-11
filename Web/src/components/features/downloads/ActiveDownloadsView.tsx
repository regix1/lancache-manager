import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, HardDrive, Users, Loader2, RefreshCw } from 'lucide-react';
import { useSpeed } from '@contexts/SpeedContext';
import { formatBytes, formatSpeed } from '@utils/formatters';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import type { GameSpeedInfo, ClientSpeedInfo } from '../../../types';

const ActiveDownloadsView: React.FC = () => {
  const { t } = useTranslation();
  const { speedSnapshot, gameSpeeds, clientSpeeds, isLoading, refreshSpeed } = useSpeed();

  const [viewMode, setViewMode] = useState<'games' | 'clients'>('games');

  // Use data from context
  const hasActiveDownloads = speedSnapshot?.hasActiveDownloads || false;
  const games = gameSpeeds;
  const clients = clientSpeeds;

  if (isLoading) {
    return (
      <div className="active-loading-state">
        <Loader2 className="active-loading-spinner" />
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
        <div className="empty-description">
          {t('downloads.active.empty.description')}
        </div>
      </div>
    );
  }

  return (
    <div className="active-downloads-view">
      {/* View Toggle */}
      <div className="view-toggle-row">
        <div className="view-toggle">
          <button
            className={`view-toggle-btn ${viewMode === 'games' ? 'active' : ''}`}
            onClick={() => setViewMode('games')}
          >
            <HardDrive />
            {t('downloads.active.tabs.games')}
            {games.length > 0 && <span className="count-badge">{games.length}</span>}
          </button>
          <button
            className={`view-toggle-btn ${viewMode === 'clients' ? 'active' : ''}`}
            onClick={() => setViewMode('clients')}
          >
            <Users />
            {t('downloads.active.tabs.clients')}
            {clients.length > 0 && <span className="count-badge">{clients.length}</span>}
          </button>
        </div>

        <button className="refresh-btn" onClick={refreshSpeed}>
          <RefreshCw />
          {t('downloads.active.refresh')}
        </button>
      </div>

      {/* Downloads List */}
      <div className="downloads-list">
        {viewMode === 'games' ? (
          games.map((game: GameSpeedInfo, index: number) => (
            <div
              key={`${game.depotId}-${game.clientIp ?? 'unknown'}`}
              className={`download-item ${index === 0 ? 'top' : ''}`}
            >
              <div className="download-avatar">
                <HardDrive className="fallback-icon" size={20} />
                <div className="active-indicator" />
              </div>

              <div className="download-info">
                <div
                  className="download-name"
                  title={game.gameName || t('downloads.active.depotLabel', { depotId: game.depotId })}
                >
                  {game.gameName || t('downloads.active.depotLabel', { depotId: game.depotId })}
                </div>
                <div className="download-meta">
                  <span className="meta-item">{formatBytes(game.totalBytes)}</span>
                  <span className="meta-divider">•</span>
                  <span className={`meta-item cache-hit ${
                    game.cacheHitPercent >= 80 ? '' : game.cacheHitPercent >= 50 ? 'medium' : 'low'
                  }`}>
                    {t('downloads.active.hitRate', { percent: game.cacheHitPercent.toFixed(0) })}
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
                <span className="speed-label">{t('downloads.active.speed')}</span>
              </div>
            </div>
          ))
        ) : (
          clients.map((client: ClientSpeedInfo, index: number) => (
            <div
              key={client.clientIp}
              className={`download-item ${index === 0 ? 'top' : ''}`}
            >
              <div className="download-avatar">
                <Users className="fallback-icon" size={20} />
                <div className="active-indicator" />
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
                <span className="speed-label">{t('downloads.active.speed')}</span>
              </div>
            </div>
          ))
        )}
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
          {t('downloads.active.summary.requestsWindowLabel')}
        </div>
      </div>
    </div>
  );
};

export default ActiveDownloadsView;
