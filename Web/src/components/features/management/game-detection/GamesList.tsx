import React, { useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Pagination } from '@components/ui/Pagination';
import { usePaginatedList } from '@hooks/usePaginatedList';
import GameCard from './GameCard';
import { getGameUniqueId } from './gameUtils';
import type { GameCacheInfo, CacheEntityVariant } from '../../../../types';
import type { UnifiedNotification } from '@contexts/notifications';

interface GamesListProps {
  games: GameCacheInfo[];
  totalGames: number;
  notifications: UnifiedNotification[];
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onRemoveGame: (game: GameCacheInfo) => void;
  variant?: CacheEntityVariant;
}

const ITEMS_PER_PAGE = 20;
const PAGINATION_TOP_THRESHOLD = 100;

const GamesList: React.FC<GamesListProps> = ({
  games,
  totalGames,
  notifications,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onRemoveGame,
  variant = 'active'
}) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedGameId, setExpandedGameId] = useState<string | null>(null);
  const [expandingGameId, setExpandingGameId] = useState<string | null>(null);

  // Memoized filtered and sorted games list
  const filteredAndSortedGames = useMemo(() => {
    // Filter by search query (search in game name, app ID, or service name)
    const query = searchQuery.toLowerCase();
    const filtered = games.filter(
      (game) =>
        game.game_name.toLowerCase().includes(query) ||
        game.game_app_id.toString().includes(searchQuery) ||
        (game.service && game.service.toLowerCase().includes(query))
    );

    // Sort alphabetically by game name (case-insensitive)
    filtered.sort((a, b) =>
      a.game_name.localeCompare(b.game_name, undefined, { sensitivity: 'base' })
    );

    return filtered;
  }, [games, searchQuery]);

  const {
    page: currentPage,
    setPage: setCurrentPage,
    totalPages,
    paginatedItems: paginatedGames
  } = usePaginatedList<GameCacheInfo>({
    items: filteredAndSortedGames,
    pageSize: ITEMS_PER_PAGE,
    resetKey: searchQuery
  });

  const toggleGameDetails = (gameId: number | string) => {
    const id = String(gameId);
    // If already expanded, collapse immediately
    if (expandedGameId === id) {
      setExpandedGameId(null);
      return;
    }

    // Show loading state for expansion
    setExpandingGameId(id);

    // Use setTimeout to allow the loading spinner to render before heavy DOM updates
    setTimeout(() => {
      setExpandedGameId(id);
      setExpandingGameId(null);
    }, 50); // Small delay to let spinner show
  };

  if (totalGames === 0) {
    return null;
  }

  return (
    <div>
      {/* Search Bar */}
      <div className="mb-3">
        <div className="relative">
          <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            placeholder={t('management.gameDetection.placeholders.searchGames')}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-full pl-10 pr-4 py-2 rounded-lg border text-sm bg-themed-secondary border-themed-secondary text-themed-primary"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery('')}
              className="absolute right-3 top-1/2 transform -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
            >
              {t('common.clear')}
            </button>
          )}
        </div>
      </div>

      {/* No Results Message */}
      {filteredAndSortedGames.length === 0 && (
        <div className="text-center py-8 text-themed-muted">
          <Search className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <div className="mb-2">
            {t('management.gameDetection.noGamesMatching', { query: searchQuery })}
          </div>
          <Button variant="subtle" size="sm" onClick={() => setSearchQuery('')}>
            {t('management.gameDetection.clearSearch')}
          </Button>
        </div>
      )}

      {filteredAndSortedGames.length > 0 && (
        <>
          {/* Top Pagination Controls (shown for long lists) */}
          {filteredAndSortedGames.length > PAGINATION_TOP_THRESHOLD && totalPages > 1 && (
            <Pagination
              currentPage={currentPage}
              totalPages={totalPages}
              totalItems={filteredAndSortedGames.length}
              itemsPerPage={ITEMS_PER_PAGE}
              onPageChange={setCurrentPage}
              itemLabel={t('management.gameDetection.gamesLabel')}
            />
          )}

          <div className="space-y-3">
            {paginatedGames.map((game) => {
              const uniqueId = getGameUniqueId(game);
              return (
                <GameCard
                  key={uniqueId}
                  game={game}
                  isExpanded={expandedGameId === uniqueId}
                  isExpanding={expandingGameId === uniqueId}
                  isRemoving={notifications.some(
                    (n) =>
                      (n.type === 'game_removal' || n.type === 'eviction_removal') &&
                      n.status === 'running' &&
                      (game.service === 'epicgames'
                        ? n.details?.gameName === game.game_name
                        : n.details?.gameAppId === game.game_app_id)
                  )}
                  isAnyRemovalRunning={isAnyRemovalRunning}
                  isAdmin={isAdmin}
                  cacheReadOnly={cacheReadOnly}
                  dockerSocketAvailable={dockerSocketAvailable}
                  checkingPermissions={checkingPermissions}
                  onToggleDetails={toggleGameDetails}
                  onRemove={onRemoveGame}
                  variant={variant}
                />
              );
            })}
          </div>

          {/* Pagination Controls */}
          <Pagination
            currentPage={currentPage}
            totalPages={totalPages}
            totalItems={filteredAndSortedGames.length}
            itemsPerPage={ITEMS_PER_PAGE}
            onPageChange={setCurrentPage}
            itemLabel={t('management.gameDetection.gamesLabel')}
          />
        </>
      )}
    </div>
  );
};

export default GamesList;
