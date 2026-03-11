import React from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Database, FolderOpen } from 'lucide-react';
import { EpicIcon } from '@components/ui/EpicIcon';
import { formatBytes } from '@utils/formatters';
import type { GameCacheInfo } from '../../../../types';
import ExpandableItemCard, { type ExpandableItemStat } from './ExpandableItemCard';
import ExpandableList from './ExpandableList';
import { getGameUniqueId } from './gameUtils';

interface GameCardProps {
  game: GameCacheInfo;
  isExpanded: boolean;
  isExpanding: boolean;
  isRemoving: boolean;
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onToggleDetails: (gameId: string) => void;
  onRemove: (game: GameCacheInfo) => void;
}

const MAX_INITIAL_PATHS = 50;
const MAX_INITIAL_URLS = 20;

const GameCard: React.FC<GameCardProps> = ({
  game,
  isExpanded,
  isExpanding,
  isRemoving,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onToggleDetails,
  onRemove
}) => {
  const { t } = useTranslation();
  const isEpic = game.service === 'epicgames';
  const gameUniqueId = getGameUniqueId(game);

  const stats: ExpandableItemStat[] = [
    {
      icon: FolderOpen,
      value: game.cache_files_found.toLocaleString(),
      label: 'management.gameDetection.files'
    },
    {
      icon: HardDrive,
      value: formatBytes(game.total_size_bytes),
      label: ''
    }
  ];

  // Only show depot count for Steam games
  if (!isEpic && game.depot_ids.length > 0) {
    stats.push({
      icon: Database,
      value: game.depot_ids.length,
      label: 'management.gameDetection.depot',
      labelCount: game.depot_ids.length
    });
  }

  const serviceBadgeClass = isEpic
    ? 'game-card-service-badge game-card-service-badge--epic'
    : 'game-card-service-badge game-card-service-badge--steam';

  const serviceBadgeLabel = isEpic
    ? t('management.gameDetection.serviceEpicGames')
    : t('management.gameDetection.serviceSteam');

  const subtitle = (
    <span className="flex items-center gap-1.5 flex-shrink-0">
      <span className={serviceBadgeClass}>
        {isEpic && <EpicIcon size={10} className="game-card-epic-icon" />}
        {serviceBadgeLabel}
      </span>
      {!isEpic && (
        <span className="text-xs text-themed-muted bg-themed-elevated px-2 py-0.5 rounded">
          AppID: {game.game_app_id}
        </span>
      )}
    </span>
  );

  // Epic games cannot be removed by AppId (game_app_id is 0)
  const removeTooltip = isEpic
    ? t('management.gameDetection.epicRemovalNotSupported')
    : t('management.gameDetection.removeGameCache');

  return (
    <ExpandableItemCard
      id={gameUniqueId}
      title={game.game_name}
      subtitle={subtitle}
      imageUrl={game.image_url}
      stats={stats}
      datasources={game.datasources}
      isExpanded={isExpanded}
      isExpanding={isExpanding}
      isRemoving={isRemoving}
      isAnyRemovalRunning={isAnyRemovalRunning || isEpic}
      isAdmin={isAdmin}
      cacheReadOnly={cacheReadOnly}
      dockerSocketAvailable={dockerSocketAvailable}
      checkingPermissions={checkingPermissions}
      onToggleDetails={(id) => onToggleDetails(String(id))}
      onRemove={() => onRemove(game)}
      removeTooltip={removeTooltip}
    >
      {/* Depot IDs - Steam only */}
      {!isEpic && game.depot_ids.length > 0 && (
        <div>
          <p className="text-xs text-themed-muted mb-1.5 font-medium">
            {t('management.gameDetection.depotIds')}
          </p>
          <div className="flex flex-wrap gap-1">
            {game.depot_ids.map((depotId) => (
              <span
                key={depotId}
                className="text-xs px-2 py-0.5 rounded border bg-themed-elevated border-themed-primary text-themed-secondary"
              >
                {depotId}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Sample URLs */}
      <ExpandableList
        items={game.sample_urls}
        maxInitial={MAX_INITIAL_URLS}
        labelKey="management.gameDetection.sampleUrls"
        showingLabelKey="management.gameDetection.showingUrls"
      />

      {/* Cache File Paths */}
      {game.cache_file_paths && (
        <ExpandableList
          items={game.cache_file_paths}
          maxInitial={MAX_INITIAL_PATHS}
          labelKey="management.gameDetection.cacheFileLocations"
          showingLabelKey="management.gameDetection.showingPaths"
        />
      )}
    </ExpandableItemCard>
  );
};

export default GameCard;
