import React from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Database, FolderOpen } from 'lucide-react';
import { EpicIcon } from '@components/ui/EpicIcon';
import { formatBytes, formatCount } from '@utils/formatters';
import type { GameCacheInfo, CacheEntityVariant } from '../../../../types';
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
  variant?: CacheEntityVariant;
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
  onRemove,
  variant = 'active'
}) => {
  const { t } = useTranslation();
  const isEpic = game.service === 'epicgames';
  const gameUniqueId = getGameUniqueId(game);
  const isEvictedVariant = variant === 'evicted';

  const stats: ExpandableItemStat[] = [
    {
      icon: FolderOpen,
      value: isEvictedVariant
        ? formatCount(game.evicted_downloads_count ?? 0)
        : formatCount(game.cache_files_found),
      label: 'management.gameDetection.files'
    },
    {
      icon: HardDrive,
      value: isEvictedVariant
        ? formatBytes(game.evicted_bytes ?? 0)
        : formatBytes(game.total_size_bytes),
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

  const isEvicted = game.is_evicted === true;

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
      {isEvicted && (
        <span className="themed-badge status-badge-error">
          {t('management.gameDetection.evictedBadge')}
        </span>
      )}
      {!isEvicted && variant === 'active' && (game.evicted_downloads_count ?? 0) > 0 && (
        <span className="themed-badge status-badge-warning">
          {t('management.gameDetection.partialEvictedBadge', {
            count: game.evicted_downloads_count
          })}
        </span>
      )}
    </span>
  );

  const removeTooltip = isEvictedVariant
    ? t('management.gameDetection.removePartialEvictedTooltip')
    : t('management.gameDetection.removeGameCache');

  return (
    <div className={isEvicted ? 'game-card-evicted' : undefined}>
      <ExpandableItemCard
        id={gameUniqueId}
        title={game.game_name}
        subtitle={subtitle}
        gameAppId={game.game_app_id}
        epicAppId={game.epic_app_id}
        service={game.service}
        stats={stats}
        datasources={game.datasources}
        isExpanded={isExpanded}
        isExpanding={isExpanding}
        isRemoving={isRemoving}
        isAnyRemovalRunning={isAnyRemovalRunning}
        isAdmin={isAdmin}
        cacheReadOnly={cacheReadOnly}
        dockerSocketAvailable={dockerSocketAvailable}
        checkingPermissions={checkingPermissions}
        onToggleDetails={(id) => onToggleDetails(String(id))}
        onRemove={() => onRemove(game)}
        removeTooltip={removeTooltip}
      >
        {/* Depot IDs - Steam only */}
        {!isEpic &&
          (() => {
            const depotIds = isEvictedVariant ? (game.evicted_depot_ids ?? []) : game.depot_ids;
            return depotIds.length > 0 ? (
              <div>
                <p className="text-xs text-themed-muted mb-1.5 font-medium">
                  {t('management.gameDetection.depotIds')}
                </p>
                <div className="flex flex-wrap gap-1">
                  {depotIds.map((depotId) => (
                    <span
                      key={depotId}
                      className="text-xs px-2 py-0.5 rounded border bg-themed-elevated border-themed-primary text-themed-secondary"
                    >
                      {depotId}
                    </span>
                  ))}
                </div>
              </div>
            ) : null;
          })()}

        {/* Sample URLs */}
        <ExpandableList
          items={isEvictedVariant ? (game.evicted_sample_urls ?? []) : game.sample_urls}
          maxInitial={MAX_INITIAL_URLS}
          labelKey="management.gameDetection.sampleUrls"
          showingLabelKey="management.gameDetection.showingUrls"
        />

        {/* Cache File Paths — only available for active (on-disk) items */}
        {!isEvictedVariant && game.cache_file_paths && (
          <ExpandableList
            items={game.cache_file_paths}
            maxInitial={MAX_INITIAL_PATHS}
            labelKey="management.gameDetection.cacheFileLocations"
            showingLabelKey="management.gameDetection.showingPaths"
          />
        )}
      </ExpandableItemCard>
    </div>
  );
};

export default GameCard;
