import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, HardDrive, Users, RefreshCw } from 'lucide-react';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { Alert } from '@components/ui/Alert';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { formatBytes, formatSpeed } from '@utils/formatters';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { Tooltip } from '@components/ui/Tooltip';
import { Button } from '@components/ui/Button';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import Badge from '@components/ui/Badge';
import BadgesRow from './BadgesRow';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { buildTrafficKey, getGameDisplayName } from './liveDownloadPreviews';
import { efficiencyTier, HIT_TIER_CLASS } from '@utils/efficiencyTier';
import type { GameSpeedInfo, ClientSpeedInfo } from '../../../types';

const ActiveDownloadsView: React.FC = () => {
  const { t } = useTranslation();
  const { speedSnapshot, gameSpeeds, clientSpeeds, isLoading, refreshSpeed } = useSpeed();
  // Per-row dots resolve through useActivityStatus, whose download branch reads this same rendered
  // SpeedContext snapshot rather than the separately delivered activity event.
  const activity = useActivityStatus();
  const connectionLost = useConnectionLost();

  const [viewMode, setViewMode] = useState<'games' | 'clients'>('games');

  // Use data from context
  const hasActiveDownloads = speedSnapshot?.hasActiveDownloads || false;
  const games = gameSpeeds;
  const clients = clientSpeeds;

  // The green row means "fastest right now". It used to be whichever row came first, because the
  // list arrived sorted by speed; the list now holds fixed slots so a row keeps its place while it
  // downloads, and the fastest has to be picked out rather than assumed to be at the top.
  const fastestGame = games.reduce<GameSpeedInfo | null>(
    (fastest, game) =>
      game.bytesPerSecond > 0 && (fastest === null || game.bytesPerSecond > fastest.bytesPerSecond)
        ? game
        : fastest,
    null
  );
  const fastestClient = clients.reduce<ClientSpeedInfo | null>(
    (fastest, client) =>
      client.bytesPerSecond > 0 &&
      (fastest === null || client.bytesPerSecond > fastest.bytesPerSecond)
        ? client
        : fastest,
    null
  );

  const gameDownloading = (game: GameSpeedInfo): boolean =>
    activity.isActive('download', buildTrafficKey(game), 'downloading');
  const clientDownloading = (client: ClientSpeedInfo): boolean =>
    activity.isActive('download', client.clientIp, 'downloading');

  if (isLoading) {
    return (
      <div className="active-downloads-view">
        <div className="w-full">
          <LoadingState shape="downloads" rows={3} />
        </div>
      </div>
    );
  }

  if (!speedSnapshot || (!hasActiveDownloads && !speedSnapshot.isAvailable)) {
    return (
      <div className="active-downloads-view">
        {/* During an outage the snapshot is a local copy whose rows aged out, so the view says
            updates stopped rather than that live activity is not tracked. */}
        {speedSnapshot && connectionLost ? (
          <Alert color="yellow" title={t('downloads.activity.unavailableTitle')}>
            {t('downloads.activity.unavailableDescription')}
          </Alert>
        ) : (
          <EmptyState
            variant="panel"
            icon={Activity}
            title={t('downloads.activity.waitingTitle')}
            subtitle={t('downloads.activity.waitingDescription')}
          />
        )}
      </div>
    );
  }

  if (!hasActiveDownloads) {
    return (
      <div className="active-downloads-view">
        <EmptyState
          variant="panel"
          icon={Activity}
          title={t('downloads.active.empty.title')}
          subtitle={t('downloads.active.empty.description')}
        />
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

      <p className="text-sm text-themed-muted">{t('downloads.activity.inferenceHelp')}</p>

      {!speedSnapshot.isAvailable && (
        <Alert color="yellow" title={t('downloads.activity.unavailableTitle')}>
          {t('downloads.activity.unavailableDescription')}
        </Alert>
      )}

      {/* Downloads List */}
      <div className="downloads-list">
        {viewMode === 'games'
          ? games.map((game: GameSpeedInfo) => {
              const displayName = getGameDisplayName(
                game.gameName,
                game.service,
                t('downloads.active.depotLabel', { depotId: game.depotId })
              );
              return (
                <div
                  key={game.key}
                  className={`download-item ${game === fastestGame ? 'top' : ''}`}
                >
                  <div className="download-avatar">
                    <HardDrive className="fallback-icon" size={20} />
                    {gameDownloading(game) && <div className="active-indicator" />}
                  </div>

                  <div className="download-info">
                    <div className="download-name-row">
                      <BadgesRow service={game.service} showDatasource={false} />
                      <Tooltip content={displayName} className="download-name">
                        {displayName}
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
              );
            })
          : clients.map((client: ClientSpeedInfo) => (
              <div
                key={client.clientIp}
                className={`download-item ${client === fastestClient ? 'top' : ''}`}
              >
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
          <strong>{formatSpeed(speedSnapshot.totalBytesPerSecond)}</strong>{' '}
          {t('downloads.active.summary.totalLabel')}
        </div>
        <div className="summary-stat">
          <strong>{speedSnapshot.entriesInWindow}</strong>{' '}
          {t('downloads.active.summary.requestsWindowLabel', {
            seconds: Math.round(speedSnapshot.windowSeconds)
          })}
        </div>
      </div>
    </div>
  );
};

export default ActiveDownloadsView;
