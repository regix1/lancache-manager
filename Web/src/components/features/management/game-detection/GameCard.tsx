import React from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Database, FolderOpen } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import type { GameCacheInfo } from '../../../../types';
import ExpandableItemCard, { ExpandableItemStat } from './ExpandableItemCard';
import ExpandableList from './ExpandableList';

interface GameCardProps {
  game: GameCacheInfo;
  isExpanded: boolean;
  isExpanding: boolean;
  isRemoving: boolean;
  isAnyRemovalRunning: boolean;
  isAuthenticated: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onToggleDetails: (gameId: number) => void;
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
  isAuthenticated,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onToggleDetails,
  onRemove
}) => {
  const { t } = useTranslation();

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
    },
    {
      icon: Database,
      value: game.depot_ids.length,
      label: 'management.gameDetection.depot',
      labelCount: game.depot_ids.length
    }
  ];

  const subtitle = (
    <span className="text-xs text-themed-muted bg-themed-elevated px-2 py-0.5 rounded flex-shrink-0">
      AppID: {game.game_app_id}
    </span>
  );

  return (
    <ExpandableItemCard
      id={game.game_app_id}
      title={game.game_name}
      subtitle={subtitle}
      stats={stats}
      datasources={game.datasources}
      isExpanded={isExpanded}
      isExpanding={isExpanding}
      isRemoving={isRemoving}
      isAnyRemovalRunning={isAnyRemovalRunning}
      isAuthenticated={isAuthenticated}
      cacheReadOnly={cacheReadOnly}
      dockerSocketAvailable={dockerSocketAvailable}
      checkingPermissions={checkingPermissions}
      onToggleDetails={(id) => onToggleDetails(id as number)}
      onRemove={() => onRemove(game)}
      removeTooltip={t('management.gameDetection.removeGameCache')}
    >
      {/* Depot IDs */}
      {game.depot_ids.length > 0 && (
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
