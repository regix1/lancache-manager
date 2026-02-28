import React, { useState, useMemo, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Pagination } from '@components/ui/Pagination';
import GameCard from './GameCard';
import type { GameCacheInfo } from '../../../../types';
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
}

const ITEMS_PER_PAGE = 20;

const GamesList: React.FC<GamesListProps> = ({
  games,
  totalGames,
  notifications,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onRemoveGame
}) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const [expandedGameId, setExpandedGameId] = useState<string | null>(null);
  const [expandingGameId, setExpandingGameId] = useState<string | null>(null);

  // Reset page when search query changes
  useEffect(() => {
    setCurrentPage(1);
  }, [searchQuery]);

  // Memoized filtered, sorted, and paginated games list
  const filteredAndSortedGames = useMemo(() => {
    // Filter by search query (search in game name or app ID)
    const filtered = games.filter(
      (game) =>
        game.game_name.toLowerCase().includes(searchQuery.toLowerCase()) ||
        game.game_app_id.toString().includes(searchQuery)
    );

    // Sort alphabetically by game name (case-insensitive)
    filtered.sort((a, b) =>
      a.game_name.localeCompare(b.game_name, undefined, { sensitivity: 'base' })
    );

    return filtered;
  }, [games, searchQuery]);

  const paginatedGames = useMemo(() => {
    const startIndex = (currentPage - 1) * ITEMS_PER_PAGE;
    const endIndex = startIndex + ITEMS_PER_PAGE;
    return filteredAndSortedGames.slice(startIndex, endIndex);
  }, [filteredAndSortedGames, currentPage]);

  const totalPages = Math.ceil(filteredAndSortedGames.length / ITEMS_PER_PAGE);

  const toggleGameDetails = (gameId: string) => {
    // If already expanded, collapse immediately
    if (expandedGameId === gameId) {
      setExpandedGameId(null);
      return;
    }

    // Show loading state for expansion
    setExpandingGameId(gameId);

    // Use setTimeout to allow the loading spinner to render before heavy DOM updates
    setTimeout(() => {
      setExpandedGameId(gameId);
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
          <div className="space-y-3">
            {paginatedGames.map((game) => (
              <GameCard
                key={game.game_app_id}
                game={game}
                isExpanded={expandedGameId === game.game_app_id}
                isExpanding={expandingGameId === game.game_app_id}
                isRemoving={notifications.some(
                  (n) =>
                    n.type === 'game_removal' &&
                    n.details?.gameAppId === game.game_app_id &&
                    n.status === 'running'
                )}
                isAnyRemovalRunning={isAnyRemovalRunning}
                isAdmin={isAdmin}
                cacheReadOnly={cacheReadOnly}
                dockerSocketAvailable={dockerSocketAvailable}
                checkingPermissions={checkingPermissions}
                onToggleDetails={toggleGameDetails}
                onRemove={onRemoveGame}
              />
            ))}
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
