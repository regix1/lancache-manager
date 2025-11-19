import React, { useState, useMemo, useEffect } from 'react';
import { Database, Search } from 'lucide-react';
import { Button } from '@components/ui/Button';
import GameCard from './GameCard';
import type { GameCacheInfo } from '../../../types';
import type { UnifiedNotification } from '@contexts/NotificationsContext';

interface GamesListProps {
  games: GameCacheInfo[];
  totalGames: number;
  notifications: UnifiedNotification[];
  isAuthenticated: boolean;
  cacheReadOnly: boolean;
  checkingPermissions: boolean;
  onRemoveGame: (game: GameCacheInfo) => void;
}

const ITEMS_PER_PAGE = 20;

const GamesList: React.FC<GamesListProps> = ({
  games,
  totalGames,
  notifications,
  isAuthenticated,
  cacheReadOnly,
  checkingPermissions,
  onRemoveGame
}) => {
  const [searchQuery, setSearchQuery] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const [expandedGameId, setExpandedGameId] = useState<number | null>(null);
  const [expandingGameId, setExpandingGameId] = useState<number | null>(null);

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

  const toggleGameDetails = (gameId: number) => {
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
    <>
      <div
        className="mb-3 p-3 rounded-lg border"
        style={{
          backgroundColor: 'var(--theme-bg-elevated)',
          borderColor: 'var(--theme-border-secondary)'
        }}
      >
        <div className="flex items-center gap-2 text-themed-primary font-medium">
          <Database className="w-5 h-5 text-themed-accent" />
          Found {totalGames} game{totalGames !== 1 ? 's' : ''} with cache files
          {searchQuery && filteredAndSortedGames.length !== totalGames && (
            <span className="text-sm text-themed-muted font-normal">
              ({filteredAndSortedGames.length} matching)
            </span>
          )}
        </div>
      </div>

      {/* Search Bar */}
      <div className="mb-3">
        <div className="relative">
          <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            placeholder="Search by game name or AppID..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-full pl-10 pr-4 py-2 rounded-lg border text-sm"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderColor: 'var(--theme-border-secondary)',
              color: 'var(--theme-text-primary)'
            }}
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery('')}
              className="absolute right-3 top-1/2 transform -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
            >
              Clear
            </button>
          )}
        </div>
      </div>

      {/* No Results Message */}
      {filteredAndSortedGames.length === 0 && (
        <div className="text-center py-8 text-themed-muted">
          <Search className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <div className="mb-2">No games found matching &ldquo;{searchQuery}&rdquo;</div>
          <Button variant="subtle" size="sm" onClick={() => setSearchQuery('')}>
            Clear search
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
                isAuthenticated={isAuthenticated}
                cacheReadOnly={cacheReadOnly}
                checkingPermissions={checkingPermissions}
                onToggleDetails={toggleGameDetails}
                onRemove={onRemoveGame}
              />
            ))}
          </div>

          {/* Pagination Controls */}
          {totalPages > 1 && (
            <div
              className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 mt-4 p-3 rounded-lg border"
              style={{
                backgroundColor: 'var(--theme-bg-elevated)',
                borderColor: 'var(--theme-border-secondary)'
              }}
            >
              <div className="text-sm text-themed-muted text-center sm:text-left">
                Showing {(currentPage - 1) * ITEMS_PER_PAGE + 1}-
                {Math.min(currentPage * ITEMS_PER_PAGE, filteredAndSortedGames.length)} of{' '}
                {filteredAndSortedGames.length}
              </div>
              <div className="flex items-center justify-center gap-2 flex-wrap">
                <Button
                  variant="default"
                  size="sm"
                  onClick={() => setCurrentPage((prev) => Math.max(1, prev - 1))}
                  disabled={currentPage === 1}
                  className="flex-shrink-0"
                >
                  Previous
                </Button>
                <div className="flex items-center gap-1 flex-shrink-0">
                  {Array.from({ length: Math.min(5, totalPages) }, (_, i) => {
                    let pageNum: number;
                    if (totalPages <= 5) {
                      pageNum = i + 1;
                    } else if (currentPage <= 3) {
                      pageNum = i + 1;
                    } else if (currentPage >= totalPages - 2) {
                      pageNum = totalPages - 4 + i;
                    } else {
                      pageNum = currentPage - 2 + i;
                    }
                    return (
                      <button
                        key={pageNum}
                        onClick={() => setCurrentPage(pageNum)}
                        className={`px-2 sm:px-3 py-1 rounded text-sm transition-colors ${
                          currentPage === pageNum
                            ? 'text-themed-bg font-semibold'
                            : 'text-themed-secondary hover:text-themed-primary'
                        }`}
                        style={
                          currentPage === pageNum
                            ? {
                                backgroundColor: 'var(--theme-accent)'
                              }
                            : {}
                        }
                      >
                        {pageNum}
                      </button>
                    );
                  })}
                </div>
                <Button
                  variant="default"
                  size="sm"
                  onClick={() => setCurrentPage((prev) => Math.min(totalPages, prev + 1))}
                  disabled={currentPage === totalPages}
                  className="flex-shrink-0"
                >
                  Next
                </Button>
              </div>
            </div>
          )}
        </>
      )}
    </>
  );
};

export default GamesList;
