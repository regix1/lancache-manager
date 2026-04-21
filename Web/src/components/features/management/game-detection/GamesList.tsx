import React from 'react';
import { useTranslation } from 'react-i18next';
import GameCard from './GameCard';
import CacheEntityList from './CacheEntityList';
import { getGameUniqueId } from './gameUtils';
import type { GameCacheInfo, CacheEntityVariant } from '../../../../types';

interface GamesListProps {
  games: GameCacheInfo[];
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onRemoveGame: (game: GameCacheInfo) => void;
  variant?: CacheEntityVariant;
}

const filterAndSortGames = (games: GameCacheInfo[], searchQuery: string) => {
  const query = searchQuery.toLowerCase();
  const filtered = games.filter(
    (game) =>
      game.game_name.toLowerCase().includes(query) ||
      game.game_app_id.toString().includes(searchQuery) ||
      (game.service && game.service.toLowerCase().includes(query))
  );

  filtered.sort((a, b) =>
    a.game_name.localeCompare(b.game_name, undefined, { sensitivity: 'base' })
  );

  return filtered;
};

const GamesList: React.FC<GamesListProps> = ({
  games,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onRemoveGame,
  variant = 'active'
}) => {
  const { t } = useTranslation();
  return (
    <CacheEntityList
      items={games}
      searchPlaceholder={t('management.gameDetection.placeholders.searchGames')}
      getEmptyMessage={(query) => t('management.gameDetection.noGamesMatching', { query })}
      itemLabel={t('management.gameDetection.gamesLabel')}
      getItemKey={getGameUniqueId}
      filterAndSortItems={filterAndSortGames}
      renderItem={(game, state) => (
        <GameCard
          game={game}
          isExpanded={state.isExpanded}
          isExpanding={state.isExpanding}
          isAnyRemovalRunning={isAnyRemovalRunning}
          isAdmin={isAdmin}
          cacheReadOnly={cacheReadOnly}
          dockerSocketAvailable={dockerSocketAvailable}
          checkingPermissions={checkingPermissions}
          onToggleDetails={state.onToggleDetails}
          onRemove={onRemoveGame}
          variant={variant}
        />
      )}
    />
  );
};

export default GamesList;
